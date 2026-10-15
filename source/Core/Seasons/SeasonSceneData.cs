using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using San14Tenkazu.EnvironmentRules;

namespace San14Tenkazu.Seasons;

// Private immutable upload, separate from the authenticated original BC7 data.
// No game assets or local diagnostic paths are embedded in the assembly.
internal static class SeasonSceneData
{
    internal const uint Magic=0x53475331;
    internal const int HeaderBytes=64,ClimateSize=256;
    internal const string TerrainShaderHash="08f898d88d527a70b8a4f0a8bf283e5c4abffcf7cc53d7992400005878a7d381";
    internal const string DistantTerrainShaderHash="7a99093d74cea74d44fccde0d69c8a9d7af4b1300a3a7f24b7195c784d9d93f9";
    internal static byte[] Pack(IReadOnlyList<IReadOnlyList<SeasonTreeMaskMip>> masks,byte[] terrainShader,byte[] distantTerrainShader,Action<string>? log=null)
    {
        using var timing=new SeasonTiming(log,"scene-pack");
        if(masks.Count!=SeasonTreeSources.Assets.Length)throw new InvalidDataException("Scene mask count differs");
        var climate=SeasonClimateAtlas.Build(SeasonClimate.Load(),ClimateSize,ClimateSize,new(0,0,204800,204800));
        timing.Step("climate-atlas-build");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(new byte[HeaderBytes]);
        foreach(var levels in masks)foreach(var mip in levels)foreach(float value in mip.LeafBranchCoverage)writer.Write(value);
        timing.Step("mask-serialization");
        int climateOffset=checked((int)stream.Position);
        foreach(float value in climate.Data.Span)writer.Write(value);
        timing.Step("climate-serialization");
        int shaderOffset=checked((int)stream.Position);writer.Write(terrainShader);writer.Write(distantTerrainShader);
        byte[] result=stream.ToArray();uint[] header=[Magic,3,(uint)result.Length,HeaderBytes,
            (uint)(climateOffset-HeaderBytes),(uint)climateOffset,(uint)(shaderOffset-climateOffset),ClimateSize,
            ClimateSize,12,(uint)masks.Count,(uint)shaderOffset,(uint)(terrainShader.Length+distantTerrainShader.Length),0,0,0];
        for(int i=0;i<header.Length;i++)BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i*4),header[i]);
        timing.Step("shader-and-header-pack");
        Validate(result);timing.Step("payload-validation");timing.Complete();return result;
    }
    internal static void Validate(ReadOnlySpan<byte> data)
    {
        if(data.Length<HeaderBytes)throw new InvalidDataException("Scene upload is truncated");
        uint[] h=new uint[16];for(int i=0;i<h.Length;i++)h[i]=BinaryPrimitives.ReadUInt32LittleEndian(data[(i*4)..]);
        int masks=SeasonTreeSources.MaskByteCount,climate=ClimateSize*ClimateSize*12*8;
        bool art=h[1] is 5 or 6&&h[14]==(long)h[11]+h[12]&&h[15]==EnvironmentArtData.Bytes;
        long end=(long)h[11]+h[12]+(art?h[15]:0);long pathBytes=art?data.Length-end:h[15];
        bool capture=(h[1]==4||h[1]==6&&art)&&h[13] is >=1 and <=2&&(art||h[14]==end)&&pathBytes is >=8 and <=1024&&pathBytes%2==0;
        if(h[0]!=Magic||(h[1]!=3&&!capture&&!art)||h[2]!=data.Length||h[3]!=HeaderBytes||h[4]!=masks||h[5]!=HeaderBytes+masks||
            h[6]!=climate||h[7]!=ClimateSize||h[8]!=ClimateSize||h[9]!=12||h[10]!=SeasonTreeSources.Assets.Length||
            (!capture&&(art?h[13]!=0:h.Skip(13).Any(v=>v!=0)))||h[1]==6&&!capture||h[11]!=HeaderBytes+masks+climate||h[12] is <32 or >262144||
            data.Length!=end+(capture?pathBytes:0))
            throw new InvalidDataException("Scene upload layout differs");
        if(capture)
        {
            string path=Encoding.Unicode.GetString(data.Slice((int)end,(int)pathBytes));
            if(path[^1]!='\0'||path[..^1].Contains('\0')||!Path.IsPathFullyQualified(path[..^1]))
                throw new InvalidDataException("Scene capture path differs");
        }
        if(art)EnvironmentArtData.Validate(data.Slice((int)h[14],(int)h[15]));
        var shaders=data.Slice((int)h[11],(int)h[12]);
        uint firstBytes=BinaryPrimitives.ReadUInt32LittleEndian(shaders[24..]);
        if(firstBytes<32||firstBytes>shaders.Length-32||
            Convert.ToHexString(SHA256.HashData(shaders[..(int)firstBytes])).ToLowerInvariant()!=TerrainShaderHash||
            Convert.ToHexString(SHA256.HashData(shaders[(int)firstBytes..])).ToLowerInvariant()!=DistantTerrainShaderHash)
            throw new InvalidDataException("Original detailed/distant terrain shader differs");
        for(int offset=HeaderBytes;offset<HeaderBytes+masks;offset+=8)
        {
            float leaf=BinaryPrimitives.ReadSingleLittleEndian(data[offset..]),branch=BinaryPrimitives.ReadSingleLittleEndian(data[(offset+4)..]);
            if(!float.IsFinite(leaf)||!float.IsFinite(branch)||leaf<0||branch<0||leaf+branch>1.000001f)
                throw new InvalidDataException("Scene coverage is invalid");
        }
        for(int offset=HeaderBytes+masks;offset<(int)h[11];offset+=8)
        {
            float temperature=BinaryPrimitives.ReadSingleLittleEndian(data[offset..]),rain=BinaryPrimitives.ReadSingleLittleEndian(data[(offset+4)..]);
            if(!float.IsFinite(temperature)||!float.IsFinite(rain)||temperature is <-60 or >60||rain is <0 or >3000)
                throw new InvalidDataException("Scene climate is invalid");
        }
    }
    internal static byte[] WithWeatherCapture(byte[] scene,string outputPath)
    {
        Validate(scene);uint version=BinaryPrimitives.ReadUInt32LittleEndian(scene.AsSpan(4));if(version is not (3 or 5))throw new InvalidDataException("Scene already requests capture");
        string path=Path.GetFullPath(outputPath);if(File.Exists(path))throw new IOException("Weather capture already exists");
        byte[] destination=Encoding.Unicode.GetBytes(path+'\0');
        byte[] result=new byte[checked(scene.Length+destination.Length)];scene.CopyTo(result,0);destination.CopyTo(result,scene.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),version==5?6u:4u);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8),(uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(52),2);
        if(version==3){BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56),(uint)scene.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(60),(uint)destination.Length);}
        Validate(result);return result;
    }
    internal static byte[] WithDisasterArt(byte[] scene)
    {
        Validate(scene);if(BinaryPrimitives.ReadUInt32LittleEndian(scene.AsSpan(4))!=3)throw new InvalidDataException("Disaster art requires an uncaptured scene");
        byte[] art=EnvironmentArtData.Read(),result=new byte[scene.Length+art.Length];scene.CopyTo(result,0);art.CopyTo(result,scene.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),5);BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8),(uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56),(uint)scene.Length);BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(60),(uint)art.Length);
        Validate(result);return result;
    }
}
