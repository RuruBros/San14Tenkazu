using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonTextureTests
{
    private static byte[] Image(int w,int h,Func<int,int,(byte B,byte G,byte R)> color)
    {
        var pixels=new byte[w*h*4];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            int at=(y*w+x)*4;var c=color(x,y);
            pixels[at]=c.B;pixels[at+1]=c.G;pixels[at+2]=c.R;pixels[at+3]=255;
        }
        return pixels;
    }
    [Fact]
    public void SrgbBlackAndWhiteFilterToLinearLightMean()
    {
        var input=Image(2,2,(x,y)=>x==y?((byte)0,(byte)0,(byte)0):((byte)255,(byte)255,(byte)255));
        var color=SeasonTextureProcessor.Build(input,2,2,SeasonMapKind.ColorSrgb,0);
        var linear=SeasonTextureProcessor.Build(input,2,2,SeasonMapKind.Linear,0);
        Assert.Equal(new byte[]{188,188,188,255},color[^1].Bgra);
        Assert.Equal(new byte[]{128,128,128,255},linear[^1].Bgra);
        Assert.Equal((byte)0,input[0]); // The original source is never changed.
    }
    [Fact]
    public void AllMipResolutionsHaveOpaqueMatchingBorders()
    {
        var input=Image(64,32,(x,y)=>((byte)(x*3),(byte)(y*7),(byte)((x+y)*2)));
        var mips=SeasonTextureProcessor.Build(input,64,32,SeasonMapKind.ColorSrgb);
        Assert.Equal(7,mips.Count);Assert.Equal(1,mips[^1].Width);Assert.Equal(1,mips[^1].Height);
        for(int level=0;level<mips.Count;level++)
        {
            var mip=mips[level];
            Assert.Equal(Math.Max(1,64>>level),mip.Width);Assert.Equal(Math.Max(1,32>>level),mip.Height);
            Assert.Equal(mip.Width*mip.Height*4,mip.Bgra.Length);Assert.Equal(0,mip.SeamMean);
            Assert.All(mip.Bgra.Where((_,i)=>i%4==3),a=>Assert.Equal(255,a));
        }
    }
    [Fact]
    public void VectorMipAveragesAreRenormalizedAndCancelSafely()
    {
        var input=Image(8,8,(x,y)=>x<4?((byte)128,(byte)128,(byte)255):((byte)128,(byte)128,(byte)0));
        var mips=SeasonTextureProcessor.Build(input,8,8,SeasonMapKind.Normal);
        Assert.All(mips,m=>Assert.InRange(m.NormalLengthError,0,0.007));
        Assert.All(mips,m=>Assert.Equal(0,m.SeamMean));
        Assert.DoesNotContain(mips[^1].Bgra.Take(3),v=>v==0);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ConstantTexturesRemainConstant(int mapKind)
    {
        var kind=(SeasonMapKind)mapKind;
        var input=Image(32,32,(_,_)=>kind==SeasonMapKind.Normal?((byte)255,(byte)128,(byte)128):((byte)32,(byte)86,(byte)150));
        var mips=SeasonTextureProcessor.Build(input,32,32,kind);
        Assert.All(mips,m=>Assert.Equal(input.Take(4),m.Bgra.Take(4)));
    }
    [Fact]
    public void TransparencyAndInvalidDimensionsAreRejected()
    {
        var input=Image(2,2,(_,_)=>((byte)0,(byte)0,(byte)0));input[3]=0;
        Assert.Throws<InvalidDataException>(()=>SeasonTextureProcessor.Build(input,2,2,SeasonMapKind.ColorSrgb));
        Assert.Throws<ArgumentException>(()=>SeasonTextureProcessor.Build(new byte[24],3,2,SeasonMapKind.ColorSrgb));
        Assert.Throws<ArgumentException>(()=>SeasonTextureProcessor.Build(new byte[3],1,1,SeasonMapKind.ColorSrgb));
    }
}
