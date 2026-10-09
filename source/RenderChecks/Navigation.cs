using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    // Replay the same coordinate handlers as WPF input, without moving the user's pointer or opening a window.
    private static void CheckNavigation(string? output = null)
    {
        string path = Path.GetFullPath(output ?? "source/RenderChecks/artifacts/navigation.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var log = new StreamWriter(path, false);
        int failures = 0;
        void Record(string scenario, object details, bool passed)
        {
            string row = JsonSerializer.Serialize(new { scenario, passed, details });
            Console.WriteLine(row);
            log.WriteLine(row);
            if (!passed) failures++;
        }
        static T Field<T>(MapSurface map, string name) => (T)typeof(MapSurface).GetField(name, Hidden)!.GetValue(map)!;
        static void Set(MapSurface map, string name, object value) => typeof(MapSurface).GetField(name, Hidden)!.SetValue(map, value);
        static object? Call(MapSurface map, string name, params object?[] args) => typeof(MapSurface).GetMethod(name, Hidden)!.Invoke(map, args);
        static W.Point World(MapSurface map, W.Point screen) => (W.Point)Call(map, "ToWorld", screen)!;
        static W.Point Screen(MapSurface map, W.Point world) => (W.Point)Call(map, "ToScreen", world)!;
        var map = new MapSurface { Width = 1280, Height = 800 };
        map.Measure(new W.Size(1280, 800));
        map.Arrange(new W.Rect(0, 0, 1280, 800));
        try
        {
            int wrongCenters = 0;
            for (int tile = 0; tile < GameMemory.TileCount; tile++)
            {
                var p = MapTerrain.TileCenter(tile);
                if (MapTerrain.TileAt(p.X, p.Y) != tile) wrongCenters++;
            }
            Record("right-click-all-hex-centres", new { count = GameMemory.TileCount, wrongCenters }, wrongCenters == 0);
            int edgeTile = 100 * MapTerrain.Columns + 100;
            var edgeCenter = MapTerrain.TileCenter(edgeTile);
            int? slopedEdge = MapTerrain.TileAt(edgeCenter.X + .8, edgeCenter.Y + .4);
            Record("right-click-sloped-hex-edge", new { tile = slopedEdge },
                slopedEdge == MapTerrain.Neighbour(100, 100, 2));
            int rejected = new[] { (-1.0, 100.0), (100.0, -1.0), ((double)MapTerrain.WorldWidth + 1, 100.0),
                (100.0, (double)MapTerrain.WorldHeight + 1), (double.NaN, 100.0), (100.0, double.PositiveInfinity),
                (1.0, 0.0), ((double)MapTerrain.WorldWidth, (double)MapTerrain.WorldHeight) }
                .Count(p => MapTerrain.TileAt(p.Item1, p.Item2) is null);
            Record("right-click-outside-hex-grid", new { rejected }, rejected == 8);
            int? requestedTile = null; int requests = 0;
            map.TileRightClicked += tile => { requestedTile = tile; requests++; };
            foreach (double width in new[] { 1280.0, 650.0 })
            foreach (double clickZoom in new[] { 1.0, 4.0, 16.0 })
            {
                map.Width = width;
                map.Measure(new W.Size(width, 800));
                map.Arrange(new W.Rect(0, 0, width, 800));
                Set(map, "zoom", clickZoom);
                Set(map, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                Call(map, "ApplyView");
                int expected = 110 * MapTerrain.Columns + 110;
                var p = MapTerrain.TileCenter(expected);
                Call(map, "RightClickAt", Screen(map, new W.Point(p.X, p.Y)));
                Record("right-click-zoom-and-pane", new { width, clickZoom, requestedTile }, requestedTile == expected);
            }
            Set(map, "zoom", 4.0);
            Set(map, "center", new W.Point(edgeCenter.X, edgeCenter.Y));
            Call(map, "ApplyView");
            Set(map, "presentedTransform", Field<WM.MatrixTransform>(map, "worldTransform").Matrix);
            Set(map, "presentedView", (4.0, false, new W.Point(edgeCenter.X, edgeCenter.Y), 0.0));
            Set(map, "zoom", 16.0);
            Set(map, "center", new W.Point(edgeCenter.X + 15, edgeCenter.Y + 15));
            Call(map, "ApplyView");
            Call(map, "RightClickAt", new W.Point(map.ActualWidth / 2, map.ActualHeight / 2));
            Record("right-click-presented-gpu-frame", new { requestedTile, gpu = map.UsesGpu }, requestedTile == edgeTile);
            Set(map, "presentedView", default((double, bool, W.Point, double)));
            int beforeIgnored = requests;
            Call(map, "RightClickAt", new W.Point(-1, 200));
            Call(map, "RightClickAt", new W.Point(map.ActualWidth + 1, 200));
            Call(map, "BeginDrag", new W.Point(300, 400));
            Call(map, "RightClickAt", new W.Point(300, 400));
            var rightUp = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Right) { RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent };
            Call(map, "OnMouseUp", rightUp);
            Record("right-click-does-not-interrupt-left-drag", new { requests, rightUp.Handled },
                requests == beforeIgnored && Field<W.Point?>(map, "dragStart") is not null && rightUp.Handled);
            Call(map, "EndDrag");
            foreach (double width in new[] { 1280.0, 650.0 })
            {
                map.Width = width;
                map.Measure(new W.Size(width, 800));
                map.Arrange(new W.Rect(0, 0, width, 800));
                W.Point pointer = new(width * 0.35, 450);
                foreach (W.Vector delta in new W.Vector[] { new(48, 0), new(-48, 0), new(0, 48), new(0, -48), new(30, -24) })
                {
                    Set(map, "zoom", 4.0);
                    Set(map, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                    Call(map, "ApplyView");
                    W.Point held = World(map, pointer);
                    Call(map, "BeginDrag", pointer);
                    Call(map, "MoveDrag", pointer + delta);
                    double error = (Screen(map, held) - (pointer + delta)).Length;
                    Record("settled-zoom-drag", new { width, dx = delta.X, dy = delta.Y, error }, error < 1e-6);
                    Call(map, "EndDrag");
                }

                Set(map, "zoom", 4.0);
                Set(map, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                Call(map, "ApplyView");
                Set(map, "anchorScreen", new W.Point(width * 0.75, 260));
                Set(map, "anchorWorld", World(map, new W.Point(width * 0.75, 260)));
                Set(map, "targetLogZoom", Math.Log(8));
                Set(map, "zooming", true);
                Call(map, "StartLoop");
                W.Point zoomHeld = World(map, pointer);
                Call(map, "BeginDrag", pointer);
                var frame = (WM.RenderingEventArgs)Activator.CreateInstance(typeof(WM.RenderingEventArgs), Hidden, null,
                    [TimeSpan.FromSeconds(1)], null)!;
                Call(map, "OnFrame", null, frame);
                W.Vector drag = new(30, 20);
                Call(map, "MoveDrag", pointer + drag);
                double zoomError = (Screen(map, zoomHeld) - (pointer + drag)).Length;
                Record("drag-during-zoom", new { width, zoom = Field<double>(map, "zoom"), error = zoomError }, zoomError < 1e-6);
                Call(map, "EndDrag");
                Call(map, "StopLoop");

                Set(map, "zoom", 4.0);
                Set(map, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                Call(map, "ApplyView");
                Call(map, "BeginDrag", pointer);
                // Overshoot each edge, then reverse by 20 DIPs. The map must follow the reversal immediately.
                foreach (W.Vector edge in new W.Vector[] { new(4000, 0), new(-4000, 0), new(0, 4000), new(0, -4000) })
                {
                    W.Point outside = pointer + edge;
                    Call(map, "MoveDrag", outside);
                    W.Point atEdge = World(map, outside);
                    W.Vector reverse = -edge / edge.Length * 20;
                    Call(map, "MoveDrag", outside + reverse);
                    W.Vector moved = Screen(map, atEdge) - outside;
                    Record("reverse-at-edge", new { width, edgeX = edge.X, edgeY = edge.Y, expectedX = reverse.X,
                        expectedY = reverse.Y, movedX = moved.X, movedY = moved.Y }, (moved - reverse).Length < 1e-6);
                    Call(map, "EndDrag");
                    Set(map, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                    Call(map, "ApplyView");
                    Call(map, "BeginDrag", pointer);
                }
                Call(map, "EndDrag");
            }
            foreach (var size in new[] { new W.Size(1280, 800), new W.Size(650, 800), new W.Size(1700, 520) })
            foreach (bool extended in new[] { false, true })
            foreach (double zoom in new[] { 1.0, 1.005, 1.1, 1.25, 2.0, 4.0, 16.0 })
            {
                W.Rect bounds = extended ? new(-12, -28, MapTerrain.WorldWidth + 40, MapTerrain.WorldHeight + 56)
                    : new(0, 0, MapTerrain.WorldWidth, MapTerrain.WorldHeight);
                Set(map, "panBounds", bounds);
                map.Width = size.Width;
                map.Height = size.Height;
                map.Measure(size);
                map.Arrange(new W.Rect(size));
                Set(map, "zoom", zoom);
                Set(map, "center", new W.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
                Call(map, "ApplyView");
                double scale = (double)typeof(MapSurface).GetProperty("Scale", Hidden)!.GetValue(map)!;
                W.Point pointer = new(size.Width / 2, size.Height / 2);
                W.Point middle = Field<W.Point>(map, "center");
                foreach (W.Vector delta in new W.Vector[] { new(100000, 0), new(-100000, 0), new(0, 100000), new(0, -100000) })
                {
                    Set(map, "center", middle);
                    Call(map, "ApplyView");
                    Call(map, "BeginDrag", pointer);
                    Call(map, "MoveDrag", pointer + delta);
                    double halfW = size.Width / (2 * scale), halfH = size.Height / (2 * scale);
                    double expectedX = bounds.Width <= halfW * 2 ? bounds.X + bounds.Width / 2
                        : delta.X > 0 ? bounds.Left + halfW : delta.X < 0 ? bounds.Right - halfW : middle.X;
                    double expectedY = bounds.Height <= halfH * 2 ? bounds.Y + bounds.Height / 2
                        : delta.Y > 0 ? bounds.Top + halfH : delta.Y < 0 ? bounds.Bottom - halfH : middle.Y;
                    W.Point reached = Field<W.Point>(map, "center");
                    double error = (reached - new W.Point(expectedX, expectedY)).Length * scale;
                    Record("full-pan-bounds", new { size.Width, size.Height, extended, zoom, dx = delta.X, dy = delta.Y,
                        expectedX, expectedY, reached.X, reached.Y, error }, error < 1e-6);
                    Call(map, "EndDrag");
                }
            }
        }
        finally
        {
            Call(map, "EndDrag");
            Call(map, "StopLoop");
            var handler = typeof(MapSurface).GetMethod("OnGpuFrame", Hidden)!.CreateDelegate<EventHandler>(map);
            WM.CompositionTarget.Rendering -= handler;
            Field<GpuMap?>(map, "gpu")?.Dispose();
        }
        Require(failures == 0, $"Navigation replay failed {failures} checks; see {path}");
    }
}
