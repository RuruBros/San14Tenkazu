using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WT = System.Windows.Threading;

internal static class MapReloadChecks
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly List<object> Results = [];
    static string output = "";
    static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;
    static W.Window Host(object content) => new() { Content = content, Width = 420, Height = 300,
        ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
    static void Pump(int ms = 50)
    {
        var frame = new WT.DispatcherFrame();
        var timer = new WT.DispatcherTimer(WT.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); WT.Dispatcher.PushFrame(frame);
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void Run(string directory)
    {
        output = directory;
        Directory.CreateDirectory(output);
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        Environment.SetEnvironmentVariable("SAN14_NO_GPU", "1");
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", null);
        CheckActualSubscription();
        CheckTileGenerations();
        CheckMapReload();
        CheckRenderLogOrdering();
        CheckGpuReload();
        string json = JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "results.json"), json);
        Console.WriteLine(json);
        app.Shutdown();
    }
    static void CheckActualSubscription()
    {
        Action? changed = null;
        int refreshed = 0;
        var border = new WC.Border();
        VisualEventSubscription.Attach(border, callback => changed += callback, callback => changed -= callback, () => refreshed++);
        var window = Host(border);
        try
        {
            window.Show(); Pump();
            Require(border.IsLoaded && refreshed == 1 && changed?.GetInvocationList().Length == 1, "Real Loaded subscription failed");
            Task.Run(() => { for (int i = 0; i < 100; i++) changed?.Invoke(); }).Wait();
            window.Content = null; Pump();
            Require(!border.IsLoaded && changed is null, "Real Unloaded retained a subscription");
            int unloadedCount = refreshed;
            window.Content = border; Pump();
            Require(border.IsLoaded && refreshed == unloadedCount + 1 && changed?.GetInvocationList().Length == 1, "Real reload failed");
            Results.Add(new { Case = "Real WPF load/unload/reload subscription", Passed = true, Refreshed = refreshed });
        }
        finally { window.Close(); Pump(); }
    }
    static void CheckTileGenerations()
    {
        using var release = new ManualResetEventSlim(false);
        int calls = 0;
        WMI.BitmapSource Image(byte red)
        {
            var made = WMI.BitmapSource.Create(1, 1, 96, 96, WM.PixelFormats.Bgra32, null, new byte[] { 0, 0, red, 255 }, 4);
            made.Freeze(); return made;
        }
        var old = Image(20); var current = Image(200);
        var layer = new MapTiles((_, _, _, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call <= 2) { release.Wait(TimeSpan.FromSeconds(10)); return old; }
            return current;
        }, firstLevel: 1);
        layer.Update(new W.Rect(40, 40, 5, 5), 8);
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, 5000), "Old workers did not start");
        for (int i = 0; i < 20; i++) { layer.Enabled = false; layer.Enabled = true; layer.Invalidate(); }
        release.Set();
        var watch = Stopwatch.StartNew();
        while ((layer.Pending != 0 || Get<int>(layer, "running") != 0) && watch.ElapsedMilliseconds < 10000) Pump(10);
        var tiles = (IDictionary)typeof(MapTiles).GetField("tiles", Hidden)!.GetValue(layer)!;
        int stale = 0;
        foreach (object tile in tiles.Values) if (ReferenceEquals(Get<WMI.BitmapSource?>(tile, "Image"), old)) stale++;
        Require(layer.Pending == 0 && Get<int>(layer, "running") == 0 && stale == 0, "Stale completion damaged current generation");
        Results.Add(new { Case = "20 tile disable/re-enable/invalidate cycles with late workers", Passed = true, Calls = calls, StaleImages = stale, layer.Pending });
        layer.Enabled = false;
    }
    static void CheckMapReload()
    {
        string path = Path.GetFullPath(Path.Combine(output, "reload-render-" + Guid.NewGuid().ToString("N") + ".csv"));
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", path);
        var map = new MapSurface();
        var window = Host(map);
        try
        {
            window.Show(); Pump();
            var made = new TestGame().Force(1, 1, 1).Officer(1, "test", 1, 1, 4).Leading(1).Build();
            UnitRecord original = made.Units[0];
            int tile = MapTerrain.Columns * (MapTerrain.Rows / 2) + MapTerrain.Columns / 2;
            GameSnapshot Snapshot(int at)
            {
                var unit = original with { Data = (byte[])original.Data.Clone() };
                BinaryPrimitives.WriteUInt16LittleEndian(unit.Data.AsSpan(UnitFields.TileOffset), (ushort)at);
                return new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
                    CastleByCity = made.CastleByCity, TileOwners = made.TileOwners, TileCounties = made.TileCounties,
                    Forces = made.Forces, CorpsForce = made.CorpsForce, OfficerById = made.OfficerById, Units = [unit] };
            }
            map.SetSnapshot(Snapshot(tile)); Pump(800);
            map.SetSnapshot(Snapshot(tile + 1));
            bool before = Get<bool>(map, "gliding");
            var first = Get<MapRenderLog>(map, "renderLog");
            first.TryWrite("sample,before-unload");
            window.Content = null; Pump(10);
            Require(first.Completion.Wait(TimeSpan.FromSeconds(5)), "First render log did not close");
            string beforeText = File.ReadAllText(path);
            window.Content = map; Pump(10);
            bool after = Get<bool>(map, "gliding");
            double now = (double)typeof(MapSurface).GetProperty("Now", Hidden)!.GetValue(map)!;
            bool remaining = (bool)typeof(MapSurface).GetMethod("AnyGliding", Hidden)!.Invoke(map, [now])!;
            bool loadedAgain = map.IsLoaded;
            W.Point? TokenPoint()
            {
                var tokens = Get<IList>(map, "unitTokens");
                return tokens.Count == 0 ? null : (W.Point)tokens[0]!.GetType().GetField("Item2")!.GetValue(tokens[0])!;
            }
            Pump(60); W.Point? displayedBefore = TokenPoint();
            Pump(180); W.Point? displayedAfter = TokenPoint();
            map.RefreshFaces(); Pump(60); W.Point? displayedAfterRedraw = TokenPoint();
            Require(before && after && remaining && loadedAgain, "Reload failed to resume the unfinished move");
            Require(displayedBefore is { } p && displayedAfter is { } q && (q - p).Length > 0,
                "The displayed unit stayed frozen after reload until an explicit redraw");
            var second = Get<MapRenderLog>(map, "renderLog");
            second.TryWrite("sample,after-reload");
            window.Content = null; Pump(10);
            Require(second.Completion.Wait(TimeSpan.FromSeconds(5)), "Second render log did not close");
            string afterText = File.ReadAllText(path);
            Require(beforeText.Contains("sample,before-unload") && afterText.Contains("sample,before-unload")
                && afterText.Contains("sample,after-reload"), "Reload discarded render log samples");
            Require(afterText.Split('\n').Count(line => line.StartsWith("event,utc,")) == 1, "Reload duplicated the CSV header");
            Results.Add(new { Case = "Real map reload while units are moving", BeforeGliding = before,
                AfterReloadGliding = after, MovementStillInProgress = remaining, IsLoaded = loadedAgain,
                DisplayedBefore = displayedBefore, DisplayedAfter180ms = displayedAfter, DisplayedAfterExplicitRedraw = displayedAfterRedraw });
            Results.Add(new { Case = "Explicit render log path across reload", BeforeMarkerWasWritten = beforeText.Contains("sample,before-unload"),
                BeforeMarkerRetainedAfterReload = afterText.Contains("sample,before-unload"), AfterMarkerWritten = afterText.Contains("sample,after-reload") });
            // A move that finishes while unloaded must arrive, without starting a new animation.
            Pump(1000); window.Content = map; Pump(100);
            W.Point destination = (W.Point)typeof(MapSurface).GetMethod("TilePoint", Hidden)!.Invoke(map, [tile + 1])!;
            Require(!Get<bool>(map, "gliding") && TokenPoint() is { } arrived && (arrived - destination).Length < 1e-9,
                "Reload restarted an expired move or left it short of its destination");
            Results.Add(new { Case = "Reload after the move finished while unloaded", Passed = true });
        }
        finally { window.Close(); Pump(); Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", null); }
    }
    private sealed class PausedFileWriter(TextWriter inner) : TextWriter
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed;
        public override System.Text.Encoding Encoding => inner.Encoding;
        public override void WriteLine(string? value) => inner.WriteLine(value);
        public override async Task WriteLineAsync(string? value) { await Release.Task; await inner.WriteLineAsync(value); }
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); Closed = true; } base.Dispose(disposing); }
    }
    static void CheckRenderLogOrdering()
    {
        string path = Path.GetFullPath(Path.Combine(output, "ordered-render-" + Guid.NewGuid().ToString("N") + ".csv"));
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", path);
        var writer = new PausedFileWriter(new StreamWriter(path) { AutoFlush = true });
        using var first = new MapRenderLog(writer);
        MapRenderLog? second = null;
        try
        {
            Require(first.TryWrite("sample,before-unload"), "First lifetime rejected its sample");
            first.Dispose();
            second = MapRenderLog.Open(first.Completion) ?? throw new InvalidOperationException("Reopen returned no render log");
            Require(second.TryWrite("sample,after-reload"), "New lifetime could not queue a sample while the old writer was blocked");
            second.Dispose();
            Require(!second.Completion.IsCompleted, "New writer did not wait for the old writer to close");
            writer.Release.SetResult();
            Require(second.Completion.Wait(TimeSpan.FromSeconds(5)), "Queued render log lifetimes did not drain");
            string[] lines = File.ReadAllLines(path);
            Require(writer.Closed && lines.Count(line => line.StartsWith("event,utc,")) == 1
                && lines.Where(line => line.StartsWith("sample,")).SequenceEqual(["sample,before-unload", "sample,after-reload"]),
                "Rapid reload lost/reordered accepted samples or duplicated the CSV header");
            Results.Add(new { Case = "Rapid render log reopen with the prior file writer blocked", Passed = true, writer.Closed });
        }
        finally { writer.Release.TrySetResult(); second?.Dispose(); Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", null); }
    }
    static void CheckGpuReload()
    {
        Environment.SetEnvironmentVariable("SAN14_NO_GPU", null);
        var map = new MapSurface();
        var window = Host(map);
        try
        {
            window.Show(); Pump(300);
            var first = Get<GpuMap>(map, "gpu");
            var initialWait = Stopwatch.StartNew();
            while (first.PresentedFrames == 0 && initialWait.ElapsedMilliseconds < 3000) Pump(20);
            Require(first is not null && first.PresentedFrames > 0, "First GPU frame failed");
            int frames = first!.PresentedFrames;
            window.Content = null; Pump(30);
            Require(Get<GpuMap?>(map, "gpu") is null && Get<bool>(first, "disposed"), "Old GPU not disposed");
            window.Content = map; Pump(300);
            var second = Get<GpuMap>(map, "gpu");
            var wait = Stopwatch.StartNew();
            while (second.PresentedFrames == 0 && wait.ElapsedMilliseconds < 3000) Pump(20);
            Require(!ReferenceEquals(first, second) && second.PresentedFrames > 0 && !second.Lost, "Reload did not restore GPU rendering");
            Results.Add(new { Case = "Real GPU map load/unload/reload", Passed = !ReferenceEquals(first, second) && second.PresentedFrames > 0 && !second.Lost,
                BeforeFrames = frames, AfterFrames = second.PresentedFrames, second.SubmittedFrames, second.Lost, second.Pending, second.RenderError,
                map.IsLoaded, map.IsVisible, map.ActualWidth, map.ActualHeight,
                Dirty = Get<bool>(map, "gpuDirty"), Hooked = Get<bool>(map, "gpuHooked"), LastFrame = Get<double>(map, "lastGpuFrame") });
        }
        finally { window.Close(); Pump(); Environment.SetEnvironmentVariable("SAN14_NO_GPU", "1"); }
    }
}
