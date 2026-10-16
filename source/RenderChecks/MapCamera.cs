using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Explicit live check: replay one map right click through the product wiring.
    // Calls the game's camera function; preserves the existing editor and its pending values.
    private static void CheckMapCameraLive(string output, int tile)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tile);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tile, GameMemory.TileCount);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Camera evidence directory already exists");
        Directory.CreateDirectory(output);
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
        static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
        static void Call(MapSurface owner, string name, params object[] args) => typeof(MapSurface).GetMethod(name, Hidden)!.Invoke(owner, args);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        var window = OfflineEditor(() => true, Path.Combine(output, "season-storage"), Path.Combine(output, "ai-storage"));
        var map = Field<MapView>(window, "map");
        var surface = Field<MapSurface>(map, "surface");
        try
        {
            GameIdentity identity = GameMemory.FindGame();
            GameLayout layout = GameMemory.Resolve(identity, null, CancellationToken.None);
            GameSnapshot data = GameMemory.ReadSnapshot(identity, layout);
            Set(window, "game", identity); Set(window, "snapshot", data);
            map.SetSnapshot(data);
            map.Measure(new W.Size(1280, 800)); map.Arrange(new W.Rect(0, 0, 1280, 800));
            var world = MapTerrain.TileCenter(tile);
            Set(surface, "zoom", 4.0); Set(surface, "center", new W.Point(world.X, world.Y));
            Call(surface, "ApplyView");
            int? requested = null;
            map.TileRightClicked += value => requested = value;
            var screen = new W.Point(surface.ActualWidth / 2, surface.ActualHeight / 2);
            Call(surface, "RightClickAt", screen);
            var watch = Stopwatch.StartNew();
            while (Field<bool>(window, "busy"))
            {
                if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Map camera call did not complete");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
            string status = Field<WC.TextBlock>(window, "status").Text;
            bool passed = requested == tile && status == L.Card.WentTo(L.Map.ClickedPosition);
            string json = JsonSerializer.Serialize(new { identity.ProcessId, tile, requested, screen.X, screen.Y, status,
                coordinateReplay = true, gameCameraCalled = passed, cameraPositionReadbackRequired = true, passed }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "result.json"), json); Console.WriteLine(json);
            Require(passed, "Product map click did not complete its game camera call");
        }
        finally
        {
            Call(surface, "StopLoop");
            WM.CompositionTarget.Rendering -= typeof(MapSurface).GetMethod("OnGpuFrame", Hidden)!.CreateDelegate<EventHandler>(surface);
            Field<GpuMap?>(surface, "gpu")?.Dispose();
            window.Close(); SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
