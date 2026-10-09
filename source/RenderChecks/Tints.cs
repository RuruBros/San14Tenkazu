using System.Diagnostics;
using San14Tenkazu;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckGpuTints()
    {
        var random = new Random(314);
        var pixels = new byte[128 * 128 * 4]; random.NextBytes(pixels);
        var raw = WMI.BitmapSource.Create(128, 128, 96, 96, WM.PixelFormats.Bgra32, null, pixels, 512); raw.Freeze();
        using GpuMap gpu = GpuMap.TryCreate() ?? throw new InvalidOperationException("GPU unavailable");
        gpu.Resize(128, 128, 96);
        var colour = System.Drawing.Color.FromArgb(92, 137, 211);
        var area = new W.Rect(0, 0, 128, 128);
        gpu.Render(canvas => { canvas.Clear(WM.Colors.Transparent); canvas.DrawImage(MapArt.Tinted(pixels, 128, 128, colour), area); }); Finish(gpu);
        byte[] expected = ReadPixels(gpu, 128, 128);
        gpu.Render(canvas => { canvas.Clear(WM.Colors.Transparent); canvas.DrawTinted(raw, colour, area); }); Finish(gpu);
        byte[] actual = ReadPixels(gpu, 128, 128);
        int max = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
        int alpha = Enumerable.Range(0, expected.Length / 4).Max(i => Math.Abs(expected[i * 4 + 3] - actual[i * 4 + 3]));
        Console.WriteLine($"Tint byte error {max}, alpha error {alpha}");
        Require(max <= 2 && alpha == 0, "GPU tint changed colour/alpha beyond premultiplication rounding");
        gpu.Render(canvas => canvas.DrawTinted(raw, colour, new W.Rect(8, 8, 110, 110)));
        Require(gpu.Canvas.BitmapUploads == 0, "GPU tint rebuilt on moving/scaling"); Finish(gpu);
        gpu.Render(canvas => canvas.DrawTinted(raw, System.Drawing.Color.FromArgb(3, 4, 5), new W.Rect(-200, -200, 128, 128)));
        Require(gpu.Canvas.BitmapUploads == 0, "Off-screen tint created GPU resources"); Finish(gpu);
        for (int pass = 0; pass < 2; pass++)
        {
            var watch = Stopwatch.StartNew(); long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                var tint = System.Drawing.Color.FromArgb(i, 255 - i, 100);
                WMI.BitmapSource? cpu = pass == 0 ? MapArt.Tinted(pixels, 128, 128, tint) : null;
                gpu.Render(canvas => { canvas.Clear(WM.Colors.Transparent); if (cpu is not null) canvas.DrawImage(cpu, area); else canvas.DrawTinted(raw, tint, area); });
                Finish(gpu);
            }
            Console.WriteLine($"{(pass == 0 ? "CPU" : "GPU")} 200 variants {watch.Elapsed.TotalMilliseconds:F3} ms; {GC.GetAllocatedBytesForCurrentThread() - bytes} bytes");
        }
        string directory = FaceLibrary.FindGameDirectory(null) ?? throw new InvalidOperationException("Actual game art unavailable");
        MapArt.Load(FaceLibrary.Load(directory));
        string[] names = (string[])typeof(MapArt).GetField("Names", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        object cpuCache = typeof(MapArt).GetField("tinted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        int CpuCount() => (int)cpuCache.GetType().GetProperty("Count")!.GetValue(cpuCache)!;
        int checkedIcons = 0, worst = 0;
        foreach (string name in names.Distinct().Where(MapArt.Has))
        {
            foreach (var tint in new[] { colour, System.Drawing.Color.Black, System.Drawing.Color.White })
            {
                // Establish dimensions using a different CPU colour, then require GPU rendering not to add a CPU tint.
                WMI.BitmapSource size = MapArt.Get(name, System.Drawing.Color.FromArgb(17, 23, 29))!;
                int w = size.PixelWidth, h = size.PixelHeight;
                gpu.Resize(w, h, 96); var rect = new W.Rect(0, 0, w, h);
                int before = CpuCount();
                gpu.Render(canvas => { canvas.Clear(WM.Colors.Transparent); Require(MapArt.Draw(canvas, name, tint, rect), "Loaded icon missing from GPU path"); }); Finish(gpu);
                Require(CpuCount() == before, "GPU art created a CPU-coloured bitmap");
                byte[] made = ReadPixels(gpu, w, h);
                gpu.Render(canvas => { canvas.Clear(WM.Colors.Transparent); canvas.DrawImage(MapArt.Get(name, tint)!, rect); }); Finish(gpu);
                byte[] reference = ReadPixels(gpu, w, h);
                int iconError = made.Zip(reference, (a, b) => Math.Abs(a - b)).Max();
                int alphaError = Enumerable.Range(0, made.Length / 4).Max(i => Math.Abs(made[i * 4 + 3] - reference[i * 4 + 3]));
                worst = Math.Max(worst, iconError); checkedIcons++;
                Require(iconError <= 2 && alphaError == 0, $"Actual icon tint changed: {name}, RGB {iconError}, alpha {alphaError}");
            }
        }
        var drawing = new WM.DrawingGroup();
        using (WM.DrawingContext dc = drawing.Open()) Require(MapArt.Draw(new WpfCanvas(dc), MapArt.Shield, colour, area), "CPU art fallback failed");
        Require(drawing.Children.OfType<WM.ImageDrawing>().Single().ImageSource == MapArt.Get(MapArt.Shield, colour), "CPU fallback changed its bitmap");
        Console.WriteLine($"PASS actual art: {checkedIcons} icon/colour pairs, max RGB rounding {worst}, exact alpha, CPU fallback and no CPU tint in GPU path");
    }
}
