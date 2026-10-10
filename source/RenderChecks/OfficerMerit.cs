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
    private static void CheckOfficerMerit(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        void Check(string scenario, bool passed, object? details = null)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, details, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line); Require(passed, scenario);
        }
        var data = new TestGame().Force(1, 1, 1)
            .Officer(12, "조인", 1, 1, 4).With(12, OfficerFields.Merit, 1234)
            .Officer(13, "조홍", 1, 1, 4).With(13, OfficerFields.Merit, 500).Build();
        var card = new OfficerEditorCard { Visibility = W.Visibility.Visible };
        var host = new W.Window { Content = card, Width = 500, Height = 1100, WindowStyle = W.WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        card.SetCatalog(GameCatalog.Empty);
        var standing = new OfficerEditorCard.Standing(1, "조조군", System.Drawing.Color.Blue, "일반", 1, "진류", "");
        void Open(int id = 12) => card.Open(data.OfficerById[id], standing, [], [], [], new Dictionary<int, string>(), []);
        var merit = (NumberField)typeof(OfficerEditorCard).GetField("merit", Hidden)!.GetValue(card)!;
        OfficerRecord Updated(int value)
        {
            var officer = data.OfficerById[12] with { Data = (byte[])data.OfficerById[12].Data.Clone() };
            OfficerFields.Merit.Encode(value).CopyTo(officer.Data, OfficerFields.Merit.Offset);
            return officer;
        }
        try
        {
            host.Show(); Open();
            Check("open-shows-current-merit-with-no-pending-edit", merit.Value == 1234 && merit.Current == 1234
                && card.Pending == 0 && card.Changes(out _) is { Own.Count: 0 });
            foreach (double width in new[] { 500.0, 600.0 })
            {
                card.Measure(new W.Size(width, 1100)); card.Arrange(new W.Rect(0, 0, width, 1100)); card.UpdateLayout();
                var line = (WC.DockPanel)((WC.WrapPanel)merit.Box.Parent).Parent;
                Check($"merit-input-and-label-fit-{width}", line.Children.OfType<WC.TextBlock>().Single().Text == L.Fields.Merit
                    && merit.Box.IsVisible && merit.Box.ActualWidth > 0 && merit.Box.ActualHeight > 0
                    && merit.Box.TranslatePoint(new W.Point(), card).X + merit.Box.ActualWidth <= width,
                    new { label = line.Children.OfType<WC.TextBlock>().Single().Text, merit.Box.IsVisible,
                        merit.Box.ActualWidth, merit.Box.ActualHeight, position = merit.Box.TranslatePoint(new W.Point(), card),
                        cardWidth = card.ActualWidth, card.Visibility });
                foreach (int dpi in new[] { 96, 144 })
                {
                    var bitmap = new WMI.RenderTargetBitmap((int)(width * dpi / 96), 1100 * dpi / 96, dpi, dpi, WM.PixelFormats.Pbgra32);
                    bitmap.Render(card); var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"officer-merit-{width}-{dpi}.png")); encoder.Save(file);
                }
            }
            card.Follow(Updated(1240), standing);
            Check("untouched-merit-follows-game", merit.Value == 1240 && merit.Current == 1240 && card.Pending == 0);
            merit.Set(65535);
            card.Follow(Updated(1640), standing);
            var changed = card.Changes(out string error);
            Check("refresh-preserves-pending-merit-and-apply-request", merit.Value == 65535 && merit.Current == 1640
                && card.Pending == 1 && error.Length == 0 && changed is { Own.Count: 1 }
                && changed.Own[0] == new FieldChange(OfficerFields.Merit, 65535));
            card.Follow(Updated(65535), standing);
            Check("game-catching-up-clears-pending-merit", !merit.IsChanged && card.Pending == 0);
            Open();
            foreach (int value in new[] { 0, 32768, 65535 })
            {
                merit.Set(value); changed = card.Changes(out error);
                Check($"accepts-merit-{value}", error.Length == 0 && changed is { Own.Count: 1 }
                    && changed.Own[0] == new FieldChange(OfficerFields.Merit, value));
            }
            foreach (string invalid in new[] { "-1", "65536", "invalid", "" })
            {
                merit.Box.Text = invalid;
                Check("rejects-invalid-merit-" + invalid, !merit.IsValid && card.Changes(out error) is null
                    && error == OfficerFields.Merit.RangeMessage);
            }
            merit.Set(1234);
            Check("restoring-merit-clears-pending-edit", card.Pending == 0 && card.Changes(out _) is { Own.Count: 0 });
            merit.Set(5000); Open(13);
            Check("opening-another-officer-resets-merit", merit.Value == 500 && merit.Current == 500 && card.Pending == 0);
        }
        finally { host.Close(); }
    }
}
