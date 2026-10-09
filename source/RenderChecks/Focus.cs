using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Keep fixture data and camera fixed while real native window activation changes.
    // Compare complete GPU pixels, rather than treating nonzero D3DImage dimensions as proof of stability.
    private static void CheckFocus(string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", Path.GetFullPath(Path.Combine(output, "frames.csv")));
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        var map = new MapView();
        var surface = Field<MapSurface>(map, "surface");
        var gpu = Field<GpuMap?>(surface, "gpu") ?? throw new InvalidOperationException("GPU renderer unavailable");
        var host = new W.Window { Content = map, Width = 1280, Height = 800, Left = 20, Top = 20,
            UseLayoutRounding = true, ShowActivated = false, ShowInTaskbar = false, Title = "천하도 포커스 렌더링 검사" };
        var other = new W.Window { Content = new WC.TextBox { Text = "Focus test", Margin = new W.Thickness(12) },
            Width = 240, Height = 120, Left = 1330, Top = 20, ShowActivated = false, ShowInTaskbar = false,
            Title = "천하도 포커스 전환 검사" };
        var fixture = new TestGame().Force(1, 1, 1);
        for (int i = 1; i <= 180; i++) fixture.Officer(i, "장수" + i, 1, 1, 4).Leading(i);
        GameSnapshot made = fixture.Build();
        var data = new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
            CastleByCity = made.CastleByCity, TileOwners = new byte[GameMemory.TileCount], TileCounties = new ushort[GameMemory.TileCount],
            Forces = made.Forces, ForceRecords = made.ForceRecords, CorpsForce = made.CorpsForce, OfficerById = made.OfficerById, Units = made.Units };
        for (int i = 0; i < data.Units.Length; i++)
        {
            UnitRecord unit = data.Units[i];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(unit.Data.AsSpan(UnitFields.TileOffset), (ushort)(220 * (104 + i / 30 * 2) + 80 + (i % 30) * 3));
            UnitFields.Troops.Encode(3000 + i * 73).CopyTo(unit.Data, UnitFields.Troops.Offset);
        }
        map.SetSnapshot(data);
        using var events = new StreamWriter(Path.Combine(output, "events.jsonl")) { AutoFlush = true };
        var watch = Stopwatch.StartNew();
        string phase = "load";
        int activations = 0, deactivations = 0, compared = 0, changed = 0;
        void Record(string kind, object? details = null) => events.WriteLine(JsonSerializer.Serialize(new { kind,
            ms = watch.Elapsed.TotalMilliseconds, phase, active = host.IsActive, gpu.Pending, gpu.Lost,
            gpu.SubmittedFrames, gpu.PresentedFrames, available = gpu.Image.IsFrontBufferAvailable, details }));
        host.Activated += (_, _) => { activations++; Record("activated"); };
        host.Deactivated += (_, _) => { deactivations++; Record("deactivated"); };
        gpu.Image.IsFrontBufferAvailableChanged += (_, _) => Record("front-buffer");
        void Pump(double seconds, Action? action = null)
        {
            double end = watch.Elapsed.TotalSeconds + seconds;
            do
            {
                var frame = new WT.DispatcherFrame();
                surface.Dispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame);
                action?.Invoke();
                Thread.Sleep(2);
            } while (watch.Elapsed.TotalSeconds < end);
        }
        try
        {
            host.Show(); other.Show();
            Pump(10);
            Require(Field<WC.Image>(surface, "terrain").Source is not null, "Terrain did not load");
            foreach (double zoom in new[] { 4.0, 8.0, 16.0 })
            {
                phase = "warmup-" + zoom;
                Set(surface, "zoom", zoom);
                Set(surface, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                Call(surface, "ApplyView");
                Pump(4);
                while (gpu.Pending) Pump(.01);
                byte[] reference = ReadPixels(gpu, gpu.Image.PixelWidth, gpu.Image.PixelHeight);
                string referenceHash = Convert.ToHexString(SHA256.HashData(reference));
                int previous = gpu.PresentedFrames;
                void Compare()
                {
                    if (gpu.Pending || gpu.PresentedFrames == previous) return;
                    previous = gpu.PresentedFrames;
                    byte[] actual = ReadPixels(gpu, gpu.Image.PixelWidth, gpu.Image.PixelHeight);
                    int pixels = 0, maximum = 0;
                    for (int i = 0; i < actual.Length; i += 4)
                    {
                        int delta = 0;
                        for (int channel = 0; channel < 4; channel++) delta = Math.Max(delta, Math.Abs(actual[i + channel] - reference[i + channel]));
                        if (delta > 0) pixels++;
                        maximum = Math.Max(maximum, delta);
                    }
                    compared++;
                    if (pixels != 0) changed++;
                    Record("compare", new { zoom, pixels, maximum, referenceHash,
                        actualHash = Convert.ToHexString(SHA256.HashData(actual)), deferred = gpu.Canvas.Deferred,
                        labelsPending = Field<bool>(surface, "labelsPending"), imageLocks = typeof(System.Windows.Interop.D3DImage).GetField("_lockCount", Hidden)!.GetValue(gpu.Image) });
                }
                for (int trial = 0; trial < 4; trial++)
                {
                    phase = "active-" + zoom;
                    Record("activate-request", new { succeeded = host.Activate() });
                    map.SetSnapshot(data);
                    Pump(.5, Compare);
                    phase = "inactive-" + zoom;
                    Record("deactivate-request", new { succeeded = other.Activate() });
                    map.SetSnapshot(data);
                    Pump(.5, Compare);
                }
                // Reproduce the first refresh after a long background pause and cache expiry.
                phase = "background-idle-" + zoom;
                Pump(31);
                phase = "background-refresh-" + zoom;
                map.SetSnapshot(data);
                Pump(1, Compare);
            }
            var summary = new { activations, deactivations, compared, changed, gpu.Lost,
                gpu.BackBufferBindings, seconds = watch.Elapsed.TotalSeconds };
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(summary));
            Require(activations >= 12 && deactivations >= 12, "Native focus transitions were not exercised");
            Require(compared >= 24, "Focus test did not observe enough completed refresh frames");
            Require(changed == 0 && !gpu.Lost, "Static zoomed map pixels changed during focus/refresh; see events.jsonl");
        }
        finally
        {
            Call(surface, "EndDrag"); Call(surface, "StopLoop");
            var handler = typeof(MapSurface).GetMethod("OnGpuFrame", Hidden)!.CreateDelegate<EventHandler>(surface);
            WM.CompositionTarget.Rendering -= handler;
            var log = Field<MapRenderLog?>(surface, "renderLog");
            host.Close(); other.Close();
            log?.Dispose(); log?.Completion.GetAwaiter().GetResult();
            gpu.Dispose();
        }
    }
}
