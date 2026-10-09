namespace San14Tenkazu.Seasons;

internal enum SeasonMapKind { ColorSrgb, Normal, Linear }
internal sealed record SeasonMip(int Width, int Height, byte[] Bgra,
    double SeamMean, double NormalLengthError);

// Offline terrain preparation. Colors are filtered in linear light, normals as vectors.
// Symmetric border conditioning is an artwork operation, applied at every mip resolution.
internal static class SeasonTextureProcessor
{
    private static readonly float[] SrgbDecode = Enumerable.Range(0,256)
        .Select(i => (float)ToLinear(i/255.0)).ToArray();

    internal static IReadOnlyList<SeasonMip> Build(byte[] bgra, int width, int height,
        SeasonMapKind kind, int borderPixels=16)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width<1 || height<1 || (width&(width-1))!=0 || (height&(height-1))!=0 ||
            (long)width*height*4!=bgra.Length || borderPixels<0 || !Enum.IsDefined(kind))
            throw new ArgumentException("Power-of-two dimensions, BGRA8 data and a valid map kind are required");
        var work=new float[bgra.Length];
        for(int at=0;at<bgra.Length;at+=4)
        {
            if(bgra[at+3]!=255)throw new InvalidDataException("Terrain input must be opaque; alpha is not discarded");
            for(int c=0;c<3;c++)work[at+c]=kind switch
            {
                SeasonMapKind.ColorSrgb => SrgbDecode[bgra[at+c]],
                SeasonMapKind.Normal => bgra[at+c]/127.5f-1,
                _ => bgra[at+c]/255f
            };
            work[at+3]=1;
        }
        var levels=new List<SeasonMip>();int w=width,h=height,level=0;
        while(true)
        {
            // Keep the same physical border width; a subpixel border becomes one texel.
            int margin=borderPixels==0?0:Math.Max(1,borderPixels>>Math.Min(level,30));
            Condition(work,w,h,margin);
            if(kind==SeasonMapKind.Normal)Normalize(work);
            var pixels=Encode(work,kind);double seam=Seam(pixels,w,h),lengthError=0;
            if(kind==SeasonMapKind.Normal)
                for(int at=0;at<pixels.Length;at+=4)
                {
                    double z=pixels[at]/127.5-1,y=pixels[at+1]/127.5-1,x=pixels[at+2]/127.5-1;
                    lengthError=Math.Max(lengthError,Math.Abs(Math.Sqrt(x*x+y*y+z*z)-1));
                }
            levels.Add(new(w,h,pixels,seam,lengthError));
            if(w==1 && h==1)break;
            work=Downsample(work,w,h);w=Math.Max(1,w/2);h=Math.Max(1,h/2);level++;
        }
        return levels.AsReadOnly();
    }
    private static double ToLinear(double v) => v<=0.04045?v/12.92:Math.Pow((v+0.055)/1.055,2.4);
    private static double ToSrgb(double v) => v<=0.0031308?v*12.92:1.055*Math.Pow(v,1/2.4)-0.055;
    private static byte[] Encode(float[] work,SeasonMapKind kind)
    {
        var result=new byte[work.Length];
        for(int at=0;at<work.Length;at+=4)
        {
            for(int c=0;c<3;c++)
            {
                double v=kind==SeasonMapKind.Normal?(work[at+c]+1)/2:work[at+c];
                if(kind==SeasonMapKind.ColorSrgb)v=ToSrgb(Math.Clamp(v,0,1));
                result[at+c]=(byte)Math.Clamp(Math.Round(v*255),0,255);
            }
            result[at+3]=255;
        }
        return result;
    }
    private static void Condition(float[] p,int w,int h,int margin)
    {
        int mx=Math.Min(margin,w/2),my=Math.Min(margin,h/2);
        for(int y=0;y<h;y++)for(int x=0;x<mx;x++)for(int c=0;c<3;c++)
        {
            int a=(y*w+x)*4+c,b=(y*w+w-1-x)*4+c;float blend=(mx-x)/(float)mx;
            float mean=(p[a]+p[b])*0.5f;
            if(x==0)p[a]=p[b]=mean;
            else { p[a]+=(mean-p[a])*blend;p[b]+=(mean-p[b])*blend; }
        }
        for(int y=0;y<my;y++)for(int x=0;x<w;x++)for(int c=0;c<3;c++)
        {
            int a=(y*w+x)*4+c,b=((h-1-y)*w+x)*4+c;float blend=(my-y)/(float)my;
            float mean=(p[a]+p[b])*0.5f;
            if(y==0)p[a]=p[b]=mean;
            else { p[a]+=(mean-p[a])*blend;p[b]+=(mean-p[b])*blend; }
        }
    }
    private static float[] Downsample(float[] source,int w,int h)
    {
        int nw=Math.Max(1,w/2),nh=Math.Max(1,h/2);var p=new float[nw*nh*4];
        for(int y=0;y<nh;y++)for(int x=0;x<nw;x++)
            for(int c=0;c<4;c++)
            {
                float total=0;
                for(int j=0;j<2;j++)for(int i=0;i<2;i++)
                    total+=source[(Math.Min(h-1,y*2+j)*w+Math.Min(w-1,x*2+i))*4+c];
                p[(y*nw+x)*4+c]=total*0.25f;
            }
        return p;
    }
    private static void Normalize(float[] p)
    {
        for(int at=0;at<p.Length;at+=4)
        {
            double z=p[at],y=p[at+1],x=p[at+2],length=Math.Sqrt(x*x+y*y+z*z);
            if(length<1e-8){x=y=0;z=length=1;}
            p[at]=(float)(z/length);p[at+1]=(float)(y/length);p[at+2]=(float)(x/length);
        }
    }
    private static double Seam(byte[] p,int w,int h)
    {
        long sum=0;
        for(int y=0;y<h;y++)for(int c=0;c<3;c++)sum+=Math.Abs(p[y*w*4+c]-p[(y*w+w-1)*4+c]);
        for(int x=0;x<w;x++)for(int c=0;c<3;c++)sum+=Math.Abs(p[x*4+c]-p[((h-1)*w+x)*4+c]);
        return sum/((w+h)*3.0);
    }
}
