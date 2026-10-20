using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckOperationProgress(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool passed, string name) { Require(passed, name); checks.Add(name); }
        static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
        static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        string storage = Path.GetFullPath(Path.Combine(output, "storage"));
        var editor = OfflineEditor(() => false, storage, storage, storage);
        var root = (W.FrameworkElement)editor.Content;
        editor.Content = null;
        // Host only the visual tree: the live editor's ContentRendered handler never runs.
        var host = new W.Window { Content = root, Width = 1000, Height = 700, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false };
        var bar = Field<WC.ProgressBar>(editor, "progress");
        var label = Field<WC.TextBlock>(editor, "progressStatus");
        var status = Field<WC.TextBlock>(editor, "status");
        void Busy(bool value, bool scanning = false) { Call(editor, "SetBusy", value, scanning); host.UpdateLayout(); }
        void Progress(string text, int completed = 0, int total = 0) { Call(editor, "SetOperationProgress", text, completed, total); host.UpdateLayout(); }
        void Capture(string name)
        {
            foreach (int dpi in new[] { 96, 144 })
            {
                var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(root.ActualHeight * dpi / 96), dpi, dpi, WM.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{name}-{dpi}.png")); encoder.Save(file);
            }
        }
        try
        {
            host.Show(); host.UpdateLayout();
            Check(!bar.IsVisible && !label.IsVisible, "idle-hides-both-progress-controls");
            status.Text = L.Connection.Ready;
            Busy(true);
            Check(bar.IsVisible && bar.IsIndeterminate && label.IsVisible && label.Text == L.OperationProgress.Working,
                "non-scan-work-shows-animated-progress-and-description");
            Check(!Field<PlateButton>(editor, "connectButton").IsEnabled, "progress-keeps-existing-busy-input-lock");
            Progress(L.Surrender.Checking(3), 0, 3);
            Check(!bar.IsIndeterminate && bar.Value == 0 && bar.Maximum == 3, "validation-does-not-count-as-completed-surrender");
            foreach (int completed in new[] { 0, 1, 2 })
            {
                string message = L.Surrender.Progress(completed, 3, "유비", "조조");
                Progress(message, completed, 3);
                Check(!bar.IsIndeterminate && bar.Value == completed && bar.Maximum == 3 && label.Text == message,
                    $"surrender-shows-completed-{completed}-of-3-and-current-target");
            }
            Check((string)label.ToolTip == label.Text && W.Automation.AutomationProperties.GetName(bar) == label.Text,
                "full-progress-description-is-accessible-and-in-tooltip");
            Check(bar.ActualWidth > 100 && bar.ActualHeight >= 6 && label.ActualHeight > 0,
                "progress-fits-minimum-editor-width");
            Capture("batch-in-progress");
            Busy(true, scanning: true);
            Check(bar.Value == 2 && bar.Maximum == 3 && !bar.IsIndeterminate,
                "nested-busy-does-not-reset-active-counts");
            Progress(L.Surrender.Completed(3, 3), 3, 3);
            Check(bar.Value == bar.Maximum && !bar.IsIndeterminate, "last-completed-surrender-fills-measured-bar");
            Progress(L.Surrender.Refreshing(3, 3));
            Check(bar.IsVisible && bar.IsIndeterminate && label.Text == L.Surrender.Refreshing(3, 3),
                "final-read-stays-visible-with-completed-count-and-unmeasured-indicator");
            // Partial failure must retain its actual count while the changed game state is reread.
            Progress(L.Surrender.Refreshing(1, 3));
            Check(bar.IsIndeterminate && label.Text == L.Surrender.Refreshing(1, 3),
                "partial-failure-refresh-does-not-report-unfinished-surrenders-as-complete");
            status.Text = L.Surrender.BatchStopped(1, 3, "fixture");
            Busy(false);
            Check(!bar.IsVisible && !bar.IsIndeterminate && bar.Value == 0 && !label.IsVisible && label.Text.Length == 0,
                "finished-or-failed-work-clears-progress-and-stops-animation");
            Check(status.Text == L.Surrender.BatchStopped(1, 3, "fixture"), "progress-cleanup-preserves-operation-result");
            Busy(true, scanning: true);
            Check(bar.IsIndeterminate && bar.Maximum == 1 && bar.Value == 0 && label.Text == L.Connection.FindingData,
                "next-scan-starts-without-previous-counts");
            Progress(L.OperationProgress.Reading);
            Capture("rereading");
            Busy(false);
            Set(editor, "seasonDisconnecting", true); Busy(false);
            Check(bar.IsVisible && bar.IsIndeterminate, "disconnect-cleanup-keeps-progress-until-busy-really-ends");
            Set(editor, "seasonDisconnecting", false); Busy(false);
            Check(!bar.IsVisible && Field<PlateButton>(editor, "connectButton").IsEnabled, "idle-restores-input-after-cleanup");
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, checks },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS operation progress: {checks.Count} WPF checks; gameAccess=false");
        }
        finally { host.Close(); editor.Close(); app.Shutdown(); }
    }
}
