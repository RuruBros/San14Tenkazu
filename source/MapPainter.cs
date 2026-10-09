using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace San14Tenkazu;

// Paints the static terrain once into a large bitmap: blended land colours, shaded relief,
// smooth coastlines and rivers, and sea depth. World units: hex circumradius = 1.
internal static class MapPainter
{
    internal const float PixelsPerUnit = 8f;
    internal static readonly Color DeepSea = Color.FromArgb(98, 150, 196);
    private const float WaterLevel = 0.42f;

    private const int Cols = MapTerrain.Columns, Rows = MapTerrain.Rows;
    private static readonly int GridHeight = Rows * 2 + 2; // two half-hex cells per tile, staggered by column

    // Relative height per terrain type; -1 = take the neighbours' average (roads, towns).
    private static float Height(int type) => type switch
    {
        22 or 23 => -0.35f,
        13 or 14 or 24 => -0.1f,
        1 or 3 or 4 => 0.05f,
        5 or 6 or 7 => 0.15f,
        8 => 0.35f,
        9 => 0.55f,
        10 => 0.8f,
        19 => 1.35f,
        20 => 1.0f,
        _ => -1f
    };

    internal static Bitmap Build(Func<int, Color> landColor)
    {
        byte[] terrain = MapTerrain.Terrain;
        int width = (int)MathF.Ceiling(MapTerrain.WorldWidth * PixelsPerUnit), height = (int)MathF.Ceiling(MapTerrain.WorldHeight * PixelsPerUnit);

        // Fields on the staggered grid (column = hex column, row = half-hex cell).
        var r = new float[Cols, GridHeight]; var g = new float[Cols, GridHeight]; var b = new float[Cols, GridHeight];
        var water = new float[Cols, GridHeight]; var sea = new float[Cols, GridHeight];
        var elevation = new float[Cols, GridHeight]; var depth = new float[Cols, GridHeight];

        float[] tileHeight = new float[terrain.Length];
        for (int i = 0; i < terrain.Length; i++) tileHeight[i] = Height(terrain[i]);
        // Neutral tiles (roads, towns) take the average height of their neighbours.
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < terrain.Length; i++)
            {
                if (Height(terrain[i]) >= -0.5f) continue;
                float sum = 0; int n = 0;
                foreach (int nb in MapTerrain.Neighbours(i))
                    if (Height(terrain[nb]) >= -0.5f || pass > 0) { sum += Math.Max(0, tileHeight[nb]); n++; }
                tileHeight[i] = n > 0 ? sum / n : 0.05f;
            }

        // Land colour for water tiles is borrowed from nearby land so blends stay earthy.
        var tileColor = new Color[terrain.Length];
        for (int i = 0; i < terrain.Length; i++) tileColor[i] = MapTerrain.IsWater(terrain[i]) ? Color.Empty : landColor(terrain[i]);
        for (int pass = 0; pass < 3; pass++)
            for (int i = 0; i < terrain.Length; i++)
            {
                if (!tileColor[i].IsEmpty) continue;
                int rr = 0, gg = 0, bb = 0, n = 0;
                foreach (int nb in MapTerrain.Neighbours(i))
                    if (!tileColor[nb].IsEmpty) { rr += tileColor[nb].R; gg += tileColor[nb].G; bb += tileColor[nb].B; n++; }
                if (n > 0) tileColor[i] = Color.FromArgb(rr / n, gg / n, bb / n);
            }

        // Sea depth: distance in tiles from the nearest land tile.
        int[] distance = Enumerable.Repeat(int.MaxValue, terrain.Length).ToArray();
        var queue = new Queue<int>();
        for (int i = 0; i < terrain.Length; i++)
            if (!MapTerrain.IsSea(terrain[i])) { distance[i] = 0; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach (int nb in MapTerrain.Neighbours(i))
                if (distance[nb] == int.MaxValue) { distance[nb] = distance[i] + 1; queue.Enqueue(nb); }
        }

        for (int col = 0; col < Cols; col++)
            for (int y = 0; y < GridHeight; y++)
            {
                int tile = TileAtCell(col, y);
                Color c = tileColor[tile].IsEmpty ? Color.FromArgb(224, 214, 176) : tileColor[tile];
                r[col, y] = c.R; g[col, y] = c.G; b[col, y] = c.B;
                water[col, y] = MapTerrain.IsWater(terrain[tile]) ? 1 : 0;
                sea[col, y] = MapTerrain.IsSea(terrain[tile]) ? 1 : 0;
                elevation[col, y] = tileHeight[tile];
                depth[col, y] = Math.Min(distance[tile], 12);
            }
        for (int pass = 0; pass < 5; pass++) Blur(elevation);
        Blur(depth);
        // Round off water edges; thin rivers survive because the threshold below is under 0.5.
        SoftBlur(water);
        SoftBlur(sea);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = locked.Stride;
            var pixels = new byte[stride * height];
            float h = MapTerrain.Sqrt3 / 2;
            // Light from the north-west, slightly above the horizon.
            float lx = -0.55f, ly = -0.65f, lz = 0.52f;
            float flat = lz;
            Parallel.For(0, height, py =>
            {
                float wy = (py + 0.5f) / PixelsPerUnit;
                float gy = wy / h - 0.5f;
                for (int px = 0; px < width; px++)
                {
                    float wx = (px + 0.5f) / PixelsPerUnit;
                    float gx = (wx - 1f) / 1.5f;
                    float w = Sample(water, gx, gy);
                    float cr, cg, cb;
                    if (w > WaterLevel)
                    {
                        float seaness = Math.Clamp(Sample(sea, gx, gy) * 1.4f, 0, 1);
                        float t = Math.Clamp(Sample(depth, gx, gy) / 9f, 0, 1);
                        // Shallow water near shore, deep blue offshore; rivers stay light.
                        cr = Lerp(150, DeepSea.R, t * seaness); cg = Lerp(196, DeepSea.G, t * seaness); cb = Lerp(222, DeepSea.B, t * seaness);
                        float shore = Math.Clamp((WaterLevel + 0.1f - w) / 0.1f, 0, 1);
                        cr = Lerp(cr, 96, shore * 0.5f); cg = Lerp(cg, 128, shore * 0.5f); cb = Lerp(cb, 150, shore * 0.5f);
                    }
                    else
                    {
                        cr = Sample(r, gx, gy); cg = Sample(g, gx, gy); cb = Sample(b, gx, gy);
                        // Shaded relief from the smoothed height field.
                        float dzdx = (Sample(elevation, gx + 0.35f, gy) - Sample(elevation, gx - 0.35f, gy)) / (0.7f * 1.5f);
                        float dzdy = (Sample(elevation, gx, gy + 0.6f) - Sample(elevation, gx, gy - 0.6f)) / (1.2f * h);
                        const float exaggeration = 2.2f;
                        float nx = -dzdx * exaggeration, ny = -dzdy * exaggeration, nz = 1f;
                        float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        float shade = (nx * lx + ny * ly + nz * lz) / length - flat;
                        float light = 1f + shade * 1.05f;
                        // Higher ground gets slightly paler and browner.
                        float e = Math.Clamp(Sample(elevation, gx, gy), 0, 1.4f);
                        cr = (cr + e * 10) * light; cg = (cg + e * 2) * light; cb = (cb - e * 6) * light;
                        // Dark rim along the coast and river banks.
                        float rim = Math.Clamp((w - (WaterLevel - 0.2f)) / 0.2f, 0, 1);
                        cr = Lerp(cr, 120, rim * 0.5f); cg = Lerp(cg, 110, rim * 0.5f); cb = Lerp(cb, 90, rim * 0.5f);
                    }
                    // Paper grain.
                    float grain = 1f + (Hash(px, py) - 0.5f) * 0.05f + (ValueNoise(px / 48f, py / 48f) - 0.5f) * 0.06f;
                    int o = py * stride + px * 4;
                    pixels[o] = ToByte(cb * grain);
                    pixels[o + 1] = ToByte(cg * grain);
                    pixels[o + 2] = ToByte(cr * grain);
                    pixels[o + 3] = 255;
                }
            });
            Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
        }
        finally { bitmap.UnlockBits(locked); }
        return bitmap;
    }

    // Road network: centre-to-centre segments between road tiles and the settlements they reach.
    internal static (PointF A, PointF B)[] RoadSegments()
    {
        byte[] terrain = MapTerrain.Terrain;
        static bool IsRoad(int t) => t is 2 or 11;
        static bool IsStop(int t) => t is 15 or 16 or 17 or 18;
        var segments = new List<(PointF, PointF)>();
        for (int i = 0; i < terrain.Length; i++)
        {
            if (!IsRoad(terrain[i]) && !IsStop(terrain[i])) continue;
            foreach (int nb in MapTerrain.Neighbours(i))
                if (nb > i && (IsRoad(terrain[nb]) || IsStop(terrain[nb])) && (IsRoad(terrain[i]) || IsRoad(terrain[nb])))
                    segments.Add((MapTerrain.TileCenter(i), MapTerrain.TileCenter(nb)));
        }
        return segments.ToArray();
    }

    private static int TileAtCell(int col, int cell)
    {
        int shift = col % 2 == 0 ? 1 : 0;
        int row = Math.Clamp((cell - shift) / 2, 0, Rows - 1);
        if (cell - shift < 0) row = 0;
        return row * Cols + col;
    }

    private static float Sample(float[,] field, float x, float y)
    {
        x = Math.Clamp(x, 0, Cols - 1.001f); y = Math.Clamp(y, 0, GridHeight - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        float top = field[x0, y0] + (field[x0 + 1, y0] - field[x0, y0]) * fx;
        float bottom = field[x0, y0 + 1] + (field[x0 + 1, y0 + 1] - field[x0, y0 + 1]) * fx;
        return top + (bottom - top) * fy;
    }

    private static void Blur(float[,] field)
    {
        var copy = (float[,])field.Clone();
        for (int x = 0; x < Cols; x++)
            for (int y = 0; y < GridHeight; y++)
            {
                float sum = 0; int n = 0;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= Cols || yy >= GridHeight) continue;
                        sum += copy[xx, yy]; n++;
                    }
                field[x, y] = sum / n;
            }
    }

    // [1 2 1] smoothing in both directions.
    private static void SoftBlur(float[,] field)
    {
        var copy = (float[,])field.Clone();
        for (int x = 0; x < Cols; x++)
            for (int y = 0; y < GridHeight; y++)
            {
                float sum = 0, weight = 0;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= Cols || yy >= GridHeight) continue;
                        float k = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1);
                        sum += copy[xx, yy] * k; weight += k;
                    }
                field[x, y] = sum / weight;
            }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static byte ToByte(float v) => (byte)Math.Clamp((int)v, 0, 255);

    private static float Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }

    private static float ValueNoise(float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        float a = Hash(x0, y0), b = Hash(x0 + 1, y0), c = Hash(x0, y0 + 1), d = Hash(x0 + 1, y0 + 1);
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
    }
}
