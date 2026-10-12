using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection.PortableExecutable;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.EnvironmentRules;

internal static class EnvironmentNativeBundle
{
    // Updated only after native build/checks and exact source snapshot packaging.
    internal const string DllHash="e413bb68315edaef30c410572b38a62a4d4aca1b4c1a076e52ff954a1bf9627a";
    internal const string ManifestHash="3a4cd0e880cb212baaa3ef29ad4547610cc46033b221f8938522d70fdc9e1cf3";
    internal static readonly (ulong Rva,int Size,string Hash)[] Targets=[
        (0x14b600,835,"69988a6862929a3dc85ba0cee04b1a0594b4f1f7007f3a7b70e4514afc4d2de4"),
        (0x269150,131,"a1c1aef8569eda856dbfa23caa7f16cc6db0e80e67b6e735bbec0aadc7b97bf2"),
        (0x270550,2209,"cd03f1f0c0ef7bc813b93053d0f467fb81c6612821069dcb27d070d22ae4a97d")];
    internal static readonly string[] SourceNames=["EnvironmentBridge.cpp","EnvironmentProtocol.h","EnvironmentPolicy.h","EnvironmentSignatures.h","EnvironmentWeather.h","EnvironmentChecks.cpp","BuildEnvironmentBridge.ps1","EnvironmentNotification.h","EnvironmentNotificationSignatures.h","EnvironmentNoticeSound.h"];
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static Dictionary<string,byte[]> Read()
    {
        using var stream=typeof(EnvironmentNativeBundle).Assembly.GetManifestResourceStream("Environment.EnvironmentBridge.zip")??throw new InvalidDataException("Environment bundle missing");
        using var zip=new ZipArchive(stream);
        var names=SourceNames.Select(n=>"sources/"+n).Concat(["EnvironmentBridge.dll","build.json"]).ToHashSet();
        var result=new Dictionary<string,byte[]>();
        foreach(var entry in zip.Entries)
        {
            if(!names.Contains(entry.FullName)||entry.Length is <=0 or >2000000||result.ContainsKey(entry.FullName))throw new InvalidDataException("Environment bundle entry differs");
            using var source=entry.Open();using var bytes=new MemoryStream();source.CopyTo(bytes);result.Add(entry.FullName,bytes.ToArray());
        }
        if(result.Count!=names.Count||Hash(result["EnvironmentBridge.dll"])!=DllHash||Hash(result["build.json"])!=ManifestHash)throw new InvalidDataException("Environment bundle hash differs");
        using var json=JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(result["build.json"]).TrimStart('\uFEFF'));
        if(json.RootElement.GetProperty("protocolVersion").GetUInt32()!=EnvironmentProtocol.Version)throw new InvalidDataException("Environment bundle protocol differs");
        foreach(string name in SourceNames)if(Hash(result["sources/"+name])!=json.RootElement.GetProperty("hashes").GetProperty(name).GetString())throw new InvalidDataException("Environment source hash differs: "+name);
        return result;
    }
    internal static void VerifyDirectory(string path)
    {
        foreach(var pair in Read())if(Hash(File.ReadAllBytes(Path.Combine(path,pair.Key)))!=Hash(pair.Value))throw new InvalidDataException("Environment bundle directory differs: "+pair.Key);
    }
    internal static void VerifySources(string path)
    {
        var files=Read();foreach(string name in SourceNames)if(Hash(File.ReadAllBytes(Path.Combine(path,"Native",name)))!=Hash(files["sources/"+name]))throw new InvalidDataException("Environment checkout source differs: "+name);
    }
    internal static string Resolve(GameIdentity identity,string root,Action<string> log)
    {
        using var game=Process.GetProcessById(identity.ProcessId);
        var resident=game.Modules.Cast<ProcessModule>().Where(m=>Path.GetFileName(m.FileName).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(resident.Length>1)throw new InvalidDataException(L.Seasons.RestartRequired);
        if(resident.Length==1)
        {
            string directory=Path.GetDirectoryName(resident[0].FileName)!;
            try{VerifyDirectory(directory);}catch(Exception error){throw new InvalidDataException(L.Seasons.RestartRequired,error);}
            log("Environment verified resident: "+resident[0].FileName);return directory;
        }
        return Extract(root);
    }
    internal static string Extract(string root)
    {
        string destination=Path.GetFullPath(Path.Combine(root,"environment-v4-"+DllHash));
        if(Directory.Exists(destination)){VerifyDirectory(destination);return destination;}
        Directory.CreateDirectory(Path.GetFullPath(root));string staging=destination+"-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
        try
        {
            foreach(var pair in Read()){string file=Path.Combine(staging,pair.Key);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,pair.Value);}
            VerifyDirectory(staging);Directory.Move(staging,destination);return destination;
        }
        finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
    }
    internal static ulong FrameEntry(GameIdentity identity,ulong entry)
    {
        if(entry==identity.ModuleBase+0x14b600)return entry;
        using var game=Process.GetProcessById(identity.ProcessId);
        var modules=game.Modules.Cast<ProcessModule>().Where(m=>Path.GetFileName(m.FileName).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(modules.Length!=1)return 0;
        VerifyDirectory(Path.GetDirectoryName(modules[0].FileName)!);
        ulong expected=(ulong)modules[0].BaseAddress.ToInt64()+SeasonNativeProcess.ExportRva(modules[0].FileName,"EnvironmentFrame");
        return entry==expected?expected:0;
    }
    internal static (ulong Address,byte[] Code) Export(GameIdentity identity,string name)
    {
        using var game=Process.GetProcessById(identity.ProcessId);
        var module=game.Modules.Cast<ProcessModule>().Single(m=>Path.GetFileName(m.FileName).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase));
        VerifyDirectory(Path.GetDirectoryName(module.FileName)!);
        uint rva=SeasonNativeProcess.ExportRva(module.FileName,name);
        using var file=File.OpenRead(module.FileName);using var pe=new PEReader(file);
        return((ulong)module.BaseAddress.ToInt64()+rva,pe.GetSectionData((int)rva).GetContent(0,32).ToArray());
    }
}
