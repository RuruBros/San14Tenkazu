using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace San14Tenkazu.Seasons;

// The verified DLL, manifest and complete source snapshot travel together.
// A matching resident DLL uses its real path; the native path guard stays intact.
internal static class SeasonNativeBundle
{
    internal const string DllHash="42d2ed9d2b0640aedb45a92d64f62d1a081262de38e2c9b25b0859bb0d3214f0";
    private const string ManifestHash="bebbcd2ef29115e68c542a1b4144ce4276a55e4b6884b1e2febfbf8280d5b4d3";
    private static readonly string[] SourceNames=["SeasonBridge.cpp","SeasonBridgeProtocol.h","SeasonBridgeChecks.cpp",
        "FrameSignatures.h","PrepareSignature.h","RuntimeGuard.h","SeasonTreeColorGpu.h","SeasonTreeColorSource.hlsl","BuildSeasonBridge.ps1",
        "SeasonSceneChecks.cpp","SeasonSceneGpu.h","SeasonWeatherCapture.h","SeasonWorldGpu.h","SeasonShaderTranslation.h","SeasonShaderProfiles.h","SeasonShaderAdmission.h","SeasonGroundSourceGpu.h","SeasonTreeCatalog.h",
        "SeasonRiceGpu.h","SeasonRiceChecks.cpp","SeasonGameRice.hlsl","SeasonVegetation.hlsli",
        "tree-catalog.json","season_tree_catalog.py","SeasonWorldMatrix.hlsl","SeasonWorldPosition.hlsli","SeasonGameTree.hlsl","SeasonGameTerrain.hlsli",
        "SeasonGameWeather.hlsl","SeasonGameParameters.hlsli","SeasonCommon.hlsli","SeasonTreeCoverage.hlsli","SeasonWeather.hlsli","EnvironmentWeather.h","SeasonNoticeArt.h","GameBuildFacts.g.h"];
    private static readonly object Gate=new();
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Dictionary<string,byte[]> ReadBundle()
    {
        using var input=typeof(SeasonNativeBundle).Assembly.GetManifestResourceStream("Seasons.SeasonBridge.zip") ??
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
        if(files.Count!=expected.Count||Hash(files["SeasonBridge.dll"])!=DllHash||Hash(files["build.json"])!=ManifestHash)
            throw new InvalidDataException("Native bundle differs from the verified build");
        using var reader=new StreamReader(new MemoryStream(files["build.json"]));
        using var manifest=JsonDocument.Parse(reader.ReadToEnd());
        if(manifest.RootElement.GetProperty("protocolVersion").GetUInt32()!=SeasonBridgeProtocol.Version)
            throw new InvalidDataException("Native bundle protocol differs");
        GameBuildProfiles.Current.VerifyNativeManifest(manifest.RootElement);
        GameBuildProfiles.Current.VerifyNativeBinary(files["SeasonBridge.dll"]);
        foreach(var name in SourceNames)
            if(Hash(files["sources/"+name])!=manifest.RootElement.GetProperty("hashes").GetProperty(name).GetString())
                throw new InvalidDataException($"Native bundle snapshot differs: {name}");
        return files;
    }

    internal static void VerifyDirectory(string directory)
    {
        var files=ReadBundle();
        foreach(var file in files)
            if(Hash(File.ReadAllBytes(Path.Combine(directory,file.Key)))!=Hash(file.Value))
                throw new InvalidDataException($"Native bundle file differs: {file.Key}");
    }
#if LOCAL_DEVELOPER
    internal static void VerifyEmbedded() => _ = ReadBundle();
#endif

    internal static void VerifySources(string seasonDirectory)
    {
        var files=ReadBundle();
        foreach(var name in SourceNames)
        {
            string folder=Path.GetExtension(name) switch{".hlsl" or ".hlsli"=>"Shaders",".json"=>"Assets",".py"=>Path.Combine("..","Reverse"),_=>"Native"};
            string path=name=="EnvironmentWeather.h"?Path.Combine(seasonDirectory,"..","Environment","Native",name):Path.Combine(seasonDirectory,folder,name);
            if(Hash(File.ReadAllBytes(path))!=Hash(files["sources/"+name]))
                throw new InvalidDataException($"Native bundle is stale for checkout source: {name}");
        }
    }

    internal static string Extract(string cacheRoot)
    {
        lock(Gate)
        {
            var files=ReadBundle();
            string directory=Path.GetFullPath(Path.Combine(cacheRoot,"bridge-v6-"+DllHash));
            if(Directory.Exists(directory)){VerifyDirectory(directory);return directory;}
            Directory.CreateDirectory(Path.GetFullPath(cacheRoot));
            string staging=directory+"-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
            try
            {
                foreach(var file in files)
                {
                    string path=Path.Combine(staging,file.Key);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(file.Value);
                }
                VerifyDirectory(staging);
                try{Directory.Move(staging,directory);}
                catch(IOException) when(Directory.Exists(directory)){VerifyDirectory(directory);}
                VerifyDirectory(directory);return directory;
            }
            finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
        }
    }

    internal static string ResolveForGame(GameIdentity game,string cacheRoot,Action<string>? log)
    {
        if(!GameMemory.IsSameGame(game))throw new InvalidDataException("Native bundle target process changed");
        using var target=Process.GetProcessById(game.ProcessId);
        var residents=target.Modules.Cast<ProcessModule>().Where(m=>
            Path.GetFileName(m.FileName).Equals("SeasonBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(residents.Length>1)throw new InvalidDataException(L.Seasons.RestartRequired);
        if(residents.Length==1)
        {
            string directory=Path.GetDirectoryName(residents[0].FileName)!;
            try{VerifyDirectory(directory);}
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException)
            {throw new InvalidDataException(L.Seasons.RestartRequired,error);}
            log?.Invoke($"Native bundle: verified resident path={residents[0].FileName}, sha256={DllHash}");return directory;
        }
        string extracted=Extract(cacheRoot);log?.Invoke($"Native bundle: verified extracted path={extracted}, sha256={DllHash}");return extracted;
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
