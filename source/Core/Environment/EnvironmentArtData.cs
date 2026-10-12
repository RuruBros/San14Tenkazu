using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
namespace San14Tenkazu.EnvironmentRules;

// Generated project art only. The original game illustration is never redistributed.
internal static class EnvironmentArtData
{
    internal const int Bytes=32+4*1024*1024*4;
    internal const string Sha256="a70e5ae892d6ed89e8e72f1698f37cdf6ed1421ebe05622e97da8b7dc1977430";
    internal static byte[] Read()
    {
        using var resource=typeof(EnvironmentArtData).Assembly.GetManifestResourceStream("Environment.event-art-v2.bin.gz")??throw new InvalidDataException("Disaster art is missing");
        using var compressed=new GZipStream(resource,CompressionMode.Decompress);byte[] bytes=new byte[Bytes];compressed.ReadExactly(bytes);
        if(compressed.ReadByte()!=-1)throw new InvalidDataException("Disaster art length differs");Validate(bytes);return bytes;
    }
    internal static void Validate(ReadOnlySpan<byte> bytes)
    {
        uint[] expected=[0x31415244,2,4,1024,1024,864,648,0];
        if(bytes.Length!=Bytes||Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()!=Sha256)throw new InvalidDataException("Disaster art hash differs");
        for(int i=0;i<expected.Length;i++)if(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i*4)..])!=expected[i])throw new InvalidDataException("Disaster art layout differs");
    }
}
