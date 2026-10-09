using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Vortice.Direct3D11;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckMapDisplay()
    {
        // Real WPF composition and a read-only snapshot of the running game. No editor/game writes.
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", Path.GetFullPath("source/RenderChecks/artifacts/map-display.csv"));
        var window = new EditorWindow();
        var root = (W.FrameworkElement)window.Content;
        var map = (MapView)typeof(EditorWindow).GetField("map", Hidden)!.GetValue(window)!;
        window.Content = null;
        var host = new W.Window { Content = root, Width = window.Width, Height = window.Height, UseLayoutRounding = true,
            Left = 0, Top = 0, ShowActivated = false, ShowInTaskbar = false, FontFamily = window.FontFamily,
            FontSize = window.FontSize };
        host.Show();
        var surface = (MapSurface)typeof(MapView).GetField("surface", Hidden)!.GetValue(map)!;
        var gpu = (GpuMap)typeof(MapSurface).GetField("gpu", Hidden)!.GetValue(surface)!;
        var terrain = (WC.Image)typeof(MapSurface).GetField("terrain", Hidden)!.GetValue(surface)!;
        var gpuImage = (WC.Image)typeof(MapSurface).GetField("gpuImage", Hidden)!.GetValue(surface)!;
        var watch = Stopwatch.StartNew();
        Task<GameSnapshot>? read = GameMemory.IsRunning() ? Task.Run(() =>
        {
            GameIdentity identity = GameMemory.FindGame();
            GameLayout layout = GameMemory.Resolve(identity, message => Console.WriteLine("read: " + message), CancellationToken.None, []);
            return GameMemory.ReadSnapshot(identity, layout);
        }) : null;
        int checkedFrames = 0, previousFrame = -1;
        double lastSample = -1;
        int lastDistinct = 0;
        bool sawTerrain = false;
        while (watch.Elapsed.TotalSeconds < 18)
        {
            var frame = new WT.DispatcherFrame();
            surface.Dispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            WT.Dispatcher.PushFrame(frame);
            if (read is { IsCompleted: true })
            {
                GameSnapshot data = read.GetAwaiter().GetResult();
                map.SetSnapshot(data);
                Console.WriteLine(JsonSerializer.Serialize(new { snapshot = true, cities = data.Cities.Length, units = data.Units.Length }));
                read = null;
            }
            root.UpdateLayout();
            if (gpu.PresentedFrames != previousFrame || watch.Elapsed.TotalSeconds > lastSample + 1)
            {
                lastSample = watch.Elapsed.TotalSeconds;
                previousFrame = gpu.PresentedFrames;
                int width = gpu.Image.PixelWidth, height = gpu.Image.PixelHeight;
                byte[] pixels = !gpu.Pending && width > 0 && height > 0 && (bool)typeof(GpuMap).GetField("frontReady", Hidden)!.GetValue(gpu)!
                    ? ReadPixels(gpu, width, height) : [];
                WM.Color background = ((WM.SolidColorBrush)surface.Background).Color;
                int distinct = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] != background.B || pixels[i + 1] != background.G || pixels[i + 2] != background.R) distinct++;
                var context = (ID3D11DeviceContext)typeof(GpuMap).GetField("immediate", Hidden)!.GetValue(gpu)!;
                var query = (ID3D11Query)typeof(GpuMap).GetField("finished", Hidden)!.GetValue(gpu)!;
                int status = gpu.Pending ? context.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush).Code : 0;
                Console.WriteLine(JsonSerializer.Serialize(new { seconds = watch.Elapsed.TotalSeconds, frame = previousFrame,
                    surface = new[] { surface.ActualWidth, surface.ActualHeight }, image = new[] { gpuImage.ActualWidth, gpuImage.ActualHeight },
                    terrain = terrain.Source is not null, width, height, terrainPixels = distinct, gpu.Lost, gpu.Pending,
                    status, locks = typeof(System.Windows.Interop.D3DImage).GetField("_lockCount", Hidden)!.GetValue(gpu.Image),
                    available = gpu.Image.IsFrontBufferAvailable,
                    hooked = typeof(MapSurface).GetField("gpuHooked", Hidden)!.GetValue(surface), dirty = typeof(MapSurface).GetField("gpuDirty", Hidden)!.GetValue(surface),
                    looping = typeof(MapSurface).GetField("looping", Hidden)!.GetValue(surface) }));
                if (terrain.Source is not null && distinct > 10000) sawTerrain = true;
                if (sawTerrain)
                {
                    checkedFrames++;
                }
                lastDistinct = distinct;
            }
            Thread.Sleep(10);
        }
        var log = (MapRenderLog?)typeof(MapSurface).GetField("renderLog", Hidden)!.GetValue(surface);
        host.Close();
        log?.Dispose();
        log?.Completion.GetAwaiter().GetResult();
        if (!sawTerrain) throw new InvalidOperationException("Terrain never appeared");
        if (lastDistinct < 10000 || gpu.Pending || gpu.Lost || (uint)typeof(System.Windows.Interop.D3DImage).GetField("_lockCount", Hidden)!.GetValue(gpu.Image)! != 0)
            throw new InvalidOperationException("Loaded map became blank or stopped presenting frames");
        Console.WriteLine(JsonSerializer.Serialize(new { checkedFrames, retainedForSeconds = watch.Elapsed.TotalSeconds }));
    }
}
