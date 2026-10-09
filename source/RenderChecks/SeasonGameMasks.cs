using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;

internal static partial class RenderChecks
{
    private static void PrepareSeasonGameMasks(string[] args)
    {
        string directory=args.FirstOrDefault(a=>a.StartsWith("--game-directory="))?[17..]
            ??throw new ArgumentException("--game-directory is required");
        string output=args.FirstOrDefault(a=>a.StartsWith("--output="))?[9..]
            ??throw new ArgumentException("--output is required");
        Directory.CreateDirectory(output);var records=new List<object>();
        foreach(var material in GameSeasonMasks.ReadDirectory(directory))
        {
            var asset=material.Asset;string name=$"{asset.Ktid:X8}";
            using(var stream=File.Create(Path.Combine(output,name+".mask")))
            using(var writer=new BinaryWriter(stream))
            {
                writer.Write(0x534D4153u);writer.Write(2u);writer.Write((uint)asset.Width);writer.Write((uint)asset.Height);
                writer.Write((uint)asset.Mips);writer.Write(material.Policy);writer.Write((uint)asset.Bytes);
                writer.Write((uint)material.Mips.Sum(m=>m.LeafBranchCoverage.Length*4));writer.Write((uint)asset.Format);writer.Write(material.Bc7);
                foreach(var mip in material.Mips)foreach(float value in mip.LeafBranchCoverage)writer.Write(value);
            }
            var first=material.Mips[0];int width=first.Diffuse.Width,height=first.Diffuse.Height;
            byte[] preview=new byte[width*3*height*4];int full=0,half=0,bare=0;
            for(int panel=0;panel<3;panel++)for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int source=(y*width+x)*4,target=(y*width*3+panel*width+x)*4,pixel=y*width+x;
                float retention=material.Policy==2?1:1-panel*.5f;
                float alpha=first.LeafBranchCoverage[pixel*2+1]+first.LeafBranchCoverage[pixel*2]*retention;
                // Display the same alpha test as the stored game color CB0.
                bool visible=alpha>=.49843138456344604f;
                preview[target]=(byte)(visible?first.Diffuse.Bgra[source]:40);
                preview[target+1]=(byte)(visible?first.Diffuse.Bgra[source+1]:40);
                preview[target+2]=(byte)(visible?first.Diffuse.Bgra[source+2]:40);preview[target+3]=255;
                if(visible){if(panel==0)full++;else if(panel==1)half++;else bare++;}
            }
            SavePng(Path.Combine(output,name+"-retention-1-half-0.png"),width*3,height,preview);
            string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output,name+".mask")))).ToLowerInvariant();
            records.Add(new{ktid=name,policy=material.Policy,asset.Hash,mipCount=asset.Mips,maskSha256=hash,
                alphaPixels=material.Mips.Sum(m=>m.Diffuse.Width*m.Diffuse.Height),full,half,bare,
                mips=material.Mips.Select((m,index)=>new{index,m.Diffuse.Width,m.Diffuse.Height,m.Diffuse.MeanAlpha})});
        }
        File.WriteAllText(Path.Combine(output,"masks.json"),JsonSerializer.Serialize(new{gameAccess=false,gpuVerified=false,
            authoredTree01BranchRule="Encoded RGB: R >= 85, G >= 85, B >= 70, max-min <= 55; green atlas support reused for its authenticated autumn pair",
            unknownSourcesAccepted=false,sourceMipsRebuilt=false,records},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Prepared {records.Count} authenticated source masks; no game process access.");
    }
}
