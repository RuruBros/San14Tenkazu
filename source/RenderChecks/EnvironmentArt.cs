using San14Tenkazu;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
internal static partial class RenderChecks
{
    private static void ExportEnvironmentArt(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var game=FaceLibrary.FindGameDirectory(null)??throw new IOException("Game resources unavailable");var library=FaceLibrary.Load(game);
        using var sheet=new Bitmap(1440,1848);using var graphics=Graphics.FromImage(sheet);graphics.Clear(Color.FromArgb(35,38,42));
        using var font=new Font("Arial",10);int count=0;
        for(int id=1;id<=92;id++){
            using var art=library.Sprite($"stl_event_{id:000}");if(art is null)continue;int x=(id-1)%8*180,y=(id-1)/8*154;
            graphics.DrawImage(art,x,y,180,135);graphics.DrawString(id.ToString(),font,Brushes.White,x+4,y+135);count++;
        }
        sheet.Save(Path.Combine(output,"original-event-art.png"),ImageFormat.Png);
        Console.WriteLine("Exported "+count+" original event illustrations from "+game+" (read-only game resources)");
        var entry=KtglArchive.ReadIndex(Path.Combine(game,"0002"),"ScreenLayout").Single(e=>e.Bin=="ScreenLayout.rdb.bin"&&e.Offset==1727528960);
        var g1t=KtglArchive.Read(Path.Combine(game,"0002"),entry);var t=KtglArchive.G1tTextures(g1t)[0];
        Require(t.Format is 0x08 or 0x5B&&t.Width==1024&&t.Height==1024,"Original flood must be the installed BC3 sprite");
        int bytes=t.Width*t.Height*(t.Format==6?8:16)/16;var pixels=g1t.AsSpan(t.DataOffset,bytes).ToArray();
        File.WriteAllBytes(Path.Combine(output,"flood-texture-level0.bin"),pixels);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{entry.Ktid,t,bytes,sha256=Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant()}));
    }
    private static void PrepareEnvironmentArt(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var payload=new MemoryStream();using var writer=new BinaryWriter(payload);
        foreach(uint value in new uint[]{0x31415244,2,4,1024,1024,864,648,0})writer.Write(value);
        foreach(string name in new[]{"wildfire-v1.png","drought-v1.png","blizzard-v1.png","heatwave-v1.png"}){
            using var image=new Bitmap(Path.Combine(output,name));Require(image.Width*3==image.Height*4,"Event illustration must be 4:3");
            using var texture=new Bitmap(1024,1024,PixelFormat.Format32bppArgb);using(var graphics=Graphics.FromImage(texture)){
                graphics.Clear(Color.Transparent);graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;graphics.DrawImage(image,new Rectangle(0,0,864,648));
            }
            var data=texture.LockBits(new Rectangle(0,0,1024,1024),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try{byte[] row=new byte[4096];for(int y=0;y<1024;y++){Marshal.Copy(data.Scan0+y*data.Stride,row,0,row.Length);writer.Write(row);}}
            finally{texture.UnlockBits(data);}
        }
        byte[] bytes=payload.ToArray();using(var file=new FileStream(Path.Combine(output,"event-art-v2.bin.gz"),FileMode.CreateNew))using(var gzip=new GZipStream(file,CompressionLevel.SmallestSize))gzip.Write(bytes);
        Console.WriteLine("Event art payload bytes="+bytes.Length+", sha256="+Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
