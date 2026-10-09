using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// Model preparation on a worker. The compute pass keeps the software renderer's projection,
// triangle order, nearest texture sampling, alpha cutout and 2x supersampling. CPU parsing and
// spatial bins prepare the inputs; pixel coverage, depth, lighting and downsampling run on the GPU.
internal sealed class GpuModels : IDisposable
{
    private static readonly object gate = new();
    private static GpuModels? shared;
    private static bool unavailable;
    internal static int Rendered { get; private set; }
    internal static string? FailureReason { get; private set; }
    internal static string ShaderCacheFolder { get; set; } = Path.Combine(Program.DataFolder, "cache");
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11ComputeShader shader;
    private readonly ID3D11Buffer frame;
    private readonly ID3D11Buffer tones;
    private readonly ID3D11ShaderResourceView toneView;
    private const int BinSize = 16;

    private GpuModels()
    {
        device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0]);
        context = device.ImmediateContext;
        try
        {
            if (!device.CheckFeatureSupport<FeatureDataDoubles>(Vortice.Direct3D11.Feature.Doubles).DoublePrecisionFloatShaderOps)
                throw new InvalidOperationException("D3D11 double precision required for model depth/texel rounding");
            shader = MapShaders.CachedShader(Source, "ModelCS", "cs_5_0", code => device.CreateComputeShader(code), ShaderCacheFolder);
            frame = device.CreateBuffer(new Vector4[2], BindFlags.ConstantBuffer);
            tones = device.CreateBuffer(Enumerable.Range(0, 256).Select(c => MathF.Pow(c / 255f, 0.78f)).ToArray(), BindFlags.ShaderResource, ResourceUsage.Immutable);
            toneView = device.CreateShaderResourceView(tones, new ShaderResourceViewDescription(tones, Format.R32_Float, 0, 256));
        }
        catch
        {
            toneView?.Dispose(); tones?.Dispose(); frame?.Dispose(); shader?.Dispose();
            context.Dispose(); device.Dispose(); throw;
        }
    }

    internal static GameModels.Sprite? TryRender(List<GameModels.Triangle> triangles, double pixelsPerUnit, float tilt, float yaw)
    {
        if (triangles.Count == 0 || Environment.GetEnvironmentVariable("SAN14_NO_GPU") == "1") return null;
        lock (gate)
        {
            if (unavailable) return null;
            try
            {
                if (shared is null)
                {
                    shared = new GpuModels();
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (gate) { shared?.Dispose(); shared = null; } };
                }
                GameModels.Sprite? sprite = shared.Render(triangles, pixelsPerUnit, tilt, yaw);
                if (sprite is not null) Rendered++;
                return sprite;
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException)
            {
                FailureReason = ex.Message;
                Debug.WriteLine("GPU model preparation unavailable: " + FailureReason);
                shared?.Dispose(); shared = null; unavailable = true;
                return null; // The existing software renderer remains the fallback.
            }
        }
    }

    private GameModels.Sprite? Render(List<GameModels.Triangle> triangles, double pixelsPerUnit, float tilt, float yaw)
    {
        float a = tilt * MathF.PI / 180, cos = MathF.Cos(a), sin = MathF.Sin(a), lift = MathF.Tan(a);
        Matrix4x4 turn = Matrix4x4.CreateRotationY(yaw * MathF.PI / 180);
        Vector3 Project(Vector3 p)
        {
            p = Vector3.Transform(p, turn);
            return new Vector3(p.X, p.Z - p.Y * lift, p.Y * cos + p.Z * sin);
        }
        var projected = new Vector3[triangles.Count * 3];
        for (int i = 0; i < triangles.Count; i++)
        {
            projected[i * 3] = Project(triangles[i].A);
            projected[i * 3 + 1] = Project(triangles[i].B);
            projected[i * 3 + 2] = Project(triangles[i].C);
        }
        float minX = projected.Min(p => p.X), maxX = projected.Max(p => p.X), minY = projected.Min(p => p.Y), maxY = projected.Max(p => p.Y);
        const int ss = 2;
        float scale = (float)(pixelsPerUnit / GameModels.GameUnitsPerWorldUnit) * ss;
        int width = Math.Max(ss, (int)MathF.Ceiling((maxX - minX) * scale) + ss * 2);
        int height = Math.Max(ss, (int)MathF.Ceiling((maxY - minY) * scale) + ss * 2);
        width += width % ss; height += height % ss;
        if ((long)width * height > 24_000_000) return null;
        int columns = (width + BinSize - 1) / BinSize, rows = (height + BinSize - 1) / BinSize;
        var counts = new int[columns * rows];
        var boxes = new (int X0, int Y0, int X1, int Y1)[triangles.Count];
        var data = new Vector4[triangles.Count * 8];
        var materials = new uint[triangles.Count * 4];
        var textures = new Dictionary<GameModels.Texture, int>(ReferenceEqualityComparer.Instance);
        int pixelCount = 0;
        Span<Vector3> s = stackalloc Vector3[3];
        for (int i = 0; i < triangles.Count; i++)
        {
            GameModels.Triangle t = triangles[i];
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = projected[i * 3 + k];
                s[k] = new Vector3((p.X - minX) * scale + ss, (p.Y - minY) * scale + ss, p.Z);
            }
            float area = (s[1].X - s[0].X) * (s[2].Y - s[0].Y) - (s[2].X - s[0].X) * (s[1].Y - s[0].Y);
            if (MathF.Abs(area) < 1e-6f) continue;
            int offset = -1, tw = 0, th = 0;
            if (t.Texture is { } texture)
            {
                if (!textures.TryGetValue(texture, out offset))
                { offset = pixelCount; pixelCount += texture.Pixels.Length; textures.Add(texture, offset); }
                tw = texture.Width; th = texture.Height;
            }
            int at = i * 8;
            data[at] = new Vector4(s[0], area); data[at + 1] = new Vector4(s[1], 0); data[at + 2] = new Vector4(s[2], 0);
            data[at + 3] = new Vector4(t.NA, 0); data[at + 4] = new Vector4(t.NB, 0); data[at + 5] = new Vector4(t.NC, 0);
            materials[i * 4] = (uint)offset; materials[i * 4 + 1] = (uint)tw; materials[i * 4 + 2] = (uint)th;
            data[at + 6] = new Vector4(t.UA.X, t.UA.Y, t.UB.X, t.UB.Y); data[at + 7] = new Vector4(t.UC, 0, 0);
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].X, MathF.Min(s[1].X, s[2].X))));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(s[0].X, MathF.Max(s[1].X, s[2].X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].Y, MathF.Min(s[1].Y, s[2].Y))));
            int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(s[0].Y, MathF.Max(s[1].Y, s[2].Y))));
            boxes[i] = (x0, y0, x1, y1);
            for (int by = y0 / BinSize; by <= y1 / BinSize; by++)
                for (int bx = x0 / BinSize; bx <= x1 / BinSize; bx++)
                    counts[by * columns + bx]++;
        }
        var ranges = new uint[counts.Length * 2];
        int indexCount = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            ranges[i * 2] = (uint)indexCount; ranges[i * 2 + 1] = (uint)counts[i];
            indexCount += counts[i]; counts[i] = 0;
        }
        var indices = new uint[Math.Max(1, indexCount)];
        for (int i = 0; i < boxes.Length; i++)
        {
            if (MathF.Abs(data[i * 8].W) < 1e-6f) continue;
            var box = boxes[i];
            for (int by = box.Y0 / BinSize; by <= box.Y1 / BinSize; by++)
                for (int bx = box.X0 / BinSize; bx <= box.X1 / BinSize; bx++)
                {
                    int bin = by * columns + bx;
                    indices[ranges[bin * 2] + counts[bin]++] = (uint)i;
                }
        }
        var pixels = new uint[Math.Max(1, pixelCount)];
        foreach (var texture in textures) texture.Key.Pixels.CopyTo(pixels, texture.Value);
        using var triangleBuffer = device.CreateBuffer(data, BindFlags.ShaderResource, ResourceUsage.Immutable);
        using var pixelBuffer = device.CreateBuffer(pixels, BindFlags.ShaderResource, ResourceUsage.Immutable);
        using var rangeBuffer = device.CreateBuffer(ranges, BindFlags.ShaderResource, ResourceUsage.Immutable);
        using var indexBuffer = device.CreateBuffer(indices, BindFlags.ShaderResource, ResourceUsage.Immutable);
        using var materialBuffer = device.CreateBuffer(materials, BindFlags.ShaderResource, ResourceUsage.Immutable);
        using var triangleView = device.CreateShaderResourceView(triangleBuffer, new ShaderResourceViewDescription(triangleBuffer, Format.R32G32B32A32_Float, 0, (uint)data.Length));
        using var pixelView = device.CreateShaderResourceView(pixelBuffer, new ShaderResourceViewDescription(pixelBuffer, Format.R32_UInt, 0, (uint)pixels.Length));
        using var rangeView = device.CreateShaderResourceView(rangeBuffer, new ShaderResourceViewDescription(rangeBuffer, Format.R32_UInt, 0, (uint)ranges.Length));
        using var indexView = device.CreateShaderResourceView(indexBuffer, new ShaderResourceViewDescription(indexBuffer, Format.R32_UInt, 0, (uint)indices.Length));
        using var materialView = device.CreateShaderResourceView(materialBuffer, new ShaderResourceViewDescription(materialBuffer, Format.R32G32B32A32_UInt, 0, (uint)triangles.Count));
        int w = width / ss, h = height / ss;
        using var target = device.CreateTexture2D(Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, 1, null, BindFlags.UnorderedAccess);
        using var outputView = device.CreateUnorderedAccessView(target);
        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
        });
        try
        {
            context.UpdateSubresource(new[] { new Vector4(w, h, columns, BinSize), new Vector4(Vector3.Normalize(new(-0.48f, 0.66f, -0.58f)), 0) }, frame);
            context.CSSetShader(shader); context.CSSetConstantBuffer(0, frame);
            context.CSSetShaderResources(0, [triangleView, pixelView, rangeView, indexView, materialView, toneView]);
            context.CSSetUnorderedAccessView(0, outputView);
            context.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
            context.CSSetUnorderedAccessView(0, null);
            context.CopyResource(staging, target);
            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var result = new uint[w * h];
            var row = new int[w];
            try
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch), row, 0, w);
                    for (int x = 0; x < w; x++)
                    {
                        uint c = (uint)row[x];
                        result[y * w + x] = (c & 0xFF00FF00) | (c & 0xFF) << 16 | (c >> 16 & 0xFF);
                    }
                }
            }
            finally { context.Unmap(staging, 0); }
            var image = WMI.BitmapSource.Create(w, h, 96, 96, WM.PixelFormats.Pbgra32, null, result, w * 4);
            image.Freeze();
            return new GameModels.Sprite(image, new W.Rect((minX - 1f / scale * ss) / GameModels.GameUnitsPerWorldUnit,
                (minY - 1f / scale * ss) / GameModels.GameUnitsPerWorldUnit, w / pixelsPerUnit, h / pixelsPerUnit));
        }
        finally { context.ClearState(); }
    }

    public void Dispose() { toneView.Dispose(); tones.Dispose(); frame.Dispose(); shader.Dispose(); context.Dispose(); device.Dispose(); }

    private const string Source = """
        cbuffer Frame : register(b0) { float4 Size; float4 Light; };
        Buffer<float4> Triangles : register(t0);
        Buffer<uint> Pixels : register(t1);
        Buffer<uint> Ranges : register(t2);
        Buffer<uint> Indices : register(t3);
        Buffer<uint4> Materials : register(t4);
        Buffer<float> Tones : register(t5);
        RWTexture2D<float4> Output : register(u0);

        // Correct the approximate shader reciprocal with an exact double product/remainder.
        // This retains float division rounding at texture-texel and depth boundaries.
        float Divide(float a, float b)
        {
            precise float q = a / b;
            precise double remainder = (double)a - (double)q * (double)b;
            precise float correction = (float)remainder / b;
            return q + correction;
        }

        uint4 SampleModel(uint2 p)
        {
            uint bin = (p.y / (uint)Size.w) * (uint)Size.z + p.x / (uint)Size.w;
            uint start = Ranges[bin * 2], count = Ranges[bin * 2 + 1];
            precise float depth = -3.402823466e+38;
            uint4 colour = 0;
            [loop] for (uint i = 0; i < count; i++)
            {
                uint tri = Indices[start + i], at = tri * 8;
                float4 a = Triangles[at], b = Triangles[at + 1], c = Triangles[at + 2];
                precise float2 pixel = p + 0.5;
                precise float w0 = Divide((b.x - pixel.x) * (c.y - pixel.y) - (c.x - pixel.x) * (b.y - pixel.y), a.w);
                precise float w1 = Divide((c.x - pixel.x) * (a.y - pixel.y) - (a.x - pixel.x) * (c.y - pixel.y), a.w);
                precise float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                precise float z = w0 * a.z + w1 * b.z + w2 * c.z;
                if (z <= depth) continue;
                float4 na = Triangles[at + 3], nb = Triangles[at + 4], nc = Triangles[at + 5];
                float3 rgb = float3(0.75, 0.72, 0.68);
                uint4 material = Materials[tri];
                if (material.x != 0xffffffff)
                {
                    float4 uv = Triangles[at + 6];
                    precise float2 tex = uv.xy * w0 + uv.zw * w1 + Triangles[at + 7].xy * w2;
                    tex -= floor(tex);
                    uint x = min((uint)(tex.x * material.y), material.y - 1), y = min((uint)(tex.y * material.z), material.z - 1);
                    uint rgba = Pixels[material.x + y * material.y + x];
                    if ((rgba >> 24) < 128) continue;
                    rgb = float3(Tones[(rgba >> 16) & 255], Tones[(rgba >> 8) & 255], Tones[rgba & 255]);
                }
                precise float3 n = na.xyz * w0 + nb.xyz * w1 + nc.xyz * w2;
                precise float lengthSquared = n.x * n.x + n.y * n.y + n.z * n.z;
                precise float length = sqrt(lengthSquared);
                n = float3(Divide(n.x, length), Divide(n.y, length), Divide(n.z, length));
                precise float facing = n.x * Light.x + n.y * Light.y + n.z * Light.z;
                precise float light = 0.45 + 0.75 * abs(facing);
                depth = z;
                colour = uint4(min((uint3)(rgb * light * 255 + 0.5), 255), 255);
            }
            return colour;
        }

        [numthreads(8, 8, 1)]
        void ModelCS(uint3 thread : SV_DispatchThreadID)
        {
            if (thread.x >= (uint)Size.x || thread.y >= (uint)Size.y) return;
            uint2 p = thread.xy * 2;
            uint4 sum = SampleModel(p) + SampleModel(p + uint2(1, 0)) + SampleModel(p + uint2(0, 1)) + SampleModel(p + 1);
            Output[thread.xy] = (sum / 4) / 255.0;
        }
        """;
}
