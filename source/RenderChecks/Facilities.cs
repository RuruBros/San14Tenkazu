using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    // Real map drawing/hit dispatch and editor controls, without a visible window or live game writes.
    private static void CheckFacilities(string? output)
    {
        string path = Path.GetFullPath(output ?? "source/RenderChecks/artifacts/facilities.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var log = new StreamWriter(path);
        void Record(string scenario, object details, bool passed)
        {
            string row = JsonSerializer.Serialize(new { scenario, passed, details });
            log.WriteLine(row);
            log.Flush();
            Console.WriteLine(row);
            Require(passed, $"Facility check failed: {scenario}");
        }
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(obj, args);

        var fixture = new TestGame().Force(4, 35, 4).Officer(35, "Ruler", 4, 6, 1);
        for (int kind = 3; kind <= 17; kind++) fixture.Facility(70 + kind, kind, 22080 + kind * 5, 4, 120, built: kind % 2 == 0);
        GameSnapshot made = fixture.Build();
        var data = new GameSnapshot
        {
            Cities = made.Cities, Counties = [], Officers = made.Officers, CastleByCity = [],
            TileOwners = new byte[GameMemory.TileCount], TileCounties = new ushort[GameMemory.TileCount],
            Forces = made.Forces, ForceRecords = made.ForceRecords, CorpsForce = made.CorpsForce,
            OfficerById = made.OfficerById, Units = [], Facilities = made.Facilities
        };
        var window = new EditorWindow();
        Set(window, "snapshot", data);
        var map = Field<MapView>(window, "map");
        map.SetSnapshot(data);
        var surface = Field<MapSurface>(map, "surface");
        FacilityEditorCard card = map.FacilityEditor;
        var box = Field<NumberField>(card, "durability");
        try
        {
            foreach (Facility facility in data.Facilities)
            {
                map.Measure(new W.Size(1500, 900));
                map.Arrange(new W.Rect(0, 0, 1500, 900));
                System.Drawing.PointF world = MapTerrain.TileCenter(facility.Tile);
                Set(surface, "zoom", 5.0);
                Set(surface, "center", new W.Point(world.X, world.Y));
                Call(surface, "ApplyView");
                var drawing = new WM.DrawingVisual();
                using (WM.DrawingContext dc = drawing.RenderOpen()) Call(surface, "DrawCities", new WpfCanvas(dc));
                FacilityTarget target = FacilityTarget.Of(facility);
                var spots = Field<List<(W.Rect Area, string Text, int Gate, FacilityTarget? Facility)>>(surface, "spots");
                var spot = spots.Single(s => s.Facility == target);
                W.Point point = spot.Area.TopLeft + new W.Vector(spot.Area.Width / 2, spot.Area.Height / 2);
                object hit = Call(surface, "HitTest", point)!;
                Record("facility-map-hit", new { facility.Kind, facility.Index, facility.Built },
                    Equals(hit.GetType().GetProperty("Facility")!.GetValue(hit), target));
                Call(surface, "ClickAt", point);
                Record("facility-click-opens-card", new { facility.Kind, card.DisplayName, box.Current },
                    card.Shown && card.Identity == target && box.Current == 120 && map.SelectedFacility == target);

                EditPlan plan = FacilityEdits.Plan(data, target.Index, target.Kind, target.Tile, target.Force, 60);
                GameSnapshot damaged = data.With(plan.Edits);
                Set(window, "snapshot", damaged);
                Call(window, "RefreshEditor");
                Record("unedited-durability-follows-game", new { facility.Kind, box.Value }, box.Value == 60 && card.Pending == 0);
                box.Set(300);
                Set(window, "snapshot", data);
                Call(window, "RefreshEditor");
                Record("typed-durability-survives-refresh", new { facility.Kind, box.Value, box.Current, card.Pending },
                    box.Value == 300 && box.Current == 120 && card.Pending == 1 && card.Changes(out _) == 300);
                box.Box.Text = "0";
                Record("zero-input-rejected", new { facility.Kind }, card.Changes(out string error) is null && error.Length > 0);
                card.Open(new FacilityEditorCard.View(facility, data.ForceName(facility.Force), MapView.ForceColor(facility.Force)));
            }
            box.Set(300);
            Set(window, "snapshot", new TestGame().Build());
            Call(window, "RefreshEditor");
            Record("gone-facility-closes-card", new { card.Pending }, !card.Shown && map.SelectedFacility is null);
            Record("gone-facility-history-skipped", new { card.Identity }, !(bool)Call(window, "Show", card.Identity)!);
        }
        finally
        {
            Call(surface, "StopLoop");
            Field<GpuMap?>(surface, "gpu")?.Dispose();
        }
    }
}
