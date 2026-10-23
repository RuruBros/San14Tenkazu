namespace San14Tenkazu;

// Trees on wooded hexes, from the game's own tree models (the broadleaf obj-n02-tree-01 and the palm
// obj-n00-tree-05; the game's other trees carry far-view cards that the renderer cannot leave out). They are
// drawn into the close-up ground tiles with their shadows, so thousands of them cost nothing per frame. The
// game places its trees itself; here each wooded hex gets a fixed scatter of several trees.
internal static class MapTrees
{
    // A tree's picture at several sizes (each half the one before), and its area in world units around the
    // point it stands on, at scale 1.
    internal sealed record Sprite(Level[] Levels, float X, float Y, float Width, float Height);
    internal sealed record Level(uint[] Pixels, int Width, int Height);

    private static volatile Sprite[]? sprites;      // [0] broadleaf, [1] palm

    internal static bool Ready => sprites is not null;

    // The pictures by kind (null until the models are rendered), for the GPU's shaders (MapShaders).
    internal static Sprite[]? Sprites => sprites;

    // A tree: where it stands (world units), its size against the sprite's, how light its crown is, its kind.
    internal readonly record struct Tree(float X, float Y, float Scale, float Tone, int Kind);

    // How the trees are drawn, here and by the GPU's shaders: shadows to the south-east, as dark as this at most;
    // the renderer's greyish foliage given this much more colour.
    internal const float ShadowX = 0.22f, ShadowY = 0.3f, ShadowStrength = 0.42f, Saturation = 1.4f;
    internal static readonly (int R, int G, int B) ShadowColour = (20, 14, 8);

    // From the model renderer's premultiplied BGRA picture.
    internal static void Set(Sprite? broadleaf, Sprite? palm) =>
        sprites = broadleaf is null ? null : [broadleaf, palm ?? broadleaf];

    internal static Sprite FromPixels(uint[] pixels, int width, int height, float x, float y, float worldWidth, float worldHeight)
    {
        var levels = new List<Level> { new(pixels, width, height) };
        while (levels[^1].Width >= 8 && levels[^1].Height >= 8) levels.Add(Half(levels[^1]));
        return new Sprite([.. levels], x, y, worldWidth, worldHeight);
    }

    private static Level Half(Level level)
    {
        int width = level.Width / 2, height = level.Height / 2;
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint a = level.Pixels[2 * y * level.Width + 2 * x], b = level.Pixels[2 * y * level.Width + 2 * x + 1];
                uint c = level.Pixels[(2 * y + 1) * level.Width + 2 * x], d = level.Pixels[(2 * y + 1) * level.Width + 2 * x + 1];
                uint Average(int shift) => ((a >> shift & 255) + (b >> shift & 255) + (c >> shift & 255) + (d >> shift & 255) + 2) / 4;
                pixels[y * width + x] = Average(24) << 24 | Average(16) << 16 | Average(8) << 8 | Average(0);
            }
        return new Level(pixels, width, height);
    }

    // Trees per hex and which kind, by terrain (half the jungle's are palms).
    private static (int Count, float Palms) Wood(int terrain) => terrain switch
    {
        MapTerrain.Woods => (6, 0f),
        MapTerrain.Jungle => (6, 0.5f),
        MapTerrain.MarshForest => (4, 0f),
        _ => (0, 0f)
    };

    private const float MinimumPixelsPerUnit = 24;    // smaller, the trees would be specks

    // Draws the trees standing on or near a ground tile into its pixels (opaque BGR, size x size, starting at
    // world point (x0, y0), ppu pixels per world unit), northern ones first so that southern ones stand in front.
    internal static void Draw(int[] output, int size, float x0, float y0, float ppu, byte[]? terrain = null)
    {
        if (sprites is not { } kinds || ppu < MinimumPixelsPerUnit) return;
        float x1 = x0 + size / ppu, y1 = y0 + size / ppu, margin = 1.5f;
        int column0 = Math.Max(0, (int)((x0 - margin) / 1.5f) - 1), column1 = Math.Min(MapTerrain.Columns - 1, (int)((x1 + margin) / 1.5f) + 1);
        int row0 = Math.Max(0, (int)((y0 - margin) / MapTerrain.Sqrt3) - 1), row1 = Math.Min(MapTerrain.Rows - 1, (int)((y1 + margin) / MapTerrain.Sqrt3) + 1);
        List<Tree> trees = Trees(column0, column1, row0, row1, terrain);
        if (trees.Count == 0) return;
        // Shadows first (to the south-east, as the models'), then the trees.
        foreach (Tree tree in trees) Blit(output, size, x0, y0, ppu, kinds[tree.Kind], tree.X + ShadowX * tree.Scale, tree.Y + ShadowY * tree.Scale, tree.Scale, 0, shadow: true);
        foreach (Tree tree in trees) Blit(output, size, x0, y0, ppu, kinds[tree.Kind], tree.X, tree.Y, tree.Scale, tree.Tone, shadow: false);
    }

    // The trees on the hexes of these columns and rows, northern ones first.
    internal static List<Tree> Trees(int column0, int column1, int row0, int row1, byte[]? terrain = null)
    {
        var trees = new List<Tree>();
        for (int row = row0; row <= row1; row++)
            for (int column = column0; column <= column1; column++)
            {
                int tile = row * MapTerrain.Columns + column;
                (int count, float palms) = Wood((terrain ?? MapTerrain.Terrain)[tile]);
                if (count == 0) continue;
                System.Drawing.PointF centre = MapTerrain.TileCenter(tile);
                uint seed = (uint)tile * 2654435761u + 12345;
                float Next()
                {
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    return (seed & 0xFFFFFF) / (float)0x1000000;
                }
                for (int k = 0; k < count; k++)
                {
                    // Spread over the hex: a ring of positions with jitter, a little different in every hex.
                    float angle = (k + Next() * 0.7f) / count * MathF.Tau, radius = 0.3f + 0.45f * MathF.Sqrt(Next());
                    float x = centre.X + MathF.Cos(angle) * radius, y = centre.Y + MathF.Sin(angle) * radius * 0.9f;
                    // Each crown a little lighter or darker than the next, so that they stand apart.
                    trees.Add(new Tree(x, y, 0.58f + 0.24f * Next(), 1.2f + 0.25f * Next(), Next() < palms ? 1 : 0));
                }
            }
        trees.Sort((a, b) => a.Y.CompareTo(b.Y));
        return trees;
    }

    private static void Blit(int[] output, int size, float x0, float y0, float ppu, Sprite sprite, float x, float y, float scale, float tone, bool shadow)
    {
        // The tree's area on the tile, in tile pixels.
        float left = (x + sprite.X * scale - x0) * ppu, top = (y + sprite.Y * scale - y0) * ppu;
        float width = sprite.Width * scale * ppu, height = sprite.Height * scale * ppu;
        int px0 = Math.Max(0, (int)MathF.Floor(left)), px1 = Math.Min(size, (int)MathF.Ceiling(left + width));
        int py0 = Math.Max(0, (int)MathF.Floor(top)), py1 = Math.Min(size, (int)MathF.Ceiling(top + height));
        if (px0 >= px1 || py0 >= py1) return;
        // The picture size nearest above the size it is drawn at.
        Level level = sprite.Levels[0];
        foreach (Level candidate in sprite.Levels)
        {
            if (candidate.Width < width) break;
            level = candidate;
        }
        float su = level.Width / width, sv = level.Height / height;
        for (int py = py0; py < py1; py++)
        {
            float v = (py + 0.5f - top) * sv - 0.5f;
            for (int px = px0; px < px1; px++)
            {
                float u = (px + 0.5f - left) * su - 0.5f;
                (float a, float r, float g, float b) = Sample(level, u, v);
                if (a < 0.01f) continue;
                int o = py * size + px, d = output[o];
                if (shadow)
                {
                    float dark = a * ShadowStrength, keep = 1 - dark;
                    output[o] = ToByte((d >> 16 & 255) * keep + ShadowColour.R * dark) << 16 | ToByte((d >> 8 & 255) * keep + ShadowColour.G * dark) << 8 |
                        ToByte((d & 255) * keep + ShadowColour.B * dark);
                }
                else
                {
                    // The renderer's foliage is greyish; a little more colour, and the crown's own tone.
                    float keep = 1 - a, grey = 0.3f * r + 0.59f * g + 0.11f * b;
                    r = (grey + (r - grey) * Saturation) * tone;
                    g = (grey + (g - grey) * Saturation) * tone;
                    b = (grey + (b - grey) * Saturation) * tone;
                    output[o] = ToByte(r + (d >> 16 & 255) * keep) << 16 | ToByte(g + (d >> 8 & 255) * keep) << 8 | ToByte(b + (d & 255) * keep);
                }
            }
        }
    }

    // Bilinear, premultiplied; outside the picture is clear.
    private static (float A, float R, float G, float B) Sample(Level level, float u, float v)
    {
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        float a = 0, r = 0, g = 0, b = 0;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int x = x0 + i, y = y0 + j;
                if (x < 0 || y < 0 || x >= level.Width || y >= level.Height) continue;
                float w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                uint p = level.Pixels[y * level.Width + x];
                a += (p >> 24) * w; r += (p >> 16 & 255) * w; g += (p >> 8 & 255) * w; b += (p & 255) * w;
            }
        return (a / 255, r, g, b);
    }

    private static int ToByte(float v) => Math.Clamp((int)(v + 0.5f), 0, 255);
}
