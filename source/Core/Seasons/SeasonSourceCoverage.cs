namespace San14Tenkazu.Seasons;

// Registered source mip chains need not reach 1x1. Keep each source alpha
// exactly; do not regenerate diffuse mips with an unrelated cutoff policy.
internal static class SeasonSourceCoverage
{
    internal static IReadOnlyList<SeasonTreeMaskMip> Build(IReadOnlyList<SeasonCutoutMip> source,ReadOnlySpan<byte> branchLabels)
    {
        ArgumentNullException.ThrowIfNull(source);
        if(source.Count==0)throw new ArgumentException("Source mip chain required");
        int width=source[0].Width,height=source[0].Height,maximum=1;
        if(width<1||height<1||width>2048||height>2048||(width&(width-1))!=0||(height&(height-1))!=0||branchLabels.Length!=width*height)
            throw new ArgumentException("Matching power-of-two source and branch labels required");
        for(int size=Math.Max(width,height);size>1;size>>=1)maximum++;
        if(source.Count>maximum)throw new InvalidDataException("Too many source mips");
        for(int level=0;level<source.Count;level++)
        {
            var mip=source[level];int w=Math.Max(1,width>>level),h=Math.Max(1,height>>level);
            if(mip.Width!=w||mip.Height!=h||mip.Bgra.Length!=w*h*4)
                throw new InvalidDataException("Source mip extent differs");
        }
        var work=new float[width*height*2];
        for(int pixel=0;pixel<width*height;pixel++)
        {
            float alpha=source[0].Bgra[pixel*4+3]/255f,branch=branchLabels[pixel]/255f;
            work[pixel*2]=alpha*(1-branch);work[pixel*2+1]=alpha*branch;
        }
        var result=new List<SeasonTreeMaskMip>();
        foreach(var input in source)
        {
            var diffuse=SeasonCutoutMip.FromSource(input.Width,input.Height,input.Bgra);
            var coverage=new float[work.Length];
            for(int pixel=0;pixel<input.Width*input.Height;pixel++)
            {
                float alpha=input.Bgra[pixel*4+3]/255f,total=work[pixel*2]+work[pixel*2+1];
                // Authored branch support comes only from the base labels.
                // Source mip compression outside that support never invents a
                // retained branch. Both contributions still sum to source alpha.
                float branch=total>0?alpha*(work[pixel*2+1]/total):0;
                coverage[pixel*2]=alpha-branch;coverage[pixel*2+1]=branch;
                SeasonTreeMaskProcessor.ValidatePair(coverage[pixel*2],branch,alpha);
            }
            result.Add(new(diffuse,coverage));
            if(result.Count==source.Count)break;
            int nw=Math.Max(1,input.Width/2),nh=Math.Max(1,input.Height/2);var next=new float[nw*nh*2];
            for(int y=0;y<nh;y++)for(int x=0;x<nw;x++)for(int channel=0;channel<2;channel++)
            {
                float sum=0;
                for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    sum+=work[(Math.Min(input.Height-1,y*2+dy)*input.Width+Math.Min(input.Width-1,x*2+dx))*2+channel];
                next[(y*nw+x)*2+channel]=sum/4;
            }
            work=next;
        }
        return result.AsReadOnly();
    }
}
