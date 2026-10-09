using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using San14Tenkazu.Seasons;

internal static partial class RenderChecks
{
    private static void CheckSeasonTextures(string root)
    {
        root=Path.GetFullPath(root);string generated=Path.Combine(root,"Assets","Processed");
        string evidence=Path.GetFullPath(Path.Combine(root,"..","Reverse",".cache","season-safety","20261007-textures"));
        Directory.CreateDirectory(evidence);var lines=new List<string>{"SeasonTextureList\t1"};var records=new List<object>();
        void Add(string folder,string file,SeasonTextureRole role,string hash,int width,int height,int mips,int layers,string license)
        {
            if(Path.GetFileName(file)!=file || !file.EndsWith(".dds",StringComparison.Ordinal))throw new InvalidDataException("Texture manifest filename rejected");
            string path=Path.Combine(folder,file);var texture=SeasonTextureData.Load(path,role,hash);
            if(texture.Width!=width || texture.Height!=height || texture.Mips!=mips || texture.Layers!=layers)
                throw new InvalidDataException("Texture manifest dimensions differ: "+file);
            if(path.Contains('\t') || path.Contains('\n'))throw new InvalidDataException("Texture list path rejected");
            lines.Add($"{(int)role}\t{texture.Sha256}\t{path}");
            records.Add(new{file,role=role.ToString(),texture.Sha256,texture.Width,texture.Height,texture.Mips,texture.Layers,
                texture.Format,texture.PayloadBytes,parts=texture.Parts,license,gamePassVerified=false});
        }
        void SourceHash(string path,string expected)
        {
            string actual=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if(actual!=expected)throw new InvalidDataException("Original texture source SHA256 mismatch");
        }
        using var sources=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"Assets","sources.json")));
        var approved=sources.RootElement.GetProperty("assets").EnumerateArray().ToDictionary(a=>a.GetProperty("file").GetString()!,a=>a);
        using(var terrain=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated,"manifest.json"))))
        foreach(var item in terrain.RootElement.EnumerateArray())
        {
            string source=item.GetProperty("source").GetString()!;
            if(!approved.TryGetValue(source,out var original) || original.GetProperty("sha256").GetString()!=item.GetProperty("sourceSha256").GetString() ||
                item.GetProperty("license").GetString()!="CC0-1.0")throw new InvalidDataException("Terrain source provenance differs");
            SourceHash(Path.Combine(root,"Assets","Sources",source),item.GetProperty("sourceSha256").GetString()!);
            var role=original.GetProperty("map").GetString() switch{
                "Diffuse"=>SeasonTextureRole.TerrainDiffuse,"nor_dx"=>SeasonTextureRole.TerrainNormal,"Rough"=>SeasonTextureRole.TerrainRough,
                _=>throw new InvalidDataException("Unknown terrain texture map")};
            Add(generated,item.GetProperty("file").GetString()!,role,item.GetProperty("sha256").GetString()!,
                item.GetProperty("width").GetInt32(),item.GetProperty("height").GetInt32(),item.GetProperty("mips").GetInt32(),1,"CC0-1.0");
        }
        using(var climate=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated,"climate-manifest.json"))))
        {
            var item=climate.RootElement;SourceHash(Path.Combine(root,"Assets",item.GetProperty("source").GetString()!),item.GetProperty("sourceSha256").GetString()!);
            Add(generated,item.GetProperty("file").GetString()!,SeasonTextureRole.Climate,item.GetProperty("sha256").GetString()!,
                item.GetProperty("width").GetInt32(),item.GetProperty("height").GetInt32(),item.GetProperty("mipLevels").GetInt32(),
                item.GetProperty("arraySize").GetInt32(),item.GetProperty("license").GetString()!);
        }
        string trees=Path.GetFullPath(Path.Combine(root,"..","Reverse",".cache","season-assets","trees"));
        using var treeManifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(trees,"manifest.json")));
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var tree in treeManifest.RootElement.EnumerateArray())foreach(var item in tree.GetProperty("textures").EnumerateArray())
        {
            if(!item.GetProperty("diffuse").GetBoolean() || !seen.Add(item.GetProperty("ktid").GetString()!))continue;
            var shape=item.GetProperty("containerTextures")[0];
            SourceHash(Path.Combine(trees,item.GetProperty("ktid").GetString()+".g1t"),item.GetProperty("sha256").GetString()!);
            Add(trees,item.GetProperty("cutoutDds").GetString()!,SeasonTextureRole.TreeDiffuse,item.GetProperty("cutoutSha256").GetString()!,
                shape.GetProperty("Width").GetInt32(),shape.GetProperty("Height").GetInt32(),item.GetProperty("mips").GetArrayLength(),1,
                "Installed game derivative; local ignored cache only; not redistributed");
        }
        File.WriteAllText(Path.Combine(evidence,"verified-textures.tsv"),string.Join('\n',lines)+"\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(evidence,"texture-contract.json"),JsonSerializer.Serialize(new{gameAccess=false,
            productionReady=false,resourceCount=records.Count,resources=records},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Validated {records.Count} immutable DDS snapshots and source hashes. No game access. Native upload list: {evidence}");
    }
}
