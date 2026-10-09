using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Seasons;
using WM=System.Windows.Media;
using WMI=System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void PrepareSeasonTrees(string root,string? directory=null)
    {
        root=Path.GetFullPath(root);
        // The standalone command resolves an installation folder, never a running process.
        directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam","steamapps","common","Romance_of_the_Three_Kingdoms_14");
        if(!File.Exists(Path.Combine(directory,"0002","FieldEditor4.rdb")))
            throw new InvalidDataException("Pass --game-directory=<installed game folder> for read-only asset preparation");
        string local=Path.Combine(root,"..","Reverse",".cache","season-assets","trees");Directory.CreateDirectory(local);
        var trees=new List<object>();var seen=new Dictionary<uint,string>();int unchangedPreviews=0;
        foreach(var tree in GameModels.SeasonTrees(directory))
        {
            string preview=Path.Combine(local,tree.Name+".png");string? before=File.Exists(preview)?FileHash(preview):null;
            var sprite=GameModels.Render(directory,tree.Name,120,45,useGpu:false) ?? throw new InvalidDataException("Tree render failed: "+tree.Name);
            var encoder=new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(sprite.Image));
            using(var f=File.Create(preview))encoder.Save(f);
            string after=FileHash(preview);bool? same=before is null?null:before==after;
            if(same==false)throw new InvalidDataException("Material audit changed original preview pixels: "+tree.Name);
            if(same==true)unchangedPreviews++;
            var textures=new List<object>();
            foreach(var pair in tree.Textures)
            {
                string id=$"{pair.Key:X8}",sourceHash=Hash(pair.Value);
                if(seen.TryGetValue(pair.Key,out string? known) && known!=sourceHash)
                    throw new InvalidDataException("One texture identifier resolves to different bytes: "+id);
                seen[pair.Key]=sourceHash;File.WriteAllBytes(Path.Combine(local,id+".g1t"),pair.Value);
                var headers=KtglArchive.G1tTextures(pair.Value);
                bool diffuse=tree.Geometry.Materials.Any(m=>m.DiffuseSlot>=0 && tree.TextureSlots[m.DiffuseSlot]==pair.Key);
                var decoded=GameModels.Texture.Decode(pair.Value);
                string? png=null,bgraHash=null;int[]? alphaHistogram=null;object[]? mips=null;
                if(decoded is not null)
                {
                    png=id+".png";var data=new byte[decoded.Pixels.Length*4];Buffer.BlockCopy(decoded.Pixels,0,data,0,data.Length);
                    bgraHash=Hash(data);SavePng(Path.Combine(local,png),decoded.Width,decoded.Height,data);
                    alphaHistogram=new int[256];for(int at=3;at<data.Length;at+=4)alphaHistogram[data[at]]++;
                    if(diffuse)
                    {
                        if(headers.Length!=1)throw new InvalidDataException("Diffuse texture-array layer mapping is unverified: "+id);
                        var levels=SeasonCutoutProcessor.Build(data,decoded.Width,decoded.Height);
                        WriteDds(Path.Combine(local,id+"-cutout.dds"),decoded.Width,decoded.Height,levels.Select(m=>m.Bgra).ToList(),true);
                        mips=levels.Select(m=>(object)new{m.Width,m.Height,m.MeanAlpha,m.CoverageAt10,m.CoverageAt50,m.CoverageAt90}).ToArray();
                    }
                }
                else if(diffuse)throw new InvalidDataException("Diffuse decode unavailable: "+id);
                textures.Add(new{ktid=id,png,sha256=sourceHash,bgraSha256=bgraHash,diffuse,
                    containerTextures=headers.Select(t=>new{format=$"0x{t.Format:X2}",t.Width,t.Height,t.Mips,t.DataOffset,t.DataEnd}),
                    alphaHistogram,mips,cutoutDds=diffuse?id+"-cutout.dds":null,
                    cutoutSha256=diffuse?FileHash(Path.Combine(local,id+"-cutout.dds")):null,
                    processing=diffuse?"Original base pixels; float premultiplied linear-light box-filter mips; original alpha preserved; no UV atlas border conditioning":"Original bytes/first texture preview; binding type meanings remain unverified"});
            }
            File.WriteAllBytes(Path.Combine(local,tree.Name+".g1m"),tree.Model);
            foreach(var triangle in tree.Triangles)
            {
                if(triangle.SubmeshIndex<0 || triangle.MaterialIndex<0 || triangle.DiffuseSlot<0 || triangle.DiffuseSlot>=tree.TextureSlots.Length || triangle.Texture is null ||
                    new[]{triangle.A.X,triangle.A.Y,triangle.A.Z,triangle.B.X,triangle.B.Y,triangle.B.Z,triangle.C.X,triangle.C.Y,triangle.C.Z,
                        triangle.UA.X,triangle.UA.Y,triangle.UB.X,triangle.UB.Y,triangle.UC.X,triangle.UC.Y}.Any(v=>!float.IsFinite(v)))
                    throw new InvalidDataException("Unresolved/nonfinite tree UV material: "+tree.Name);
            }
            string uvFile=tree.Name+"-uv.json";
            File.WriteAllText(Path.Combine(local,uvFile),JsonSerializer.Serialize(tree.Triangles.Select(t=>new{
                a=new[]{t.A.X,t.A.Y,t.A.Z},b=new[]{t.B.X,t.B.Y,t.B.Z},c=new[]{t.C.X,t.C.Y,t.C.Z},
                ua=new[]{t.UA.X,t.UA.Y},ub=new[]{t.UB.X,t.UB.Y},uc=new[]{t.UC.X,t.UC.Y},
                submesh=t.SubmeshIndex,material=t.MaterialIndex,diffuseSlot=t.DiffuseSlot,diffuseKtid=$"{tree.TextureSlots[t.DiffuseSlot]:X8}",
                meshGroups=tree.Geometry.Groups.Where(g=>g.Submeshes.Contains(t.SubmeshIndex)).Select(g=>g.Index)})));
            trees.Add(new{tree.Name,placementId=$"{tree.PlacementId:X8}",modelId=$"{tree.ModelId:X8}",modelKtid=$"{tree.ModelKtid:X8}",
                modelSha256=Hash(tree.Model),triangles=tree.Triangles.Count,textures,
                textureSlots=tree.TextureSlots.Select((id,slot)=>new{slot,ktid=$"{id:X8}"}),
                materials=tree.Geometry.Materials.Select(m=>new{m.Index,m.DiffuseSlot,bindings=m.Bindings.Select(b=>new{b.Slot,b.TypeId,
                    ktid=$"{tree.TextureSlots[b.Slot]:X8}",b.RawWords})}),
                submeshes=tree.Geometry.Submeshes.Select(s=>new{s.Index,s.VertexLayout,s.MaterialIndex,s.IndexBuffer,s.Primitive,s.IndexOffset,s.IndexCount,s.RawWords}),
                meshGroups=tree.Geometry.Groups.Select(g=>new{g.Index,g.RawName,g.RawHeader,g.Submeshes}),
                geometryVersion=$"0x{tree.Geometry.Version:X}",uvFile,uvSha256=FileHash(Path.Combine(local,uvFile)),
                previewSha256=after,unchangedOriginalPreview=same,
                license="Installed game source; local derivative only; not CC0; never redistribute with the product",
                classification="Unverified winter foliage policy; UV/material source data only",
                leafBranchMasksVerified=false,seasonVariantsBuilt=false,gamePassMappingVerified=false});
        }
        File.WriteAllText(Path.Combine(local,"manifest.json"),JsonSerializer.Serialize(trees,new JsonSerializerOptions{WriteIndented=true}));
        SaveTreeContactSheet(Path.Combine(local,"tree-source-contact-sheet.png"),local,trees.Count);
        Console.WriteLine($"Exported {trees.Count} tree models, {seen.Count} complete bound texture sources; {unchangedPreviews} original previews unchanged. File-only, no game process/GPU access.");
    }
    private static string FileHash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void SaveTreeContactSheet(string path,string local,int count)
    {
        using var json=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(local,"manifest.json")));
        const int columns=6,cellWidth=200,cellHeight=260;int rows=(count+columns-1)/columns;
        var visual=new WM.DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new WM.SolidColorBrush(WM.Color.FromRgb(44,48,54)),null,new System.Windows.Rect(0,0,columns*cellWidth,rows*cellHeight));
            int index=0;
            foreach(var model in json.RootElement.EnumerateArray())
            {
                string name=model.GetProperty("Name").GetString()!;
                using var stream=File.OpenRead(Path.Combine(local,name+".png"));
                var frame=WMI.BitmapFrame.Create(stream,WMI.BitmapCreateOptions.PreservePixelFormat,WMI.BitmapCacheOption.OnLoad);
                double scale=Math.Min((cellWidth-16)/(double)frame.PixelWidth,(cellHeight-44)/(double)frame.PixelHeight);
                int x=index%columns*cellWidth,y=index/columns*cellHeight;
                dc.DrawImage(frame,new System.Windows.Rect(x+(cellWidth-frame.PixelWidth*scale)/2,y+10,frame.PixelWidth*scale,frame.PixelHeight*scale));
                dc.DrawText(new WM.FormattedText(name,System.Globalization.CultureInfo.InvariantCulture,System.Windows.FlowDirection.LeftToRight,
                    new WM.Typeface("Segoe UI"),11,WM.Brushes.White,1),new System.Windows.Point(x+6,y+cellHeight-32));
                dc.DrawText(new WM.FormattedText(model.GetProperty("modelKtid").GetString()!,System.Globalization.CultureInfo.InvariantCulture,System.Windows.FlowDirection.LeftToRight,
                    new WM.Typeface("Segoe UI"),11,WM.Brushes.White,1),new System.Windows.Point(x+6,y+cellHeight-16));
                index++;
            }
        }
        var bitmap=new WMI.RenderTargetBitmap(columns*cellWidth,rows*cellHeight,96,96,WM.PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
