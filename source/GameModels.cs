using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// The game's 3D field models (cities, gates, county seats, ports), rendered once into sprites for the 2D map.
// Read-only from the game's archives in 0002:
//   KIDSSystemResource: the object database (_DOK files of IDOK objects). A placement object (named, e.g.
//     "obj-mc01-l-city-a-nm-001") names a model object, which names the G1M model and a texture set; the set's
//     slot file maps texture slots to texture objects, which name G1T textures.
//   FieldEditor4: the G1M models.  MaterialEditor: their G1T textures.
// The G1M layout follows eArmada8's g1m tools and the Noesis G1M plugin. GpuModels performs pixel work
// on a background D3D11 device; the software rasterizer keeps the same output as the CPU fallback.
// Model units are game units (468 per map world unit); +Z is south, +Y up.
internal static class GameModels
{
    // Image: the rendered model (premultiplied BGRA). World: where it lies relative to the model's origin, in
    // map world units, so that it is placed at the origin's world position.
    internal sealed record Sprite(WMI.BitmapSource Image, W.Rect World);

    private const uint DokType = 0x20A6A0BB, G1mType = 0x563BDEF1, G1tType = 0xAFBEC60C, SlotFileType = 0x8E39AA37;
    private const uint PlacementType = 0xC4B9B28D, ModelType = 0xD40B3C8F, TextureSetType = 0x3059B9C3, TextureObjectType = 0xFF7DBFD4;
    private const uint PropName = 0xC69EB271, PropModel = 0x0B6E1578, PropG1m = 0x8AB68B3F, PropTextureSet = 0xF92C5190, PropSlots = 0x7A1E1EF8, PropG1t = 0x6C7321D2;
    internal const float GameUnitsPerWorldUnit = GameTerrain.UnitsPerHex;

    private sealed record DokObject(uint Id, uint Type, Dictionary<uint, uint[]> Props);

    // Everything the resolver needs, loaded once (a second or two).
    private sealed class Database
    {
        internal readonly string Data;
        internal readonly Dictionary<uint, List<KtglArchive.Entry>> Entries = [];
        internal readonly Dictionary<uint, DokObject> Objects = [];
        internal readonly Dictionary<string, uint> PlacementByName = [];

        internal Database(string gameDirectory)
        {
            Data = Path.Combine(gameDirectory, "0002");
            foreach (string archive in new[] { "KIDSSystemResource", "FieldEditor4", "MaterialEditor" })
                foreach (KtglArchive.Entry entry in KtglArchive.ReadIndex(Data, archive))
                {
                    if (!Entries.TryGetValue(entry.Ktid, out var list)) Entries[entry.Ktid] = list = [];
                    list.Add(entry);
                }
            // The object database exists in two generations; the newer one (bin22) matches the current models.
            var doks = Entries.Values.SelectMany(l => l).Where(e => e.Type == DokType && e.Bin.StartsWith("KIDSSystemResource", StringComparison.Ordinal))
                .OrderBy(e => e.Bin.EndsWith("bin22", StringComparison.Ordinal) ? 1 : 0);
            foreach (KtglArchive.Entry entry in doks)
                foreach (DokObject item in ParseDok(KtglArchive.Read(Data, entry)))
                    Objects[item.Id] = item;
            foreach (DokObject item in Objects.Values.Where(o => o.Type == PlacementType))
                if (item.Props.TryGetValue(PropName, out uint[]? chars)) PlacementByName[new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray())] = item.Id;
        }

        internal uint Prop(uint objectId, uint name) =>
            Objects.TryGetValue(objectId, out DokObject? item) && item.Props.TryGetValue(name, out uint[]? values) && values.Length > 0 ? values[0] : 0;

        internal byte[]? Read(uint ktid, uint type) =>
            Entries.TryGetValue(ktid, out var list) && list.FirstOrDefault(e => e.Type == type) is { Size: > 0 } entry ? KtglArchive.Read(Data, entry) : null;
    }

    private static readonly object gate = new();
    private static Database? database;
    private static string? databaseDirectory;

    private static Database Load(string gameDirectory)
    {
        lock (gate)
        {
            if (database is null || databaseDirectory != gameDirectory) (database, databaseDirectory) = (new Database(gameDirectory), gameDirectory);
            return database;
        }
    }

    // Asset preparation uses the same validated model/texture/UV resolver as the map renderer.
    // Original game bytes stay local; callers must not distribute these as CC0 resources.
    internal sealed record SeasonTextureBinding(int Slot,int TypeId,ushort[] RawWords);
    internal sealed record SeasonMaterial(int Index,int DiffuseSlot,SeasonTextureBinding[] Bindings);
    internal sealed record SeasonSubmesh(int Index,int VertexLayout,int MaterialIndex,int IndexBuffer,
        int Primitive,int IndexOffset,int IndexCount,int[] RawWords);
    internal sealed record SeasonMeshGroup(int Index,string RawName,uint[] RawHeader,int[] Submeshes);
    internal sealed class SeasonGeometryAudit
    {
        internal int Version;
        internal readonly List<SeasonMaterial> Materials=[];
        internal readonly List<SeasonSubmesh> Submeshes=[];
        internal readonly List<SeasonMeshGroup> Groups=[];
    }
    internal sealed record SeasonTreeSource(string Name, uint PlacementId, uint ModelId, uint ModelKtid,
        byte[] Model, Dictionary<uint, byte[]> Textures, List<Triangle> Triangles,uint[] TextureSlots,
        SeasonGeometryAudit Geometry);
    internal static IEnumerable<SeasonTreeSource> SeasonTrees(string gameDirectory)
    {
        Database db=Load(gameDirectory);
        foreach (var placement in db.PlacementByName.Where(p=>p.Key.Contains("tree",StringComparison.Ordinal)).OrderBy(p=>p.Key))
        {
            uint model=db.Prop(placement.Value,PropModel), modelKtid=db.Prop(model,PropG1m);
            byte[]? bytes=db.Read(modelKtid,G1mType);
            if(bytes is null) throw new InvalidDataException($"Tree model missing: {placement.Key}");
            uint[] slots=Slots(db,db.Prop(model,PropTextureSet));
            var originals=new Dictionary<uint,byte[]>();var decoded=new Dictionary<int,Texture?>();
            Texture? Diffuse(G1m.Material material)
            {
                int slot=material.DiffuseSlot;
                if(slot<0 || slot>=slots.Length) throw new InvalidDataException($"Tree diffuse slot missing: {placement.Key}");
                if(decoded.TryGetValue(slot,out var known))return known;
                uint ktid=slots[slot];byte[]? texture=db.Read(ktid,G1tType);
                if(texture is null)throw new InvalidDataException($"Tree texture missing: {placement.Key}/{ktid:X8}");
                originals[ktid]=texture;
                return decoded[slot]=Texture.Decode(texture) ?? throw new InvalidDataException($"Tree texture decode failed: {ktid:X8}");
            }
            var audit=new SeasonGeometryAudit();List<Triangle> triangles=G1m.Triangles(bytes,Diffuse,audit);
            // Preserve every declared binding, not only the diffuse textures used by the preview.
            foreach(var binding in audit.Materials.SelectMany(m=>m.Bindings))
            {
                if(binding.Slot<0 || binding.Slot>=slots.Length || slots[binding.Slot]==0)
                    throw new InvalidDataException($"Tree material binding is unresolved: {placement.Key}/{binding.Slot}");
                uint ktid=slots[binding.Slot];
                if(!originals.ContainsKey(ktid))originals[ktid]=db.Read(ktid,G1tType)
                    ?? throw new InvalidDataException($"Tree material texture missing: {placement.Key}/{ktid:X8}");
            }
            yield return new(placement.Key,placement.Value,model,modelKtid,bytes,originals,triangles,slots,audit);
        }
    }

    // IDOK objects: id, type and typed properties. Only the object types on the model path are kept.
    private static IEnumerable<DokObject> ParseDok(byte[] b)
    {
        static uint U(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
        int p = (int)U(b, 8);
        while (p + 24 <= b.Length)
        {
            if (b[p] != (byte)'I' || Encoding.ASCII.GetString(b, p, 8) != "IDOK0000")
            {
                p++;
                continue;
            }
            int size = (int)U(b, p + 8);
            uint id = U(b, p + 12), type = U(b, p + 16);
            int count = (int)U(b, p + 20);
            if (type is PlacementType or ModelType or TextureSetType or TextureObjectType)
            {
                var props = new Dictionary<uint, uint[]>();
                int descriptors = p + 24, values = descriptors + count * 12;
                for (int i = 0; i < count && values <= b.Length; i++)
                {
                    uint kind = U(b, descriptors + i * 12), name = U(b, descriptors + i * 12 + 8);
                    int n = (int)U(b, descriptors + i * 12 + 4), element = ElementSize(kind);
                    if (n < 0 || values + n * element > b.Length) break;
                    var array = new uint[n];
                    for (int k = 0; k < n; k++, values += element)
                        array[k] = element switch { 1 => b[values], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(values)), _ => U(b, values) };
                    props[name] = array;
                }
                yield return new DokObject(id, type, props);
            }
            p += Math.Max(size, 24);
        }
    }

    // Element sizes per property type (checked against the objects' sizes for the types on the model path).
    private static int ElementSize(uint kind) => kind switch { 0 or 1 or 2 or 10 or 12 => 1, 3 => 2, 9 => 8, _ => 4 };

    // Sprites rendered on earlier runs, by game data version: %LOCALAPPDATA%\San14Tenkazu\models\<stamp>.
    internal static Sprite? RenderCached(string gameDirectory, string placement, double pixelsPerUnit, float tilt, int yaw)
    {
        string folder = Path.Combine(Program.DataFolder, "models", Stamp(gameDirectory));
        string key = Path.Combine(folder, $"{placement}_{yaw}_{pixelsPerUnit:0}_{tilt:0}");
        try
        {
            if (File.Exists(key + ".png") && File.Exists(key + ".txt"))
            {
                double[] rect = File.ReadAllText(key + ".txt").Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                using var stream = File.OpenRead(key + ".png");
                var frame = WMI.BitmapFrame.Create(stream, WMI.BitmapCreateOptions.None, WMI.BitmapCacheOption.OnLoad);
                var image = new WMI.FormatConvertedBitmap(frame, WM.PixelFormats.Pbgra32, null, 0);
                image.Freeze();
                return new Sprite(image, new W.Rect(rect[0], rect[1], rect[2], rect[3]));
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException or NotSupportedException or UnauthorizedAccessException) { }
        Sprite? sprite = Render(gameDirectory, placement, pixelsPerUnit, tilt, yaw);
        if (sprite is null) return null;
        try
        {
            Directory.CreateDirectory(folder);
            var encoder = new WMI.PngBitmapEncoder();
            encoder.Frames.Add(WMI.BitmapFrame.Create(sprite.Image));
            using (var file = File.Create(key + ".png")) encoder.Save(file);
            W.Rect r = sprite.World;
            File.WriteAllText(key + ".txt", string.Join(" ", new[] { r.X, r.Y, r.Width, r.Height }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Only an optimisation. */ }
        return sprite;
    }

    // The game's model and texture archives' sizes and times: a game update renders the sprites again.
    internal static string Stamp(string gameDirectory)
    {
        long hash = 17;
        foreach (string archive in new[] { "KIDSSystemResource.rdb", "FieldEditor4.rdb", "MaterialEditor.rdb" })
        {
            var info = new FileInfo(Path.Combine(gameDirectory, "0002", archive));
            if (info.Exists) hash = hash * 31 + info.Length ^ info.LastWriteTimeUtc.Ticks;
        }
        return "v3_" + (hash & 0xFFFFFFFFFFFF).ToString("X");
    }

    // Renders a placement's model; null when the game's data is missing or does not parse.
    // pixelsPerUnit: image pixels per map world unit. tilt: 0 looks straight down, larger tilts the view to the
    // north so that walls and roofs show. yaw: the model's rotation about the vertical, in degrees.
    internal static Sprite? Render(string gameDirectory, string placement, double pixelsPerUnit, float tilt, float yaw = 0, bool useGpu = true)
    {
        try
        {
            Database db = Load(gameDirectory);
            if (!db.PlacementByName.TryGetValue(placement, out uint placementId)) return null;
            uint model = db.Prop(placementId, PropModel);
            byte[]? g1m = db.Read(db.Prop(model, PropG1m), G1mType);
            if (g1m is null) return null;
            uint[] slots = Slots(db, db.Prop(model, PropTextureSet));
            var textures = new Dictionary<uint, Texture?>();
            Texture? Diffuse(G1m.Material material)
            {
                int slot = material.DiffuseSlot;
                if (slot < 0 || slot >= slots.Length) return null;
                uint ktid = slots[slot];
                if (textures.TryGetValue(ktid, out Texture? known)) return known;
                byte[]? g1t = db.Read(ktid, G1tType);
                return textures[ktid] = g1t is null ? null : Texture.Decode(g1t);
            }
            List<Triangle> triangles = G1m.Triangles(g1m, Diffuse);
            return (useGpu ? GpuModels.TryRender(triangles, pixelsPerUnit, tilt, yaw) : null)
                ?? Rasterize(triangles, pixelsPerUnit, tilt, yaw);
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex))
        {
            return null;
        }
    }

    private static uint[] Slots(Database db, uint textureSet)
    {
        byte[]? file = db.Read(db.Prop(textureSet, PropSlots), SlotFileType);
        if (file is null) return [];
        int pairs = file.Length / 8;
        var slots = new uint[pairs == 0 ? 0 : Enumerable.Range(0, pairs).Max(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(i * 8))) + 1];
        for (int i = 0; i < pairs; i++)
            slots[BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(i * 8))] = db.Prop(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(i * 8 + 4)), PropG1t);
        return slots;
    }

    internal sealed record Triangle(Vector3 A, Vector3 B, Vector3 C, Vector3 NA, Vector3 NB, Vector3 NC, Vector2 UA, Vector2 UB, Vector2 UC,
        Texture? Texture,int MaterialIndex=-1,int SubmeshIndex=-1,int DiffuseSlot=-1);

    // A decoded texture, ARGB.
    internal sealed record Texture(int Width, int Height, uint[] Pixels)
    {
        internal static Texture? Decode(byte[] g1t)
        {
            KtglArchive.G1tTexture[] list = KtglArchive.G1tTextures(g1t);
            if (list.Length == 0) return null;
            KtglArchive.G1tTexture t = list[0];
            int blockBytes = t.Format switch { 0x59 => 8, 0x5B or 0x5F => 16, _ => 0 };
            if (blockBytes == 0) return null;
            var pixels = new uint[t.Width * t.Height];
            int columns = Math.Max(1, (t.Width + 3) / 4), rows = Math.Max(1, (t.Height + 3) / 4);
            Span<uint> block = stackalloc uint[16];
            for (int by = 0; by < rows; by++)
                for (int bx = 0; bx < columns; bx++)
                {
                    int at = t.DataOffset + (by * columns + bx) * blockBytes;
                    if (at + blockBytes > g1t.Length) continue;
                    ReadOnlySpan<byte> source = g1t.AsSpan(at, blockBytes);
                    switch (t.Format)
                    {
                        case 0x59: Bc.Bc1(source, block, true); break;
                        case 0x5B: Bc.Bc3(source, block); break;
                        default: Bc.Bc7(source, block); break;
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                        if (x < t.Width && y < t.Height) pixels[y * t.Width + x] = block[i];
                    }
                }
            return new Texture(t.Width, t.Height, pixels);
        }

        internal uint Sample(Vector2 uv)
        {
            float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
            return Pixels[Math.Clamp((int)(v * Height), 0, Height - 1) * Width + Math.Clamp((int)(u * Width), 0, Width - 1)];
        }
    }

    // Oblique projection: the ground plane stays exactly as on the top-down map and heights rise towards the
    // north by tan(tilt), as if seen from the south. 2x supersampled and filtered down. Light from the north-west,
    // as on the base map.
    private static Sprite? Rasterize(List<Triangle> triangles, double pixelsPerUnit, float tilt, float yaw)
    {
        if (triangles.Count == 0) return null;
        float a = tilt * MathF.PI / 180, cos = MathF.Cos(a), sin = MathF.Sin(a), lift = MathF.Tan(a);
        // Positive yaw turns the model counter-clockwise as seen from above (+X east, +Z south).
        Matrix4x4 turn = Matrix4x4.CreateRotationY(yaw * MathF.PI / 180);
        // Screen x = X, y (down) = Z - Y tan, depth (towards the viewer) = Y cos + Z sin.
        Vector3 Project(Vector3 p)
        {
            p = Vector3.Transform(p, turn);
            return new Vector3(p.X, p.Z - p.Y * lift, p.Y * cos + p.Z * sin);
        }
        var light = Vector3.Normalize(new Vector3(-0.48f, 0.66f, -0.58f));
        var projected = new Vector3[triangles.Count * 3];
        for (int i = 0; i < triangles.Count; i++)
        {
            projected[i * 3] = Project(triangles[i].A);
            projected[i * 3 + 1] = Project(triangles[i].B);
            projected[i * 3 + 2] = Project(triangles[i].C);
        }
        float minX = projected.Min(p => p.X), maxX = projected.Max(p => p.X), minY = projected.Min(p => p.Y), maxY = projected.Max(p => p.Y);
        const int ss = 2;
        float scale = (float)(pixelsPerUnit / GameUnitsPerWorldUnit) * ss;
        int width = Math.Max(ss, (int)MathF.Ceiling((maxX - minX) * scale) + ss * 2);
        int height = Math.Max(ss, (int)MathF.Ceiling((maxY - minY) * scale) + ss * 2);
        width += width % ss;
        height += height % ss;
        if ((long)width * height > 24_000_000) return null;
        var depth = new float[width * height];
        Array.Fill(depth, float.NegativeInfinity);
        var colour = new uint[width * height];
        Span<Vector3> s = stackalloc Vector3[3];
        for (int i = 0; i < triangles.Count; i++)
        {
            Triangle t = triangles[i];
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = projected[i * 3 + k];
                s[k] = new Vector3((p.X - minX) * scale + ss, (p.Y - minY) * scale + ss, p.Z);
            }
            float area = (s[1].X - s[0].X) * (s[2].Y - s[0].Y) - (s[2].X - s[0].X) * (s[1].Y - s[0].Y);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].X, MathF.Min(s[1].X, s[2].X))));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(s[0].X, MathF.Max(s[1].X, s[2].X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(s[0].Y, MathF.Min(s[1].Y, s[2].Y))));
            int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(s[0].Y, MathF.Max(s[1].Y, s[2].Y))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((s[1].X - px) * (s[2].Y - py) - (s[2].X - px) * (s[1].Y - py)) / area;
                    float w1 = ((s[2].X - px) * (s[0].Y - py) - (s[0].X - px) * (s[2].Y - py)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * s[0].Z + w1 * s[1].Z + w2 * s[2].Z;
                    int o = y * width + x;
                    if (z <= depth[o]) continue;
                    float r = 0.75f, g = 0.72f, b = 0.68f;
                    if (t.Texture is { } texture)
                    {
                        uint c = texture.Sample(t.UA * w0 + t.UB * w1 + t.UC * w2);
                        if ((c >> 24) < 128) continue;
                        // Lifted a little, like the base map's colours, so the models match the ground's brightness.
                        r = MathF.Pow((c >> 16 & 255) / 255f, 0.78f);
                        g = MathF.Pow((c >> 8 & 255) / 255f, 0.78f);
                        b = MathF.Pow((c & 255) / 255f, 0.78f);
                    }
                    Vector3 n = Vector3.Normalize(t.NA * w0 + t.NB * w1 + t.NC * w2);
                    float l = 0.45f + 0.75f * MathF.Abs(Vector3.Dot(n, light));
                    depth[o] = z;
                    colour[o] = 0xFF000000u | (uint)(Byte(r * l) << 16 | Byte(g * l) << 8 | Byte(b * l));
                }
        }
        // Down to 1x, premultiplied.
        int w = width / ss, h = height / ss;
        var output = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int ra = 0, rr = 0, rg = 0, rb = 0;
                for (int j = 0; j < ss; j++)
                    for (int i = 0; i < ss; i++)
                    {
                        uint c = colour[(y * ss + j) * width + x * ss + i];
                        int alpha = (int)(c >> 24);
                        ra += alpha;
                        rr += (int)(c >> 16 & 255) * alpha / 255;
                        rg += (int)(c >> 8 & 255) * alpha / 255;
                        rb += (int)(c & 255) * alpha / 255;
                    }
                int n = ss * ss;
                output[y * w + x] = ra / n << 24 | rr / n << 16 | rg / n << 8 | rb / n;
            }
        var image = WMI.BitmapSource.Create(w, h, 96, 96, WM.PixelFormats.Pbgra32, null, output, w * 4);
        image.Freeze();
        float unit = GameUnitsPerWorldUnit;
        return new Sprite(image, new W.Rect((minX - 1f / scale * ss) / unit, (minY - 1f / scale * ss) / unit, w / pixelsPerUnit, h / pixelsPerUnit));
    }

    private static int Byte(float v) => Math.Clamp((int)(v * 255 + 0.5f), 0, 255);

    // ---- G1M ------------------------------------------------------------------------------------------

    private static class G1m
    {
        internal sealed record Material(int DiffuseSlot);
        private sealed record Attribute(int Buffer, int Offset, int DataType, int Semantic, int Layer);
        private sealed record Layout(int[] Buffers, Attribute[] Attributes);
        private sealed record VertexBuffer(int Stride, int Count, int DataOffset);
        private sealed record IndexBuffer(int Count, int Type, int DataOffset);
        private sealed record Submesh(int VertexLayout, int Material, int IndexBuffer, int Primitive, int IndexOffset, int IndexCount);

        private static int I32(byte[] b, int p) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        private static int U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
        private static float F32(byte[] b, int p) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p));
        private static float Half(byte[] b, int p) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)));

        // Existing selection from the first 0x10009 record, or all submeshes when no list exists.
        // Exporting this list does not establish the running game's Pass/LOD selection.
        internal static List<Triangle> Triangles(byte[] b, Func<Material, Texture?> diffuse,SeasonGeometryAudit? audit=null)
        {
            int first = I32(b, 12), chunks = I32(b, 20), at = first, geometry = -1, version = 0;
            for (int i = 0; i < chunks && at + 12 <= b.Length; i++)
            {
                if (Encoding.ASCII.GetString(b, at, 4) == "GM1G")
                {
                    geometry = at;
                    char[] digits = Encoding.ASCII.GetString(b, at + 4, 4).ToCharArray();
                    Array.Reverse(digits);
                    version = int.TryParse(new string(digits), System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : 0;
                }
                int size = I32(b, at + 8);
                if (size <= 0) break;
                at += size;
            }
            if (geometry < 0) return [];
            if(audit is not null)audit.Version=version;

            var materials = new List<Material>();
            var buffers = new List<VertexBuffer>();
            var layouts = new List<Layout>();
            var indices = new List<IndexBuffer>();
            var submeshes = new List<Submesh>();
            var lod = new List<int>();
            int sections = I32(b, geometry + 44), p = geometry + 48;
            for (int s = 0; s < sections; s++)
            {
                int type = I32(b, p), size = I32(b, p + 4), q = p + 8, n = I32(b, q);
                q += 4;
                switch (type)
                {
                    case 0x10002:
                        for (int i = 0; i < n; i++)
                        {
                            int textures = I32(b, q + 4);
                            q += 16;
                            int slot = -1;
                            var bindings=new List<SeasonTextureBinding>();
                            for (int j = 0; j < textures; j++, q += 12)
                            {
                                if (U16(b, q + 4) == 1 && slot < 0) slot = U16(b, q);   // type 1: diffuse
                                if(audit is not null)bindings.Add(new(U16(b,q),U16(b,q+4),
                                    Enumerable.Range(0,6).Select(k=>(ushort)U16(b,q+k*2)).ToArray()));
                            }
                            materials.Add(new Material(slot));
                            audit?.Materials.Add(new(i,slot,bindings.ToArray()));
                        }
                        break;
                    case 0x10004:
                        for (int i = 0; i < n; i++)
                        {
                            int stride = I32(b, q + 4), count = I32(b, q + 8);
                            q += version > 0x30 ? 16 : 12;
                            buffers.Add(new VertexBuffer(stride, count, q));
                            q += stride * count;
                        }
                        break;
                    case 0x10005:
                        for (int i = 0; i < n; i++)
                        {
                            int count = I32(b, q);
                            q += 4;
                            var list = new int[count];
                            for (int j = 0; j < count; j++, q += 4) list[j] = I32(b, q);
                            int attributes = I32(b, q);
                            q += 4;
                            var attrs = new Attribute[attributes];
                            for (int j = 0; j < attributes; j++, q += 8) attrs[j] = new Attribute(U16(b, q), U16(b, q + 2), b[q + 4], b[q + 6], b[q + 7]);
                            layouts.Add(new Layout(list, attrs));
                        }
                        break;
                    case 0x10007:
                        for (int i = 0; i < n; i++)
                        {
                            int count = I32(b, q), kind = I32(b, q + 4);
                            q += version > 0x30 ? 12 : 8;
                            indices.Add(new IndexBuffer(count, kind, q));
                            q = (q + count * (kind / 8) + 3) & ~3;
                        }
                        break;
                    case 0x10008:
                        for (int i = 0; i < n; i++, q += 56)
                        {
                            submeshes.Add(new Submesh(I32(b, q + 4), I32(b, q + 24), I32(b, q + 28), I32(b, q + 36), I32(b, q + 48), I32(b, q + 52)));
                            audit?.Submeshes.Add(new(i,I32(b,q+4),I32(b,q+24),I32(b,q+28),I32(b,q+36),I32(b,q+48),I32(b,q+52),
                                Enumerable.Range(0,14).Select(k=>I32(b,q+k*4)).ToArray()));
                        }
                        break;
                    case 0x10009 when n > 0:
                        q += 4;
                        if (version > 0x30) q += 8;
                        int c1 = I32(b, q), c2 = I32(b, q + 4);
                        q += 8;
                        if (version > 0x30) q += 16;
                        for (int k = 0; k < c1 + c2; k++)
                        {
                            string rawName=audit is null?"":Encoding.ASCII.GetString(b,q,16).TrimEnd('\0');
                            uint[] rawHeader=audit is null?[]:Enumerable.Range(0,6).Select(i=>(uint)I32(b,q+i*4)).ToArray();
                            q += 16 + 4 + 4;
                            int count = I32(b, q);
                            q += 4;
                            var group=audit is null?[]:new int[count];
                            for (int j = 0; j < count; j++, q += 4){int value=I32(b,q);if(audit is not null)group[j]=value;lod.Add(value);}
                            audit?.Groups.Add(new(k,rawName,rawHeader,group));
                        }
                        break;
                }
                p += size;
            }

            (Attribute Attr, int Buffer)? Find(int layoutIndex, int semantic, int layer = 0)
            {
                if (layoutIndex<0 || layoutIndex>=layouts.Count) return null;
                Layout layout=layouts[layoutIndex];
                foreach (Attribute attribute in layout.Attributes)
                    if (attribute.Semantic==semantic && attribute.Layer==layer && attribute.Buffer<layout.Buffers.Length)
                        return (attribute,layout.Buffers[attribute.Buffer]);
                return null;
            }
            Vector3 Read3(int buffer, Attribute attribute, int vertex)
            {
                VertexBuffer vb = buffers[buffer];
                int o = vb.DataOffset + vertex * vb.Stride + attribute.Offset;
                return attribute.DataType switch
                {
                    0x02 or 0x03 => new Vector3(F32(b, o), F32(b, o + 4), F32(b, o + 8)),
                    0x0B => new Vector3(Half(b, o), Half(b, o + 2), Half(b, o + 4)),
                    _ => new Vector3(0, 1, 0)
                };
            }
            Vector2 Read2(int buffer, Attribute attribute, int vertex)
            {
                VertexBuffer vb = buffers[buffer];
                int o = vb.DataOffset + vertex * vb.Stride + attribute.Offset;
                return attribute.DataType switch
                {
                    0x01 or 0x02 or 0x03 => new Vector2(F32(b, o), F32(b, o + 4)),
                    0x0A or 0x0B => new Vector2(Half(b, o), Half(b, o + 2)),
                    _ => Vector2.Zero
                };
            }

            var triangles = new List<Triangle>();
            IEnumerable<int> used = lod.Count > 0 ? lod.Distinct() : Enumerable.Range(0, submeshes.Count);
            foreach (int index in used)
            {
                if (index < 0 || index >= submeshes.Count) continue;
                Submesh sm = submeshes[index];
                if (sm.Primitive != 3 || sm.IndexBuffer >= indices.Count || sm.VertexLayout >= layouts.Count) continue;
                var position = Find(sm.VertexLayout, 0);
                if (position is null) continue;
                var normal = Find(sm.VertexLayout, 3);
                var uv = Find(sm.VertexLayout, 5);
                Texture? texture = sm.Material >= 0 && sm.Material < materials.Count ? diffuse(materials[sm.Material]) : null;
                IndexBuffer ib = indices[sm.IndexBuffer];
                int Vertex(int i) => ib.Type switch
                {
                    8 => b[ib.DataOffset + i],
                    16 => U16(b, ib.DataOffset + i * 2),
                    _ => I32(b, ib.DataOffset + i * 4)
                };
                for (int i = 0; i + 2 < sm.IndexCount; i += 3)
                {
                    int va = Vertex(sm.IndexOffset + i), vb = Vertex(sm.IndexOffset + i + 1), vc = Vertex(sm.IndexOffset + i + 2);
                    Vector3 N(int v) => normal is { } nn ? Vector3.Normalize(Read3(nn.Buffer, nn.Attr, v) + new Vector3(1e-6f)) : Vector3.UnitY;
                    Vector2 T(int v) => uv is { } tt ? Read2(tt.Buffer, tt.Attr, v) : Vector2.Zero;
                    triangles.Add(new Triangle(Read3(position.Value.Buffer, position.Value.Attr, va), Read3(position.Value.Buffer, position.Value.Attr, vb),
                        Read3(position.Value.Buffer, position.Value.Attr, vc), N(va), N(vb), N(vc), T(va), T(vb), T(vc), texture,
                        sm.Material,index,sm.Material>=0 && sm.Material<materials.Count?materials[sm.Material].DiffuseSlot:-1));
                }
            }
            return triangles;
        }
    }
}
