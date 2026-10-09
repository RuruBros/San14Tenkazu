using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void ReadCalendarGame(string output)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException("Calendar evidence already exists");
        GameIdentity game = GameMemory.FindGame();
        GameLayout layout = GameMemory.ReadCurrentLayout(game);
        GameClock before = GameMemory.ReadCalendar(game, layout);
        GamePace pace = GameMemory.ReadPace(game, layout);
        GameClock after = GameMemory.ReadCalendar(game, layout);
        var result = new { utc = DateTime.UtcNow, machine = Environment.MachineName, game.ProcessId,
            game.StartTimeUtc, game.ExeHash, game.Verified, state = $"0x{layout.State:X}", before, after,
            stable = before == after, pace = pace.ToString(), writes = 0 };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Console.WriteLine(File.ReadAllText(output));
    }

    private static void CheckCalendar(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Calendar evidence already exists");
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        void Record(string name, bool passed)
        {
            string row = JsonSerializer.Serialize(new { name, passed, gameAccess = false });
            journal.WriteLine(row); Console.WriteLine(row); Require(passed, name);
        }
        var before = new GameCalendarDate(197, 10, 11);
        var dialog = new CalendarDialog(before) { Opacity = 0, ShowActivated = false };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            Record("same-date-cannot-apply", dialog.Target == before && !dialog.ApplyButton.IsEnabled);
            dialog.YearBox.Text = "200"; dialog.MonthBox.Text = "2"; dialog.DayBox.Text = "30";
            Record("game-february-30-is-editable", dialog.Target == new GameCalendarDate(200, 2, 30) && dialog.ApplyButton.IsEnabled);
            dialog.UpdateLayout();
            var content = (W.FrameworkElement)dialog.Content;
            W.Rect Bounds(W.FrameworkElement control) => control.TransformToAncestor(content)
                .TransformBounds(new W.Rect(0, 0, control.ActualWidth, control.ActualHeight));
            Record("fields-and-apply-fit-content", new W.FrameworkElement[] { dialog.YearBox, dialog.MonthBox, dialog.DayBox, dialog.ApplyButton }
                .All(control => { W.Rect bounds = Bounds(control); return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= content.ActualWidth && bounds.Bottom <= content.ActualHeight; }));
            foreach (int dpi in new[] { 96, 144 })
            {
                var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(content.ActualHeight * dpi / 96), dpi, dpi, WM.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "calendar-" + dpi + ".png")); encoder.Save(file);
            }
            foreach ((string year, string month, string day) in new[]
                { ("100", "1", "1"), ("352", "1", "1"), ("200", "13", "1"), ("200", "2", "31"), ("", "2", "1"), ("200.5", "2", "1") })
            {
                dialog.YearBox.Text = year; dialog.MonthBox.Text = month; dialog.DayBox.Text = day;
                Record("invalid-input-" + year + "-" + month + "-" + day, dialog.Target is null && !dialog.ApplyButton.IsEnabled);
            }
            dialog.YearBox.Text = "351"; dialog.MonthBox.Text = "12"; dialog.DayBox.Text = "30";
            Record("upper-bound-is-editable", dialog.Target == new GameCalendarDate(351, 12, 30) && dialog.ApplyButton.IsEnabled);
            dialog.YearBox.Text = "101"; dialog.MonthBox.Text = "1"; dialog.DayBox.Text = "1";
            Record("lower-bound-is-editable", dialog.Target == new GameCalendarDate(101, 1, 1) && dialog.ApplyButton.IsEnabled);
            Record("calendar-icon-renders", GameSkin.Icon(Icons.Calendar) is { Width: > 0 });
        }
        finally { dialog.Close(); }

        foreach (bool accept in new[] { true, false })
        {
            var modal = new CalendarDialog(before) { Opacity = 0, ShowActivated = false };
            modal.Dispatcher.BeginInvoke(WT.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                modal.YearBox.Text = "200"; modal.MonthBox.Text = "2"; modal.DayBox.Text = "30";
                var key = new WI.KeyEventArgs(WI.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(modal), 0,
                    accept ? WI.Key.Enter : WI.Key.Escape) { RoutedEvent = WI.Keyboard.PreviewKeyDownEvent };
                modal.RaiseEvent(key);
            }));
            Record(accept ? "enter-accepts-selected-date" : "escape-cancels", modal.ShowDialog() == accept);
        }

        // Measure the real toolbar without showing the editor (ContentRendered auto-connects to a live game).
        var editor = new EditorWindow(() => false, Path.Combine(output, "seasons"), Path.Combine(output, "ai"), Path.Combine(output, "rules"));
        try
        {
            void Set(string name, object? value) => typeof(EditorWindow).GetField(name, Hidden)!.SetValue(editor, value);
            var button = (PlateButton)typeof(EditorWindow).GetField("calendarButton", Hidden)!.GetValue(editor)!;
            void Update(bool connected = true) => typeof(EditorWindow).GetMethod("UpdateCalendarControls", Hidden)!.Invoke(editor, [connected]);
            Record("disconnected-toolbar-date-disabled", !button.IsEnabled);
            GameIdentity game = new(1, DateTime.UnixEpoch, 1, "fixture", true, San14Tenkazu.Tests.TestGame.Classes);
            var tables = new GameTables(0, 0, 0, 0, 0, 0, 0, 0, 0x100, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var layout = new GameLayout(tables, 0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, 0x1000);
            Set("game", game); Set("layout", layout); Set("snapshot", new GameSnapshot { Date = (197, 10, 11), Hour = 14,
                Cities = [], Counties = [], Officers = [], CastleByCity = [], TileOwners = [], TileCounties = [],
                Forces = [], CorpsForce = [], OfficerById = [], Units = [] }); Update();
            Record("verified-date-toolbar-enabled", button.IsEnabled);
            Set("busy", true); Update(); Record("busy-date-toolbar-disabled", !button.IsEnabled); Set("busy", false);
            Set("game", game with { Verified = false }); Update(); Record("unknown-build-date-toolbar-disabled", !button.IsEnabled); Set("game", game);
            Set("layout", layout with { State = 0 }); Update(); Record("missing-state-date-toolbar-disabled", !button.IsEnabled); Set("layout", layout);
            Update(false); Record("recovering-layout-date-toolbar-disabled", !button.IsEnabled); Update();
            var root = (W.FrameworkElement)editor.Content;
            root.Measure(new W.Size(1000, 680)); root.Arrange(new W.Rect(0, 0, 1000, 680)); root.UpdateLayout();
            var bounds = button.TransformToAncestor(root).TransformBounds(new W.Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Record("date-toolbar-fits-minimum-window", bounds.Left >= 0 && bounds.Right <= 1000 && bounds.Top >= 0 && bounds.Bottom <= 680 && bounds.Width > 0);
            Set("game", null); Set("snapshot", null); Set("layout", null);
        }
        finally { editor.Close(); }
    }
}
