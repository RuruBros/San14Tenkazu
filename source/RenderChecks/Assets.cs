using System.Diagnostics;
using System.Text.Json;
using San14Tenkazu;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckAssetPreparation(bool all)
    {
        string directory = FaceLibrary.FindGameDirectory(null) ?? throw new InvalidOperationException("Actual game assets unavailable");
        GpuModels.ShaderCacheFolder = "source/RenderChecks/artifacts/shaders";
        string[] names = ["obj-mc01-s-city-a-nm-001", "obj-mc01-m-city-b-nm-001", "obj-mc01-l-city-c-nm-001", "obj-mc01-gate-nm-001", "obj-mc01-prefecture-nm-001", "obj-mc01-port-nm-001", "obj-mb01-jin-001", "obj-n02-tree-01"];
        var cases = names.Select(name => (Model: name, Yaw: 0, Pixels: 80.0, Tilt: 35f)).ToList();
        if (all)
        {
            string cache = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "San14Tenkazu", "models");
            foreach (string file in System.IO.Directory.EnumerateFiles(cache, "*.png", System.IO.SearchOption.AllDirectories))
            {
                string[] key = System.IO.Path.GetFileNameWithoutExtension(file).Split('_');
                if (key.Length == 4 && int.TryParse(key[1], out int yaw) && double.TryParse(key[2], out double pixels) && float.TryParse(key[3], out float tilt))
                    cases.Add((key[0], yaw, pixels, tilt));
            }
        }
        var results = new List<object>();
        int rejected = 0;
        double cpuTotal = 0, gpuTotal = 0;
        foreach (var item in cases.Distinct())
        {
            string name = item.Model;
            // Warm archive parsing before either timed renderer; bypass the persistent sprite cache.
            _ = GameModels.Render(directory, name, item.Pixels, item.Tilt, item.Yaw, useGpu: false);
            var watch = Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            GameModels.Sprite cpu = GameModels.Render(directory, name, item.Pixels, item.Tilt, item.Yaw, useGpu: false) ?? throw new InvalidOperationException("CPU model unavailable: " + name);
            double cpuMs = watch.Elapsed.TotalMilliseconds;
            long cpuBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            watch.Restart(); before = GC.GetAllocatedBytesForCurrentThread();
            int rendered = GpuModels.Rendered;
            GameModels.Sprite gpu = GameModels.Render(directory, name, item.Pixels, item.Tilt, item.Yaw) ?? throw new InvalidOperationException("GPU model unavailable: " + name);
            double gpuMs = watch.Elapsed.TotalMilliseconds;
            long gpuBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(GpuModels.Rendered == rendered + 1, "Model fell back to CPU: " + name + ": " + GpuModels.FailureReason);
            Require(cpu.World == gpu.World && cpu.Image.PixelWidth == gpu.Image.PixelWidth && cpu.Image.PixelHeight == gpu.Image.PixelHeight, "Model projection/dimensions changed: " + name);
            int stride = cpu.Image.PixelWidth * 4;
            var expected = new byte[stride * cpu.Image.PixelHeight];
            var actual = new byte[expected.Length];
            cpu.Image.CopyPixels(expected, stride, 0); gpu.Image.CopyPixels(actual, stride, 0);
            int differing = 0, max = 0, alphaError = 0; long error = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                int diff = Math.Abs(expected[i] - actual[i]);
                if (diff != 0) differing++;
                max = Math.Max(max, diff); error += diff;
                if (i % 4 == 3) alphaError = Math.Max(alphaError, diff);
            }
            // Float sqrt/normalisation can round a final RGB byte by one on a different GPU.
            // Coverage, alpha cutout and downsampled alpha must remain exact.
            if (max > 1 || alphaError != 0) rejected++;
            cpuTotal += cpuMs; gpuTotal += gpuMs;
            var row = new { Model = name, item.Yaw, item.Pixels, item.Tilt, cpu.Image.PixelWidth, cpu.Image.PixelHeight, CpuMs = cpuMs, GpuMs = gpuMs, CpuBytes = cpuBytes, GpuBytes = gpuBytes,
                DifferentBytes = differing, MaxByteError = max, MaxAlphaError = alphaError, MeanByteError = error / (double)expected.Length };
            results.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
            // Save both outputs for investigation; comparison is reported without masking differences.
            string key = $"{name}_{item.Yaw}_{item.Pixels:0}_{item.Tilt:0}";
            SaveAsset(cpu.Image, key + "-cpu.png"); SaveAsset(gpu.Image, key + "-gpu.png");
        }
        System.IO.File.WriteAllText("source/RenderChecks/artifacts/assets.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Models {results.Count}, CPU {cpuTotal:F3} ms, GPU {gpuTotal:F3} ms (including first device initialisation), rejected {rejected}");
        Require(rejected == 0, "GPU model pixels differ beyond RGB rounding; inspect artifacts/assets.json");
        string? noGpu = Environment.GetEnvironmentVariable("SAN14_NO_GPU");
        try
        {
            Environment.SetEnvironmentVariable("SAN14_NO_GPU", "1");
            int rendered = GpuModels.Rendered;
            GameModels.Sprite fallback = GameModels.Render(directory, names[0], 80, 35) ?? throw new InvalidOperationException("CPU fallback returned no model");
            GameModels.Sprite expected = GameModels.Render(directory, names[0], 80, 35, useGpu: false)!;
            Require(GpuModels.Rendered == rendered && fallback.World == expected.World, "CPU fallback used GPU or changed projection");
            var a = new byte[fallback.Image.PixelWidth * fallback.Image.PixelHeight * 4]; var b = new byte[a.Length];
            fallback.Image.CopyPixels(a, fallback.Image.PixelWidth * 4, 0); expected.Image.CopyPixels(b, expected.Image.PixelWidth * 4, 0);
            Require(a.SequenceEqual(b), "CPU fallback pixels changed");
            Console.WriteLine("CPU model fallback pixels passed");
        }
        finally { Environment.SetEnvironmentVariable("SAN14_NO_GPU", noGpu); }
    }

    private static void SaveAsset(WMI.BitmapSource image, string name)
    {
        string folder = "source/RenderChecks/artifacts/models";
        System.IO.Directory.CreateDirectory(folder);
        var encoder = new WMI.PngBitmapEncoder();
        encoder.Frames.Add(WMI.BitmapFrame.Create(image));
        using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name));
        encoder.Save(file);
    }
}
