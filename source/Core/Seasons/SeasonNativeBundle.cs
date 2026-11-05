using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace San14Tenkazu.Seasons;

// The verified DLL, manifest and complete source snapshot travel together.
// A matching resident DLL uses its real path; the native path guard stays intact.
internal static class SeasonNativeBundle
{
    internal const string DllHash="62e964b1acc70188374da754a6bba8ea270e5f2643be742042adefa7e878535b";
    private const string ManifestHash="3a82dfdca149442bc7ee4082f452bf452172b1df7b8fcb9a64346ebb6f56c0aa";
    private const string NovemberDllHash="df675e050e008e46c1b4192c02ff66f3f19258b3e5aea744477384a4517c2fd5";
    private const string NovemberManifestHash="9e572a98cc5880db449f44652824f73ac8e2784f532fee7766dfe77fbb2fdefb";
    private static readonly string[] SourceNames=["SeasonBridge.cpp","SeasonBridgeProtocol.h","SeasonBridgeChecks.cpp",
        "FrameSignatures.h","PrepareSignature.h","RuntimeGuard.h","SeasonTreeColorGpu.h","SeasonTreeColorSource.hlsl","BuildSeasonBridge.ps1",
        "SeasonSceneChecks.cpp","SeasonSceneGpu.h","SeasonWeatherCapture.h","SeasonWorldGpu.h","SeasonShaderTranslation.h","SeasonShaderProfiles.h","SeasonShaderAdmission.h","SeasonGroundSourceGpu.h","SeasonTreeCatalog.h",
        "SeasonRiceGpu.h","SeasonRiceChecks.cpp","SeasonGameRice.hlsl","SeasonVegetation.hlsli",
        "tree-catalog.json","season_tree_catalog.py","SeasonWorldMatrix.hlsl","SeasonWorldPosition.hlsli","SeasonGameTree.hlsl","SeasonGameTerrain.hlsli",
        "SeasonGameWeather.hlsl","SeasonGameParameters.hlsli","SeasonCommon.hlsli","SeasonTreeCoverage.hlsli","SeasonWeather.hlsli","EnvironmentWeather.h","EnvironmentNoticeTrace.h","SeasonNoticeArt.h","SeasonNoticeCausality.h","SeasonPerformance.h","SeasonPerformanceChecks.cpp","SeasonPerformanceWeather.h","EnvironmentWeatherTiming.h","GameBuildFacts.g.h"];
    private static readonly object Gate=new();
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool November(GameBuildProfile profile)=>profile==GameBuildProfiles.NovemberNative;
    internal static string DllHashFor(GameBuildProfile profile)=>November(profile)?NovemberDllHash:DllHash;

    private static Dictionary<string,byte[]> ReadBundle(GameBuildProfile profile)
    {
        string resource=November(profile)?"Seasons.SeasonBridge.November2026.zip":"Seasons.SeasonBridge.zip";
        using var input=typeof(SeasonNativeBundle).Assembly.GetManifestResourceStream(resource) ??
            throw new InvalidDataException("The native season bundle is missing");
        using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        var expected=SourceNames.Select(n=>"sources/"+n).Concat(["build.json","SeasonBridge.dll"]).ToHashSet(StringComparer.Ordinal);
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(var entry in zip.Entries)
        {
            if(!expected.Contains(entry.FullName)||entry.Length is <=0 or >2_000_000||files.ContainsKey(entry.FullName))
                throw new InvalidDataException("Unexpected native bundle entry");
            using var stream=entry.Open();using var bytes=new MemoryStream();stream.CopyTo(bytes);files.Add(entry.FullName,bytes.ToArray());
        }
        if(files.Count!=expected.Count||Hash(files["SeasonBridge.dll"])!=DllHashFor(profile)||
            Hash(files["build.json"])!=(November(profile)?NovemberManifestHash:ManifestHash))
            throw new InvalidDataException("Native bundle differs from the verified build");
        using var reader=new StreamReader(new MemoryStream(files["build.json"]));
        using var manifest=JsonDocument.Parse(reader.ReadToEnd());
        if(manifest.RootElement.GetProperty("protocolVersion").GetUInt32()!=SeasonBridgeProtocol.Version)
            throw new InvalidDataException("Native bundle protocol differs");
        profile.VerifyNativeManifest(manifest.RootElement);
        profile.VerifyNativeBinary(files["SeasonBridge.dll"]);
        foreach(var name in SourceNames)
            if(Hash(files["sources/"+name])!=manifest.RootElement.GetProperty("hashes").GetProperty(name).GetString())
                throw new InvalidDataException($"Native bundle snapshot differs: {name}");
        return files;
    }

    internal static void VerifyDirectory(string directory,GameBuildProfile? profile=null)
    {
        var files=ReadBundle(profile??GameBuildProfiles.Current);
        foreach(var file in files)
            if(Hash(File.ReadAllBytes(Path.Combine(directory,file.Key)))!=Hash(file.Value))
                throw new InvalidDataException($"Native bundle file differs: {file.Key}");
    }
#if LOCAL_DEVELOPER
    internal static void VerifyEmbedded(GameBuildProfile profile) => _ = ReadBundle(profile);
#endif

    internal static void VerifySources(string seasonDirectory)
    {
        VerifySources(seasonDirectory,GameBuildProfiles.Current);
        VerifySources(seasonDirectory,GameBuildProfiles.NovemberNative);
    }
    private static void VerifySources(string seasonDirectory,GameBuildProfile profile)
    {
        var files=ReadBundle(profile);
        foreach(var name in SourceNames)
        {
            string folder=Path.GetExtension(name) switch{".hlsl" or ".hlsli"=>"Shaders",".json"=>"Assets",".py"=>Path.Combine("..","Reverse"),_=>"Native"};
            string path=name is "EnvironmentWeather.h" or "EnvironmentWeatherTiming.h" or "EnvironmentNoticeTrace.h"?Path.Combine(seasonDirectory,"..","Environment","Native",name):Path.Combine(seasonDirectory,folder,name);
            if(November(profile)&&name is ("GameBuildFacts.g.h" or "FrameSignatures.h" or "PrepareSignature.h"))
                path=Path.Combine(seasonDirectory,"..","BuildProfiles","November2026","Seasons","Native",name);
            if(Hash(File.ReadAllBytes(path))!=Hash(files["sources/"+name]))
                throw new InvalidDataException($"Native bundle is stale for checkout source: {name}");
        }
    }

    internal static string Extract(string cacheRoot,GameBuildProfile? profile=null)
    {
        profile??=GameBuildProfiles.Current;
        lock(Gate)
        {
            var files=ReadBundle(profile);
            string directory=Path.GetFullPath(Path.Combine(cacheRoot,"bridge-v6-"+DllHashFor(profile)));
            if(Directory.Exists(directory)){VerifyDirectory(directory,profile);return directory;}
            Directory.CreateDirectory(Path.GetFullPath(cacheRoot));
            string staging=directory+"-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
            try
            {
                foreach(var file in files)
                {
                    string path=Path.Combine(staging,file.Key);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(file.Value);
                }
                VerifyDirectory(staging,profile);
                try{Directory.Move(staging,directory);}
                catch(IOException) when(Directory.Exists(directory)){VerifyDirectory(directory,profile);}
                VerifyDirectory(directory,profile);return directory;
            }
            finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
        }
    }

    internal static string ResolveForGame(GameIdentity game,string cacheRoot,Action<string>? log)
    {
        if(!GameMemory.IsSameGame(game))throw new InvalidDataException("Native bundle target process changed");
        var profile=GameBuildProfiles.RequireNative(game);
        using var target=Process.GetProcessById(game.ProcessId);
        var residents=target.Modules.Cast<ProcessModule>().Where(m=>
            Path.GetFileName(m.FileName).Equals("SeasonBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(residents.Length>1)throw new InvalidDataException(L.Seasons.RestartRequired);
        if(residents.Length==1)
        {
            string directory=Path.GetDirectoryName(residents[0].FileName)!;
            try{VerifyDirectory(directory,profile);}
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException)
            {throw new InvalidDataException(L.Seasons.RestartRequired,error);}
            log?.Invoke($"Native bundle: verified resident path={residents[0].FileName}, sha256={DllHashFor(profile)}");return directory;
        }
        string extracted=Extract(cacheRoot,profile);log?.Invoke($"Native bundle: verified extracted path={extracted}, sha256={DllHashFor(profile)}");return extracted;
    }
}

internal sealed class SeasonPackagedBackend(GameIdentity game,string cacheRoot,Action<string>? log=null,Func<byte[]>? sources=null,Func<byte[]>? sceneData=null,Func<byte[],byte[]>? sceneFromSources=null):ISeasonNativeBackend
{
    public async ValueTask<ISeasonNativeSession> StartAsync(SeasonSnapshot snapshot,ulong generation,CancellationToken token)
    {
        using var timing=new SeasonTiming(log,"renderer-start");
        string directory=await Task.Run(()=>SeasonNativeBundle.ResolveForGame(game,cacheRoot,log),token).ConfigureAwait(false);
        timing.Step("bundle-verification");
        var session=await new SeasonNativeBackend(game,directory,log,sources,sceneData,sceneFromSources).StartAsync(snapshot,generation,token).ConfigureAwait(false);
        timing.Complete("connection-returned");return session;
    }
}
