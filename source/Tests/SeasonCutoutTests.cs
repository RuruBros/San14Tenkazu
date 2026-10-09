using San14Tenkazu.Seasons;
namespace San14Tenkazu.Tests;

public class SeasonCutoutTests
{
    [Fact]
    public void TransparentGreenDoesNotContaminateRedFoliageMips()
    {
        byte[] source=[0,0,255,255,0,255,0,0,0,255,0,0,0,0,255,255];
        var mips=SeasonCutoutProcessor.Build(source,2,2);
        Assert.Equal(source,mips[0].Bgra);Assert.Equal(new byte[]{0,0,255,128},mips[1].Bgra);
        Assert.Equal(0.5,mips[0].MeanAlpha);Assert.Equal(128/255.0,mips[1].MeanAlpha);
        Assert.Equal(new byte[]{0,255,0,0},source.Skip(4).Take(4));
    }
    [Fact]
    public void OpaqueSrgbStillFiltersInLinearLight()
    {
        byte[] source=[0,0,0,255,255,255,255,255,255,255,255,255,0,0,0,255];
        var result=SeasonCutoutProcessor.Build(source,2,2);
        Assert.Equal(new byte[]{188,188,188,255},result[^1].Bgra);
    }
    [Fact]
    public void TransparentAndUnevenDimensionsHaveCompleteBoundedMipChains()
    {
        var source=new byte[8*2*4];
        for(int at=0;at<source.Length;at+=4){source[at]=255;source[at+1]=36;}
        var result=SeasonCutoutProcessor.Build(source,8,2);
        Assert.Equal(4,result.Count);Assert.Equal(1,result[^1].Width);Assert.Equal(1,result[^1].Height);
        foreach(var mip in result)
        {
            Assert.Equal(0,mip.MeanAlpha);Assert.Equal(0,mip.CoverageAt10);Assert.Equal(0,mip.CoverageAt50);Assert.Equal(0,mip.CoverageAt90);
            Assert.Equal(mip.Width*mip.Height*4,mip.Bgra.Length);
        }
        Assert.All(result[^1].Bgra,b=>Assert.Equal(0,b));
        Assert.Throws<ArgumentException>(()=>SeasonCutoutProcessor.Build(new byte[12],3,1));
    }
}
