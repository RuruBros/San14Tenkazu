using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace San14Tenkazu;

// Officer portraits read straight from the installed game (read-only).
// 0002\ScreenLayout.rdb lists KTGL resources stored in ScreenLayout.rdb.bin*: G1T textures (BC3)
// and sprite sheets that name them. A sprite sheet is stored right after the texture it describes.
// Face sprites are "face_NNNN_ctr" (icon, 512px) and "face_NNNN_a/b/c" (portrait layers, 636x900),
// where NNNN is the officer's face number (officer record +0xA6; a few numbers name a second
// portrait "face_NNNNb_*", see SecondPortraits). Faces from 7000 are the
// player's own portraits in Documents\KoeiTecmo\San14\ADD_FACE.
internal sealed class FaceLibrary
{
    private const string CacheVersion = "sprites-v4";

    private readonly string dataDirectory;
    private readonly Dictionary<string, SpriteRef> sprites;
    // The texture containers read last, as read, newest first. The UI's sprites sit dozens to a texture (the map's
    // 54 in three), and reading one (2 MB to inflate) took ~45 ms: read once per sprite, the map's art took 2.4 s.
    private readonly LinkedList<((string Bin, long Offset) At, byte[] G1t)> recentTextures = new();
    private const int RecentTextures = 4;

    private sealed record SpriteRef(string Bin, long Offset, int Size, int Texture, float U0, float V0, float U1, float V1);

    private FaceLibrary(string dataDirectory, Dictionary<string, SpriteRef> sprites, string stamp)
    {
        this.dataDirectory = dataDirectory;
        this.sprites = sprites;
        Stamp = stamp;
    }

    internal int Count => sprites.Count;

    // Identifies the game's resource version; caches made from these resources carry it.
    internal string Stamp { get; }

    internal static string? FindGameDirectory(string? exePath)
    {
        exePath ??= GameMemory.RunningGamePath();
        return SteamInstallation.FindGameDirectory(exePath);
    }

    // Builds the face index (a few seconds the first time) or loads it from the local cache.
    internal static FaceLibrary Load(string gameDirectory)
    {
        string data = Path.Combine(gameDirectory, "0002");
        string index = Path.Combine(data, "ScreenLayout.rdb");
        var info = new FileInfo(index);
        string stamp = $"{CacheVersion}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        string cacheFile = Path.Combine(Program.DataFolder, "faces.txt");
        if (TryReadCache(cacheFile, stamp) is { } cached) return new FaceLibrary(data, cached, stamp);

        Dictionary<string, SpriteRef> sprites = BuildIndex(data);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var text = new StringBuilder(stamp).Append('\n');
            foreach ((string name, SpriteRef s) in sprites)
                text.Append(CultureInfo.InvariantCulture, $"{name}\t{s.Bin}\t{s.Offset}\t{s.Size}\t{s.Texture}\t{s.U0}\t{s.V0}\t{s.U1}\t{s.V1}\n");
            File.WriteAllText(cacheFile, text.ToString());
        }
        catch (IOException) { /* The cache is only an optimisation. */ }
        catch (UnauthorizedAccessException) { }
        return new FaceLibrary(data, sprites, stamp);
    }

    // Faces 7000+n are portraits the player added with the official face tool (신무장/신군주):
    // Documents\KoeiTecmo\San14\ADD_FACE\EDIT_KAO_{S,M}\EDIT_KAO_{S,M}_n.s14.zp.
    internal const int CustomFaceBase = 7000;
    private static readonly string CustomFaceDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KoeiTecmo", "San14", "ADD_FACE");

    private static string CustomFacePath(char size, int face) =>
        Path.Combine(CustomFaceDirectory, $"EDIT_KAO_{size}", $"EDIT_KAO_{size}_{face - CustomFaceBase}.s14.zp");

    // Some officers have a second portrait (younger or older) that the game switches by year
    // (여몽, 손권, 순욱, ...). Their record then holds an alternate face number that the game maps back
    // to the base face and draws as "face_NNNNb_*". Alternate -> base, from the 19-entry table in
    // SAN14CE_KO.exe (.data RVA 0x1A22E2C, 20-byte rows starting with base, alternate).
    private static readonly Dictionary<int, int> SecondPortraits = new()
    {
        [2179] = 17, [2180] = 117, [2181] = 30, [2182] = 91, [2183] = 63, [2184] = 64, [2185] = 37,
        [2186] = 25, [2187] = 14, [2188] = 49, [2189] = 44, [2190] = 60, [2191] = 84, [2192] = 9,
        [2193] = 1, [2194] = 2, [2195] = 45, [2196] = 36, [2197] = 7,
    };

    // Sprite names for a face are this prefix plus "_ctr" (icon) or "_a/_b/_c" (portrait layers).
    private static string SpritePrefix(int face) =>
        SecondPortraits.TryGetValue(face, out int baseFace) ? $"face_{baseFace:0000}b" : $"face_{face:0000}";

    // Icons are 484x512 busts; the custom S image uses the same frame on a 512x512 texture.
    internal Bitmap? Icon(int face) => face >= CustomFaceBase
        ? CustomFace(CustomFacePath('S', face), new RectangleF(0, 0, 484f / 512f, 1f))
        : Sprite($"{SpritePrefix(face)}_ctr");

    // The full portrait is three 636x900 layers (body, lower body, head) drawn on one canvas;
    // a custom M image holds the whole 636x900 portrait in the top-left of a 1024x1024 texture.
    internal Bitmap? Portrait(int face)
    {
        if (face >= CustomFaceBase)
            return CustomFace(CustomFacePath('M', face), new RectangleF(0, 0, 636f / 1024f, 900f / 1024f)) ?? Icon(face);
        string prefix = SpritePrefix(face);
        Bitmap?[] layers = new[] { "a", "b", "c" }.Select(layer => Sprite($"{prefix}_{layer}")).ToArray();
        try
        {
            Bitmap? first = layers.FirstOrDefault(l => l is not null);
            if (first is null) return Icon(face);
            var portrait = new Bitmap(first.Width, first.Height, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(portrait);
            foreach (Bitmap? layer in layers)
                if (layer is not null) g.DrawImage(layer, new Rectangle(0, 0, portrait.Width, portrait.Height));
            return portrait;
        }
        finally
        {
            foreach (Bitmap? layer in layers) layer?.Dispose();
        }
    }

    // Returns a new bitmap (the caller owns it) or null when the sprite is missing or unreadable.
    // Safe to call from background threads.
    internal Bitmap? Sprite(string name) => SpritePixels(name) is { } pixels ? ToBitmap(pixels) : null;

    // Officer list icon as BGRA pixels: the top square of the bust (the face), box-filtered to
    // size x size. Pure managed code: GDI+ serialises much of its work across threads, so decoding
    // hundreds of icons with it in the background would stall the UI's own drawing.
    internal byte[]? IconPixels(int face, int size)
    {
        Pixels? bust = face >= CustomFaceBase
            ? CustomFacePixels(CustomFacePath('S', face), new RectangleF(0, 0, 484f / 512f, 1f))
            : SpritePixels($"{SpritePrefix(face)}_ctr");
        return bust is { } pixels ? Downscale(pixels, size) : null;
    }

    // Decoded texture region, BGRA with a stride of Width * 4.
    private readonly record struct Pixels(byte[] Data, int Width, int Height);

    private Pixels? SpritePixels(string name)
    {
        if (!sprites.TryGetValue(name, out SpriteRef? sprite)) return null;
        try
        {
            return DecodeG1t(Texture(sprite), sprite.Texture, RectangleF.FromLTRB(sprite.U0, sprite.V0, sprite.U1, sprite.V1));
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { return null; }
    }

    // A sprite's texture container, from those read last when it is one of them (threads may share them).
    private byte[] Texture(SpriteRef sprite)
    {
        var at = (sprite.Bin, sprite.Offset);
        lock (recentTextures)
            for (LinkedListNode<((string, long) At, byte[] G1t)>? node = recentTextures.First; node is not null; node = node.Next)
                if (node.Value.At == at)
                {
                    recentTextures.Remove(node);
                    recentTextures.AddFirst(node);
                    return node.Value.G1t;
                }
        byte[] g1t = KtglArchive.Read(dataDirectory, new KtglArchive.Entry(KtglArchive.TextureType, 0, sprite.Bin, sprite.Offset, sprite.Size));
        lock (recentTextures)
        {
            recentTextures.AddFirst((at, g1t));
            while (recentTextures.Count > RecentTextures) recentTextures.RemoveLast();
        }
        return g1t;
    }

    private static Bitmap? CustomFace(string path, RectangleF uv) => CustomFacePixels(path, uv) is { } pixels ? ToBitmap(pixels) : null;

    private static Pixels? CustomFacePixels(string path, RectangleF uv)
    {
        try { return File.Exists(path) ? DecodeG1t(ReadZp(path), 0, uv) : null; }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { return null; }
    }

    private static Bitmap ToBitmap(Pixels pixels) => ToBitmap(pixels.Data, pixels.Width, pixels.Height);

    // BGRA pixels (a stride of width * 4) as a bitmap.
    internal static Bitmap ToBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width * 4, locked.Scan0 + y * locked.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(locked); }
        return bitmap;
    }

    // Area average over the top square, weighted by alpha so transparent edges do not darken.
    private static byte[] Downscale(Pixels source, int size)
    {
        int side = Math.Min(source.Width, source.Height);
        byte[] data = source.Data, result = new byte[size * size * 4];
        for (int oy = 0; oy < size; oy++)
        {
            int y0 = oy * side / size, y1 = Math.Max(y0 + 1, (oy + 1) * side / size);
            for (int ox = 0; ox < size; ox++)
            {
                int x0 = ox * side / size, x1 = Math.Max(x0 + 1, (ox + 1) * side / size);
                long b = 0, g = 0, r = 0, a = 0;
                for (int y = y0; y < y1; y++)
                    for (int i = (y * source.Width + x0) * 4, end = (y * source.Width + x1) * 4; i < end; i += 4)
                    {
                        int alpha = data[i + 3];
                        b += data[i] * alpha; g += data[i + 1] * alpha; r += data[i + 2] * alpha; a += alpha;
                    }
                int o = (oy * size + ox) * 4;
                if (a > 0) { result[o] = (byte)(b / a); result[o + 1] = (byte)(g / a); result[o + 2] = (byte)(r / a); }
                result[o + 3] = (byte)(a / ((y1 - y0) * (x1 - x0)));
            }
        }
        return result;
    }

    // One texture of a G1T container (KtglArchive.G1tTextures), its first mip level cut to the sprite's
    // rectangle. BC1 = 0x06/0x59, BC3 = 0x08/0x5B.
    private static Pixels? DecodeG1t(byte[] g1t, int texture, RectangleF uv)
    {
        KtglArchive.G1tTexture[] textures = KtglArchive.G1tTextures(g1t);
        if (texture < 0 || texture >= textures.Length) return null;
        (int format, int width, int height, int start) = (textures[texture].Format, textures[texture].Width, textures[texture].Height, textures[texture].DataOffset);
        bool bc1 = format is 0x06 or 0x59;
        if (!bc1 && format is not (0x08 or 0x5B)) return null;
        int level0 = (width + 3) / 4 * ((height + 3) / 4) * (bc1 ? 8 : 16);
        if (start + level0 > g1t.Length) return null;

        int x0 = (int)Math.Round(uv.Left * width), y0 = (int)Math.Round(uv.Top * height);
        int x1 = (int)Math.Round(uv.Right * width), y1 = (int)Math.Round(uv.Bottom * height);
        x0 = Math.Clamp(x0, 0, width - 1); x1 = Math.Clamp(x1, x0 + 1, width);
        y0 = Math.Clamp(y0, 0, height - 1); y1 = Math.Clamp(y1, y0 + 1, height);
        return DecodeBlocks(g1t.AsSpan(start, level0), width, new Rectangle(x0, y0, x1 - x0, y1 - y0), bc1);
    }

    private static Pixels DecodeBlocks(ReadOnlySpan<byte> data, int width, Rectangle crop, bool bc1)
    {
        int stride = crop.Width * 4;
        var pixels = new byte[stride * crop.Height];
        Span<uint> block = stackalloc uint[16];
        int blocksPerRow = (width + 3) / 4, blockSize = bc1 ? 8 : 16;
        for (int by = crop.Top / 4; by <= (crop.Bottom - 1) / 4; by++)
            for (int bx = crop.Left / 4; bx <= (crop.Right - 1) / 4; bx++)
            {
                ReadOnlySpan<byte> source = data.Slice((by * blocksPerRow + bx) * blockSize, blockSize);
                // Output is BGRA in memory (Format32bppArgb); with c0 <= c1 a BC1 block's fourth colour is transparent.
                if (bc1) Bc.Bc1(source, block, true);
                else Bc.Bc3(source, block);
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + i % 4 - crop.Left, y = by * 4 + i / 4 - crop.Top;
                    if (x < 0 || y < 0 || x >= crop.Width || y >= crop.Height) continue;
                    BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan(y * stride + x * 4), block[i]);
                }
            }
        return new Pixels(pixels, crop.Width, crop.Height);
    }

    // ".s14.zp": "zp1\0", total size, chunk size, chunk count, chunk sizes; chunks start at 0x800,
    // each is [u32 length][zlib stream] and the next one starts on a 0x80 boundary.
    private static byte[] ReadZp(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 0x10 || Encoding.ASCII.GetString(file, 0, 3) != "zp1") throw new InvalidDataException(L.Errors.NotZp);
        int total = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        var output = new MemoryStream(total);
        long pos = 0x800;
        for (int i = 0; i < count && pos + 4 <= file.Length; i++)
        {
            int stored = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16 + i * 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)pos));
            using (var zlib = new ZLibStream(new MemoryStream(file, (int)pos + 4, length), CompressionMode.Decompress))
                zlib.CopyTo(output);
            pos = (pos + stored + 0x7F) & ~0x7FL;
        }
        if (output.Length != total) throw new InvalidDataException(L.Errors.ZpBroken);
        return output.ToArray();
    }

    // ---- Index -------------------------------------------------------------------------------

    // Every named sprite in ScreenLayout (portraits and UI art). A sprite sheet follows the texture
    // it describes directly or after up to 15 bytes of alignment padding. When an update moved that
    // texture to another file (울지공's face_0992 did), the old copy still sits before the sheet but
    // is no longer indexed: its header names the texture's KTID, and the index says where it is now.
    private static Dictionary<string, SpriteRef> BuildIndex(string data)
    {
        List<KtglArchive.Entry> all = KtglArchive.ReadIndex(data, "ScreenLayout");
        List<KtglArchive.Entry> entries = all.Where(e => e.Type is KtglArchive.TextureType or KtglArchive.SpriteSheetType).ToList();
        var byPosition = entries.ToDictionary(e => (e.Bin, e.Offset));
        var sprites = new Dictionary<string, SpriteRef>();
        var streams = new Dictionary<string, FileStream>();
        var described = new HashSet<(string Bin, long Offset)>();
        FileStream Open(string bin) => streams.TryGetValue(bin, out FileStream? stream) ? stream : streams[bin] = File.OpenRead(Path.Combine(data, bin));

        void AddSprites(KtglArchive.Entry sheet, KtglArchive.Entry texture)
        {
            described.Add((sheet.Bin, sheet.Offset));
            byte[] meta = KtglArchive.Read(Open(sheet.Bin), sheet.Offset, sheet.Size);
            int count = BinaryPrimitives.ReadInt32LittleEndian(meta);
            for (int i = 0; i < count && 4 + (i + 1) * 104 <= meta.Length; i++)
            {
                var record = meta.AsSpan(4 + i * 104, 104);
                string name = Encoding.ASCII.GetString(record[..64]).Split('\0')[0];
                if (name.Length == 0) continue;
                int textureIndex = BinaryPrimitives.ReadInt32LittleEndian(record[64..]);
                float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
                for (int corner = 0; corner < 4; corner++)
                {
                    float u = BinaryPrimitives.ReadSingleLittleEndian(record[(68 + corner * 8)..]);
                    float v = BinaryPrimitives.ReadSingleLittleEndian(record[(72 + corner * 8)..]);
                    u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                }
                sprites.TryAdd(name, new SpriteRef(texture.Bin, texture.Offset, texture.Size, textureIndex, u0, v0, u1, v1));
            }
        }

        try
        {
            foreach (KtglArchive.Entry texture in entries.Where(e => e.Type == KtglArchive.TextureType).OrderBy(e => e.Bin).ThenBy(e => e.Offset))
            {
                KtglArchive.Entry sheet = default;
                bool found = false;
                for (long gap = 0; gap < 16 && !found; gap++)
                    found = byPosition.TryGetValue((texture.Bin, texture.Offset + texture.Size + gap), out sheet) && sheet.Type == KtglArchive.SpriteSheetType;
                if (found) AddSprites(sheet, texture);
            }

            // Sheets whose texture an update moved elsewhere (names found above are kept).
            var textureById = entries.Where(e => e.Type == KtglArchive.TextureType).GroupBy(e => e.Ktid).ToDictionary(g => g.Key, g => g.First());
            foreach (IGrouping<string, KtglArchive.Entry> bin in all.GroupBy(e => e.Bin).OrderBy(g => g.Key))
            {
                KtglArchive.Entry[] ordered = bin.OrderBy(e => e.Offset).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    KtglArchive.Entry sheet = ordered[i];
                    if (sheet.Type != KtglArchive.SpriteSheetType || described.Contains((sheet.Bin, sheet.Offset))) continue;
                    if (KtglArchive.UnindexedBefore(Open(sheet.Bin), ordered[i - 1].Offset + ordered[i - 1].Size, sheet.Offset) is { Type: KtglArchive.TextureType } old &&
                        textureById.TryGetValue(old.Ktid, out KtglArchive.Entry texture))
                        AddSprites(sheet, texture);
                }
            }
        }
        finally
        {
            foreach (FileStream stream in streams.Values) stream.Dispose();
        }
        return sprites;
    }

    private static Dictionary<string, SpriteRef>? TryReadCache(string path, string stamp)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != stamp) return null;
            var sprites = new Dictionary<string, SpriteRef>();
            foreach (string line in lines.Skip(1))
            {
                string[] f = line.Split('\t');
                if (f.Length != 9) continue;
                var ci = CultureInfo.InvariantCulture;
                sprites[f[0]] = new SpriteRef(f[1], long.Parse(f[2], ci), int.Parse(f[3], ci), int.Parse(f[4], ci),
                    float.Parse(f[5], ci), float.Parse(f[6], ci), float.Parse(f[7], ci), float.Parse(f[8], ci));
            }
            return sprites.Count > 0 ? sprites : null;
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { return null; }
    }
}

