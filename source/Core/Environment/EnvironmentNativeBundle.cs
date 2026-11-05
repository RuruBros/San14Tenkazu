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
    internal const string DllHash="cc6ef30a41187b055e25f8577dc660063cf4c2ca5370fb28a05f83512c104565";
    internal const string ManifestHash="591153077cdb337ae99269ae0c4ba78a038ce71b7f686080e7009c090cb3e7b5";
    private const string NovemberDllHash="b02d7abb87ea7b334d9f9cae5f8bc3a14292d6b6cd33f6b5835156fb6f194d89";
    private const string NovemberManifestHash="73f2ead2c791b78c464f2cec53b0756c19a8f49e7b31ae3a097c3d3cc46e3a1d";
    internal static readonly (ulong Rva,int Size,string Hash)[] Targets=GameBuildFacts.EnvironmentTargets();
    internal static readonly string[] SourceNames=["EnvironmentBridge.cpp","EnvironmentProtocol.h","EnvironmentPolicy.h","EnvironmentSignatures.h","EnvironmentWeather.h","EnvironmentWeatherTiming.h","EnvironmentChecks.cpp","BuildEnvironmentBridge.ps1","EnvironmentNotification.h","EnvironmentNotificationSignatures.h","EnvironmentNoticeSound.h","EnvironmentNoticeTrace.h","EnvironmentNoticeTraceStore.h","GameBuildFacts.g.h"];
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static bool November(GameBuildProfile profile)=>profile==GameBuildProfiles.NovemberNative;
    internal static string DllHashFor(GameBuildProfile profile)=>November(profile)?NovemberDllHash:DllHash;
    private static IEnumerable<string> Sources(GameBuildProfile profile)=>November(profile)
        ? SourceNames.Append("EnvironmentNoticeSignatures.g.h") : SourceNames;
    private static Dictionary<string,byte[]> Read(GameBuildProfile profile)
    {
        string resource=November(profile)?"Environment.EnvironmentBridge.November2026.zip":"Environment.EnvironmentBridge.zip";
        using var stream=typeof(EnvironmentNativeBundle).Assembly.GetManifestResourceStream(resource)??throw new InvalidDataException("Environment bundle missing");
        using var zip=new ZipArchive(stream);
        var names=Sources(profile).Select(n=>"sources/"+n).Concat(["EnvironmentBridge.dll","build.json"]).ToHashSet();
        var result=new Dictionary<string,byte[]>();
        foreach(var entry in zip.Entries)
        {
            if(!names.Contains(entry.FullName)||entry.Length is <=0 or >2000000||result.ContainsKey(entry.FullName))throw new InvalidDataException("Environment bundle entry differs");
            using var source=entry.Open();using var bytes=new MemoryStream();source.CopyTo(bytes);result.Add(entry.FullName,bytes.ToArray());
        }
        if(result.Count!=names.Count||Hash(result["EnvironmentBridge.dll"])!=DllHashFor(profile)||
            Hash(result["build.json"])!=(November(profile)?NovemberManifestHash:ManifestHash))throw new InvalidDataException("Environment bundle hash differs");
        using var json=JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(result["build.json"]).TrimStart('\uFEFF'));
        if(json.RootElement.GetProperty("protocolVersion").GetUInt32()!=EnvironmentProtocol.Version)throw new InvalidDataException("Environment bundle protocol differs");
        profile.VerifyNativeManifest(json.RootElement);
        profile.VerifyNativeBinary(result["EnvironmentBridge.dll"]);
        foreach(string name in Sources(profile))if(Hash(result["sources/"+name])!=json.RootElement.GetProperty("hashes").GetProperty(name).GetString())throw new InvalidDataException("Environment source hash differs: "+name);
        return result;
    }
    internal static void VerifyDirectory(string path,GameBuildProfile? profile=null)
    {
        foreach(var pair in Read(profile??GameBuildProfiles.Current))if(Hash(File.ReadAllBytes(Path.Combine(path,pair.Key)))!=Hash(pair.Value))throw new InvalidDataException("Environment bundle directory differs: "+pair.Key);
    }
#if LOCAL_DEVELOPER
    internal static void VerifyEmbedded(GameBuildProfile profile) => _ = Read(profile);
#endif
    internal static void VerifySources(string path)
    {
        VerifySources(path,GameBuildProfiles.Current);
        VerifySources(path,GameBuildProfiles.NovemberNative);
    }
    private static void VerifySources(string path,GameBuildProfile profile)
    {
        var files=Read(profile);
        foreach(string name in Sources(profile))
        {
            string native=November(profile)&&name is ("GameBuildFacts.g.h" or "EnvironmentSignatures.h" or "EnvironmentNoticeSignatures.g.h")
                ? Path.Combine(path,"..","BuildProfiles","November2026","Environment","Native") : Path.Combine(path,"Native");
            if(Hash(File.ReadAllBytes(Path.Combine(native,name)))!=Hash(files["sources/"+name]))throw new InvalidDataException("Environment checkout source differs: "+name);
        }
    }
    internal static string Resolve(GameIdentity identity,string root,Action<string> log)
    {
        var profile=GameBuildProfiles.RequireNative(identity);
        using var game=Process.GetProcessById(identity.ProcessId);
        var resident=game.Modules.Cast<ProcessModule>().Where(m=>Path.GetFileName(m.FileName).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(resident.Length>1)throw new InvalidDataException(L.Seasons.RestartRequired);
        if(resident.Length==1)
        {
            string directory=Path.GetDirectoryName(resident[0].FileName)!;
            try{VerifyDirectory(directory,profile);}catch(Exception error){throw new InvalidDataException(L.Seasons.RestartRequired,error);}
            log("Environment verified resident: "+resident[0].FileName);return directory;
        }
        return Extract(root,profile);
    }
    internal static string Extract(string root,GameBuildProfile? profile=null)
    {
        profile??=GameBuildProfiles.Current;
        string destination=Path.GetFullPath(Path.Combine(root,"environment-v4-"+DllHashFor(profile)));
        if(Directory.Exists(destination)){VerifyDirectory(destination,profile);return destination;}
        Directory.CreateDirectory(Path.GetFullPath(root));string staging=destination+"-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
        try
        {
            foreach(var pair in Read(profile)){string file=Path.Combine(staging,pair.Key);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,pair.Value);}
            VerifyDirectory(staging,profile);
            try{Directory.Move(staging,destination);}
            catch(IOException) when(Directory.Exists(destination)){VerifyDirectory(destination,profile);}
            return destination;
        }
        finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
    }
    internal static ulong FrameEntry(GameIdentity identity,ulong entry)
    {
        var profile=GameBuildProfiles.RequireNative(identity);
        if(entry==identity.ModuleBase+profile.UpdateRva)return entry;
        using var game=Process.GetProcessById(identity.ProcessId);
        var modules=game.Modules.Cast<ProcessModule>().Where(m=>Path.GetFileName(m.FileName).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(modules.Length!=1)return 0;
        VerifyDirectory(Path.GetDirectoryName(modules[0].FileName)!,profile);
        ulong expected=(ulong)modules[0].BaseAddress.ToInt64()+SeasonNativeProcess.ExportRva(modules[0].FileName,"EnvironmentFrame");
        return entry==expected?expected:0;
    }
    internal static (ulong Address,byte[] Code) Export(GameIdentity identity,string name)
    {
        using var game=Process.GetProcessById(identity.ProcessId);
        var module=game.Modules.Cast<ProcessModule>().Single(m=>Path.GetFileName(m.FileName).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase));
        VerifyDirectory(Path.GetDirectoryName(module.FileName)!,GameBuildProfiles.RequireNative(identity));
        uint rva=SeasonNativeProcess.ExportRva(module.FileName,name);
        using var file=File.OpenRead(module.FileName);using var pe=new PEReader(file);
        return((ulong)module.BaseAddress.ToInt64()+rva,pe.GetSectionData((int)rva).GetContent(0,32).ToArray());
    }
}
