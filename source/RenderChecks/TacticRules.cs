using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckTacticRules(string output)
    {
        using var journal = new StreamWriter(Path.Combine(output, "tactic-rules.jsonl")) { AutoFlush = true };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
        static void Set(object o, string name, object? value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
        static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
        static void Pump(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("Tactic WPF operation did not complete");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(2);
            }
        }
        static void Await(Task t) { Pump(() => t.IsCompleted); t.GetAwaiter().GetResult(); }
        void Record(string scenario, bool passed, object details)
        {
            journal.WriteLine(JsonSerializer.Serialize(new { scenario, passed, details, gameAccess = false, browserUsed = false }));
            Console.WriteLine($"Tactic rule check: {scenario} = {passed}");
            if (!passed) throw new InvalidOperationException("Tactic rule check failed: " + scenario);
        }
        var fixture = new TacticFixture(); Func<GamePace> pace = () => GamePace.Paused;
        var access = new RuleGameAccess(g => FormationMemory.Read(fixture, g), (_, _) => pace(),
            (g, s, c) => FormationMemory.Apply(fixture, g, s, c), g => TacticMemory.Read(fixture, g),
            (g, s, c) => TacticMemory.Apply(fixture, g, s, c));
        string storage = Path.Combine(output, "tactic-storage-" + Guid.NewGuid().ToString("N"));
        var window = OfflineEditor(() => false, Path.Combine(storage, "seasons"), Path.Combine(storage, "ai"), storage, access);
        try
        {
            RuleProfile original = fixture.Formations.Snapshot().Baseline(fixture.Identity.ExeHash);
            var file = Field<RuleProfileFile>(window, "ruleFile"); var kept = file.Save(original, "");
            Set(window, "game", fixture.Identity); Set(window, "snapshot", new TestGame().Build());
            Set(window, "layout", new GameLayout(new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), 0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, fixture.Formations.State));
            Set(window, "ruleSnapshot", fixture.Formations.Snapshot()); Set(window, "ruleKept", kept);
            Field<List<EditorUndo>>(window, "undoHistory").Add(new("formation-history", [], new(original, original, kept.Stamp, [])));
            Await((Task)Call(window, "PollRulesAsync")!); kept = file.Read();
            Record("initial-tactic-capture-preserves-formations", kept.Profile!.Table("tactic") is not null && kept.Profile.Defaults.Length == 1900
                && kept.Profile.Defaults.Where(d => d.Key.Table == "formation").SequenceEqual(original.Defaults) && fixture.Writes == 0, new { defaults = kept.Profile.Defaults.Length });
            var migratedUndo = Field<List<EditorUndo>>(window, "undoHistory")[0].Rule!;
            Record("baseline-addition-preserves-undo", migratedUndo.Stamp == kept.Stamp && migratedUndo.Before.Table("tactic") is not null
                && migratedUndo.After.Table("tactic") is not null && migratedUndo.Before.Defaults.Where(d => d.Key.Table == "formation").SequenceEqual(original.Defaults), new { preserved = true });
            var map = Field<MapView>(window, "map"); map.Lists.ShowTab((int)ListKind.Tactics);
            Record("tactic-list-navigation", map.Lists.Section == PaneSection.Data && (bool)Call(window, "Show", new TacticTarget(1))!, new { tab = map.Lists.Tab });
            var card = map.TacticEditor;
            var fields = Field<Dictionary<TacticField, NumberField>>(card, "fields");
            var choices = Field<Dictionary<TacticField, NameChoice>>(card, "choices");
            choices[TacticRules.Field("kind2")].Pick(8); fields[TacticRules.Field("range2")].Set(3);
            fields[TacticRules.Field("amount2")].Set(240); fields[TacticRules.Field("level2")].Set(10); choices[TacticRules.Field("ability")].Pick(2);
            Await((Task)Call(window, "PollRulesAsync")!); map.ReturnToList(); map.ResumeEditor();
            Record("nine-fields-and-pending-input", fields.Count == 6 && choices.Count == 3 && card.Pending == 5 && choices[TacticRules.Field("kind2")].Picked == 8
                && fields[TacticRules.Field("amount2")].Value == 240 && map.Section == PaneSection.Data, new { fields = fields.Count + choices.Count, card.Pending });
            var labels = Field<Dictionary<TacticField, WC.TextBlock>>(card, "labels");
            Record("effect-dependent-label", labels[TacticRules.Field("amount2")].Text == L.Rules.Duration, new { label = labels[TacticRules.Field("amount2")].Text });
            for (int width = 500; width <= 850; width += width == 500 ? 100 : 250)
            {
                Set(map, "paneWidth", (double)width); map.Measure(new W.Size(width + 306, 800)); map.Arrange(new W.Rect(0, 0, width + 306, 800)); map.UpdateLayout();
                Record($"native-layout-{width}", card.ActualWidth > 0 && fields.Values.All(f => f.Box.ActualWidth == 80) && choices.Values.All(c => c.Box.ActualWidth == 180), new { width });
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width + 306, 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(map);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(output, $"tactic-{width}.png")); png.Save(image);
            }
            var apply = (Task<bool>)Call(window, "ApplyTacticAsync")!; Await(apply); kept = file.Read();
            Record("manual-edit-memory-and-profile", apply.Result && kept.Profile!.Overrides.Length == 5 && fixture.Snapshot().Record(1).MirrorsAgree
                && fixture.Snapshot().Values[TacticRules.Key(1, TacticRules.Field("amount2"))] == 240 && card.Pending == 0,
                new { fixture.Writes, apply.Result, overrides = kept.Profile?.Overrides.Length, card.Pending,
                    value = fixture.Snapshot().Values[TacticRules.Key(1, TacticRules.Field("amount2"))] });
            string stamp = kept.Stamp; fixture.Load();
            Await((Task)Call(window, "PollRulesAsync")!);
            Record("reload-reapplies-and-keeps-baseline", fixture.Snapshot().Values[TacticRules.Key(1, TacticRules.Field("amount2"))] == 240 && file.Read().Stamp == stamp,
                new { fixture.Writes, profileUnchanged = file.Read().Stamp == stamp });
            int transitions = 0; W.UIElement[] controls = [Field<PlateButton>(window, "connectButton"), Field<PlateButton>(card, "restore"), Field<WC.CheckBox>(card, "auto")];
            foreach (var control in controls) control.IsEnabledChanged += (_, _) => ++transitions;
            int writes = fixture.Writes;
            for (int i = 0; i < 8; ++i) Await((Task)Call(window, "PollRulesAsync")!);
            Record("idle-poll-does-not-flicker", transitions == 0 && fixture.Writes == writes, new { polls = 8, transitions });
            fixture.Load(); using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            pace = () => { started.Set(); if (!release.Wait(10000)) throw new TimeoutException(); return GamePace.Paused; };
            Task poll = (Task)Call(window, "PollRulesAsync")!; Pump(() => started.IsSet || poll.IsCompleted);
            choices[TacticRules.Field("kind1")].Pick(2); release.Set(); Await(poll);
            Record("input-during-pause-defers-auto-write", fixture.Writes == writes && card.Pending > 0 && choices[TacticRules.Field("kind1")].Picked == 2,
                new { fixture.Writes, card.Pending });
            var mixedState = new RuleReadState(fixture.Formations.Snapshot(), fixture.Snapshot());
            RuleChange[] mixed = [new(FormationRules.Key(1, FormationRules.Fields[0]), 10, 20),
                new(TacticRules.Key(1, TacticRules.Field("amount1")), 10, 30)];
            fixture.FailWrite = fixture.Writes + 3;
            bool rejected = false;
            try { Call(window, "ApplyRules", fixture.Identity, mixedState, mixed); }
            catch (System.Reflection.TargetInvocationException ex) { rejected = ex.InnerException is IOException; }
            Record("mixed-table-failure-restores-first-table", rejected && fixture.Formations.Snapshot().Values[mixed[0].Key] == 10
                && fixture.Snapshot().Values[mixed[1].Key] == 10 && fixture.Snapshot().Record(1).MirrorsAgree,
                new { rejected, formation = fixture.Formations.Snapshot().Values[mixed[0].Key], tactic = fixture.Snapshot().Values[mixed[1].Key] });
        }
        finally
        {
            // Dispose the fixture's pending-input scenario without opening the production discard dialog.
            Field<MapView>(window, "map").HideEditors();
            window.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
}
