using System.Buffers.Binary;
using System.Security.Cryptography;
using San14Tenkazu.Seasons;
namespace San14Tenkazu.Tests;

public class SeasonTextureDataTests
{
    private static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    private static void Word(byte[] data,int index,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index*4,4),value);
    private static byte[] Texture(bool climate=false)
    {
        var data=new byte[148+(climate?12*8:4)];
        foreach(var (index,value) in new (int,uint)[]{(0,0x20534444),(1,124),(2,climate?0x100Fu:0x2100Fu),
            (3,1),(4,1),(5,climate?8u:4u),(7,1),(19,32),(20,4),(21,0x30315844),(27,climate?0x1008u:0x401008u),
            (32,climate?16u:91u),(33,3),(35,climate?12u:1u)})Word(data,index,value);
        if(!climate)data[151]=255;return data;
    }
    [Fact]
    public void OwnsVerifiedBytesAndRejectsHashMismatchAndForeignPart()
    {
        var input=Texture();var texture=SeasonTextureData.Parse(input,SeasonTextureRole.TerrainDiffuse,Hash(input));input[148]=123;
        Assert.Equal(0,texture.Data(texture.Parts[0])[0]);Assert.Equal(4,texture.PayloadBytes);
        Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(input,SeasonTextureRole.TerrainDiffuse,texture.Sha256));
        Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(input,SeasonTextureRole.TerrainDiffuse,""));
        Assert.Throws<ArgumentException>(()=>texture.Data(texture.Parts[0] with{Offset=0}));
    }
    [Theory]
    [InlineData(0,0u)] [InlineData(1,123u)] [InlineData(2,0u)] [InlineData(3,3u)] [InlineData(4,0xffffffffu)]
    [InlineData(5,99u)] [InlineData(6,1u)] [InlineData(7,14u)] [InlineData(8,1u)] [InlineData(19,31u)]
    [InlineData(20,0u)] [InlineData(21,0u)] [InlineData(22,32u)] [InlineData(27,0u)] [InlineData(28,0x200u)]
    [InlineData(32,87u)] [InlineData(33,4u)] [InlineData(34,4u)] [InlineData(35,6u)] [InlineData(36,2u)]
    public void MalformedHeaderIsRejectedBeforeGpuUpload(int index,uint value)
    {
        var data=Texture();Word(data,index,value);
        Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(data,SeasonTextureRole.TerrainDiffuse,Hash(data)));
    }
    [Fact]
    public void RoleAlphaAndExactPayloadLengthAreEnforced()
    {
        var data=Texture();data[151]=0;
        Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(data,SeasonTextureRole.TerrainDiffuse,Hash(data)));
        Assert.Equal(0,SeasonTextureData.Parse(data,SeasonTextureRole.TreeDiffuse,Hash(data)).Data(new(0,0,1,1,148,4,4))[3]);
        foreach(var bad in new[]{data[..^1],data.Concat(new byte[]{0}).ToArray(),new byte[147]})
            Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(bad,SeasonTextureRole.TreeDiffuse,Hash(bad)));
        Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(data,(SeasonTextureRole)99,Hash(data)));
    }
    [Theory]
    [InlineData(float.NaN,0f)] [InlineData(float.PositiveInfinity,0f)] [InlineData(61f,0f)]
    [InlineData(0f,-1f)] [InlineData(0f,3001f)]
    public void ClimateRejectsNonFiniteOrOutOfRangeTexels(float temperature,float rain)
    {
        var data=Texture(true);BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(148,4),temperature);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(152,4),rain);
        Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(data,SeasonTextureRole.Climate,Hash(data)));
    }
    [Fact]
    public void ClimateLayerOrderAndLinearFormatsAreExplicit()
    {
        var data=Texture(true);var climate=SeasonTextureData.Parse(data,SeasonTextureRole.Climate,Hash(data));
        Assert.Equal(12,climate.Layers);Assert.Equal(16,climate.Format);Assert.Equal(1,climate.Mips);
        Assert.Equal(236,climate.Parts[^1].Offset);
        data=Texture();Word(data,32,87);
        foreach(var role in new[]{SeasonTextureRole.TerrainNormal,SeasonTextureRole.TerrainRough})
            Assert.Equal(87,SeasonTextureData.Parse(data,role,Hash(data)).Format);
    }
}
