using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace San14Tenkazu;

// The game's own terrain, read from 0002\FieldEditor4.rdb (read-only):
//   - a 4096x4096 BC3 colour map and a 4096x4096 BC3 normal map of the whole field (50 units/texel),
//   - a 2049x2049 "_MHK0100" height map (100 units/cell, 24-bit heights in bits 8..31).
// Hex tiles map to game units as X = 468 * hexX + 37126, Z = 468 * hexY + 12930
// (fitted exactly on all 46 cities), so the painted bitmap lines up with the hex grid.
internal static class GameTerrain
{
    internal const float UnitsPerHex = 468f;
    internal const float OriginX = 37126f, OriginZ = 12930f;
    private const float UnitsPerTexel = 50f, UnitsPerCell = 100f;
    private const int HeightSize = 2049;
    private const float HeightScale = 2352f;   // raw height units per game unit (header max 6966.25)
    private const float WaterLevel = 1_000_000f; // raw height: rivers and sea lie below this

    internal static float PixelsPerUnit => UnitsPerHex / UnitsPerTexel; // bitmap pixels per hex unit (9.36)

    // FieldEditor4 holds two versions of the field (textures and height map). The one in the "_0" bins
    // matches the running game's hex terrain better (36,449 against 35,359 of 40,022 hex centres), so it
    // is tried first; the other set, and then any set that looks right, are the fallbacks.
    internal const uint FieldSetKtid = 0xF7C960B0, HeightMapKtid = 0x99461FE5;

    // The whole field texture, which reaches past the playable map (about 79 hexes' width of mountains to
    // the west, some 28 units of land or sea on the other sides): scenery for views wider than the map, and
    // close up the ground past its edges. Painted at the map's resolution with its pixels on the map's, so the
    // map is a part of it. Origin is the world position of its top-left corner.
    internal sealed record Surroundings(Bitmap Image, float OriginX, float OriginY, float PixelsPerUnit);

    // Builds the lit map for the hex world rectangle; returns null when the resources are missing.
    internal static Bitmap? Build(string gameDirectory, out Surroundings? surroundings)
    {
        surroundings = null;
        string data = Path.Combine(gameDirectory, "0002");
        if (!File.Exists(Path.Combine(data, "FieldEditor4.rdb"))) return null;
        List<KtglArchive.Entry> entries = KtglArchive.ReadIndex(data, "FieldEditor4");

        byte[]? heights = null;
        foreach (KtglArchive.Entry entry in entries.Where(e => e.Type == KtglArchive.HeightMapType).OrderByDescending(e => e.Ktid == HeightMapKtid))
        {
            byte[] candidate = KtglArchive.Read(data, entry);
            if (Encoding.ASCII.GetString(candidate, 0, 8) != "_MHK0100" || candidate.Length < 0x20 + HeightSize * HeightSize * 4) continue;
            heights = candidate;
            break;
        }
        if (heights is null) return null;

        // The field texture set holds several 4096x4096 BC3 maps: colour (earthy) and normals (blue).
        byte[]? g1t = null;
        KtglArchive.G1tTexture colour = default, normal = default;
        foreach (KtglArchive.Entry entry in entries.Where(e => e.Type == KtglArchive.TextureType && e.Size > 20_000_000).OrderByDescending(e => e.Ktid == FieldSetKtid))
        {
            byte[] candidate = KtglArchive.Read(data, entry);
            var big = KtglArchive.G1tTextures(candidate).Where(t => t.Format == 0x5B && t.Width == 4096 && t.Height == 4096).ToArray();
            if (big.Length < 2) continue;
            var normals = big.Where(t => IsNormalMap(candidate, t)).ToArray();
            var colours = big.Where(t => !IsNormalMap(candidate, t) && Brightness(candidate, t) > 12).ToArray();
            if (normals.Length == 0 || colours.Length == 0) continue;
            (g1t, colour, normal) = (candidate, colours[0], normals[0]);
            break;
        }
        if (g1t is null) return null;
        float originTexX = OriginX / UnitsPerTexel, originTexY = OriginZ / UnitsPerTexel;
        int width = (int)MathF.Ceiling(MapTerrain.WorldWidth * PixelsPerUnit), height = (int)MathF.Ceiling(MapTerrain.WorldHeight * PixelsPerUnit);
        // The field from the fraction of a texel the map starts at: pixel (skipX + x, skipY + y) is the map's (x, y).
        float skipX = MathF.Floor(originTexX), skipY = MathF.Floor(originTexY);
        Bitmap field = Paint(g1t, colour, normal, heights, originTexX - skipX, originTexY - skipY, colour.Width, colour.Height);
        surroundings = new Surroundings(field, -skipX / PixelsPerUnit, -skipY / PixelsPerUnit, PixelsPerUnit);
        return field.Clone(new Rectangle((int)skipX, (int)skipY, width, height), field.PixelFormat);
    }

    // Paints width x height pixels from texel (texX0, texY0), a texel per pixel.
    private static Bitmap Paint(byte[] g1t, KtglArchive.G1tTexture colour, KtglArchive.G1tTexture normal, byte[] heights,
        float texX0, float texY0, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = locked.Stride;
            var pixels = new byte[stride * height];
            // Light from the north-west, as on a printed relief map.
            float lx = -0.48f, ly = -0.58f, lz = 0.66f;
            Parallel.For(0, height, py =>
            {
                Span<uint> colourBlock = stackalloc uint[16], normalBlock = stackalloc uint[16];
                int lastBlock = -1;
                float ty = texY0 + py;
                int texY = Math.Clamp((int)ty, 0, colour.Height - 1);
                for (int px = 0; px < width; px++)
                {
                    float tx = texX0 + px;
                    int texX = Math.Clamp((int)tx, 0, colour.Width - 1);
                    int block = (texY / 4) * (colour.Width / 4) + texX / 4;
                    if (block != lastBlock)
                    {
                        Bc.Bc3Colour(g1t.AsSpan(colour.DataOffset + block * 16, 16), colourBlock);
                        Bc.Bc3Colour(g1t.AsSpan(normal.DataOffset + block * 16, 16), normalBlock);
                        lastBlock = block;
                    }
                    int i = (texY % 4) * 4 + texX % 4;
                    uint c = colourBlock[i], n = normalBlock[i];
                    float r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                    float nx = ((n >> 16) & 0xFF) / 127.5f - 1, ny = ((n >> 8) & 0xFF) / 127.5f - 1, nz = (n & 0xFF) / 127.5f - 1;

                    // Large relief from the height map, fine detail from the normal map.
                    float hx = tx * UnitsPerTexel / UnitsPerCell, hy = ty * UnitsPerTexel / UnitsPerCell;
                    float h = Height(heights, hx, hy);
                    float dx = (Height(heights, hx + 2, hy) - Height(heights, hx - 2, hy)) / (4 * UnitsPerCell * HeightScale);
                    float dy = (Height(heights, hx, hy + 2) - Height(heights, hx, hy - 2)) / (4 * UnitsPerCell * HeightScale);
                    const float relief = 2.6f;
                    float sx = -dx * relief, sy = -dy * relief;
                    float slope = MathF.Sqrt(sx * sx + sy * sy + 1);
                    float diffuse = Math.Max(0, (sx * lx + sy * ly + lz) / slope);
                    float detail = (nx * lx + ny * ly + nz * lz) - lz;
                    float light = 0.42f + 0.78f * diffuse + 0.3f * detail;

                    // The colour map is dark (linear); lift it, then add a little saturation.
                    r = Tone(r); g = Tone(g); b = Tone(b);
                    float grey = (r + g + b) / 3;
                    r = (grey + (r - grey) * 1.25f) * light; g = (grey + (g - grey) * 1.25f) * light; b = (grey + (b - grey) * 1.25f) * light;

                    if (h < WaterLevel)
                    {
                        float depth = Math.Clamp((WaterLevel - h) / WaterLevel, 0, 1);
                        float edge = Math.Clamp((WaterLevel - h) / 120_000f, 0, 1);
                        float wr = 118 - 58 * depth, wg = 170 - 52 * depth, wb = 192 - 20 * depth;
                        float a = edge * (0.62f + 0.38f * depth);
                        r = r * (1 - a) + wr * a; g = g * (1 - a) + wg * a; b = b * (1 - a) + wb * a;
                    }
                    int o = py * stride + px * 4;
                    pixels[o] = ToByte(b); pixels[o + 1] = ToByte(g); pixels[o + 2] = ToByte(r); pixels[o + 3] = 255;
                }
            });
            Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
        }
        finally { bitmap.UnlockBits(locked); }
        return bitmap;
    }

    private static float Tone(float c) => 255f * MathF.Pow(c / 255f, 1 / 1.9f);

    private static float Height(byte[] map, float x, float y)
    {
        x = Math.Clamp(x, 0, HeightSize - 1.001f); y = Math.Clamp(y, 0, HeightSize - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        float Raw(int cx, int cy) => BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan(0x20 + (cy * HeightSize + cx) * 4)) >> 8;
        float top = Raw(x0, y0) + (Raw(x0 + 1, y0) - Raw(x0, y0)) * fx;
        float bottom = Raw(x0, y0 + 1) + (Raw(x0 + 1, y0 + 1) - Raw(x0, y0 + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    private static bool IsNormalMap(byte[] g1t, KtglArchive.G1tTexture texture)
    {
        (float r, float g, float b) = AverageColour(g1t, texture);
        return b > 180 && b > r + 40 && b > g + 40;
    }

    private static float Brightness(byte[] g1t, KtglArchive.G1tTexture texture)
    {
        (float r, float g, float b) = AverageColour(g1t, texture);
        return (r + g + b) / 3;
    }

    private static (float, float, float) AverageColour(byte[] g1t, KtglArchive.G1tTexture texture)
    {
        Span<uint> block = stackalloc uint[16];
        float r = 0, g = 0, b = 0;
        int blocks = texture.Width / 4 * (texture.Height / 4), samples = 0;
        for (int i = 0; i < blocks; i += 997)
        {
            Bc.Bc3Colour(g1t.AsSpan(texture.DataOffset + i * 16, 16), block);
            r += (block[5] >> 16) & 0xFF; g += (block[5] >> 8) & 0xFF; b += block[5] & 0xFF;
            samples++;
        }
        return (r / samples, g / samples, b / samples);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)v, 0, 255);
}
