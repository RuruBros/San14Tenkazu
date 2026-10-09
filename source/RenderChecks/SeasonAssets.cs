using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Seasons;
using WM=System.Windows.Media;
using WMI=System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void PrepareSeasonAssets(string root,bool terrainOnly=false,string? gameDirectory=null)
    {
        root=Path.GetFullPath(root);string sources=Path.Combine(root,"Assets","Sources");
        string generated=Path.Combine(root,"Assets","Processed");Directory.CreateDirectory(generated);
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"Assets","sources.json")));
        var outputs=new List<object>();
        foreach(var entry in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name=entry.GetProperty("file").GetString()!,path=Path.Combine(sources,name);
            byte[] original=File.ReadAllBytes(path);string originalHash=Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            if(originalHash!=entry.GetProperty("sha256").GetString())throw new InvalidDataException("Source hash differs: "+name);
            using var stream=new MemoryStream(original);
            var frame=WMI.BitmapFrame.Create(stream,WMI.BitmapCreateOptions.PreservePixelFormat,WMI.BitmapCacheOption.OnLoad);
            var converted=new WMI.FormatConvertedBitmap(frame,WM.PixelFormats.Bgra32,null,0);
            int width=converted.PixelWidth,height=converted.PixelHeight;
            if(width!=1024 || height!=1024)throw new InvalidDataException("Expected approved 1k source: "+name);
            var pixels=new byte[width*height*4];converted.CopyPixels(pixels,width*4,0);
            string map=entry.GetProperty("map").GetString()!;
            bool srgb=map=="Diffuse";
            var kind=map switch { "Diffuse"=>SeasonMapKind.ColorSrgb,"nor_dx"=>SeasonMapKind.Normal,
                "Rough"=>SeasonMapKind.Linear,_=>throw new InvalidDataException("Unknown terrain map kind: "+map) };
            var levels=SeasonTextureProcessor.Build(pixels,width,height,kind);
            var mips=levels.Select(m=>m.Bgra).ToList();
            string outputName=Path.GetFileNameWithoutExtension(name)+".dds",output=Path.Combine(generated,outputName);
            WriteDds(output,width,height,mips,srgb);
            SavePng(Path.Combine(generated,Path.GetFileNameWithoutExtension(name)+".png"),width,height,mips[0]);
            foreach(var mip in levels)
                if(mip.SeamMean!=0 || mip.NormalLengthError>0.007 || mip.Bgra.Where((_,i)=>i%4==3).Any(a=>a!=255))
                    throw new InvalidDataException("Terrain mip seam/normal/alpha validation failed: "+name);
            outputs.Add(new{file=outputName,source=name,sourceUrl=entry.GetProperty("source").GetString(),sourceSha256=originalHash,
                sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant(),license="CC0-1.0",
                width,height,mips=mips.Count,format=srgb?"B8G8R8A8_UNORM_SRGB":"B8G8R8A8_UNORM",alpha=255,
                mipValidation=levels.Select(m=>new{m.Width,m.Height,m.SeamMean,m.NormalLengthError}),
                processingVersion=2,
                processing="WPF BGRA8 decode; linear-light sRGB filtering; float box-filter chain; symmetric border conditioning at every mip (16 base pixels, scaled, minimum one texel); vector normals renormalized; opaque alpha verified"});
        }
        File.WriteAllText(Path.Combine(generated,"manifest.json"),JsonSerializer.Serialize(outputs,new JsonSerializerOptions{WriteIndented=true}));
        if(terrainOnly){Console.WriteLine($"Prepared {outputs.Count} terrain maps; every mip validated. No game access.");return;}
        PrepareSeasonTrees(root,gameDirectory);
    }
    private static void SavePng(string path,int width,int height,byte[] pixels)
    {
        var image=WMI.BitmapSource.Create(width,height,96,96,WM.PixelFormats.Bgra32,null,pixels,width*4);
        var encoder=new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(image));using var f=File.Create(path);encoder.Save(f);
    }
    private static void WriteDds(string path,int width,int height,List<byte[]> mips,bool srgb)
    {
        using var f=File.Create(path);using var b=new BinaryWriter(f);
        b.Write(0x20534444);b.Write(124);b.Write(0x2100F);b.Write(height);b.Write(width);b.Write(width*4);b.Write(0);b.Write(mips.Count);
        for(int i=0;i<11;i++)b.Write(0);b.Write(32);b.Write(4);b.Write(0x30315844);for(int i=0;i<5;i++)b.Write(0);
        b.Write(0x401008);for(int i=0;i<4;i++)b.Write(0);b.Write(srgb?91:87);b.Write(3);b.Write(0);b.Write(1);b.Write(0);
        foreach(var mip in mips)b.Write(mip);
    }
}
