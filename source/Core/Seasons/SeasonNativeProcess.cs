using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using San14Tenkazu.EnvironmentRules;

namespace San14Tenkazu.Seasons;

// Only immutable bootstrap/private data is transferred. The complete native
// recovery receipt is read after the exact session worker signals termination.
internal sealed class SeasonNativeProcess:IDisposable
{
    private readonly GameIdentity game;
    private readonly string dll;
    private readonly Action<string> log;
    private readonly IntPtr handle;
    private sealed class Allocation(IntPtr address,int size){internal IntPtr Address=address;internal int Size=size;internal bool Freed,Confirmed;}
    private readonly List<Allocation> allocations=[];
    private readonly List<(ulong Address,byte[] Bytes,uint Protection)> originals=[];
    private IntPtr loader,worker,bootstrap;
    private bool released;
    private bool disposed;
    private PublicLibraryLoader? loaderPlan;
    private readonly string dllHash;
    private readonly bool environment;
    private readonly (ulong Rva,int Size,string Hash)[] targets;
    private static readonly (ulong Rva,int Size,string Hash)[] Targets=GameBuildFacts.SeasonTargets();
    [StructLayout(LayoutKind.Explicit,Size=48)] private struct Region
    {
        [FieldOffset(0)] internal IntPtr Base;
        [FieldOffset(24)] internal UIntPtr Size;
        [FieldOffset(32)] internal uint State;
        [FieldOffset(36)] internal uint Protection;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CloseHandle(IntPtr value);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr VirtualAllocEx(IntPtr process,IntPtr address,UIntPtr size,uint type,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool VirtualFreeEx(IntPtr process,IntPtr address,UIntPtr size,uint type);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern UIntPtr VirtualQueryEx(IntPtr process,IntPtr address,out Region region,UIntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] data,UIntPtr size,out UIntPtr read);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool WriteProcessMemory(IntPtr process,IntPtr address,byte[] data,UIntPtr size,out UIntPtr written);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateRemoteThread(IntPtr process,IntPtr attributes,UIntPtr stack,IntPtr start,IntPtr parameter,uint flags,out uint tid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint WaitForSingleObject(IntPtr value,uint milliseconds);
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static IntPtr Pointer(ulong value)=>unchecked((IntPtr)(long)value);
    private static Win32Exception Error()=>new(Marshal.GetLastWin32Error());
    internal SeasonNativeProcess(GameIdentity identity,string buildDirectory,Action<string> journal,bool environment=false)
    {
        if(!identity.Verified||!GameMemory.IsSameGame(identity))throw new InvalidDataException("Verified native season process required");
        var profile=GameBuildProfiles.Require(identity);
        GameCapabilities.Require(identity,environment?GameFeature.Environment:GameFeature.Seasons);
        this.environment=environment;targets=(environment?profile.EnvironmentTargets:profile.SeasonTargets).ToArray();
        game=identity;log=journal;dll=Path.GetFullPath(Path.Combine(buildDirectory,environment?"EnvironmentBridge.dll":"SeasonBridge.dll"));
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(buildDirectory,"build.json")));
        if(!manifest.RootElement.TryGetProperty("protocolVersion",out var version)||version.GetUInt32()!=(environment?EnvironmentProtocol.Version:SeasonBridgeProtocol.Version))
            throw new InvalidDataException("Native bridge protocol version differs before process access");
        profile.VerifyNativeManifest(manifest.RootElement);
        dllHash=manifest.RootElement.GetProperty("dllSha256").GetString()!;
        if(Hash(File.ReadAllBytes(dll))!=dllHash)throw new InvalidDataException("Native DLL differs from build receipt");
        profile.VerifyNativeBinary(File.ReadAllBytes(dll));
        foreach(var file in manifest.RootElement.GetProperty("hashes").EnumerateObject())
            if(Hash(File.ReadAllBytes(Path.Combine(buildDirectory,"sources",file.Name)))!=file.Value.GetString())
                throw new InvalidDataException($"Native source snapshot differs: {file.Name}");
        handle=OpenProcess(0x43a,false,identity.ProcessId);if(handle==IntPtr.Zero)throw Error();
        try
        {
            foreach(var target in targets)
            {
                byte[] bytes=Read(identity.ModuleBase+target.Rva,target.Size);uint protect=Query(identity.ModuleBase+target.Rva).Protection;
                if(Hash(bytes)!=target.Hash||protect!=0x20)throw new InvalidDataException("Season engine code/protection differs");
                originals.Add((identity.ModuleBase+target.Rva,bytes,protect));
            }
        }
        catch{CloseHandle(handle);throw;}
    }
    private byte[] Read(ulong address,int length)
    {
        byte[] data=new byte[length];if(!ReadProcessMemory(handle,Pointer(address),data,(UIntPtr)length,out var read)||read.ToUInt64()!=(ulong)length)throw Error();return data;
    }
    private Region Query(ulong address)
    {
        if(VirtualQueryEx(handle,Pointer(address),out var region,(UIntPtr)48)==UIntPtr.Zero)throw Error();return region;
    }
    private IntPtr Allocate(byte[] bytes)
    {
        IntPtr address=VirtualAllocEx(handle,IntPtr.Zero,(UIntPtr)bytes.Length,0x3000,0x04);if(address==IntPtr.Zero)throw Error();
        allocations.Add(new(address,bytes.Length));
        if(!WriteProcessMemory(handle,address,bytes,(UIntPtr)bytes.Length,out var written)||written.ToUInt64()!=(ulong)bytes.Length||
            !Read((ulong)address.ToInt64(),bytes.Length).AsSpan().SequenceEqual(bytes))throw new IOException("Native private bootstrap readback differs");
        return address;
    }
    private IntPtr Launch(ulong at,IntPtr data)
    {
        IntPtr thread=CreateRemoteThread(handle,IntPtr.Zero,UIntPtr.Zero,Pointer(at),data,0,out uint tid);if(thread==IntPtr.Zero)throw Error();
        log($"Native worker started: tid={tid}, entry=0x{at:x}");return thread;
    }
    private static void Wait(IntPtr thread,uint milliseconds)
    {
        uint result=WaitForSingleObject(thread,milliseconds);if(result==0)return;
        if(result==0x102)throw new IOException("Native worker termination is unconfirmed; retain its data and handle");throw Error();
    }
    private (string Path,ulong Base)[] Modules()
    {
        using var process=Process.GetProcessById(game.ProcessId);
        return process.Modules.Cast<ProcessModule>().Select(m=>(m.FileName,(ulong)m.BaseAddress.ToInt64())).ToArray();
    }
    private ulong LoaderEntry()
    {
        loaderPlan=PublicLibraryLoader.Inspect(handle,game.ProcessId);
        log($"Validated public LoadLibraryW: entry=0x{loaderPlan.Entry:x}, hops={loaderPlan.Hops}; loaded PE, executable mappings and stable dispatch checked");
        return loaderPlan.Entry;
    }
    internal void Start(SeasonBridgeProtocol.Bootstrap input,byte[] sources,CancellationToken token,byte[]? sceneData=null)
    {
        using var timing=new SeasonTiming(log,"renderer-native-start");
        token.ThrowIfCancellationRequested();SeasonTreeSources.Validate(sources);var modules=Modules();
        if(modules.Any(m=>Path.GetFileName(m.Path).Equals("renderdoc.dll",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Wrapped game contexts are not accepted");
        var resident=modules.Where(m=>Path.GetFileName(m.Path).Equals("SeasonBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(resident.Any(m=>!m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Another native bridge build is already resident");
        timing.Step("source-and-resident-check",$"resident={resident.Length==1}");
        if(resident.Length==0)
        {
            ulong entry=LoaderEntry();IntPtr path=Allocate(Encoding.Unicode.GetBytes(dll+'\0'));
            loaderPlan!.Verify();
            timing.Step("public-loader-validation");
            loader=Launch(entry,path);Wait(loader,5000);
        }
        timing.Step("library-ready");
        token.ThrowIfCancellationRequested();
        var loaded=Modules().Single(m=>m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase));
        if(Hash(File.ReadAllBytes(dll))!=dllHash||GameMemory.ReadSeasonSnapshot(game).Identity.ModuleBase!=input.Module)
            throw new InvalidDataException("Native DLL/process identity changed before session start");
        timing.Step("loaded-library-validation");
        input.Sources=(ulong)Allocate(sources).ToInt64();input.SourceBytes=(uint)sources.Length;
        timing.Step("material-upload-readback",$"bytes={sources.Length}");
        if(sceneData is not null){SeasonSceneData.Validate(sceneData);input.SceneData=(ulong)Allocate(sceneData).ToInt64();input.SceneBytes=(uint)sceneData.Length;}
        timing.Step("scene-validation-upload-readback",$"bytes={sceneData?.Length??0}");
        log($"Native authenticated original material upload: bytes={input.SourceBytes}, assets={SeasonTreeSources.Assets.Length}");
        bootstrap=Allocate(SeasonBridgeProtocol.Bytes(input));
        worker=Launch(loaded.Base+ExportRva(dll,"SeasonBridgeSession"),bootstrap);
        timing.Step("worker-launched");timing.Complete();
    }
    // The independently bundled gameplay worker reuses only the authenticated CPU loader and allocation lifetime.
    // It receives no render source, scene resource or GPU pointer, and has its own protocol/recovery receipt.
    internal void StartEnvironment(EnvironmentProtocol.Bootstrap input,CancellationToken token)
    {
        using var timing=new SeasonTiming(log,"environment-native-start");
        if(!environment)throw new InvalidOperationException("Environment worker contract required");
        token.ThrowIfCancellationRequested();var modules=Modules();
        var residents=modules.Where(m=>Path.GetFileName(m.Path).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(residents.Any(m=>!m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException(L.Seasons.RestartRequired);
        timing.Step("resident-check",$"resident={residents.Length==1}");
        if(residents.Length==0)
        {
            ulong entry=LoaderEntry();IntPtr path=Allocate(Encoding.Unicode.GetBytes(dll+'\0'));
            loaderPlan!.Verify();
            timing.Step("public-loader-validation");
            loader=Launch(entry,path);Wait(loader,5000);
        }
        timing.Step("library-ready");
        token.ThrowIfCancellationRequested();
        var loaded=Modules().Single(m=>m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase));
        if(Hash(File.ReadAllBytes(dll))!=dllHash||!GameMemory.IsSameGame(game)||input.Module!=game.ModuleBase)
            throw new InvalidDataException("Environment DLL/process identity changed before session start");
        bootstrap=Allocate(EnvironmentProtocol.Bytes(input));worker=Launch(loaded.Base+ExportRva(dll,"EnvironmentSession"),bootstrap);
        timing.Step("worker-launched");timing.Complete();
    }
    internal EnvironmentProtocol.Recovery RecoverEnvironment(ulong generation)
    {
        if(!environment)throw new InvalidOperationException("Environment worker contract required");
        EnvironmentProtocol.Recovery complete=new(){Magic=EnvironmentProtocol.Magic,Version=EnvironmentProtocol.Version,Generation=generation,Complete=1};
        if(released)return complete;
        bool exited;try{using var target=Process.GetProcessById(game.ProcessId);exited=target.HasExited;}catch(ArgumentException){exited=true;}
        if(exited){released=true;log("Environment target exited; process allocations reclaimed");return complete;}
        if(!GameMemory.IsSameGame(game))throw new IOException("Environment target identity changed before recovery");
        if(loader!=IntPtr.Zero)Wait(loader,30000);
        EnvironmentProtocol.Recovery receipt=complete;
        if(worker!=IntPtr.Zero)
        {
            Wait(worker,30000);
            receipt=EnvironmentProtocol.Read<EnvironmentProtocol.Bootstrap>(Read((ulong)bootstrap.ToInt64(),376)).Recovery;
            if(!receipt.Confirmed(generation))
            {
                // Retry the same closed session's recovery; never load another build/name or free live callback data.
                var resident=Modules().Single(m=>m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase));
                CloseHandle(worker);worker=Launch(resident.Base+ExportRva(dll,"EnvironmentRecover"),bootstrap);Wait(worker,30000);
                receipt=EnvironmentProtocol.Read<EnvironmentProtocol.Bootstrap>(Read((ulong)bootstrap.ToInt64(),376)).Recovery;
            }
            if(!receipt.Confirmed(generation))throw new IOException($"Environment recovery unconfirmed: error={receipt.Error}");
        }
        if(!originals.All(p=>Read(p.Address,p.Bytes.Length).AsSpan().SequenceEqual(p.Bytes)&&Query(p.Address).Protection==p.Protection)||
            BinaryPrimitives.ReadUInt64LittleEndian(Read(game.ModuleBase+GameBuildFacts.Engine.UpdateSlotRva,8))!=game.ModuleBase+GameBuildFacts.Engine.UpdateRva)
            throw new IOException("Environment original code/dispatch restoration differs");
        foreach(var allocation in allocations)
        {
            if(allocation.Freed){if(!allocation.Confirmed)throw new IOException("Environment CPU release unconfirmed");continue;}
            if(!VirtualFreeEx(handle,allocation.Address,UIntPtr.Zero,0x8000))throw Error();allocation.Freed=true;
            if(Query((ulong)allocation.Address.ToInt64()).State!=0x10000)throw new IOException("Environment MEM_FREE readback differs");
            allocation.Confirmed=true;log($"Environment private allocation MEM_FREE: address=0x{allocation.Address.ToInt64():x}, bytes={allocation.Size}");
        }
        allocations.Clear();released=true;log($"Environment recovered: frames={receipt.Frames}, thread={receipt.Thread}, error={receipt.Error}, runtimeError={receipt.Reserved}");return receipt;
    }
    internal SeasonRestoreReceipt Recover(ulong generation)
    {
        if(released)return new(generation,true,true,true,true,true);
        bool exited;
        try{using var target=Process.GetProcessById(game.ProcessId);exited=target.HasExited;}catch(ArgumentException){exited=true;}
        if(exited){released=true;log("Native game process exited; its allocations and GPU ownership were reclaimed with the process");return new(generation,true,true,true,true,true);}
        if(!GameMemory.IsSameGame(game))return new(generation,false,false,false,false,false);
        if(loader!=IntPtr.Zero)Wait(loader,30000);
        SeasonRestoreReceipt receipt=new(generation,true,true,true,true,true);
        if(worker!=IntPtr.Zero)
        {
            Wait(worker,30000);
            var input=SeasonBridgeProtocol.Read<SeasonBridgeProtocol.Bootstrap>(Read((ulong)bootstrap.ToInt64(),448));
            if(!input.Recovery.Valid(generation))throw new InvalidDataException("Terminated native worker has no complete recovery receipt");
            receipt=input.Recovery.Receipt;
            log($"Native terminated receipt: error={input.Recovery.Error}, actualDraws={input.Recovery.ActualDraws}, thread={input.Recovery.RenderThread}");
        }
        bool code=originals.All(p=>Read(p.Address,p.Bytes.Length).AsSpan().SequenceEqual(p.Bytes)&&Query(p.Address).Protection==p.Protection);
        if(!receipt.Confirmed||!code)return receipt with{DispatchDetached=false,ProtectionRestored=false};
        foreach(var allocation in allocations)
        {
            if(allocation.Freed){if(!allocation.Confirmed)throw new IOException("A completed native CPU release has no MEM_FREE confirmation");continue;}
            if(!VirtualFreeEx(handle,allocation.Address,UIntPtr.Zero,0x8000))
                throw new IOException("Native CPU allocation release has not been confirmed");
            // Never free the same address again, even if later readback fails or
            // the game immediately reuses this virtual address for another object.
            allocation.Freed=true;
            if(Query((ulong)allocation.Address.ToInt64()).State!=0x10000)
                throw new IOException("Native CPU release completed but MEM_FREE readback was not confirmed");
            allocation.Confirmed=true;
            log($"Native private allocation MEM_FREE: address=0x{allocation.Address.ToInt64():x}, bytes={allocation.Size}");
        }
        allocations.Clear();released=true;return receipt;
    }
    public void Dispose()
    {
        if(disposed)return;
        if(!released)throw new IOException("Retain native process ownership until recovery is confirmed");
        if(worker!=IntPtr.Zero){CloseHandle(worker);worker=IntPtr.Zero;}if(loader!=IntPtr.Zero){CloseHandle(loader);loader=IntPtr.Zero;}CloseHandle(handle);disposed=true;
    }
    internal static uint ExportRva(string file,string name) => NativeExports.Rva(File.ReadAllBytes(file),name);
}
