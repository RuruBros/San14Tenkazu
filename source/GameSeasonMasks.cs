using San14Tenkazu.Seasons;

namespace San14Tenkazu;

internal static class GameSeasonMasks
{
    internal sealed record Material(SeasonTreeSources.Asset Asset,byte[] Bc7,uint Policy,IReadOnlyList<SeasonTreeMaskMip> Mips);
    internal static IReadOnlyList<Material> ReadDirectory(string directory)
    {
        byte[] sources=GameSeasonSources.ReadDirectory(directory);SeasonTreeSources.Validate(sources);
        var decoded=new List<SeasonCutoutMip>[SeasonTreeSources.Assets.Length];int start=0;
        Span<uint> block=stackalloc uint[16];
        for(int index=0;index<decoded.Length;index++)
        {
            var asset=SeasonTreeSources.Assets[index];decoded[index]=[];int at=start;
            for(int level=0;level<asset.Mips;level++)
            {
                int w=asset.Width>>level,h=asset.Height>>level,columns=Math.Max(1,(w+3)/4),rows=Math.Max(1,(h+3)/4);
                var pixels=new byte[w*h*4];
                for(int by=0;by<rows;by++)for(int bx=0;bx<columns;bx++)
                {
                    if(asset.Format==71)Bc.Bc1(sources.AsSpan(at+(by*columns+bx)*8,8),block,true);
                    else Bc.Bc7(sources.AsSpan(at+(by*columns+bx)*16,16),block);
                    for(int p=0;p<16;p++)
                    {
                        int x=bx*4+p%4,y=by*4+p/4;if(x>=w||y>=h)continue;
                        int pixel=(y*w+x)*4;uint value=block[p];
                        pixels[pixel]=(byte)value;pixels[pixel+1]=(byte)(value>>8);pixels[pixel+2]=(byte)(value>>16);pixels[pixel+3]=(byte)(value>>24);
                    }
                }
                decoded[index].Add(SeasonCutoutMip.FromSource(w,h,pixels));at+=columns*rows*(asset.Format==71?8:16);
            }
            if(at-start!=asset.Bytes)throw new InvalidDataException("Decoded source mip count differs");start=at;
        }
        // Catalog policies are authored per authenticated atlas. Paired masks
        // share support only for UV-aligned sources recorded in the catalog.
        var result=new List<Material>();start=0;
        for(int index=0;index<decoded.Length;index++)
        {
            var asset=SeasonTreeSources.Assets[index];uint policy=(uint)asset.Policy;
            var mask=AuthorBranches(decoded[asset.Green][0].Bgra,asset.BranchRule);
            result.Add(new(asset,sources.AsSpan(start,asset.Bytes).ToArray(),policy,SeasonSourceCoverage.Build(decoded[index],mask)));start+=asset.Bytes;
        }
        return result.AsReadOnly();
    }
    private static byte[] AuthorBranches(byte[] bgra,int rule)
    {
        var labels=new byte[bgra.Length/4];
        if(rule==0)return labels;
        if(rule==2){Array.Fill(labels,(byte)255);return labels;}
        for(int pixel=0;pixel<labels.Length;pixel++)
        {
            int b=bgra[pixel*4],g=bgra[pixel*4+1],r=bgra[pixel*4+2];
            int maximum=Math.Max(r,Math.Max(g,b)),minimum=Math.Min(r,Math.Min(g,b));
            labels[pixel]=(byte)(r>=85&&g>=85&&b>=70&&maximum-minimum<=55?255:0);
        }
        return labels;
    }
}
