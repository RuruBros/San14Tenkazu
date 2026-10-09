using San14Tenkazu.Seasons;

namespace San14Tenkazu;

internal static class GameSeasonSources
{
    internal static byte[] Read(GameIdentity game)
    {
        string executable=GameMemory.ExecutablePath(game)??throw new IOException("Game installation is unavailable");
        return ReadDirectory(Path.GetDirectoryName(executable)!);
    }
    internal static byte[] ReadDirectory(string directory)
    {
        string data=Path.Combine(directory,"0002");
        var index=KtglArchive.ReadIndex(data,"MaterialEditor");
        return SeasonTreeSources.Pack(asset=>
        {
            var candidates=index.Where(e=>e.Ktid==asset.Ktid&&e.Type==KtglArchive.TextureType).ToArray();
            if(candidates.Length!=1)throw new InvalidDataException($"Original season material is ambiguous: {asset.Ktid:X8}");
            byte[] g1t=KtglArchive.Read(data,candidates[0]);var textures=KtglArchive.G1tTextures(g1t);
            if(textures.Length!=1)throw new InvalidDataException("Expected a single tree atlas");
            var t=textures[0];
            if(t.Format!=(asset.Format==71?0x59:0x5F)||t.Width!=asset.Width||t.Height!=asset.Height||t.Mips!=asset.Mips||t.DataEnd-t.DataOffset!=asset.Bytes)
                throw new InvalidDataException($"Original season texture descriptor differs: {asset.Ktid:X8}, format={t.Format:X2}, {t.Width}x{t.Height}, mips={t.Mips}, bytes={t.DataEnd-t.DataOffset}");
            return g1t.AsSpan(t.DataOffset,asset.Bytes).ToArray();
        });
    }
}
