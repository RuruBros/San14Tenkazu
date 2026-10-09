using System.Buffers.Binary;
using System.Security.Cryptography;
using San14Tenkazu.Seasons;

namespace San14Tenkazu;

internal static class GameSeasonScene
{
    internal static byte[] Read(GameIdentity game)
    {
        string executable=GameMemory.ExecutablePath(game)??throw new IOException("Game installation is unavailable");
        return ReadDirectory(Path.GetDirectoryName(executable)!);
    }
    internal static byte[] ReadDirectory(string directory)
    {
        string data=Path.Combine(directory,"0002");
        var entry=KtglArchive.ReadIndex(data,"system").Single(e=>e.Ktid==0xDC52FD1C);
        byte[] container=KtglArchive.Read(data,entry);byte[]? shader=null,distant=null;
        for(int at=0;at<=container.Length-32;at++)
        {
            if(!container.AsSpan(at,4).SequenceEqual("DXBC"u8))continue;
            uint length=BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(at+24));
            if(length<32||length>262144||length>container.Length-at)continue;
            var candidate=container.AsSpan(at,(int)length);
            string hash=Convert.ToHexString(SHA256.HashData(candidate)).ToLowerInvariant();
            if(hash==SeasonSceneData.TerrainShaderHash)
            {if(shader is not null)throw new InvalidDataException("Duplicate terrain shader source");shader=candidate.ToArray();}
            if(hash==SeasonSceneData.DistantTerrainShaderHash)
            {if(distant is not null)throw new InvalidDataException("Duplicate distant terrain shader source");distant=candidate.ToArray();}
        }
        if(shader is null||distant is null)throw new InvalidDataException("Authenticated terrain shader is missing from the installation");
        return SeasonSceneData.Pack(GameSeasonMasks.ReadDirectory(directory).Select(m=>m.Mips).ToArray(),shader,distant);
    }
}
