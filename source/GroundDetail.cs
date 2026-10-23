using System.Collections.Concurrent;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// Close-up ground, as the game draws it (read-only from 0002\FieldEditor4.rdb):
//   - 19 tiling ground layers: 1024x1024 BC3 array textures in KTID E369753A, the albedo first and the
//     layers' normal maps second (the third, nearly black, is not used),
//   - a material map: the field set's 2049x2049 RGBA8 texture, one cell per 100 game units, with
//     byte 0 = blend weight, byte 1 = second material (255 none), byte 2 = main material.
// Each material is one layer repeated every so many game units. The game's own table was not found;
// the one below was fitted against the game's baked colour map (correlation 0.79~0.97 per layer).
// A tile multiplies the lit base map by each layer's texture relative to its own surroundings (about two of
// the base map's texels), so the layers add only grain finer than the base map: colours, relief, water and
// any larger pattern stay those of the base map (which the game baked from the same layers; multiplying a
// layer's large patches in again doubled them into blotches), and the change of zoom level does not show. The layers'
// normal maps add their own fine relief, lit from the north-west like the base map, so that close up the
// ground has grain instead of the base map's soft, magnified shading.
internal sealed class GroundDetail
{
    internal const uint ArrayKtid = 0xE369753A, OlderArrayKtid = 0x57EE6CEA;
    internal const int MaterialMapSize = 2049;
    internal const float UnitsPerCell = 100f;

    // Material index -> (array layer, game units per repeat).
    internal static readonly (int Layer, float Repeat)[] Materials =
    [
        (12, 6000), (16, 4000), (15, 4000), (10, 8000), (9, 6000), (11, 3000), (8, 2000), (17, 5000),
        (13, 5000), (14, 1000), (18, 5000), (0, 6000), (4, 4000), (3, 6000), (2, 1500), (6, 4000), (6, 4000)
    ];

    private static readonly Lazy<GroundDetail?> shared = new(Load);

    private readonly byte[] albedo;                 // the albedo array's pixel data (BC3, stored mip by mip)
    private readonly byte[]? normals;               // the normal array's, laid out the same way
    private readonly int layerSize, layerCount, mipCount;
    private readonly byte[] materials;              // 2049 x 2049 x 4
    private readonly ConcurrentDictionary<(int Layer, int Mip), Lazy<Level>> levels = new();
    private readonly ConcurrentDictionary<(int Layer, int Mip), Lazy<Shade>> shades = new();

    private sealed record Level(uint[] Pixels, int Size, float InverseR, float InverseG, float InverseB);

    // A layer's fine relief as light: per texel, how much its normal faces the light against flat ground
    // (100 = as flat ground).
    private sealed record Shade(byte[] Light, int Size);

    // North-west light, as the base map (GameTerrain): normalised, and its height for flat ground.
    internal const float LightX = -0.479f, LightY = -0.579f, LightZ = 0.659f;
    internal const float ReliefStrength = 0.85f;

    private GroundDetail(byte[] albedo, byte[]? normals, int layerSize, int layerCount, int mipCount, byte[] materials)
    {
        this.albedo = albedo;
        this.normals = normals;
        this.layerSize = layerSize;
        this.layerCount = layerCount;
        this.mipCount = mipCount;
        this.materials = materials;
    }

    // Loaded on first use (a second or two); null when the game's files are missing or look different.
    private static GroundDetail? Load()
    {
        try
        {
            if (FaceLibrary.FindGameDirectory(null) is not { } directory) return null;
            string data = Path.Combine(directory, "0002");
            List<KtglArchive.Entry> entries = KtglArchive.ReadIndex(data, "FieldEditor4");

            byte[]? materials = null;
            foreach (KtglArchive.Entry entry in entries.Where(e => e.Type == KtglArchive.TextureType && e.Size > 20_000_000)
                         .OrderByDescending(e => e.Ktid == GameTerrain.FieldSetKtid))
            {
                byte[] g1t = KtglArchive.Read(data, entry);
                var map = KtglArchive.G1tTextures(g1t).FirstOrDefault(t => t.Format == 0x01 && t.Width == MaterialMapSize && t.Height == MaterialMapSize);
                int bytes = MaterialMapSize * MaterialMapSize * 4;
                if (map.DataOffset <= 0 || map.DataOffset + bytes > g1t.Length) continue;
                materials = g1t.AsSpan(map.DataOffset, bytes).ToArray();
                break;
            }
            if (materials is null) return null;

            foreach (KtglArchive.Entry entry in entries.Where(e => e.Ktid is ArrayKtid or OlderArrayKtid).OrderByDescending(e => e.Ktid == ArrayKtid))
            {
                byte[] g1t = KtglArchive.Read(data, entry);
                KtglArchive.G1tTexture[] textures = KtglArchive.G1tTextures(g1t);
                if (textures.Length == 0 || textures[0] is not { Format: 0x5B, Width: 1024, Height: 1024 } layers) continue;
                long chain = 0;
                for (int mip = 0, size = layers.Width; mip < layers.Mips; mip++, size = Math.Max(1, size / 2)) chain += LevelBytes(size);
                int count = (int)((layers.DataEnd - layers.DataOffset) / chain);
                if (count < Materials.Max(m => m.Layer) + 1) continue;
                byte[] albedo = g1t.AsSpan(layers.DataOffset, (int)(chain * count)).ToArray();
                byte[]? normals = textures.Length > 1 && textures[1] is { Format: 0x5B, Width: 1024, Height: 1024 } n && n.Mips == layers.Mips
                    && n.DataOffset + chain * count <= g1t.Length ? g1t.AsSpan(n.DataOffset, (int)(chain * count)).ToArray() : null;
                return new GroundDetail(albedo, normals, layers.Width, count, layers.Mips, materials);
            }
            return null;
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { return null; }
    }

    internal static long LevelBytes(int size) => (long)Math.Max(1, (size + 3) / 4) * Math.Max(1, (size + 3) / 4) * 16;

    // Reads the ground data ahead of the first close-up (about 0.7 s on a worker), so zooming in is sharp at once.
    internal static void Preload() => _ = shared.Value;

    // Retained only by the CPU fallback, whose later tiles need these bytes. GPU upload storage
    // has a separate, deterministic lifetime in GroundGpuData.
    internal static GroundDetail? Data => shared.Value;
    internal byte[] Albedo => albedo;
    internal byte[]? Normals => normals;
    internal byte[] MaterialMap => materials;
    internal int LayerSize => layerSize;
    internal int LayerCount => layerCount;
    internal int MipCount => mipCount;

    // One close-up tile over the lit base map (null when the ground data is missing or the tile is off the map).
    internal static WMI.BitmapSource? Render(MapTiles.TileKey key, WMI.BitmapSource baseImage, float basePixelsPerUnit, byte[]? terrain = null) =>
        shared.Value?.RenderTile(key, baseImage, basePixelsPerUnit, terrain);

    private WMI.BitmapSource? RenderTile(MapTiles.TileKey key, WMI.BitmapSource baseImage, float basePpu, byte[]? terrain)
    {
        const int size = MapTiles.TileSize;
        float ppu = MapTiles.PixelsPerUnit(key.Level);
        float wx0 = key.X * size / ppu, wy0 = key.Y * size / ppu;

        // The base map under the tile, with two pixels of margin for bicubic sampling.
        int bx0 = Math.Max(0, (int)MathF.Floor(wx0 * basePpu) - 2), by0 = Math.Max(0, (int)MathF.Floor(wy0 * basePpu) - 2);
        int bx1 = Math.Min(baseImage.PixelWidth, (int)MathF.Ceiling((wx0 + size / ppu) * basePpu) + 3);
        int by1 = Math.Min(baseImage.PixelHeight, (int)MathF.Ceiling((wy0 + size / ppu) * basePpu) + 3);
        if (bx1 - bx0 < 4 || by1 - by0 < 4) return null;
        int bw = bx1 - bx0, bh = by1 - by0;
        var basePixels = new int[bw * bh];
        baseImage.CopyPixels(new W.Int32Rect(bx0, by0, bw, bh), basePixels, bw * 4, 0);

        // Each material's texture level for this zoom: about one texel per screen pixel.
        var used = new Level?[Materials.Length];
        var usedShades = new Shade?[Materials.Length];
        int MipFor(int material)
        {
            float texelsPerPixel = layerSize * GameTerrain.UnitsPerHex / Materials[material].Repeat / ppu;
            return Math.Clamp((int)MathF.Floor(MathF.Log2(Math.Max(1, texelsPerPixel))), 0, Math.Min(mipCount - 1, 8));
        }
        Level LevelFor(int material)
        {
            if (used[material] is { } known) return known;
            return used[material] = levels.GetOrAdd((Materials[material].Layer, MipFor(material)), k => new Lazy<Level>(() => Decode(k.Layer, k.Mip))).Value;
        }
        Shade? ShadeFor(int material)
        {
            if (normals is null) return null;
            if (usedShades[material] is { } known) return known;
            return usedShades[material] = shades.GetOrAdd((Materials[material].Layer, MipFor(material)), k => new Lazy<Shade>(() => DecodeShade(k.Layer, k.Mip))).Value;
        }

        var output = new int[size * size];
        Span<float> weights = stackalloc float[Materials.Length];
        for (int py = 0; py < size; py++)
        {
            float wy = wy0 + (py + 0.5f) / ppu;
            float z = GameTerrain.OriginZ + wy * GameTerrain.UnitsPerHex;
            for (int px = 0; px < size; px++)
            {
                float wx = wx0 + (px + 0.5f) / ppu;
                float x = GameTerrain.OriginX + wx * GameTerrain.UnitsPerHex;
                (float r, float g, float b) = SampleBase(basePixels, bw, bh, wx * basePpu - 0.5f - bx0, wy * basePpu - 0.5f - by0);

                // Material weights from the four surrounding cells of the material map.
                weights.Clear();
                float fx = x / UnitsPerCell, fz = z / UnitsPerCell;
                int cx = (int)MathF.Floor(fx), cz = (int)MathF.Floor(fz);
                float tx = fx - cx, tz = fz - cz;
                for (int j = 0; j < 2; j++)
                    for (int i = 0; i < 2; i++)
                    {
                        int o = (Math.Clamp(cz + j, 0, MaterialMapSize - 1) * MaterialMapSize + Math.Clamp(cx + i, 0, MaterialMapSize - 1)) * 4;
                        float corner = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - tz : tz);
                        int main = materials[o + 2], second = materials[o + 1];
                        float blend = second < Materials.Length ? materials[o] / 255f : 0;
                        if (main < Materials.Length) weights[main] += corner * (1 - blend);
                        if (blend > 0) weights[second] += corner * blend;
                    }

                float dr = 0, dg = 0, db = 0, light = 0, total = 0, lit = 0;
                for (int m = 0; m < weights.Length; m++)
                {
                    float weight = weights[m];
                    if (weight < 0.02f) continue;
                    Level level = LevelFor(m);
                    float repeat = Materials[m].Repeat;
                    (float lr, float lg, float lb) = SampleLayer(level, x / repeat, z / repeat);
                    dr += weight * lr * level.InverseR;
                    dg += weight * lg * level.InverseG;
                    db += weight * lb * level.InverseB;
                    total += weight;
                    if (ShadeFor(m) is { } shade)
                    {
                        light += weight * SampleShade(shade, x / repeat, z / repeat);
                        lit += weight;
                    }
                }
                if (total > 0)
                {
                    float relief = lit > 0 ? Math.Clamp(1 + ReliefStrength * (light / lit / 100f - 1), 0.45f, 1.6f) : 1;
                    r *= Math.Clamp(dr / total, 0.3f, 2.2f) * relief;
                    g *= Math.Clamp(dg / total, 0.3f, 2.2f) * relief;
                    b *= Math.Clamp(db / total, 0.3f, 2.2f) * relief;
                }
                output[py * size + px] = ToByte(r) << 16 | ToByte(g) << 8 | ToByte(b);
            }
        }
        MapTrees.Draw(output, size, wx0, wy0, ppu, terrain);
        var image = WMI.BitmapSource.Create(size, size, 96, 96, WM.PixelFormats.Bgr32, null, output, size * 4);
        image.Freeze();
        return image;
    }

    private static int ToByte(float v) => Math.Clamp((int)(v + 0.5f), 0, 255);

    // Catmull-Rom: the base map is magnified many times close up, where bilinear sampling shows its texels
    // as soft diamonds.
    private static (float, float, float) SampleBase(int[] pixels, int width, int height, float x, float y)
    {
        x = Math.Clamp(x, 0, width - 1.001f);
        y = Math.Clamp(y, 0, height - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
        CatmullRom(fx, wx);
        CatmullRom(fy, wy);
        float r = 0, g = 0, b = 0;
        for (int j = 0; j < 4; j++)
        {
            int row = Math.Clamp(y0 - 1 + j, 0, height - 1) * width;
            for (int i = 0; i < 4; i++)
            {
                int p = pixels[row + Math.Clamp(x0 - 1 + i, 0, width - 1)];
                float w = wx[i] * wy[j];
                r += (p >> 16 & 255) * w;
                g += (p >> 8 & 255) * w;
                b += (p & 255) * w;
            }
        }
        return (r, g, b);
    }

    private static void CatmullRom(float t, Span<float> w)
    {
        float t2 = t * t, t3 = t2 * t;
        w[0] = 0.5f * (-t3 + 2 * t2 - t);
        w[1] = 0.5f * (3 * t3 - 5 * t2 + 2);
        w[2] = 0.5f * (-3 * t3 + 4 * t2 + t);
        w[3] = 0.5f * (t3 - t2);
    }

    // Bilinear, repeating, like SampleLayer.
    private static float SampleShade(Shade shade, float u, float v)
    {
        int size = shade.Size, mask = size - 1;
        u = (u - MathF.Floor(u)) * size - 0.5f;
        v = (v - MathF.Floor(v)) * size - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        int xa = x0 & mask, xb = (x0 + 1) & mask, ya = (y0 & mask) * size, yb = ((y0 + 1) & mask) * size;
        byte[] l = shade.Light;
        return (l[ya + xa] * (1 - fx) + l[ya + xb] * fx) * (1 - fy) + (l[yb + xa] * (1 - fx) + l[yb + xb] * fx) * fy;
    }

    // Bilinear, repeating: u and v are in texture repeats.
    private static (float, float, float) SampleLayer(Level level, float u, float v)
    {
        int size = level.Size;
        u = (u - MathF.Floor(u)) * size - 0.5f;
        v = (v - MathF.Floor(v)) * size - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        int mask = size - 1;
        int xa = x0 & mask, xb = (x0 + 1) & mask, ya = (y0 & mask) * size, yb = ((y0 + 1) & mask) * size;
        uint[] p = level.Pixels;
        uint a = p[ya + xa], b = p[ya + xb], c = p[yb + xa], d = p[yb + xb];
        float Channel(int shift) =>
            ((a >> shift & 255) * (1 - fx) + (b >> shift & 255) * fx) * (1 - fy) + ((c >> shift & 255) * (1 - fx) + (d >> shift & 255) * fx) * fy;
        return (Channel(16), Channel(8), Channel(0));
    }

    // Decodes one layer's normal map at one mip level into light against flat ground (x east, y south).
    private Shade DecodeShade(int layer, int mip)
    {
        long offset = 0;
        int size = layerSize;
        for (int m = 0; m < mip; m++, size = Math.Max(1, size / 2)) offset += LevelBytes(size) * layerCount;
        offset += LevelBytes(size) * layer;
        int blocks = Math.Max(1, size / 4);
        size = Math.Max(4, size);
        var light = new byte[size * size];
        Span<uint> block = stackalloc uint[16];
        for (int by = 0; by < blocks; by++)
            for (int bx = 0; bx < blocks; bx++)
            {
                Bc.Bc3Colour(normals.AsSpan((int)(offset + ((long)by * blocks + bx) * 16), 16), block);
                for (int i = 0; i < 16; i++)
                {
                    uint n = block[i];
                    float nx = ((n >> 16) & 255) / 127.5f - 1, ny = ((n >> 8) & 255) / 127.5f - 1, nz = (n & 255) / 127.5f - 1;
                    float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    float facing = length > 0 ? (nx * LightX + ny * LightY + nz * LightZ) / length / LightZ : 1;
                    light[(by * 4 + i / 4) * size + bx * 4 + i % 4] = (byte)Math.Clamp((int)(facing * 100 + 0.5f), 0, 255);
                }
            }
        return new Shade(light, size);
    }

    // Game units over which a layer's texture is averaged: the grain finer than this is the layer's own.
    internal const float GrainUnits = 100f;

    // Decodes one layer at one mip level as its ratio to its local average (128 = as the average).
    private Level Decode(int layer, int mip)
    {
        long offset = 0;
        int size = layerSize;
        for (int m = 0; m < mip; m++, size = Math.Max(1, size / 2)) offset += LevelBytes(size) * layerCount;
        offset += LevelBytes(size) * layer;
        int blocks = Math.Max(1, size / 4);
        size = Math.Max(4, size);
        var pixels = new uint[size * size];
        Span<uint> block = stackalloc uint[16];
        for (int by = 0; by < blocks; by++)
            for (int bx = 0; bx < blocks; bx++)
            {
                Bc.Bc3Colour(albedo.AsSpan((int)(offset + ((long)by * blocks + bx) * 16), 16), block);
                for (int i = 0; i < 16; i++) pixels[(by * 4 + i / 4) * size + bx * 4 + i % 4] = block[i];
            }
        float repeat = Materials.First(m => m.Layer == layer).Repeat;
        int radius = Math.Clamp((int)MathF.Round(GrainUnits / 2 * size / repeat), 1, size / 4);
        uint[] local = BoxBlur(pixels, size, radius);
        var ratios = new uint[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i], a = local[i];
            uint Ratio(int shift) => (uint)Math.Clamp((int)((p >> shift & 255) * 128f / Math.Max(4, a >> shift & 255) + 0.5f), 0, 255);
            ratios[i] = Ratio(16) << 16 | Ratio(8) << 8 | Ratio(0);
        }
        return new Level(ratios, size, 1 / 128f, 1 / 128f, 1 / 128f);
    }

    // A square box blur that wraps around the edges (the layers tile), in two passes.
    private static uint[] BoxBlur(uint[] pixels, int size, int radius)
    {
        var pass = new uint[pixels.Length];
        var result = new uint[pixels.Length];
        int span = radius * 2 + 1, mask = size - 1;
        for (int horizontal = 1; horizontal >= 0; horizontal--)
        {
            uint[] from = horizontal == 1 ? pixels : pass, to = horizontal == 1 ? pass : result;
            for (int line = 0; line < size; line++)
            {
                int Index(int k) => horizontal == 1 ? line * size + (k & mask) : (k & mask) * size + line;
                int r = 0, g = 0, b = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    uint p = from[Index(k)];
                    r += (int)(p >> 16 & 255); g += (int)(p >> 8 & 255); b += (int)(p & 255);
                }
                for (int k = 0; k < size; k++)
                {
                    to[Index(k)] = (uint)(r / span) << 16 | (uint)(g / span) << 8 | (uint)(b / span);
                    uint add = from[Index(k + radius + 1)], remove = from[Index(k - radius)];
                    r += (int)(add >> 16 & 255) - (int)(remove >> 16 & 255);
                    g += (int)(add >> 8 & 255) - (int)(remove >> 8 & 255);
                    b += (int)(add & 255) - (int)(remove & 255);
                }
            }
        }
        return result;
    }
}
