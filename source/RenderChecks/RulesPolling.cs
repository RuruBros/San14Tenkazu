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
    private static void CheckRulePolling(string output)
    {
        using var journal = new StreamWriter(Path.Combine(output, "rules-polling.jsonl")) { AutoFlush = true };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
        static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
        static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
        static void PumpUntil(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("Rule WPF operation did not complete");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame);
                Thread.Sleep(2);
            }
        }
        static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        void Record(string scenario, bool passed, object details)
        {
            string row = JsonSerializer.Serialize(new { utc = DateTime.UtcNow, scenario, passed, details, gameAccess = false, browserUsed = false });
            journal.WriteLine(row); Console.WriteLine(row);
            if (!passed) throw new InvalidOperationException("Rule polling check failed: " + scenario);
        }
        var fixture = new FormationFixture();
        Func<FormationSnapshot> read = fixture.Snapshot;
        Func<GamePace> pace = () => GamePace.Paused;
        Action? beforeApply = null;
        var access = new RuleGameAccess(_ => read(), (_, _) => pace(), (game, expected, changes) =>
        {
            beforeApply?.Invoke();
            return FormationMemory.Apply(fixture, game, expected, changes);
        });
        string storage = Path.Combine(output, "polling-storage-" + Guid.NewGuid().ToString("N"));
        var window = new EditorWindow(() => false, Path.Combine(storage, "seasons"), Path.Combine(storage, "ai"), storage, access);
        try
        {
            FormationSnapshot initial = fixture.Snapshot();
            RuleProfile profile = initial.Baseline(fixture.Identity.ExeHash);
            RuleProfileFile file = Field<RuleProfileFile>(window, "ruleFile");
            RuleProfileRead kept = file.Save(profile, file.Read().Stamp);
            Set(window, "game", fixture.Identity);
            Set(window, "layout", new GameLayout(new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), 0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, fixture.State));
            Set(window, "snapshot", new TestGame().Build());
            Set(window, "ruleSnapshot", initial);
            Set(window, "ruleKept", kept);
            Field<List<EditorUndo>>(window, "undoHistory").Add(new("fixture", [], new(profile, profile, kept.Stamp, [])));
            Call(window, "Show", new FormationTarget(1));
            Call(window, "UpdateControls");
            var card = Field<FormationEditorCard>(window, "formationEditor");
            var fields = Field<Dictionary<FormationField, NumberField>>(card, "fields");
            W.UIElement[] controls = [Field<PlateButton>(window, "connectButton"), Field<PlateButton>(window, "reloadButton"),
                Field<PlateButton>(window, "undoButton"), Field<PlateButton>(card, "restore"), Field<WC.CheckBox>(card, "auto")];
            int transitions = 0;
            foreach (var control in controls) control.IsEnabledChanged += (_, _) => ++transitions;
            for (int tick = 0; tick < 12; ++tick) Await((Task)Call(window, "PollRulesAsync")!);
            Record("idle-read-controls-stable", transitions == 0 && controls.All(c => c.IsEnabled) && fixture.Writes == 0 && file.Read().Stamp == kept.Stamp,
                new { polls = 12, enabledTransitions = transitions, fixture.Writes, profileUnchanged = file.Read().Stamp == kept.Stamp });

            fields[FormationRules.Fields[0]].Set(22);
            Await((Task)Call(window, "PollRulesAsync")!);
            Record("read-preserves-pending-input", card.Pending == 1 && fields[FormationRules.Fields[0]].Value == 22 && transitions == 0,
                new { card.Pending, value = fields[FormationRules.Fields[0]].Value, enabledTransitions = transitions });
            card.Open(new(initial.Record(1), profile));

            using var readStarted = new ManualResetEventSlim(); using var releaseRead = new ManualResetEventSlim();
            using var writeStarted = new ManualResetEventSlim(); using var releaseWrite = new ManualResetEventSlim();
            read = () => { var captured = fixture.Snapshot(); readStarted.Set(); if (!releaseRead.Wait(10000)) throw new TimeoutException(); return captured; };
            Task poll = (Task)Call(window, "PollRulesAsync")!;
            PumpUntil(() => readStarted.IsSet);
            bool enabledDuringRead = controls.All(c => c.IsEnabled);
            beforeApply = () => { writeStarted.Set(); if (!releaseWrite.Wait(10000)) throw new TimeoutException(); };
            RuleChange[] changes = [new(FormationRules.Key(1, FormationRules.Fields[0]), 10, 20)];
            Task<bool> write = (Task<bool>)Call(window, "WriteRuleAsync", changes, profile.WithChanges(changes), "poll race fixture", true)!;
            PumpUntil(() => writeStarted.IsSet || write.IsCompleted);
            bool disabledDuringWrite = controls.All(c => !c.IsEnabled);
            releaseWrite.Set(); Await(write); releaseRead.Set(); Await(poll);
            kept = file.Read(); profile = kept.Profile!;
            Record("manual-write-during-read", enabledDuringRead && disabledDuringWrite && write.Result && controls.All(c => c.IsEnabled)
                && Field<FormationSnapshot>(window, "ruleSnapshot").Record(1).Value(FormationRules.Fields[0]) == 20
                && Field<RuleProfileRead>(window, "ruleKept").Stamp == kept.Stamp,
                new { enabledDuringRead, disabledDuringWrite, writeSucceeded = write.Result, current = fixture.Snapshot().Record(1).Value(FormationRules.Fields[0]) });
            read = fixture.Snapshot; beforeApply = null; transitions = 0;
            for (int tick = 0; tick < 12; ++tick) Await((Task)Call(window, "PollRulesAsync")!);
            Record("already-applied-controls-stable", transitions == 0 && controls.All(c => c.IsEnabled), new { polls = 12, enabledTransitions = transitions });

            fixture.Load(); writeStarted.Reset(); releaseWrite.Reset();
            beforeApply = () => { writeStarted.Set(); if (!releaseWrite.Wait(10000)) throw new TimeoutException(); };
            poll = (Task)Call(window, "PollRulesAsync")!;
            PumpUntil(() => writeStarted.IsSet || poll.IsCompleted);
            bool autoWriteLocked = controls.All(c => !c.IsEnabled);
            releaseWrite.Set(); Await(poll); beforeApply = null;
            Record("auto-reapply-locks-only-write", autoWriteLocked && controls.All(c => c.IsEnabled)
                && fixture.Snapshot().Record(1).Value(FormationRules.Fields[0]) == 20 && file.Read().Profile!.Default(changes[0].Key) == 10,
                new { autoWriteLocked, current = fixture.Snapshot().Record(1).Value(FormationRules.Fields[0]), baseline = file.Read().Profile!.Default(changes[0].Key) });

            fixture.Load(); using var paceStarted = new ManualResetEventSlim(); using var releasePace = new ManualResetEventSlim();
            pace = () => { paceStarted.Set(); if (!releasePace.Wait(10000)) throw new TimeoutException(); return GamePace.Paused; };
            int writesBefore = fixture.Writes; transitions = 0;
            poll = (Task)Call(window, "PollRulesAsync")!; PumpUntil(() => paceStarted.IsSet);
            fields[FormationRules.Fields[0]].Set(22); releasePace.Set(); Await(poll);
            Record("input-during-pause-check-defers-write", fixture.Writes == writesBefore && card.Pending == 1
                && fields[FormationRules.Fields[0]].Value == 22 && transitions == 0,
                new { writesBefore, writesAfter = fixture.Writes, card.Pending, enabledTransitions = transitions });

            readStarted.Reset(); releaseRead.Reset();
            read = () => { var captured = fixture.Snapshot(); readStarted.Set(); if (!releaseRead.Wait(10000)) throw new TimeoutException(); return captured; };
            poll = (Task)Call(window, "PollRulesAsync")!; PumpUntil(() => readStarted.IsSet);
            Call(window, "ResetRules"); releaseRead.Set(); Await(poll);
            Record("reset-discards-old-read", window.GetType().GetField("ruleSnapshot", Hidden)!.GetValue(window) is null
                && window.GetType().GetField("ruleKept", Hidden)!.GetValue(window) is null,
                new { staleReadDiscarded = window.GetType().GetField("ruleSnapshot", Hidden)!.GetValue(window) is null });
        }
        finally
        {
            Call(window, "ResetRules");
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
}
