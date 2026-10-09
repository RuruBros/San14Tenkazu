namespace San14Tenkazu;

// The GPU needs the game's compressed bytes only until CreateTexture2D finishes. CPU tiles keep
// their own lazy GroundDetail data when required, including after a renderer/device fallback.
internal sealed class GroundGpuData : IDisposable
{
    internal NativePixelBuffer MaterialMap { get; }
    internal NativePixelBuffer Albedo { get; }
    internal NativePixelBuffer? Normals { get; }
    internal int LayerSize { get; }
    internal int LayerCount { get; }
    internal int MipCount { get; }

    private GroundGpuData(NativePixelBuffer materials, NativePixelBuffer albedo, NativePixelBuffer? normals,
        int size, int count, int mips)
    {
        MaterialMap = materials; Albedo = albedo; Normals = normals;
        LayerSize = size; LayerCount = count; MipCount = mips;
    }

    internal static GroundGpuData? Load()
    {
        NativePixelBuffer? materials = null, albedo = null, normals = null;
        try
        {
            if (FaceLibrary.FindGameDirectory(null) is not { } directory) return null;
            string data = Path.Combine(directory, "0002");
            var entries = KtglArchive.ReadIndex(data, "FieldEditor4");
            foreach (var entry in entries.Where(e => e.Type == KtglArchive.TextureType && e.Size > 20_000_000)
                         .OrderByDescending(e => e.Ktid == GameTerrain.FieldSetKtid))
            {
                using var g1t = NativePixelBuffer.ReadArchive(data, entry);
                var map = KtglArchive.G1tTextures(g1t.Bytes).FirstOrDefault(t => t.Format == 0x01 &&
                    t.Width == GroundDetail.MaterialMapSize && t.Height == GroundDetail.MaterialMapSize);
                int bytes = GroundDetail.MaterialMapSize * GroundDetail.MaterialMapSize * 4;
                if (map.DataOffset <= 0 || (long)map.DataOffset + bytes > g1t.Length) continue;
                materials = Copy(g1t, map.DataOffset, bytes);
                break;
            }
            if (materials is null) return null;
            foreach (var entry in entries.Where(e => e.Ktid is GroundDetail.ArrayKtid or GroundDetail.OlderArrayKtid)
                         .OrderByDescending(e => e.Ktid == GroundDetail.ArrayKtid))
            {
                using var g1t = NativePixelBuffer.ReadArchive(data, entry);
                var textures = KtglArchive.G1tTextures(g1t.Bytes);
                if (textures.Length == 0 || textures[0] is not { Format: 0x5B, Width: 1024, Height: 1024 } layers) continue;
                long chain = 0;
                for (int mip = 0, size = layers.Width; mip < layers.Mips; mip++, size = Math.Max(1, size / 2)) chain += GroundDetail.LevelBytes(size);
                int count = (int)((layers.DataEnd - layers.DataOffset) / chain);
                if (count < GroundDetail.Materials.Max(m => m.Layer) + 1) continue;
                int bytes = checked((int)(chain * count));
                albedo = Copy(g1t, layers.DataOffset, bytes);
                if (textures.Length > 1 && textures[1] is { Format: 0x5B, Width: 1024, Height: 1024 } n &&
                    n.Mips == layers.Mips && (long)n.DataOffset + bytes <= g1t.Length)
                    normals = Copy(g1t, n.DataOffset, bytes);
                var result = new GroundGpuData(materials, albedo, normals, layers.Width, count, layers.Mips);
                materials = albedo = normals = null; // ownership moves to result
                return result;
            }
            return null;
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { return null; }
        finally { materials?.Dispose(); albedo?.Dispose(); normals?.Dispose(); }
    }

    private static NativePixelBuffer Copy(NativePixelBuffer source, int offset, int length)
    {
        var bytes = source.Bytes.Slice(offset, length);
        var copy = new NativePixelBuffer(length);
        try { bytes.CopyTo(copy.Bytes); return copy; }
        catch { copy.Dispose(); throw; }
    }

    public void Dispose() { MaterialMap.Dispose(); Albedo.Dispose(); Normals?.Dispose(); }
}
