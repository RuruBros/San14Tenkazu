using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckGamePreparation(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        static void Await(Task task)
        {
            var watch = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("Preparation fixture timed out");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(2);
            }
            task.GetAwaiter().GetResult();
        }
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        _ = W.Application.Current ?? new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        string storage = Path.Combine(output, "storage-" + Guid.NewGuid().ToString("N"));
        var file = new RuntimeRulePreferenceFile(Path.Combine(storage, "runtime-rules"));
        var identity = new GameIdentity(123, DateTime.UtcNow, 0x140000000, GameBuildProfiles.Current.ExeSha256, true, TestGame.Classes);
        RuntimeRuleSettings? actual = RuntimeRuleSettings.Original;
        bool running = false; int launches = 0, applies = 0;
        EditorWindow? editor = null;
        GamePreparationWindow? window = null;
        var access = new GamePreparationAccess(() => running,
            () => { launches++; identity = identity with { StartTimeUtc = DateTime.UtcNow }; }, () => identity,
            _ => actual,
            (id, settings, receipt) =>
            {
                Check(file.Read()!.Settings == settings, "intent-persisted-before-prepare");
                Check(receipt == file.ReceiptPath(id), "fresh-process-receipt-used");
                Check(window!.Dispatcher.Invoke(() => window.Status.Text == L.Preparation.Waiting && !window.Launch.IsEnabled && window.Cancel.IsEnabled), "ready-not-shown-before-transaction");
                applies++; actual = settings;
            });
        try
        {
            editor = OfflineEditor(() => false, storage, storage, storage, gamePreparationAccess: access);
            window = new GamePreparationWindow(null);
            window.TraitList.IsChecked = true;
            Check(window.Traits.IsChecked == true, "nine-columns-requires-nine-traits");
            window.Ability.SelectedIndex = (int)AbilityRuleMode.Growth200;
            var method = typeof(EditorWindow).GetMethod("PrepareGameAsync", Hidden)!;
            Await((Task)method.Invoke(editor, [window])!);
            Check(launches == 1 && applies == 1, "explicit-action-launches-and-prepares-once");
            Check(window.Status.Text == L.Preparation.Ready && window.Launch.IsEnabled && !window.Cancel.IsEnabled, "ready-follows-verified-readback");
            Check(file.Read()!.Settings == new RuntimeRuleSettings(true, AbilityRuleMode.Growth200, true), "all-selected-rules-survive-new-settings-reader");
            running = true; window.Traits.IsChecked = false;
            var saved = File.ReadAllBytes(file.PathName);
            Await((Task)method.Invoke(editor, [window])!);
            Check(launches == 1 && applies == 1 && saved.SequenceEqual(File.ReadAllBytes(file.PathName)), "existing-game-rejected-before-settings-save");
            Check(window.Status.Text.Contains(L.Preparation.AlreadyRunning), "existing-game-explained");
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} game preparation WPF checks; gameAccess=false");
        }
        finally { window?.Close(); editor?.Close(); SynchronizationContext.SetSynchronizationContext(oldContext); }
    }
}
