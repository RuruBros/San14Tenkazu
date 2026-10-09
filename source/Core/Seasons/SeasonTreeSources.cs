using System.Security.Cryptography;

namespace San14Tenkazu.Seasons;

// Original game materials are read from the local installation, never bundled.
// Order and complete BC1/BC7 mip hashes form the protocol v5 CPU upload contract.
internal static class SeasonTreeSources
{
    internal readonly record struct Asset(uint Ktid,int Width,int Height,int Mips,int Format,int Bytes,int Pixels,string Hash,int Policy,int Green,int Autumn,int BranchRule,int[] Counts);
    internal static readonly Asset[] Assets=LoadCatalog();
    internal static readonly int ByteCount=Assets.Sum(a=>a.Bytes);
    internal static readonly int MaskByteCount=Assets.Sum(a=>a.Pixels*8);
    private static Asset[] LoadCatalog()
    {
        using var stream=typeof(SeasonTreeSources).Assembly.GetManifestResourceStream("Seasons.tree-catalog.json")
            ??throw new InvalidDataException("Tree catalog is missing");
        using var json=System.Text.Json.JsonDocument.Parse(stream);
        if(json.RootElement.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("Tree catalog version differs");
        var assets=json.RootElement.GetProperty("assets").EnumerateArray().Select(a=>new Asset(
            Convert.ToUInt32(a.GetProperty("ktid").GetString(),16),a.GetProperty("width").GetInt32(),a.GetProperty("height").GetInt32(),
            a.GetProperty("mips").GetInt32(),a.GetProperty("format").GetInt32(),a.GetProperty("bytes").GetInt32(),a.GetProperty("pixels").GetInt32(),
            a.GetProperty("hash").GetString()!,a.GetProperty("policy").GetInt32(),a.GetProperty("green").GetInt32(),a.GetProperty("autumn").GetInt32(),
            a.GetProperty("branchRule").GetInt32(),a.GetProperty("counts").EnumerateArray().Select(c=>c.GetInt32()).ToArray())).ToArray();
        if(assets.Length!=26||assets.Select(a=>a.Ktid).Distinct().Count()!=assets.Length)throw new InvalidDataException("Tree catalog is incomplete");
        foreach(var a in assets)
            if(a.Width is not (256 or 512)||a.Height is not (128 or 256 or 512)||a.Mips is <6 or >8||a.Format is not (71 or 98)||
                a.Policy is not (1 or 2)||a.BranchRule is <0 or >2||a.Green<0||a.Green>=assets.Length||a.Autumn<0||a.Autumn>=assets.Length||
                a.Hash.Length!=64||a.Counts.Length is <1 or >8||a.Counts.Any(c=>c<=0))throw new InvalidDataException("Tree catalog entry is invalid");
        return assets;
    }
    internal static byte[] Pack(Func<Asset,byte[]> read)
    {
        byte[] result=new byte[ByteCount];int at=0;
        foreach(var asset in Assets)
        {
            byte[] bytes=read(asset);
            if(bytes.Length!=asset.Bytes||Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()!=asset.Hash)
                throw new InvalidDataException($"Original season material differs: {asset.Ktid:X8}");
            bytes.CopyTo(result,at);at+=bytes.Length;
        }
        return result;
    }
    internal static void Validate(ReadOnlySpan<byte> data)
    {
        if(data.Length!=ByteCount)throw new InvalidDataException("Season source payload length differs");
        int at=0;foreach(var asset in Assets)
        {
            if(Convert.ToHexString(SHA256.HashData(data.Slice(at,asset.Bytes))).ToLowerInvariant()!=asset.Hash)
                throw new InvalidDataException($"Season source payload differs: {asset.Ktid:X8}");
            at+=asset.Bytes;
        }
    }
}
