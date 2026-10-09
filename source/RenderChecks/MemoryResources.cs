using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckMemoryResources()
    {
        // This reference comes from the original renderer, before its whole-map temporary arrays
        // were replaced. Cover transparent/unowned land, all palettes and varying ground light.
        const int width = 513, height = 599;
        var owners = Enumerable.Range(0, GameMemory.TileCount).Select(i => (byte)(i % 71)).ToArray();
        var ground = Enumerable.Range(0, width * height).Select(i => unchecked((int)0xFF000000) |
            (i * 37 & 255) << 16 | (i * 17 & 255) << 8 | (i * 3 & 255)).ToArray();
        var strategic = (WMI.BitmapSource)typeof(MapView).GetMethod("RenderStrategic", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [owners, (ground, width, height)])!;
        byte[] strategicPixels = new byte[width * height * 4];
        strategic.CopyPixels(strategicPixels, width * 4, 0);
        string strategicHash = Convert.ToHexString(SHA256.HashData(strategicPixels));
        Require(strategicHash == "2F947E4C3A14FB0A0312383DE45BE80F00317E7A7CB5010F1317F4AED159F2F7", "Strategic image pixels changed");

        byte[] expected = Enumerable.Range(0, 100_003).Select(i => (byte)(i * 37)).ToArray();
        using (var buffer = new NativePixelBuffer(expected.Length))
        {
            buffer.ReadExactly(new MemoryStream(expected));
            byte[] actual = new byte[expected.Length]; Marshal.Copy(buffer.Pointer, actual, 0, actual.Length);
            Require(expected.AsSpan().SequenceEqual(actual), "Chunked native image read changed bytes");
        }
        using (var buffer = new NativePixelBuffer(expected.Length))
        {
            bool failed = false;
            try { buffer.ReadExactly(new MemoryStream(expected, 0, expected.Length - 1)); }
            catch (EndOfStreamException) { failed = true; }
            Require(failed, "Truncated native image read was accepted");
        }
        WMI.BitmapSource copied;
        using (var buffer = NativePixelBuffer.Copy(strategic, width * 4))
        {
            copied = WMI.BitmapSource.Create(width, height, 96, 96, strategic.Format, null, buffer.Pointer, buffer.Length, width * 4);
            copied.Freeze();
        }
        byte[] copiedPixels = new byte[strategicPixels.Length]; copied.CopyPixels(copiedPixels, width * 4, 0);
        Require(strategicPixels.AsSpan().SequenceEqual(copiedPixels), "WPF image depended on released native pixels");
        CheckBaseMapStorage();

        using GpuMap gpu = GpuMap.TryCreate() ?? throw new InvalidOperationException("GPU unavailable");
        gpu.Resize(32, 32, 96);
        var cache = (System.Collections.IDictionary)typeof(D2DCanvas).GetField("bitmaps", Hidden)!.GetValue(gpu.Canvas)!;
        object layer = new(), secondLayer = new();
        var commands = new MapDrawList();
        WMI.BitmapSource MakeImage(byte red)
        {
            var pixels = new byte[256 * 256 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = red; pixels[i + 3] = 255; }
            var image = WMI.BitmapSource.Create(256, 256, 96, 96, WM.PixelFormats.Pbgra32, null, pixels, 256 * 4);
            image.Freeze(); return image;
        }
        ID2D1Bitmap1? previousBitmap = null;
        var previousMips = new List<ID2D1Bitmap1>();
        WMI.BitmapSource? lastImage = null;
        for (int version = 1; version <= 16; version++)
        {
            lastImage = MakeImage((byte)(version * 11));
            commands.Reset(); commands.Clear(WM.Colors.Transparent);
            commands.SetLayerImage(layer, lastImage);
            commands.DrawBitmap(lastImage, new W.Rect(0, 0, 32, 32), 1, WM.BitmapScalingMode.HighQuality);
            Require(gpu.Render(canvas => commands.Play(canvas)), "Layer replacement frame rejected"); Finish(gpu);
            Require(cache.Count == 1, "Replaced map-layer images accumulated in GPU cache");
            Require(previousBitmap is null || previousBitmap.NativePointer == IntPtr.Zero, "Replaced GPU bitmap was not disposed");
            Require(previousMips.All(mip => mip.NativePointer == IntPtr.Zero), "Replaced GPU mipmaps were not disposed");
            var pixels = ReadPixels(gpu, 32, 32);
            Require(pixels[2] == version * 11 && pixels[3] == 255, "Replacement frame changed pixels");
            var entry = cache[lastImage]!;
            previousBitmap = (ID2D1Bitmap1)entry.GetType().GetField("Bitmap", Hidden)!.GetValue(entry)!;
            previousMips = ((IEnumerable<ID2D1Bitmap1>)entry.GetType().GetField("Smaller", Hidden)!.GetValue(entry)!).ToList();
        }
        gpu.Render(canvas => { canvas.SetLayerImage(secondLayer, lastImage); }); Finish(gpu);
        var replacement = MakeImage(77);
        gpu.Render(canvas => { canvas.SetLayerImage(layer, replacement); canvas.DrawImage(replacement, new W.Rect(0, 0, 32, 32)); }); Finish(gpu);
        Require(cache.Count == 2 && previousBitmap!.NativePointer != IntPtr.Zero, "A still-shared layer image was released");
        gpu.Render(canvas => { canvas.SetLayerImage(secondLayer, null); }); Finish(gpu);
        Require(cache.Count == 1 && previousBitmap!.NativePointer == IntPtr.Zero, "Last layer reference did not release its image");
        gpu.Render(canvas => { canvas.SetLayerImage(layer, null); }); Finish(gpu);
        Require(cache.Count == 0, "Hidden layer retained its texture");
        CheckSharedGroundStorage(gpu);
        bool? groundDataReleased = CheckGroundUploadData();
        Console.WriteLine(JsonSerializer.Serialize(new { StrategicHash = strategicHash, PixelEquivalent = true,
            NativeCopyLifetimePassed = true, ReplacedLayers = 16, DisposedMipmaps = true, SharedLayersPassed = true,
            SharedGroundTexturePassed = true, TransparentGroundPassed = true, GroundUploadDataReleased = groundDataReleased,
            BaseMapCachePixelsPassed = true, RemainingBitmaps = cache.Count }));
    }

    private static void CheckBaseMapStorage()
    {
        const int width = 79, height = 93;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = (byte)(i % 127); pixels[i + 1] = (byte)(i * 3 % 127); pixels[i + 2] = (byte)(i * 7 % 127); pixels[i + 3] = 128; }
        var image = WMI.BitmapSource.Create(width, height, 96, 96, WM.PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        var crop = new WMI.CroppedBitmap(image, new W.Int32Rect(7, 11, 51, 69)); crop.Freeze();
        var field = new MapView.Scenery(image, new System.Drawing.PointF(-7f / 4, -11f / 4), 4);
        var read = typeof(MapView).GetMethod("ReadBaseMap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        object map = Activator.CreateInstance(read.ReturnType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            null, [crop, 4f, true, field], null)!;
        string path = Path.GetFullPath("source/RenderChecks/artifacts/memory-basemap/basemap-check.bin");
        typeof(MapView).GetMethod("WriteBaseMap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [path, map]);
        object loaded = read.Invoke(null, [path]) ?? throw new InvalidOperationException("Base map cache missing");
        var loadedImage = (WMI.BitmapSource)read.ReturnType.GetProperty("Image")!.GetValue(loaded)!;
        var loadedField = (MapView.Scenery)read.ReturnType.GetProperty("Around")!.GetValue(loaded)!;
        byte[] actual = new byte[pixels.Length]; loadedField.Image.CopyPixels(actual, width * 4, 0);
        Require(actual.AsSpan().SequenceEqual(pixels), "Base map cache changed field pixels");
        byte[] expectedCrop = new byte[51 * 69 * 4], actualCrop = new byte[expectedCrop.Length];
        crop.CopyPixels(expectedCrop, 51 * 4, 0); loadedImage.CopyPixels(actualCrop, 51 * 4, 0);
        Require(expectedCrop.AsSpan().SequenceEqual(actualCrop), "Base map cache changed cropped playable pixels");
        File.Delete(path);
    }

    private static void CheckSharedGroundStorage(GpuMap gpu)
    {
        var device = (ID3D11Device)typeof(GpuMap).GetField("d3d", Hidden)!.GetValue(gpu)!;
        var context = (ID3D11DeviceContext)typeof(GpuMap).GetField("immediate", Hidden)!.GetValue(gpu)!;
        var shaders = gpu.Shaders!;
        var groundField = typeof(MapShaders).GetField("ground", Hidden)!;
        var groundType = groundField.FieldType;
        object layer = new();
        foreach (bool transparent in new[] { false, true })
        {
            var pixels = new byte[32 * 32 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 31; pixels[i + 1] = 47; pixels[i + 2] = 61; pixels[i + 3] = 255; }
            if (transparent) pixels[^1] = 128; // one alpha at the end must reject sharing
            var image = WMI.BitmapSource.Create(32, 32, 96, 96, WM.PixelFormats.Pbgra32, null, pixels, 128);
            image.Freeze();
            object ground = Activator.CreateInstance(groundType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, [image], null)!;
            groundType.GetField("Constants", Hidden)!.SetValue(ground, device.CreateBuffer(16, BindFlags.ConstantBuffer));
            groundField.SetValue(shaders, ground);
            try
            {
                Require(gpu.Render(canvas => { canvas.SetLayerImage(layer, image); canvas.DrawImage(image, new W.Rect(0, 0, 32, 32)); }), "Ground storage frame failed");
                Finish(gpu);
                var view = (ID3D11ShaderResourceView)groundType.GetField("BaseMap", Hidden)!.GetValue(ground)!;
                using var resource = view.Resource;
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                using var shared = gpu.Canvas.OpaqueTexture(image);
                Require(transparent ? shared is null : shared?.NativePointer == texture.NativePointer,
                    "Ground did not share opaque storage or shared premultiplied transparency");
                // Retiring the Direct2D bitmap must leave the shader's independently owned view usable.
                gpu.Render(canvas => canvas.SetLayerImage(layer, null)); Finish(gpu);
                var expectedImage = new WMI.FormatConvertedBitmap(image, WM.PixelFormats.Bgra32, null, 0);
                byte[] expected = new byte[pixels.Length]; expectedImage.CopyPixels(expected, 128, 0);
                using var staging = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = 32, Height = 32, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
                });
                context.CopyResource(staging, texture);
                var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    byte[] actual = new byte[pixels.Length];
                    for (int y = 0; y < 32; y++) Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, actual, y * 128, 128);
                    Require(actual.AsSpan().SequenceEqual(expected), "Shader base pixels changed after bitmap retirement");
                }
                finally { context.Unmap(staging, 0); }
            }
            finally { ((IDisposable)ground).Dispose(); groundField.SetValue(shaders, null); }
        }
    }

    private static bool? CheckGroundUploadData()
    {
        if (FaceLibrary.FindGameDirectory(null) is null) return null;
        var lazy = (Lazy<GroundDetail?>)typeof(GroundDetail).GetField("shared", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Require(!lazy.IsValueCreated, "CPU ground was loaded before GPU upload check");
        using var data = GroundGpuData.Load() ?? throw new InvalidOperationException("Game ground upload data missing");
        Require(!lazy.IsValueCreated, "GPU upload retained CPU fallback data");
        var cpu = GroundDetail.Data!;
        Require(data.MaterialMap.Bytes.SequenceEqual(cpu.MaterialMap) && data.Albedo.Bytes.SequenceEqual(cpu.Albedo) &&
            data.Normals is { } normals && normals.Bytes.SequenceEqual(cpu.Normals), "Native ground data changed material, albedo or normal bytes");
        Require(data.LayerSize == cpu.LayerSize && data.LayerCount == cpu.LayerCount && data.MipCount == cpu.MipCount, "Ground texture layout changed");
        data.Dispose();
        Require(data.MaterialMap.IsClosed && data.Albedo.IsClosed && data.Normals!.IsClosed, "Native ground upload buffers were retained");
        Require(cpu.Albedo.Length > 0 && cpu.MaterialMap.Length > 0, "Disposing GPU data broke CPU fallback");
        return true;
    }
}
