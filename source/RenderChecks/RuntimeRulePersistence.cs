using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckRuntimeRulePersistence(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        static void PumpUntil(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("Runtime preference fixture timed out");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(2);
            }
        }
        static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        _ = W.Application.Current ?? new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        string storage = Path.Combine(output, "storage-" + Guid.NewGuid().ToString("N"));
        var file = new RuntimeRulePreferenceFile(Path.Combine(storage, "runtime-rules"));
        var identity = new GameIdentity(111, DateTime.UtcNow, 0x140000000, "fixture", true, TestGame.Classes);
        var layout = new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,0x1000);
        var wanted = new RuntimeRuleSettings(true, AbilityRuleMode.Growth200, true);
        RuntimeRuleSettings? actual = RuntimeRuleSettings.Original;
        GamePace pace = GamePace.Running; int writes = 0, reads = 0; bool fail = false;
        Action? onRead = null, onPace = null;
        var access = new RuntimeRuleGameAccess(_ => { Interlocked.Increment(ref reads); onRead?.Invoke(); return actual; }, (_, _) => { onPace?.Invoke(); return pace; },
            (id, target, path, convert, reapply) =>
            {
                writes++; if (fail) throw new IOException("isolated injected failure");
                Check(reapply && convert, "auto-apply-preserves-saved-conversion-policy");
                Check(path == file.ReceiptPath(id), "uses-current-process-receipt-path");
                actual = target;
                return new(layout, new TestGame().Build(target), 0);
            });
        var editor = new EditorWindow(() => false, storage, storage, storage, runtimeRuleAccess: access);
        void Setup(GameIdentity id) { Set(editor, "game", id); Set(editor, "layout", layout); Set(editor, "snapshot", new TestGame().Build(actual)); }
        void Poll(bool retry = false) => Await((Task)Call(editor, "ReapplyRuntimeRulesAsync", retry)!);
        RuntimeRulesWindow? window = null;
        try
        {
            var unknown = new RuntimeRulesWindow(null);
            Check(!unknown.HasPendingChanges, "unknown-disabled-window-does-not-invent-user-draft"); unknown.Close();
            Setup(identity); Poll(); Check(writes == 0 && !File.Exists(file.PathName), "no-saved-intent-does-not-write-or-invent-settings");
            new RuntimeRuleJournal(file.ReceiptPath(identity)).Save(new(identity.ProcessId, identity.StartTimeUtc, identity.ModuleBase,
                identity.ExeHash, RuntimeRuleSettings.Original, wanted, "committed", []));
            actual = wanted; Poll();
            Check(writes == 0 && file.Read()!.Settings == wanted, "existing-successful-application-is-adopted-without-game-write");
            actual = RuntimeRuleSettings.Original;
            file.Save(new(1, identity.ExeHash, wanted, true));
            Poll(); Check(writes == 0, "running-game-defers-apply");
            pace = GamePace.Unknown; Poll(); Check(writes == 0, "unknown-pace-defers-apply");
            pace = GamePace.Paused; actual = null; Poll(); Check(writes == 0, "unknown-patch-state-does-not-write");
            actual = RuntimeRuleSettings.Original; Set(editor, "busy", true); Poll(); Check(writes == 0, "busy-defers-apply"); Set(editor, "busy", false);
            window = new RuntimeRulesWindow(actual); window.ShowSaved(file.Read()!, select: true);
            Set(editor, "runtimeRulesWindow", window); Set(editor, "runtimeRulesIdentity", identity);
            window.TraitList.IsChecked = false; Poll(); Check(writes == 0, "unsaved-rule-selection-is-preserved");
            window.ShowSaved(file.Read()!, select: true); Poll(); Check(writes == 1 && actual == wanted, "pause-applies-saved-intent-once");
            for (int i = 0; i < 5; i++) Poll(); Check(writes == 1, "repeated-refresh-does-not-write-again");
            actual = RuntimeRuleSettings.Original;
            var changed = identity with { ProcessId = 222, StartTimeUtc = identity.StartTimeUtc.AddSeconds(5), ModuleBase = 0x7ff737fe0000 };
            Setup(changed); Poll(true); Check(writes == 2 && actual == wanted, "new-process-reconciles-same-saved-intent");
            // A callback completing after reconnect must never write to its old process.
            actual = RuntimeRuleSettings.Original; Setup(identity);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            onRead = () => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException(); };
            var polling = (Task)Call(editor, "ReapplyRuntimeRulesAsync", true)!;
            PumpUntil(() => entered.IsSet || polling.IsCompleted);
            Setup(changed); release.Set(); Await(polling); onRead = null;
            Check(writes == 2, "reconnect-during-read-discards-old-result");
            Set(editor, "runtimeRulesWindow", null); fail = true;
            Poll(true); int failedWrites = writes; Poll(); Poll(); Check(writes == failedWrites, "failed-apply-does-not-repeat-on-timer");
            fail = false; Poll(true); Check(writes == failedWrites + 1 && actual == wanted, "explicit-refresh-retries-after-failure");
            file.Save(new(1, identity.ExeHash, RuntimeRuleSettings.Original, true)); Poll();
            Check(actual == RuntimeRuleSettings.Original && file.Read()!.Settings == actual, "saved-original-settings-restore-and-persist");
            int beforeSave = writes; pace = GamePace.Running;
            Set(editor, "runtimeRulesIdentity", changed); Set(editor, "runtimeRulesWindow", window);
            Await((Task)Call(editor, "ChangeRuntimeRules", window, wanted)!);
            Check(writes == beforeSave && file.Read()!.Settings == wanted, "explicit-save-while-running-persists-without-writing-game");
            pace = GamePace.Paused; Poll(); Check(writes == beforeSave + 1, "saved-pending-setting-applies-after-pause");
            actual = RuntimeRuleSettings.Original; Set(editor, "runtimeRulesWindow", null);
            onPace = () => file.Save(new(1, identity.ExeHash, RuntimeRuleSettings.Original, true));
            int beforeChangedFile = writes; Poll(); onPace = null;
            Check(writes == beforeChangedFile, "settings-changed-during-pause-read-cancel-stale-intent");
            Check(reads > 5, "actual-state-is-reread-before-deciding");
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} runtime preference/reconnect/pause/race checks; gameAccess=false");
        }
        finally
        {
            window?.Close(); Set(editor, "game", null); Set(editor, "snapshot", null); editor.Close();
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
}
