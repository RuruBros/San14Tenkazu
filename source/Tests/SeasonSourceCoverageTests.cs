using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonSourceCoverageTests
{
    private static SeasonCutoutMip Mip(int w,int h,byte[] pixels)=>SeasonCutoutMip.FromSource(w,h,pixels);
    [Fact]
    public void SourceMipAlphaAndPartialChainsArePreserved()
    {
        byte[] basePixels=new byte[8*4*4];for(int i=0;i<32;i++)basePixels[i*4+3]=(byte)(i*8);
        byte[] coarse=new byte[4*2*4];for(int i=0;i<8;i++)coarse[i*4+3]=(byte)(255-i*13);
        var source=new[]{Mip(8,4,basePixels),Mip(4,2,coarse)};
        var labels=Enumerable.Range(0,32).Select(i=>(byte)(i%3==0?255:0)).ToArray();
        var output=SeasonSourceCoverage.Build(source,labels);Assert.Equal(2,output.Count);
        for(int level=0;level<2;level++)
        {
            Assert.Equal(source[level].Bgra,output[level].Diffuse.Bgra);
            for(int pixel=0;pixel<source[level].Width*source[level].Height;pixel++)
                Assert.Equal(source[level].Bgra[pixel*4+3]/255f,output[level].LeafBranchCoverage[pixel*2]+output[level].LeafBranchCoverage[pixel*2+1],6);
        }
        basePixels[3]=255;coarse[3]=0;labels[0]=0;
        Assert.Equal(0,output[0].Diffuse.Bgra[3]);Assert.Equal(255,output[1].Diffuse.Bgra[3]);
    }
    [Fact]
    public void MipBranchFractionUsesOpacityRatherThanIndependentLabelAverages()
    {
        var source=new[]{Mip(2,1,[0,0,0,255,0,0,0,1]),Mip(1,1,[0,0,0,200])};
        var result=SeasonSourceCoverage.Build(source,[255,0]);
        Assert.Equal((200/255f)*(255/256f),result[1].LeafBranchCoverage[1],6);
        Assert.Equal(1,result[0].LeafBranchCoverage[1]);Assert.Equal(0,result[0].LeafBranchCoverage[3]);
    }
    [Fact]
    public void CompressedAlphaOutsideBaseSupportDoesNotInventBranches()
    {
        var source=new[]{Mip(2,1,new byte[8]),Mip(1,1,[0,0,0,1])};
        var result=SeasonSourceCoverage.Build(source,[255,255]);
        Assert.Equal(0,result[1].LeafBranchCoverage[1]);Assert.Equal(1/255f,result[1].LeafBranchCoverage[0]);
    }
    [Fact]
    public void MissingAndMismatchedMipExtentsAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>SeasonSourceCoverage.Build([],[]));
        Assert.Throws<ArgumentException>(()=>SeasonSourceCoverage.Build([Mip(2,1,new byte[8])],[0]));
        Assert.Throws<InvalidDataException>(()=>SeasonSourceCoverage.Build([Mip(2,1,new byte[8]),Mip(2,1,new byte[8])],[0,0]));
        Assert.Throws<InvalidDataException>(()=>SeasonSourceCoverage.Build([Mip(1,1,new byte[4]),Mip(1,1,new byte[4])],[0]));
    }
}
