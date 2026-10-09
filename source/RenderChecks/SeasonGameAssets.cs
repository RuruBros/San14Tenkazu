using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;

internal static partial class RenderChecks
{
    // Installation-file inspection only. Does not instantiate the editor map,
    // attach to the game, infer the live shader's material table, or write saves.
    private static void InspectSeasonGroundAssets(string[] args)
    {
        string directory=args.FirstOrDefault(a=>a.StartsWith("--game-directory="))?[17..]
            ??throw new ArgumentException("--game-directory is required");
        string output=args.FirstOrDefault(a=>a.StartsWith("--output="))?[9..]
            ??throw new ArgumentException("--output is required");
        Directory.CreateDirectory(output);string data=Path.Combine(directory,"0002");
        var entries=KtglArchive.ReadIndex(data,"FieldEditor4");var records=new List<object>();
        Span<uint> block=stackalloc uint[16];
        foreach(uint ktid in new uint[]{0xF7C960B0,0xE369753A})
        {
            var entry=entries.Single(e=>e.Ktid==ktid&&e.Type==KtglArchive.TextureType);
            byte[] g1t=KtglArchive.Read(data,entry);var textures=KtglArchive.G1tTextures(g1t);
            for(int index=0;index<textures.Length;index++)
            {
                var texture=textures[index];byte[] bytes=g1t.AsSpan(texture.DataOffset,texture.DataEnd-texture.DataOffset).ToArray();
                string name=$"{ktid:X8}-{index}.bin";File.WriteAllBytes(Path.Combine(output,name),bytes);
                records.Add(new{ktid=$"{ktid:X8}",index,texture.Format,texture.Width,texture.Height,texture.Mips,
                    bytes=bytes.Length,file=name,sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()});
                if(ktid!=0xE369753A||index!=0)continue;
                if(texture.Format!=0x5B||texture.Width!=1024||texture.Height!=1024||texture.Mips!=9)
                    throw new InvalidDataException($"Ground detail source descriptor differs: format={texture.Format:X2}, {texture.Width}x{texture.Height}, mips={texture.Mips}, bytes={bytes.Length}");
                int chain=0;for(int mip=0;mip<texture.Mips;mip++)chain+=Math.Max(1,((1024>>mip)+3)/4)*Math.Max(1,((1024>>mip)+3)/4)*16;
                if(bytes.Length%chain!=0)throw new InvalidDataException("Ground array payload has a partial layer");
                int layers=bytes.Length/chain;const int size=128,columns=5;int height=((layers+columns-1)/columns)*size;
                byte[] sheet=new byte[columns*size*height*4];
                int mipOffset=1024*1024+512*512+256*256;
                for(int layer=0;layer<layers;layer++)for(int by=0;by<size/4;by++)for(int bx=0;bx<size/4;bx++)
                {
                    // G1T stores every layer of mip 0, then every layer of mip
                    // 1. D3D subresource-major upload order is different.
                    Bc.Bc3(bytes.AsSpan(mipOffset*layers+layer*size*size+(by*(size/4)+bx)*16,16),block);
                    for(int p=0;p<16;p++)
                    {
                        int x=(layer%columns)*size+bx*4+p%4,y=(layer/columns)*size+by*4+p/4,at=(y*columns*size+x)*4;
                        uint color=block[p];sheet[at]=(byte)color;sheet[at+1]=(byte)(color>>8);sheet[at+2]=(byte)(color>>16);sheet[at+3]=255;
                    }
                }
                SavePng(Path.Combine(output,"ground-layers-row-major-0-based.png"),columns*size,height,sheet);
            }
        }
        File.WriteAllText(Path.Combine(output,"installation-textures.json"),JsonSerializer.Serialize(new{gameAccess=false,
            liveMaterialTableVerified=false,records},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Inspected {records.Count} installed ground texture payloads; no process access.");
    }
}
