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
    // Fixture-only WPF checks: no live game access or visible windows.
    private static void CheckUnitConditions(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        void Check(string scenario, bool passed)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line); Require(passed, scenario);
        }
        static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject root)
        {
            for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = WM.VisualTreeHelper.GetChild(root, i); yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        var data = new TestGame().Force(1, 3002, 1).Officer(3002, "묵연", 1, 1, 4).Leading(3002)
            .Personality(53, "화신", "", (25, 0)).Build();
        var unit = data.Units.Single() with { Terrain = new(data.Units.Single().Tile, 7, 0) };
        data.Units[0] = unit; unit.Data[0x26] = 74;
        foreach (var ailment in UnitFields.Ailments) ailment.Encode(20).CopyTo(unit.Data, ailment.Offset);
        var window = new EditorWindow();
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, data);
        typeof(EditorWindow).GetMethod("Show", Hidden)!.Invoke(window, [new UnitTarget(unit.Slot, unit.Commander)]);
        var card = (UnitEditorCard)typeof(EditorWindow).GetProperty("OpenEditor", Hidden)!.GetValue(window)!;
        UnitEditorCard.View View() => (UnitEditorCard.View)typeof(EditorWindow).GetMethod("UnitView", Hidden)!.Invoke(window, [data, data.Units[0]])!;
        var panel = (WC.StackPanel)typeof(UnitEditorCard).GetField("conditions", Hidden)!.GetValue(card)!;
        var fields = (Dictionary<FieldDef, NumberField>)typeof(UnitEditorCard).GetField("fields", Hidden)!.GetValue(card)!;
        string[] Values() => panel.Children.Cast<WC.DockPanel>().Select(line =>
            ((WC.WrapPanel)line.Children[1]).Children.OfType<WC.TextBlock>().Single().Text).ToArray();
        try
        {
            typeof(EditorCard).GetMethod("ShowPage", Hidden)!.Invoke(card, [1]);
            Check("poison-shown-with-connected-supply", Values().SequenceEqual([L.Unit.PoisonDamage, L.Terms.None, L.Unit.SupplyConnected]));
            foreach (double width in new[] { 500.0, 600.0 })
            {
                card.Measure(new W.Size(width, 900)); card.Arrange(new W.Rect(0, 0, width, 900)); card.UpdateLayout();
                Check($"read-only-status-fits-{width}", panel.ActualWidth > 0 && !Descendants(panel).OfType<WC.TextBox>().Any()
                    && Descendants(panel).OfType<WC.TextBlock>().All(text => text.ActualWidth > 0
                        && text.TranslatePoint(new W.Point(), card).X + text.ActualWidth <= width));
                var bitmap = new WMI.RenderTargetBitmap((int)width, 900, 96, 96, WM.PixelFormats.Pbgra32);
                bitmap.Render(card); var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"unit-status-{width}.png")); encoder.Save(file);
            }
            fields[UnitFields.Troops].Set(777);
            UnitFields.Troops.Encode(250).CopyTo(unit.Data, UnitFields.Troops.Offset);
            data.Units[0] = unit with { Terrain = new(unit.Tile, 2, 100) }; card.Follow(View());
            Check("terrain-and-fire-refresh", Values()[0] == L.Terms.None && Values()[1] == L.Unit.FireDamage);
            Check("refresh-preserves-pending-troops", fields[UnitFields.Troops].Value == 777 && fields[UnitFields.Troops].Current == 250 && card.Pending == 1);
            OfficerFields.Personalities[0].Encode(53).CopyTo(data.Officers[0].Data, OfficerFields.Personalities[0].Offset);
            data.Personalities.Single().Data[0xBF] = 16; card.Follow(View());
            Check("fire-immunity-refresh", Values()[1] == L.Unit.FireImmune);
            unit.Data[0x26] = 0; card.Follow(View());
            Check("supply-cut-refresh", Values()[2] == L.Unit.SupplyCut);
            var clear = Descendants(card).OfType<PlateButton>().Single(button => button.Text == L.Unit.ClearAilments);
            ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(clear)!)();
            var changes = card.Changes(out _)!;
            Check("clear-only-three-ailments", changes.Count == 4 && UnitFields.Ailments.All(a => changes.Any(c => c.Field == a && c.Value == 0)));
            Check("clear-does-not-hide-environment", Values()[1] == L.Unit.FireImmune && Values()[2] == L.Unit.SupplyCut
                && data.Units[0].Terrain is { Fire: 100 } && unit.Data[0x26] == 0);
            data.Units[0] = unit with { Terrain = new(unit.Tile, 2, 0) }; card.Follow(View());
            Check("extinguished-fire-refresh", Values()[1] == L.Terms.None);
            data.Units[0] = unit with { Terrain = null }; card.Follow(View());
            Check("unknown-terrain-is-explicit", Values()[0] == L.Unit.ConditionUnknown && Values()[1] == L.Unit.ConditionUnknown);
        }
        finally { card.Open(View()); window.Close(); }
    }

    private static void ReadUnitConditionsGame(string output)
    {
        if (File.Exists(output)) throw new IOException("Unit evidence already exists");
        GameIdentity game = GameMemory.FindGame();
        var layout = GameMemory.ReadCurrentLayout(game);
        var first = GameMemory.ReadSnapshot(game, layout);
        var second = GameMemory.ReadSnapshot(game, layout, before: first);
        var records = second.Units.Select(unit => new { unit.Slot, unit.Commander, name = second.CommanderName(unit.Commander),
            unit.Tile, unit.Terrain, flags = unit.Data[0x26], troops = unit[UnitFields.Troops], wounded = unit[UnitFields.Wounded],
            ailments = UnitFields.Ailments.Select(a => unit[a]).ToArray(), conditions = UnitConditions.Read(second, unit),
            detox = UnitConditions.HasPersonality(second, second.OfficerById.GetValueOrDefault(unit.Commander), UnitConditions.DetoxPersonality),
            fireproof = UnitConditions.HasKind(second, second.OfficerById.GetValueOrDefault(unit.Commander), UnitConditions.FireImmunityKind) }).ToArray();
        var result = new { utc = DateTime.UtcNow, machine = Environment.MachineName, game.ProcessId, game.Verified,
            pace = second.Pace.ToString(), date = second.Date is { } date ? new { date.Year, date.Month, date.Day } : null, second.Hour,
            stable = first.Units.Length == second.Units.Length && first.Units.All(unit => second.Unit(unit.Slot, unit.Commander) is { } next
                && unit.Terrain == next.Terrain && unit.Data.AsSpan().SequenceEqual(next.Data)), records, writes = 0 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { game.ProcessId, game.Verified, count = records.Length, result.stable, result.pace, result.date, writes = 0 }));
    }
}
