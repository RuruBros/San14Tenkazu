using System.Buffers.Binary;

namespace San14Tenkazu.Seasons;

internal sealed record SeasonTreeMaskMip(SeasonCutoutMip Diffuse,float[] LeafBranchCoverage);

// Authoring inputs label leaves/branches; this does not discover or verify them.
// Store opacity contributions, not independently filtered normalized weights.
// Linear/bilinear/trilinear sampling then preserves Leaf + Branch == source alpha.
internal static class SeasonTreeMaskProcessor
{
    internal static IReadOnlyList<SeasonTreeMaskMip> Build(byte[] bgra,int width,int height,ReadOnlySpan<float> weights)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if(width<1 || height<1 || width>2048 || height>2048 || (width&(width-1))!=0 || (height&(height-1))!=0 ||
            (long)width*height*4!=bgra.Length || (long)width*height*2!=weights.Length)
            throw new ArgumentException("Matching bounded power-of-two source and leaf/branch weights required");
        var source=(byte[])bgra.Clone();var labels=weights.ToArray();var work=new float[labels.Length];
        for(int pixel=0;pixel<width*height;pixel++)
        {
            float leaf=labels[pixel*2],branch=labels[pixel*2+1],alpha=source[pixel*4+3]/255f;
            if(!float.IsFinite(leaf) || !float.IsFinite(branch) || leaf is <0 or >1 || branch is <0 or >1 ||
                (alpha==0 ? leaf!=0 || branch!=0 : Math.Abs(leaf+branch-1)>1e-6f))
                throw new InvalidDataException("Finite normalized labels required; transparent texels must have zero labels");
            if(alpha>0){work[pixel*2]=alpha*leaf/(leaf+branch);work[pixel*2+1]=alpha*branch/(leaf+branch);}
        }
        var diffuse=SeasonCutoutProcessor.Build(source,width,height);var result=new List<SeasonTreeMaskMip>();
        foreach(var mip in diffuse)
        {
            var coverage=new float[work.Length];
            for(int pixel=0;pixel<mip.Width*mip.Height;pixel++)
            {
                float alpha=mip.Bgra[pixel*4+3]/255f,total=work[pixel*2]+work[pixel*2+1];
                if(alpha>0)
                {
                    if(total<=0)throw new InvalidDataException("Filtered coverage cannot explain the source alpha");
                    coverage[pixel*2]=alpha*(work[pixel*2]/total);
                    coverage[pixel*2+1]=alpha-coverage[pixel*2];
                }
            }
            result.Add(new(mip,coverage));
            if(mip.Width==1 && mip.Height==1)break;
            int nw=Math.Max(1,mip.Width/2),nh=Math.Max(1,mip.Height/2);var next=new float[nw*nh*2];
            for(int y=0;y<nh;y++)for(int x=0;x<nw;x++)for(int channel=0;channel<2;channel++)
            {
                float sum=0;
                for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    sum+=work[(Math.Min(mip.Height-1,y*2+dy)*mip.Width+Math.Min(mip.Width-1,x*2+dx))*2+channel];
                next[(y*nw+x)*2+channel]=sum/4;
            }
            work=next;
        }
        return result.AsReadOnly();
    }

    internal static byte[] WriteDds(IReadOnlyList<SeasonTreeMaskMip> mips)
    {
        if(mips is null || mips.Count==0)throw new ArgumentException("Mask mip chain required");
        var first=mips[0].Diffuse;
        if(first.Width<1 || first.Height<1 || first.Width>2048 || first.Height>2048 ||
            (first.Width&(first.Width-1))!=0 || (first.Height&(first.Height-1))!=0)
            throw new InvalidDataException("Mask dimensions rejected");
        int expected=1;for(int size=Math.Max(first.Width,first.Height);size>1;size>>=1)expected++;
        if(mips.Count!=expected)throw new InvalidDataException("Complete mask mip chain required");
        int bytes=148;
        for(int level=0;level<mips.Count;level++)
        {
            var mip=mips[level];int width=Math.Max(1,first.Width>>level),height=Math.Max(1,first.Height>>level);
            if(mip.Diffuse.Width!=width || mip.Diffuse.Height!=height || mip.Diffuse.Bgra.Length!=width*height*4 ||
                mip.LeafBranchCoverage.Length!=width*height*2)throw new InvalidDataException("Mask and diffuse mip extents differ");
            bytes=checked(bytes+width*height*8);
        }
        var data=new byte[bytes];
        void Word(int index,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index*4,4),value);
        foreach(var (index,value) in new (int,uint)[]{(0,0x20534444),(1,124),(2,0x2100F),(3,(uint)first.Height),
            (4,(uint)first.Width),(5,(uint)first.Width*8),(7,(uint)mips.Count),(19,32),(20,4),(21,0x30315844),
            (27,0x401008),(32,16),(33,3),(35,1)})Word(index,value);
        int at=148;
        for(int level=0;level<mips.Count;level++)
        {
            var mip=mips[level];int width=Math.Max(1,first.Width>>level),height=Math.Max(1,first.Height>>level);
            for(int pixel=0;pixel<width*height;pixel++)
            {
                float leaf=mip.LeafBranchCoverage[pixel*2],branch=mip.LeafBranchCoverage[pixel*2+1];
                ValidatePair(leaf,branch,mip.Diffuse.Bgra[pixel*4+3]/255f);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at,4),leaf);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at+4,4),branch);at+=8;
            }
        }
        return data;
    }

    internal static void ValidatePair(float leaf,float branch,float alpha)
    {
        if(!float.IsFinite(leaf) || !float.IsFinite(branch) || leaf is <0 or >1 || branch is <0 or >1 ||
            !float.IsFinite(alpha) || alpha is <0 or >1 || (alpha==0 && (leaf!=0 || branch!=0)) || Math.Abs(leaf+branch-alpha)>1e-6f)
            throw new InvalidDataException("Leaf/branch opacity contributions must match source alpha");
    }
}
