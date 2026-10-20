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
        GamePace pace = GamePace.Running; int writes = 0, reads = 0, dataChecks = 0, paceReads = 0; bool fail = false, experienceChanged = false;
        Action? onRead = null, onPace = null, onDataCheck = null;
        var access = new RuntimeRuleGameAccess(_ => { Interlocked.Increment(ref reads); onRead?.Invoke(); return actual; }, (_, _) => { paceReads++; onPace?.Invoke(); return pace; },
            (id, target, path, convert, reapply) =>
            {
                writes++; if (fail) throw new IOException("isolated injected failure");
                Check(reapply && convert, "auto-apply-preserves-saved-conversion-policy");
                Check(path == file.ReceiptPath(id), "uses-current-process-receipt-path");
                actual = target;
                experienceChanged = false;
                return new(layout, new TestGame().Build(target), 0);
            }, (_, _, _) => { Interlocked.Increment(ref dataChecks); onDataCheck?.Invoke(); return experienceChanged; });
        var editor = OfflineEditor(() => false, storage, storage, storage, runtimeRuleAccess: access);
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
            actual = wanted; Setup(changed); file.Save(new(1, identity.ExeHash, wanted, true));
            experienceChanged = true; int beforeLoad = writes;
            pace = GamePace.Running; Poll(); Check(writes == beforeLoad, "loaded-experience-waits-for-pause");
            pace = GamePace.Paused; Poll();
            Check(writes == beforeLoad + 1 && !experienceChanged, "same-patches-after-load-still-reconcile-experience");
            Poll(); Check(writes == beforeLoad + 1, "matching-loaded-experience-is-not-converted-twice");
            int checkedBefore = dataChecks, paceBefore = paceReads, writtenBefore = writes;
            for (int i = 0; i < 3; i++) Poll();
            Check(dataChecks == checkedBefore + 3 && writes == writtenBefore && paceReads == paceBefore,
                "matching-data-is-rechecked-without-pause-observation-or-write");
            // A load or later save match can change the data without moving any table address or code patch.
            experienceChanged = true; Setup(changed); Poll();
            Check(writes == writtenBefore + 1 && !experienceChanged, "same-storage-data-change-after-negative-check-is-applied");
            Poll(); Check(writes == writtenBefore + 1, "same-storage-restoration-is-not-repeated");
            experienceChanged = true; pace = GamePace.Running; Poll();
            Check(writes == writtenBefore + 1, "same-storage-data-change-waits-for-pause");
            experienceChanged = false; pace = GamePace.Paused; paceBefore = paceReads; Poll();
            Check(writes == writtenBefore + 1 && paceReads == paceBefore, "obsolete-positive-data-check-does-not-cause-write");
            experienceChanged = true;
            onDataCheck = () => file.Save(new(1, identity.ExeHash, RuntimeRuleSettings.Original, true));
            Poll(); onDataCheck = null;
            Check(writes == writtenBefore + 1, "settings-changed-during-data-check-cancel-stale-intent");
            file.Save(new(1, identity.ExeHash, wanted, true));
            entered.Reset(); release.Reset();
            onDataCheck = () => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException(); };
            polling = (Task)Call(editor, "ReapplyRuntimeRulesAsync", false)!;
            PumpUntil(() => entered.IsSet || polling.IsCompleted);
            Setup(identity); release.Set(); Await(polling); onDataCheck = null;
            Check(writes == writtenBefore + 1, "reconnect-during-data-check-discards-old-result");
            // A settings match still needs a data check in every mode with a restoration path.
            foreach (bool traits in new[] { false, true })
            foreach (AbilityRuleMode ability in Enum.GetValues<AbilityRuleMode>())
            foreach (bool list in new[] { false, true })
            {
                if (list && !traits) continue;
                var settings = new RuntimeRuleSettings(traits, ability, list);
                actual = settings; file.Save(new(1, identity.ExeHash, settings, true));
                experienceChanged = false; Setup(identity); Poll();
                checkedBefore = dataChecks; writtenBefore = writes;
                experienceChanged = true; Setup(identity); Poll();
                bool needsData = traits || settings.Expanded;
                Check(dataChecks == checkedBefore + (needsData ? 1 : 0) && writes == writtenBefore + (needsData ? 1 : 0)
                    && actual == settings && file.Read()!.Settings == settings,
                    $"same-storage-recheck-mode-traits-{traits}-ability-{ability}-list-{list}");
            }
            CheckRuntimeRuleRecordRechecks(storage, checks, Await);
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} runtime preference/reconnect/pause/race checks; gameAccess=false");
        }
        finally
        {
            window?.Close(); Set(editor, "game", null); Set(editor, "snapshot", null); editor.Close();
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }

    // The UI lifecycle uses real Core recovery plans over synthetic record bytes. No process or user save is opened.
    private static void CheckRuntimeRuleRecordRechecks(string storage, List<string> checks, Action<Task> awaitTask)
    {
        foreach (string scenario in new[] { "growth-experience", "cap-save", "traits-save", "late-save" })
        {
            var settings = new RuntimeRuleSettings(scenario != "growth-experience" && scenario != "cap-save",
                scenario == "traits-save" ? AbilityRuleMode.Original : scenario == "cap-save" ? AbilityRuleMode.Cap200 : AbilityRuleMode.Growth200,
                scenario == "late-save");
            string folder = Path.Combine(storage, scenario);
            var file = new RuntimeRulePreferenceFile(Path.Combine(folder, "runtime-rules"));
            var identity = new GameIdentity(333, DateTime.UtcNow, 0x140000000, "fixture", true, TestGame.Classes);
            var layout = new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,0x1000);
            byte[] stream = new byte[GameSaveFile.RecordSize];
            BitConverter.GetBytes((ushort)77).CopyTo(stream, GameSaveFile.IdStream);
            byte[] abilities = Enumerable.Repeat((byte)150, 5).ToArray();
            int[] experience = Enumerable.Range(0, 5).Select(i => scenario == "growth-experience" ? (int)AbilityProgress.Threshold(150, i == 4, true) : 0).ToArray();
            ushort[] traits = Enumerable.Range(1, 9).Select(i => (ushort)i).ToArray();
            abilities.CopyTo(stream, GameSaveFile.AbilityStream);
            for (int i = 0; i < 5; i++) BitConverter.GetBytes(experience[i]).CopyTo(stream, GameSaveFile.ExperienceStream + i * 4);
            for (int i = 0; i < 9; i++) BitConverter.GetBytes(traits[i]).CopyTo(stream, GameSaveFile.TraitStream + i * 2);
            var saved = new SaveOfficer(1, 77, abilities, experience, traits, stream);
            byte[] record = new byte[GameMemory.OfficerStride]; GameSaveFile.ToRecord(stream, record);
            bool saveAvailable = scenario is "cap-save" or "traits-save";
            RuntimeRuleWrite[] Plan() => (saveAvailable ? SaveRestore.Plan(record, 0x2000, saved, settings) : [])
                .Concat(scenario == "growth-experience" ? RuntimeRuleExperience.Plan(record, 0x2000, settings, settings, true, true) : []).ToArray();
            int writes = 0;
            var access = new RuntimeRuleGameAccess(_ => settings, (_, _) => GamePace.Paused,
                (_, target, _, _, _) =>
                {
                    foreach (var write in Plan()) write.After.CopyTo(record, checked((int)(write.Address - 0x2000)));
                    writes++; return new(layout, new TestGame().Build(target), 0);
                }, (_, _, _) => Plan().Length != 0);
            file.Save(new(1, identity.ExeHash, settings, true));
            var editor = OfflineEditor(() => false, folder, folder, folder, runtimeRuleAccess: access);
            void Set(string name, object? value) => editor.GetType().GetField(name, Hidden)!.SetValue(editor, value);
            void Poll()
            {
                Set("snapshot", new TestGame().Build(settings));
                awaitTask((Task)editor.GetType().GetMethod("ReapplyRuntimeRulesAsync", Hidden)!.Invoke(editor, [false])!);
            }
            void Check(bool valid, string step)
            {
                if (!valid) throw new InvalidOperationException($"{scenario}-{step}");
                checks.Add($"{scenario}-{step}");
            }
            try
            {
                Set("game", identity); Set("layout", layout); Poll(); Check(writes == 0, "initial-match-does-not-write");
                if (scenario != "traits-save") Array.Fill(record, (byte)100, SaveRestore.AbilityOffset, 5);
                if (settings.Traits9) Array.Clear(record, SaveRestore.TraitOffset, SaveRestore.TraitBytes);
                byte[] untouched = (byte[])record.Clone();
                if (scenario == "late-save") { Poll(); Check(writes == 0, "missing-save-defers-recovery"); saveAvailable = true; }
                Poll(); Check(writes == 1 && Plan().Length == 0, "same-address-load-recovers-real-record-values");
                byte[] expected = (byte[])untouched.Clone();
                if (scenario != "traits-save") abilities.CopyTo(expected, SaveRestore.AbilityOffset);
                if (settings.Traits9) stream.AsSpan(GameSaveFile.TraitStream, SaveRestore.TraitBytes).CopyTo(expected.AsSpan(SaveRestore.TraitOffset));
                Check(record.SequenceEqual(expected), "only-recovery-fields-change-and-experience-is-preserved");
                Poll(); Check(writes == 1, "recovered-values-are-not-written-twice");
            }
            finally { Set("game", null); Set("snapshot", null); editor.Close(); }
        }
    }
}
