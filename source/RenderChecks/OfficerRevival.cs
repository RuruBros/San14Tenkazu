using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckOfficerRevival(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        void Check(string scenario, bool passed)
        {
            log.WriteLine(JsonSerializer.Serialize(new { scenario, passed }));
            Require(passed, scenario);
        }
        var data = new TestGame().Force(1, 1, 1).City(1, 1).Officer(1, "조조", 1, 1, 1)
            .Officer(12, "하진", 0, 0, 9).With(12, OfficerFields.Death, 190)
            .Officer(13, "윤대목", 0, 0, 7).With(13, OfficerFields.Birth, 211)
            .With(13, OfficerFields.Debut, 230).With(13, OfficerFields.Death, 270).Build();
        var card = new OfficerEditorCard { Visibility = W.Visibility.Visible };
        card.SetCatalog(GameCatalog.Empty);
        var host = new W.Window { Content = card, Width = 500, Height = 1050, WindowStyle = W.WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        T Field<T>(string name) => (T)typeof(OfficerEditorCard).GetField(name, Hidden)!.GetValue(card)!;
        var line = Field<WC.StackPanel>("revivalLine");
        var mode = Field<WC.ComboBox>("revivalMode");
        var destination = Field<WC.ComboBox>("revivalBase");
        var death = Field<NumberField>("revivalDeath");
        var button = Field<PlateButton>("revivalButton");
        void Open(int id)
        {
            var officer = data.OfficerById[id];
            card.Open(officer, new(0, "", System.Drawing.Color.Gray, OfficerFields.StatusName(officer.Status), 0, "", ""), [], [], [], new Dictionary<int, string>(), Transfers.Targets(data));
            card.FollowRevival(data, officer, opening: true);
            typeof(EditorCard).GetMethod("ShowPage", Hidden)!.Invoke(card, [3]);
        }
        try
        {
            host.Show(); Open(12);
            Check("dead-panel-shown", line.Visibility == W.Visibility.Visible && mode.Items.Count == 2 && button.Text == L.Revival.Revive);
            Check("revival-shows-its-own-undo-limit", Field<WC.TextBlock>("transferHow").Visibility == W.Visibility.Collapsed &&
                line.Children.OfType<WC.TextBlock>().Any(t => t.Text == L.Revival.UndoNote));
            Check("expired-lifespan-needs-explicit-input", !button.IsEnabled && death.Value == 190);
            death.Set(220);
            Check("serving-revival-ready", button.IsEnabled && destination.Items.Count == 1 && card.Pending == 0);
            mode.SelectedIndex = 1;
            Check("free-mode-offers-neutral-cities", destination.Items.Count == GameMemory.CityCount && button.IsEnabled);
            card.RevivalEnabled = false;
            card.FollowRevival(data, data.OfficerById[12]);
            Check("busy-refresh-keeps-action-disabled", !button.IsEnabled);
            card.RevivalEnabled = true;
            Check("idle-restores-valid-action", button.IsEnabled);
            foreach (string invalid in new[] { "203", "1000", "invalid", "" })
            {
                death.Box.Text = invalid;
                Check("invalid-lifespan-blocked-" + invalid, !button.IsEnabled);
            }
            Open(13);
            Check("hidden-panel-shows-appearance", line.Visibility == W.Visibility.Visible && button.Text == L.Revival.Appear &&
                ((WC.TextBlock)Field<WC.Border>("revivalHeading").Child).Text == L.Revival.AppearanceTitle);
            Check("hidden-appearance-preserves-years-note", Field<WC.TextBlock>("revivalNote").Text == L.Revival.AppearanceNote &&
                Field<WC.TextBlock>("transferHow").Visibility == W.Visibility.Collapsed);
            Check("hidden-serving-appearance-ready", button.IsEnabled && destination.Items.Count == 1 && death.Value == 270 && card.Pending == 0);
            mode.SelectedIndex = 1;
            Check("hidden-free-appearance-ready", button.IsEnabled && destination.Items.Count == GameMemory.CityCount);
            card.RevivalEnabled = false;
            card.FollowRevival(data, data.OfficerById[13]);
            Check("hidden-busy-refresh-keeps-action-disabled", !button.IsEnabled);
            card.RevivalEnabled = true;
            death.Box.Text = "203";
            Check("hidden-expired-lifespan-blocked", !button.IsEnabled);
            Open(13);
            Check("hidden-reopening-resets-action-input", mode.SelectedIndex == 0 && death.Value == 270 && button.IsEnabled);
            foreach (double width in new[] { 500.0, 600.0 })
            {
                host.Width = width;
                card.Measure(new W.Size(width, 1050)); card.Arrange(new W.Rect(0, 0, width, 1050)); card.UpdateLayout();
                Check("appearance-controls-fit-" + width, new W.FrameworkElement[] { mode, destination, death.Box, button }
                    .All(control => control.IsVisible && control.ActualWidth > 0 && control.TranslatePoint(new W.Point(), card).X + control.ActualWidth <= width));
                foreach (int dpi in new[] { 96, 144 })
                {
                    var bitmap = new WMI.RenderTargetBitmap((int)(width * dpi / 96), 1050 * dpi / 96, dpi, dpi, WM.PixelFormats.Pbgra32);
                    bitmap.Render(card); var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"officer-appearance-{width}-{dpi}.png")); encoder.Save(file);
                }
            }
            Open(12);
            death.Set(220);
            foreach (double width in new[] { 500.0, 600.0 })
            {
                host.Width = width;
                card.Measure(new W.Size(width, 1050)); card.Arrange(new W.Rect(0, 0, width, 1050)); card.UpdateLayout();
                Check("revival-controls-fit-" + width, new W.FrameworkElement[] { mode, destination, death.Box, button }
                    .All(control => control.IsVisible && control.ActualWidth > 0 && control.TranslatePoint(new W.Point(), card).X + control.ActualWidth <= width));
                foreach (int dpi in new[] { 96, 144 })
                {
                    var bitmap = new WMI.RenderTargetBitmap((int)(width * dpi / 96), 1050 * dpi / 96, dpi, dpi, WM.PixelFormats.Pbgra32);
                    bitmap.Render(card); var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"officer-revival-{width}-{dpi}.png")); encoder.Save(file);
                }
            }
            Open(1);
            Check("living-officer-hides-revival", line.Visibility == W.Visibility.Collapsed);
            Check("living-officer-keeps-transfer-help", Field<WC.TextBlock>("transferHow").Visibility == W.Visibility.Visible);
            Open(12);
            Check("reopening-resets-action-input", mode.SelectedIndex == 0 && death.Value == 190 && !button.IsEnabled && button.Text == L.Revival.Revive);
        }
        finally { host.Close(); }
    }
}
