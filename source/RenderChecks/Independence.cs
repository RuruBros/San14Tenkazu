using System.Reflection;
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
    // Native WPF layout/state checks on fixtures; no game writes or browser.
    private static void CheckIndependence(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int count = 0;
        void Record(string scenario, bool passed)
        {
            log.WriteLine(JsonSerializer.Serialize(new { scenario, passed }));
            Require(passed, scenario); count++;
        }
        var fixture = new TestGame().Force(3, 557, 3).Force(44, 0).Force(45, 0)
            .City(28, 3, 997).City(29, 3, 557).Officer(997, "염유", 3, 28, 3).Officer(557, "원소", 3, 29, 1);
        CityEditorCard.View View(GameSnapshot snapshot) => (CityEditorCard.View)typeof(EditorWindow)
            .GetMethod("CityView", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [snapshot, 28])!;
        var card = new CityEditorCard { Visibility = W.Visibility.Visible };
        var button = (PlateButton)typeof(CityEditorCard).GetField("independence", Hidden)!.GetValue(card)!;
        CityEditorCard.View eligible = View(fixture.Build());
        card.Open(eligible);
        Record("resident-governor-enabled", button.IsEnabled && button.Visibility == W.Visibility.Visible && card.Pending == 0);
        Record("disabled-reason-tooltip-supported", WC.ToolTipService.GetShowOnDisabled(button));
        card.FillsEnabled = false;
        Record("busy-disables-native-action", !button.IsEnabled);
        card.Follow(eligible);
        Record("live-refresh-keeps-busy-disabled", !button.IsEnabled);
        card.FillsEnabled = true;
        Record("idle-restores-eligible-action", button.IsEnabled);
        var blocked = eligible with { IndependenceBlock = L.Independence.OutsideCity };
        card.Follow(blocked);
        Record("live-refresh-disables-away-governor", !button.IsEnabled && Equals(button.ToolTip, L.Independence.OutsideCity));
        card.FillsEnabled = false; card.FillsEnabled = true;
        Record("idle-keeps-ineligible-action-disabled", !button.IsEnabled);
        card.Follow(eligible);
        Record("live-refresh-reenables-returned-governor", button.IsEnabled);
        card.Open(eligible with { Force = 0, IndependenceBlock = L.Independence.NoForce });
        Record("unowned-city-hides-independence", button.Visibility == W.Visibility.Collapsed);
        card.Open(View(fixture.City(28, 3, 557).Build()));
        Record("ruler-is-blocked-in-product-view", !button.IsEnabled && Equals(button.ToolTip, L.Independence.AlreadyRuler));
        fixture.City(28, 3, 997).Force(44, 999).Force(45, 999).Force(9, 0);
        GameSnapshot explicitSlot = fixture.Build();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(explicitSlot.ForceRecords[9].Data.AsSpan(0x11E), 234);
        card.Open(View(explicitSlot));
        Record("empty-ordinary-slot-enables-independence", button.IsEnabled && CityIndependence.Prepare(explicitSlot, 28).RequestedSlot == 9);
        fixture.Force(9, 999);
        card.Follow(View(fixture.Build()));
        Record("no-empty-ordinary-slot-disables-independence", !button.IsEnabled && Equals(button.ToolTip, L.Independence.NoSlot));
        card.Open(eligible);
        foreach (int width in new[] { 306, 360, 480 })
        {
            card.Measure(new W.Size(width, 800)); card.Arrange(new W.Rect(0, 0, width, 800)); card.UpdateLayout();
            W.Point at = button.TranslatePoint(new W.Point(), card);
            Record("independence-fits-card-" + width, button.ActualWidth > 0 && button.ActualHeight > 0
                && at.X >= 0 && at.X + button.ActualWidth <= width && at.Y + button.ActualHeight <= 800);
            var bitmap = new WMI.RenderTargetBitmap(width, 800, 96, 96, WM.PixelFormats.Pbgra32); bitmap.Render(card);
            var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(output, $"city-{width}.png")); encoder.Save(image);
        }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { checks = count, passed = true, gameAccess = false }));
        Console.WriteLine($"City independence WPF checks passed: {count}");
    }
}
