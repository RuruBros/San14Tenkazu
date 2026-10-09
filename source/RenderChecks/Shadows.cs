using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using San14Tenkazu;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckGpuShadows()
    {
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "San14Tenkazu", "models");
        using GpuMap gpu = GpuMap.TryCreate() ?? throw new InvalidOperationException("GPU unavailable");
        int count = 0, worst = 0, alphaWorst = 0;
        double cpuMs = 0, gpuMs = 0;
        long cpuBytes = 0, gpuBytes = 0;
        foreach (string file in System.IO.Directory.EnumerateFiles(folder, "*.png", System.IO.SearchOption.AllDirectories))
        {
            double[] world = System.IO.File.ReadAllText(System.IO.Path.ChangeExtension(file, ".txt")).Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            using var stream = System.IO.File.OpenRead(file);
            var frame = WMI.BitmapFrame.Create(stream, WMI.BitmapCreateOptions.None, WMI.BitmapCacheOption.OnLoad);
            var image = new WMI.FormatConvertedBitmap(frame, WM.PixelFormats.Pbgra32, null, 0); image.Freeze();
            var sprite = new GameModels.Sprite(image, new W.Rect(world[0], world[1], world[2], world[3]));
            double scale = image.PixelWidth / sprite.World.Width;
            var watch = Stopwatch.StartNew(); long allocated = GC.GetAllocatedBytesForCurrentThread();
            GameModels.Sprite cpu = MapSurface.WithShadow(sprite);
            cpuBytes += GC.GetAllocatedBytesForCurrentThread() - allocated; cpuMs += watch.Elapsed.TotalMilliseconds;
            int w = cpu.Image.PixelWidth, h = cpu.Image.PixelHeight;
            gpu.Resize(w, h, 96);
            watch.Restart(); allocated = GC.GetAllocatedBytesForCurrentThread();
            gpu.Render(canvas =>
            {
                canvas.Clear(WM.Colors.Transparent);
                canvas.PushMatrix(new WM.Matrix(scale, 0, 0, scale, 0, 0));
                canvas.DrawShadowed(sprite, new W.Point(-sprite.World.X, -sprite.World.Y));
                canvas.Pop();
            }); Finish(gpu);
            gpuBytes += GC.GetAllocatedBytesForCurrentThread() - allocated; gpuMs += watch.Elapsed.TotalMilliseconds;
            byte[] actual = ReadPixels(gpu, w, h);
            watch.Restart(); allocated = GC.GetAllocatedBytesForCurrentThread();
            gpu.Render(canvas => { canvas.Clear(WM.Colors.Transparent); canvas.DrawImage(cpu.Image, new W.Rect(0, 0, w, h)); }); Finish(gpu);
            cpuBytes += GC.GetAllocatedBytesForCurrentThread() - allocated; cpuMs += watch.Elapsed.TotalMilliseconds;
            byte[] expected = ReadPixels(gpu, w, h);
            int error = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
            int alpha = Enumerable.Range(0, expected.Length / 4).Max(i => Math.Abs(expected[i * 4 + 3] - actual[i * 4 + 3]));
            worst = Math.Max(worst, error); alphaWorst = Math.Max(alphaWorst, alpha); count++;
            Require(error <= 1, $"GPU shadow changed pixels: {System.IO.Path.GetFileName(file)}, error {error}, alpha {alpha}");
            gpu.Render(canvas =>
            {
                canvas.PushMatrix(new WM.Matrix(scale * 0.8, 0, 0, scale * 0.8, 0, 0));
                canvas.DrawShadowed(sprite, new W.Point(-sprite.World.X, -sprite.World.Y)); canvas.Pop();
            });
            Require(gpu.Canvas.BitmapUploads == 0, "Moving/zooming regenerated the model shadow"); Finish(gpu);
        }
        Console.WriteLine($"PASS GPU shadows: {count} actual cached models, max byte error {worst}, alpha rounding {alphaWorst}");
        Console.WriteLine($"CPU composition/upload/presentation {cpuMs:F3} ms, {cpuBytes} bytes; GPU composition/upload/presentation {gpuMs:F3} ms, {gpuBytes} bytes");
        // SetModels must not generate CPU shadow images in GPU mode; WPF fallback builds the original drawing on demand.
        var map = new MapSurface();
        var pixels = Enumerable.Repeat(0xFF998877u, 64 * 64).ToArray();
        var source = WMI.BitmapSource.Create(64, 64, 96, 96, WM.PixelFormats.Pbgra32, null, pixels, 256); source.Freeze();
        var placed = new GameModels.Sprite(source, new W.Rect(-0.4, -0.4, 0.8, 0.8));
        map.SetModels([new MapModels.Placement("test", 0, MapTerrain.CityTiles[1])], new Dictionary<(string, int, double), GameModels.Sprite?> { [("test", 0, 1)] = placed });
        object layer = typeof(MapSurface).GetField("models", Hidden)!.GetValue(map)!;
        Require(layer.GetType().GetField("drawing", Hidden)!.GetValue(layer) is null, "GPU models built the WPF drawing");
        var centre = MapTerrain.TileCenter(MapTerrain.CityTiles[1]);
        gpu.Resize(128, 128, 96);
        gpu.Render(canvas =>
        {
            canvas.Clear(WM.Colors.Transparent);
            canvas.PushMatrix(new WM.Matrix(80, 0, 0, 80, 64 - centre.X * 80, 64 - centre.Y * 80));
            layer.GetType().GetMethod("DrawGpu", Hidden)!.Invoke(layer, [canvas, 1.0]); canvas.Pop();
        }); Finish(gpu);
        byte[] layerPixels = ReadPixels(gpu, 128, 128);
        Require(layer.GetType().GetField("drawing", Hidden)!.GetValue(layer) is null, "GPU drawing materialised CPU shadows");
        GameModels.Sprite expectedSprite = MapSurface.WithShadow(placed);
        gpu.Render(canvas =>
        {
            canvas.Clear(WM.Colors.Transparent);
            W.Rect rect = expectedSprite.World; rect.Scale(80, 80); rect.Offset(64, 64);
            canvas.DrawImage(expectedSprite.Image, rect);
        }); Finish(gpu);
        Require(layerPixels.Zip(ReadPixels(gpu, 128, 128), (a, b) => Math.Abs(a - b)).Max() <= 1, "MapSurface model layer changed placement/shadows");
        var visual = new WM.DrawingVisual();
        using (WM.DrawingContext dc = visual.RenderOpen()) layer.GetType().GetMethod("OnRender", Hidden)!.Invoke(layer, [dc]);
        WM.DrawingGroup fallback = (WM.DrawingGroup)layer.GetType().GetField("drawing", Hidden)!.GetValue(layer)!;
        Require(fallback.Children.OfType<WM.ImageDrawing>().Single().ImageSource == MapSurface.WithShadow(placed).Image, "Model fallback changed its shadow image");
        Console.WriteLine("PASS lazy WPF model fallback");
    }
}
