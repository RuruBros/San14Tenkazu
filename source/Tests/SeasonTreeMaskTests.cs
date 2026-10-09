using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonTreeMaskTests
{
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Diffuse(IReadOnlyList<SeasonTreeMaskMip> mips)
    {
        var data=new byte[148+mips.Sum(m=>m.Diffuse.Bgra.Length)];var first=mips[0].Diffuse;
        void Word(int index,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index*4,4),value);
        foreach(var (index,value) in new (int,uint)[]{(0,0x20534444),(1,124),(2,0x2100F),(3,(uint)first.Height),(4,(uint)first.Width),
            (5,(uint)first.Width*4),(7,(uint)mips.Count),(19,32),(20,4),(21,0x30315844),(27,0x401008),(32,91),(33,3),(35,1)})Word(index,value);
        int offset=148;foreach(var mip in mips){mip.Diffuse.Bgra.CopyTo(data,offset);offset+=mip.Diffuse.Bgra.Length;}return data;
    }
    private static IReadOnlyList<SeasonTreeMaskMip> Fixture()=>SeasonTreeMaskProcessor.Build(
        [0,0,0,255,0,0,0,1],2,1,[0,1,1,0]);

    [Fact]
    public void IndependentNormalizedWeightSamplingLosesOpaqueBranchCoverage()
    {
        var mips=Fixture();double naive=(1+1/255.0)/2*0.5;
        double premultiplied=(mips[0].LeafBranchCoverage[1]+mips[0].LeafBranchCoverage[3])/2;
        Assert.Equal(0.5,premultiplied);Assert.True(premultiplied-naive>0.249);
        Assert.Equal(0.5,mips[1].LeafBranchCoverage[1],6);
        Assert.Equal(128,mips[1].Diffuse.Bgra[3]);
    }
    [Fact]
    public void CompleteMipsPairWithOriginalDiffuseAlphaIncludingQuantization()
    {
        var random=new Random(14);var source=new byte[16*4*4];var weights=new float[16*4*2];
        for(int i=0;i<64;i++)
        {
            source[i*4+3]=(byte)random.Next(256);
            if(source[i*4+3]>0){weights[i*2]=(float)random.NextDouble();weights[i*2+1]=1-weights[i*2];}
        }
        var mips=SeasonTreeMaskProcessor.Build(source,16,4,weights);Assert.Equal(5,mips.Count);
        Assert.Equal(source,mips[0].Diffuse.Bgra);
        foreach(var mip in mips)for(int pixel=0;pixel<mip.Diffuse.Width*mip.Diffuse.Height;pixel++)
        {
            float leaf=mip.LeafBranchCoverage[pixel*2],branch=mip.LeafBranchCoverage[pixel*2+1];
            Assert.InRange(leaf,0,1);Assert.InRange(branch,0,1);
            Assert.InRange(Math.Abs(leaf+branch-mip.Diffuse.Bgra[pixel*4+3]/255f),0,1e-7f);
        }
        var coverage=SeasonTreeMaskProcessor.WriteDds(mips);var diffuse=Diffuse(mips);
        var parsed=SeasonTextureData.Parse(coverage,SeasonTextureRole.TreeCoverage,Hash(coverage));
        parsed.ValidateCoverageSource(SeasonTextureData.Parse(diffuse,SeasonTextureRole.TreeDiffuse,Hash(diffuse)));
        Assert.Equal(16,parsed.Format);Assert.Equal(1,parsed.Layers);Assert.Equal(5,parsed.Mips);
    }
    [Fact]
    public void TransparentAtlasHasZeroCoverageAtEveryMipAndOwnsInputs()
    {
        var pixels=new byte[8*2*4];var weights=new float[8*2*2];
        var mips=SeasonTreeMaskProcessor.Build(pixels,8,2,weights);pixels[3]=255;weights[0]=1;
        foreach(var mip in mips)Assert.All(mip.LeafBranchCoverage,c=>Assert.Equal(0,c));
        Assert.Equal(0,mips[0].Diffuse.Bgra[3]);
    }
    [Fact]
    public void MalformedDimensionsAndMaskLabelsAreRejected()
    {
        foreach(var shape in new[]{(0,1),(3,1),(4096,1),(int.MaxValue,int.MaxValue)})
            Assert.Throws<ArgumentException>(()=>SeasonTreeMaskProcessor.Build([],shape.Item1,shape.Item2,[]));
        Assert.Throws<ArgumentException>(()=>SeasonTreeMaskProcessor.Build(new byte[4],1,1,new float[1]));
        foreach(var mask in new[]{new float[]{float.NaN,0},[0,float.PositiveInfinity],[-1,2],[0.7f,0.7f],[0,0]})
            Assert.Throws<InvalidDataException>(()=>SeasonTreeMaskProcessor.Build([0,0,0,255],1,1,mask));
        Assert.Throws<InvalidDataException>(()=>SeasonTreeMaskProcessor.Build([0,0,0,0],1,1,new float[]{1,0}));
    }
    [Fact]
    public void MutableOutputCannotBeEncodedWithInconsistentAlphaOrMissingMips()
    {
        var mips=Fixture();mips[0].LeafBranchCoverage[0]=0.8f;
        Assert.Throws<InvalidDataException>(()=>SeasonTreeMaskProcessor.WriteDds(mips));
        Assert.Throws<InvalidDataException>(()=>SeasonTreeMaskProcessor.WriteDds(Fixture().Take(1).ToArray()));
        Assert.Throws<ArgumentException>(()=>SeasonTreeMaskProcessor.WriteDds([]));
    }
    [Fact]
    public void HashValidButUnpairedCoverageAndNonFinitePayloadAreRejected()
    {
        var mips=Fixture();var coverage=SeasonTreeMaskProcessor.WriteDds(mips);var diffuse=Diffuse(mips);
        var mask=SeasonTextureData.Parse(coverage,SeasonTextureRole.TreeCoverage,Hash(coverage));
        diffuse[151]=254;
        Assert.Throws<InvalidDataException>(()=>mask.ValidateCoverageSource(SeasonTextureData.Parse(diffuse,SeasonTextureRole.TreeDiffuse,Hash(diffuse))));
        foreach(float value in new[]{float.NaN,float.PositiveInfinity,-0.1f,1.1f})
        {
            var bad=(byte[])coverage.Clone();BinaryPrimitives.WriteSingleLittleEndian(bad.AsSpan(148,4),value);
            Assert.Throws<InvalidDataException>(()=>SeasonTextureData.Parse(bad,SeasonTextureRole.TreeCoverage,Hash(bad)));
        }
        Assert.Throws<InvalidDataException>(()=>mask.ValidateCoverageSource(SeasonTextureData.Parse(coverage,SeasonTextureRole.TreeCoverage,Hash(coverage))));
    }
    [Fact]
    public void TransparentSourceRejectsEvenSubToleranceNonzeroCoverage()
    {
        var mips=Fixture();var coverage=SeasonTreeMaskProcessor.WriteDds(mips);var diffuse=Diffuse(mips);
        diffuse[151]=0;BinaryPrimitives.WriteSingleLittleEndian(coverage.AsSpan(148,4),0.0000005f);
        BinaryPrimitives.WriteSingleLittleEndian(coverage.AsSpan(152,4),0);
        var mask=SeasonTextureData.Parse(coverage,SeasonTextureRole.TreeCoverage,Hash(coverage));
        Assert.Throws<InvalidDataException>(()=>mask.ValidateCoverageSource(SeasonTextureData.Parse(diffuse,SeasonTextureRole.TreeDiffuse,Hash(diffuse))));
    }
    [Fact]
    public void SamplingFixturePreservesContributionsAtEveryMip()
    {
        var mips=Fixture();var coverage=SeasonTreeMaskProcessor.WriteDds(mips);var diffuse=Diffuse(mips);
        var cases=new List<(float U,float V,float Lod,float Retention,uint Policy,float Alpha,float Leaf,float Branch)>();
        foreach(float u in new[]{-0.5f,0f,0.25f,0.5f,0.75f,1f,1.5f})
        foreach(float lod in new[]{0f,0.25f,0.5f,0.75f,1f})
        foreach(float retention in new[]{0f,0.25f,0.75f,1f})foreach(uint policy in new uint[]{1,2})
        {
            // Analytic reference for the two texel fixture, independently of mask mip arrays.
            float fraction=Math.Clamp(u*2-0.5f,0,1),leaf=(fraction*(1/255f))*(1-lod)+(0.5f/255)*lod,
                branch=(1-fraction)*(1-lod)+0.5f*lod,alpha=leaf+branch;
            Assert.InRange(alpha,0,1);cases.Add((u,0.5f,lod,retention,policy,alpha,leaf,branch));
        }
        Assert.Equal(280,cases.Count);
        string? directory=Environment.GetEnvironmentVariable("SEASON_MASK_FIXTURE_DIR");if(string.IsNullOrEmpty(directory))return;
        Directory.CreateDirectory(directory);string sourcePath=Path.Combine(directory,"fixture-source.dds"),maskPath=Path.Combine(directory,"fixture-coverage.dds");
        File.WriteAllBytes(sourcePath,diffuse);File.WriteAllBytes(maskPath,coverage);
        File.WriteAllText(Path.Combine(directory,"textures.tsv"),$"SeasonTextureList\t1\n4\t{Hash(diffuse)}\t{sourcePath}\n6\t{Hash(coverage)}\t{maskPath}\n",new UTF8Encoding(false));
        using var stream=File.Create(Path.Combine(directory,"sampling.bin"));using var writer=new BinaryWriter(stream);
        writer.Write(0x534D4950u);writer.Write(1u);writer.Write((uint)cases.Count);writer.Write(0u);
        foreach(var c in cases)
        {
            writer.Write(c.U);writer.Write(c.V);writer.Write(c.Lod);writer.Write(c.Retention);writer.Write(c.Policy);
            writer.Write(0u);writer.Write(0u);writer.Write(0u);
            writer.Write(c.Branch+c.Leaf*(c.Policy==2?1:c.Retention));writer.Write(c.Alpha);writer.Write(c.Leaf);writer.Write(c.Branch);
        }
    }
}
