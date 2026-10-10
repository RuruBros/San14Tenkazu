using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckOfficerStatusChanges(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        void Check(string scenario, bool passed)
        {
            log.WriteLine(JsonSerializer.Serialize(new { scenario, passed })); Require(passed, scenario);
        }
        var game = new TestGame().Force(1, 1, 1).Force(2, 2, 2).City(1, 1).City(2, 1).City(3, 2)
            .Officer(1, "조조", 1, 1, 1).Officer(2, "유비", 2, 3, 1)
            .Officer(12, "조인", 1, 1, 4).Officer(13, "재야장수", 0, 2, 5)
            .Officer(14, "포로장수", 0, 3, 6).Officer(15, "미등장장수", 0, 0, 7);
        var data = game.Build();
        var card = new OfficerEditorCard { Visibility = W.Visibility.Visible }; card.SetCatalog(GameCatalog.Empty);
        var host = new W.Window { Content = card, Width = 500, Height = 1100, WindowStyle = W.WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        T Field<T>(string name) => (T)typeof(OfficerEditorCard).GetField(name, Hidden)!.GetValue(card)!;
        var line = Field<WC.StackPanel>("statusChangeLine"); var mode = Field<WC.ComboBox>("statusChoice");
        var force = Field<WC.ComboBox>("statusTargetForce"); var home = Field<WC.ComboBox>("statusTargetBase");
        var button = Field<PlateButton>("statusChangeButton");
        OfficerStatusChanges.Request? asked = null; card.StatusChangeClicked += request => asked = request;
        void Click() => button.RaiseEvent(new WI.KeyEventArgs(WI.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(host), 0, WI.Key.Enter)
            { RoutedEvent = W.UIElement.KeyDownEvent });
        void Open(int id)
        {
            var o = data.OfficerById[id];
            card.Open(o, new(data.ForceOfCorps(o.Corps), "", System.Drawing.Color.Gray, OfficerFields.StatusName(o.Status),
                o.BaseId, CityNames.BaseName(o.BaseId), ""), [], [], [], new Dictionary<int, string>(), Transfers.Targets(data));
            card.FollowStatusChange(data, o, opening: true);
            typeof(EditorCard).GetMethod("ShowPage", Hidden)!.Invoke(card, [3]);
        }
        try
        {
            host.Show(); Open(12);
            Check("three-status-options-and-current-mode", line.Visibility == W.Visibility.Visible && mode.Items.Count == 3 && mode.SelectedIndex == 0);
            Check("old-dismiss-button-removed", typeof(OfficerEditorCard).GetField("leaveButton", Hidden) is null);
            Check("unchanged-general-cannot-apply", !button.IsEnabled && force.SelectedValue is 1 && home.SelectedValue is 1);
            mode.SelectedIndex = 2;
            Check("captor-includes-original-force", force.Items.Count == 2 && force.SelectedValue is 1);
            Check("same-force-capture-ready", button.IsEnabled && Field<WC.TextBlock>("statusDetention").Text == CityNames.BaseName(1));
            Click(); Check("same-force-capture-click", asked == new OfficerStatusChanges.Request(12, OfficerStatusMode.Prisoner, 1, 1));
            force.SelectedValue = 2;
            Check("capture-shows-native-detention-and-undo-limit", Field<WC.DockPanel>("statusBaseLine").Visibility == W.Visibility.Collapsed &&
                Field<WC.TextBlock>("statusDetention").Text == CityNames.BaseName(3) &&
                Field<WC.TextBlock>("statusChangeNote").Text.Contains(L.StatusChange.UndoNote) && button.IsEnabled);
            Click(); Check("capture-click-keeps-selected-force", asked == new OfficerStatusChanges.Request(12, OfficerStatusMode.Prisoner, 2, 1));
            card.FollowStatusChange(data, data.OfficerById[12]);
            Check("refresh-preserves-requested-prisoner-mode", mode.SelectedIndex == 2 && force.SelectedValue is 2 && button.IsEnabled);
            card.StatusChangeEnabled = false; card.FollowStatusChange(data, data.OfficerById[12]);
            Check("busy-refresh-keeps-capture-disabled", !button.IsEnabled);
            card.StatusChangeEnabled = true;
            Check("idle-restores-capture-action", button.IsEnabled);
            mode.SelectedIndex = 0;
            Check("mode-switch-keeps-actual-force-id", force.SelectedValue is 2 && home.SelectedValue is 3 && button.IsEnabled);
            Click(); Check("regular-click-uses-force-and-base", asked == new OfficerStatusChanges.Request(12, OfficerStatusMode.Regular, 2, 3));
            mode.SelectedIndex = 1;
            Check("free-mode-hides-destinations", Field<WC.DockPanel>("statusForceLine").Visibility == W.Visibility.Collapsed &&
                Field<WC.DockPanel>("statusBaseLine").Visibility == W.Visibility.Collapsed && Field<WC.DockPanel>("statusDetentionLine").Visibility == W.Visibility.Collapsed && button.IsEnabled);
            Click(); Check("free-click-routes-dismissal", asked?.Mode == OfficerStatusMode.Free &&
                OfficerStatusChanges.Plan(data, asked).Transfer == Transfer.Dismiss);
            Open(13); Check("free-current-mode-is-unchanged", mode.SelectedIndex == 1 && !button.IsEnabled);
            mode.SelectedIndex = 0; force.SelectedValue = 2;
            Click(); Check("free-to-regular-routes-recruitment", asked?.Officer == 13 && OfficerStatusChanges.Plan(data, asked).Transfer == Transfer.Recruit);
            Open(14); Check("prisoner-current-mode-is-unchanged", mode.SelectedIndex == 2 && force.SelectedValue is 2 && !button.IsEnabled);
            mode.SelectedIndex = 1; Click();
            Check("prisoner-to-free-routes-release", asked?.Officer == 14 && OfficerStatusChanges.Plan(data, asked).Transfer == Transfer.Release);
            Open(12); mode.SelectedIndex = 2;
            foreach (double width in new[] { 500.0, 600.0 })
            {
                host.Width = width; card.Measure(new W.Size(width, 1100)); card.Arrange(new W.Rect(0, 0, width, 1100)); card.UpdateLayout();
                Check("prisoner-controls-fit-" + width, new W.FrameworkElement[] { mode, force, button, Field<WC.TextBlock>("statusDetention") }
                    .All(control => control.IsVisible && control.ActualWidth > 0 && control.TranslatePoint(new W.Point(), card).X + control.ActualWidth <= width));
                foreach (int dpi in new[] { 96, 144 })
                {
                    var bitmap = new WMI.RenderTargetBitmap((int)(width * dpi / 96), 1100 * dpi / 96, dpi, dpi, WM.PixelFormats.Pbgra32);
                    bitmap.Render(card); var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"officer-status-{width}-{dpi}.png")); encoder.Save(file);
                }
            }
            Open(12); Check("reopening-resets-status-selection", mode.SelectedIndex == 0 && force.SelectedValue is 1);
            Open(1); Check("ruler-hides-status-actions", line.Visibility == W.Visibility.Collapsed);
            Open(15); Check("hidden-officer-hides-status-actions", line.Visibility == W.Visibility.Collapsed);
        }
        finally { host.Close(); }
    }
}
