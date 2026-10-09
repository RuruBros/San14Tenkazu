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
    private static void CheckPolicyRules(string output)
    {
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "policy-rules.jsonl")) { AutoFlush = true };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
        static void Set(object o, string name, object? value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
        static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
        static void Await(Task t)
        {
            var watch = Stopwatch.StartNew();
            while (!t.IsCompleted)
            {
                if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Policy operation did not complete");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(2);
            }
            t.GetAwaiter().GetResult();
        }
        int cases = 0;
        void Check(string name, bool passed, object details)
        {
            ++cases; journal.WriteLine(JsonSerializer.Serialize(new { name, passed, details, gameAccess = false, browserUsed = false }));
            if (!passed) throw new InvalidOperationException("Policy check failed: " + name);
        }
        var fixture = new PolicyFixture();
        var access = new RuleGameAccess(g => FormationMemory.Read(fixture, g), (_, _) => GamePace.Paused,
            (g, s, c) => FormationMemory.Apply(fixture, g, s, c), g => TacticMemory.Read(fixture, g), (g, s, c) => TacticMemory.Apply(fixture, g, s, c),
            g => PolicyMemory.Read(fixture, g), (g, s, c) => PolicyMemory.Apply(fixture, g, s, c));
        string storage = Path.Combine(output, "storage-" + Guid.NewGuid().ToString("N"));
        var window = new EditorWindow(() => false, Path.Combine(storage, "seasons"), Path.Combine(storage, "ai"), storage, access);
        var map = Field<MapView>(window, "map");
        try
        {
            var file = Field<RuleProfileFile>(window, "ruleFile"); var original = fixture.Tactics.Profile(); var kept = file.Save(original, "");
            Set(window, "game", fixture.Identity); Set(window, "snapshot", new TestGame().Build());
            Set(window, "layout", new GameLayout(new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), 0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, fixture.Tactics.Formations.State));
            Set(window, "ruleSnapshot", fixture.Tactics.Formations.Snapshot()); Set(window, "tacticRuleSnapshot", fixture.Tactics.Snapshot()); Set(window, "ruleKept", kept);
            var history = Field<List<EditorUndo>>(window, "undoHistory"); history.Add(new("fixture-existing-undo", [], new(original, original, kept.Stamp, [])));
            Await((Task)Call(window, "PollRulesAsync")!); kept = file.Read();
            Check("capture-preserves-existing-baselines", fixture.Writes == 0 && kept.Profile!.Defaults.Length == original.Defaults.Length + 36
                && kept.Profile.Defaults.Where(d => d.Key.Table is "formation" or "tactic").SequenceEqual(original.Defaults), new { defaults = kept.Profile!.Defaults.Length });
            Check("capture-preserves-existing-undo", history[0].Rule!.Stamp == kept.Stamp && history[0].Rule!.Before.Table("policy") is not null
                && history[0].Rule!.After.Table("policyeffect") is not null, new { count = history.Count });
            map.ShowList(ListKind.Policies); Await((Task)Call(window, "OpenAsync", new PolicyTarget("policy", 1), true)!);
            var card = map.PolicyEditor; var choices = Field<Dictionary<PolicyField, NameChoice>>(card, "choices");
            Check("policy-eight-effects", choices.Count == 8 && map.Section == PaneSection.Data, new { count = choices.Count });
            choices[PolicyRules.Effects[7]].Pick(2); Await((Task)Call(window, "PollRulesAsync")!); map.ReturnToList(); map.ResumeEditor();
            Check("policy-draft-survives-poll-and-list", card.Pending == 1 && choices[PolicyRules.Effects[7]].Picked == 2, new { card.Pending });
            var apply = (Task<bool>)Call(window, "ApplyPolicyAsync", card)!; Await(apply);
            Check("policy-edit-memory-and-file", apply.Result && fixture.Snapshot().Values[new("policy", 1, "effect8")] == 2
                && file.Read().Profile!.Desired(new("policy", 1, "effect8")) == 2 && card.Pending == 0, new { apply.Result, fixture.Writes });
            // Real card link event follows the same route as the visible Lv.1..10 hyperlink.
            ((Action<EditorTarget>)typeof(EditorCard).GetField("LinkClicked", Hidden)!.GetValue(card)!).Invoke(new PolicyTarget("policyeffect", 1));
            Check("policy-effect-link-navigation", map.PolicyEffectEditor.Shown && map.Section == PaneSection.Data, new { target = map.PolicyEffectEditor.Target });
            Await((Task)Call(window, "StepAsync", -1)!);
            Check("linked-effect-back-returns-policy-detail", map.PolicyEditor.Shown && map.PolicyEditor.RecordId == 1, new { target = card.Target });
            map.ShowList(ListKind.PolicyEffects); Await((Task)Call(window, "OpenAsync", new PolicyTarget("policyeffect", 1), true)!);
            var effect = map.PolicyEffectEditor; var fields = Field<Dictionary<PolicyField, NumberField>>(effect, "fields");
            Check("effect-ten-levels", fields.Count == 10, new { fields = fields.Count });
            fields[PolicyRules.Levels[9]].Set(65535); Await((Task)Call(window, "PollRulesAsync")!);
            Check("effect-draft-preserved", effect.Pending == 1 && fields[PolicyRules.Levels[9]].Value == 65535, new { effect.Pending });
            foreach (int width in new[] { 500, 600, 850 })
            {
                Set(map, "paneWidth", (double)width); map.Measure(new W.Size(width + 306, 800)); map.Arrange(new W.Rect(0, 0, width + 306, 800)); map.UpdateLayout();
                Check("effect-layout-" + width, fields.Values.All(f => f.Box.ActualWidth == 80), new { width });
            }
            fields[PolicyRules.Levels[0]].Box.Text = "65536";
            Check("ushort-overflow-rejected", effect.Changes(out string error) is null && error.Length > 0, new { error });
            fields[PolicyRules.Levels[0]].Set(2);
            apply = (Task<bool>)Call(window, "ApplyPolicyAsync", effect)!; Await(apply); kept = file.Read();
            Check("effect-edit-memory-and-file", apply.Result && fixture.Snapshot().Record("policyeffect", 1).MirrorsAgree
                && kept.Profile!.Desired(new("policyeffect", 1, "level10")) == 65535 && effect.Pending == 0, new { apply.Result, fixture.Writes });
            var edit = history[^1].Rule!; var undoChanges = edit.Changes.Select(c => c with { Before = c.After, After = c.Before }).ToArray();
            var undoWrite = (Task<bool>)Call(window, "WriteRuleAsync", undoChanges, edit.Before, "fixture-undo", false)!; Await(undoWrite);
            Check("undo-write-restores-value-and-profile", undoWrite.Result && fixture.Snapshot().Values[new("policyeffect", 1, "level10")] == 20
                && file.Read().Profile!.SameAs(edit.Before), new { undoWrite.Result });
            apply = (Task<bool>)Call(window, "WriteRuleAsync", edit.Changes, edit.After, "fixture-redo", false)!; Await(apply);
            var restoreChanges = PolicyRules.Levels.Select(f => new RuleChange(PolicyRules.Key(1, f), fixture.Snapshot().Record("policyeffect", 1).Value(f), edit.After.Default(PolicyRules.Key(1, f)))).ToArray();
            var restore = (Task<bool>)Call(window, "WriteRuleAsync", restoreChanges, edit.After.WithChanges(restoreChanges, true), "fixture-restore", true)!; Await(restore);
            Check("restore-removes-only-effect-overrides", restore.Result && file.Read().Profile!.Overrides.All(o => o.Key.Table != "policyeffect")
                && file.Read().Profile!.Desired(new("policy", 1, "effect8")) == 2 && history[^1].Rule!.Before.Desired(new("policyeffect", 1, "level10")) == 65535, new { restore.Result });
            var restoreUndo = history[^1].Rule!;
            var restoredUndo = (Task<bool>)Call(window, "WriteRuleAsync", restoreUndo.Changes.Select(c => c with { Before = c.After, After = c.Before }).ToArray(), restoreUndo.Before, "fixture-restore-undo", false)!; Await(restoredUndo);
            Check("restore-undo-recovers-override", restoredUndo.Result && file.Read().Profile!.Desired(new("policyeffect", 1, "level10")) == 65535, new { restoredUndo.Result });
            string stamp = file.Read().Stamp; fixture.Load(); Await((Task)Call(window, "PollRulesAsync")!);
            Check("reload-reapplies-both-policy-tables", fixture.Snapshot().Values[new("policy", 1, "effect8")] == 2 && fixture.Snapshot().Values[new("policyeffect", 1, "level10")] == 65535
                && file.Read().Stamp == stamp, new { fixture.Writes });
            int transitions = 0, writes = fixture.Writes;
            W.UIElement[] controls = [Field<PlateButton>(window, "connectButton"), Field<PlateButton>(effect, "restore"), Field<WC.CheckBox>(effect, "auto")];
            foreach (var control in controls) control.IsEnabledChanged += (_, _) => ++transitions;
            for (int i = 0; i < 8; ++i) Await((Task)Call(window, "PollRulesAsync")!);
            Check("idle-poll-no-flicker", transitions == 0 && fixture.Writes == writes, new { transitions, writes });
            fields[PolicyRules.Levels[0]].Set(300); fixture.Load(); Await((Task)Call(window, "PollRulesAsync")!);
            Check("pending-input-defers-auto-write", fixture.Writes == writes && fields[PolicyRules.Levels[0]].Value == 300, new { fixture.Writes });
            var state = new RuleReadState(fixture.Tactics.Formations.Snapshot(), fixture.Tactics.Snapshot(), fixture.Snapshot());
            RuleChange[] mixed = [new(new("formation", 1, "mobility"), 10, 16), new(new("tactic", 1, "amount1"), 10, 50), new(new("policyeffect", 1, "level1"), 2, 500)];
            fixture.FailWrite = fixture.Writes + 5; bool rejected = false;
            try { Call(window, "ApplyRules", fixture.Identity, state, mixed); }
            catch (System.Reflection.TargetInvocationException ex) { rejected = ex.InnerException is IOException; }
            Check("three-table-failure-rolls-back-all", rejected && fixture.Tactics.Formations.Snapshot().Values[mixed[0].Key] == 10
                && fixture.Tactics.Snapshot().Values[mixed[1].Key] == 10 && fixture.Snapshot().Values[mixed[2].Key] == 2, new { rejected });
            Console.WriteLine($"Policy Native WPF checks: {cases} passed; output={output}");
        }
        finally { map.HideEditors(); window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
