using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WP = System.Windows.Controls.Primitives;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckAutomaticSettings(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
        static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
        static void Await(Task task)
        {
            var timer = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Automatic settings fixture timed out");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(2);
            }
            task.GetAwaiter().GetResult();
        }
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        _ = W.Application.Current ?? new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        string storage = Path.GetFullPath(Path.Combine(output, Guid.NewGuid().ToString("N")));
        var file = new RuntimeRulePreferenceFile(Path.Combine(storage, "runtime-rules"));
        GameIdentity? current = null;
        RuntimeRuleSettings? rules = RuntimeRuleSettings.Original;
        int writes = 0; bool fail = false;
        var access = new AutomaticGameAccess(() => current, id => current == id, _ => rules, _ => false,
            (id, wanted, receipt) => { writes++; if (fail) throw new IOException("injected preparation failure"); rules = wanted; return true; });
        var editor = new EditorWindow(() => false, storage, storage, storage, automaticGameAccess: access);
        var window = new RuntimeRulesWindow(null, offline: true);
        void Poll() => Await((Task)Call(editor, "PollAutomaticGameAsync")!);
        try
        {
            var popup = Field<WP.Popup>(editor, "settingsPopup");
            var options = (WC.StackPanel)((WC.Border)popup.Child).Child;
            Check(options.Children.Count == 3, "manual-launch-preparation-menu-removed");
            Check(Field<PlateButton>(editor, "runtimeRulesButton").IsEnabled && window.Apply.IsEnabled, "settings-available-without-game");
            window.TraitList.IsChecked = true; window.Ability.SelectedIndex = (int)AbilityRuleMode.Growth200;
            Check(window.HasPendingChanges && window.Traits.IsChecked == true, "offline-draft-tracks-nine-slots");
            Await((Task)Call(editor, "ChangeRuntimeRules", window, window.Selection)!);
            Check(file.Read()!.Settings == window.Selection && writes == 0 && !window.HasPendingChanges, "offline-save-does-not-touch-game");
            Poll(); Check(writes == 0, "absent-game-keeps-waiting");
            current = new(123, DateTime.UtcNow, 0x140000000, GameBuildProfiles.Current.ExeSha256, true, TestGame.Classes);
            rules = null; Poll(); Check(writes == 0, "startup-waits-for-known-guards");
            rules = RuntimeRuleSettings.Original; Poll();
            Check(writes == 1 && rules == file.Read()!.Settings, "external-start-automatically-applies-saved-rules");
            Poll(); Check(writes == 1, "repeated-poll-does-not-write-again");
            current = null; Poll(); current = new(124, DateTime.UtcNow, 0x150000000, GameBuildProfiles.Current.ExeSha256, true, TestGame.Classes);
            rules = RuntimeRuleSettings.Original; fail = true; Poll(); Poll();
            Check(writes == 2, "failed-process-is-not-written-repeatedly");
            fail = false;
            Await((Task)Call(editor, "ChangeRuntimeRules", window, window.Selection)!); Poll();
            Check(writes == 3 && rules == window.Selection, "explicit-save-retries-failed-preparation");
            window.TraitList.IsChecked = false;
            window.SetOffline(false); window.ShowSettings(rules, preserveSelection: true);
            Check(window.TraitList.IsChecked == false && window.HasPendingChanges, "connection-preserves-unsaved-draft");
            window.Close();
            var unknown = new RuntimeRulesWindow(null);
            Check(!unknown.Apply.IsEnabled, "unknown-connected-rules-still-block-apply"); unknown.Close();
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} automatic settings WPF checks; gameAccess=false");
        }
        finally { window.Close(); editor.Close(); SynchronizationContext.SetSynchronizationContext(oldContext); }
    }
}
