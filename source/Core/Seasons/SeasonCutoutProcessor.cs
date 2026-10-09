namespace San14Tenkazu.Seasons;

internal sealed record SeasonCutoutMip(int Width,int Height,byte[] Bgra,double MeanAlpha,
    double CoverageAt10,double CoverageAt50,double CoverageAt90)
{
    internal static SeasonCutoutMip FromSource(int width,int height,byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if(width<1||height<1||width>2048||height>2048||(long)width*height*4!=bgra.Length)
            throw new ArgumentException("Matching source pixels required");
        double alpha=0;int c10=0,c50=0,c90=0;
        for(int at=3;at<bgra.Length;at+=4)
        {
            double a=bgra[at]/255.0;alpha+=a;if(a>=.1)c10++;if(a>=.5)c50++;if(a>=.9)c90++;
        }
        double count=width*(double)height;
        return new(width,height,(byte[])bgra.Clone(),alpha/count,c10/count,c50/count,c90/count);
    }
}

// UV atlases are not terrain tiles: never join opposite borders or discard their transparency.
// Float premultiplied linear-light filtering excludes the RGB of fully transparent pixels.
internal static class SeasonCutoutProcessor
{
    private static double Linear(double v)=>v<=0.04045?v/12.92:Math.Pow((v+0.055)/1.055,2.4);
    private static double Srgb(double v)=>v<=0.0031308?v*12.92:1.055*Math.Pow(v,1/2.4)-0.055;
    private static readonly float[] Decode=Enumerable.Range(0,256).Select(i=>(float)Linear(i/255.0)).ToArray();
    internal static IReadOnlyList<SeasonCutoutMip> Build(byte[] bgra,int width,int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if(width<1 || height<1 || (width&(width-1))!=0 || (height&(height-1))!=0 || (long)width*height*4!=bgra.Length)
            throw new ArgumentException("Power-of-two BGRA8 atlas dimensions required");
        var work=new float[bgra.Length];
        for(int at=0;at<bgra.Length;at+=4)
        {
            float a=bgra[at+3]/255f;work[at+3]=a;
            for(int c=0;c<3;c++)work[at+c]=Decode[bgra[at+c]]*a;
        }
        var mips=new List<SeasonCutoutMip>();int w=width,h=height;
        while(true)
        {
            byte[] encoded;
            if(mips.Count==0)encoded=(byte[])bgra.Clone(); // Preserve the source atlas's exact base pixels/UVs.
            else
            {
                encoded=new byte[work.Length];
                for(int at=0;at<work.Length;at+=4)
                {
                    float a=work[at+3];encoded[at+3]=(byte)Math.Clamp(Math.Round(a*255),0,255);
                    for(int c=0;c<3;c++)
                        encoded[at+c]=a<=0?(byte)0:(byte)Math.Clamp(Math.Round(Srgb(Math.Clamp(work[at+c]/a,0,1))*255),0,255);
                }
            }
            double alpha=0;int c10=0,c50=0,c90=0;
            for(int at=3;at<encoded.Length;at+=4)
            {
                double a=encoded[at]/255.0;alpha+=a;if(a>=0.1)c10++;if(a>=0.5)c50++;if(a>=0.9)c90++;
            }
            double count=w*(double)h;mips.Add(new(w,h,encoded,alpha/count,c10/count,c50/count,c90/count));
            if(w==1 && h==1)break;
            int nw=Math.Max(1,w/2),nh=Math.Max(1,h/2);var next=new float[nw*nh*4];
            for(int y=0;y<nh;y++)for(int x=0;x<nw;x++)for(int c=0;c<4;c++)
            {
                float total=0;
                for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    total+=work[(Math.Min(h-1,y*2+dy)*w+Math.Min(w-1,x*2+dx))*4+c];
                next[(y*nw+x)*4+c]=total/4;
            }
            work=next;w=nw;h=nh;
        }
        return mips.AsReadOnly();
    }
}
