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
    private readonly List<(ulong Address,byte[] Data,uint Protection)> loaderSnapshots=[];
    private readonly string dllHash;
    private readonly bool environment;
    private readonly (ulong Rva,int Size,string Hash)[] targets;
    private static readonly (ulong Rva,int Size,string Hash)[] Targets=[
        (0x960f90,70,"782647f22a91f452bddacbef5c8d341bef5f2bea7d22584162ae1a0c213fe41b"),
        (0x960ee0,82,"dae9d45952c317094fd857fbce61bfd7e3f40696404ac5cfb213d0569a4a18b2"),
        (0x960e90,67,"ed619f25c921d2e81ce43933df8798febdac7bdd432a3378c4163fc562c8f5ff"),
        (0x963c60,533,"8a6edf8fc9bdf6daf9356ba7ed78adb312ad41d48c97651308298078a9e6d044"),
        (0x960e50,52,"4a1653de5006945e8945504e51158704032d997f08c4c2b03ed0b8a7ff705fdf"),
        (0x9625f0,443,"75f7fe6b0e9c27de55869454f1e20c533b8e34615c2872a2e95df949062e009b")];
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
        this.environment=environment;targets=environment?EnvironmentNativeBundle.Targets:Targets;
        game=identity;log=journal;dll=Path.GetFullPath(Path.Combine(buildDirectory,environment?"EnvironmentBridge.dll":"SeasonBridge.dll"));
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(buildDirectory,"build.json")));
        if(!manifest.RootElement.TryGetProperty("protocolVersion",out var version)||version.GetUInt32()!=(environment?EnvironmentProtocol.Version:SeasonBridgeProtocol.Version))
            throw new InvalidDataException("Native bridge protocol version differs before process access");
        dllHash=manifest.RootElement.GetProperty("dllSha256").GetString()!;
        if(Hash(File.ReadAllBytes(dll))!=dllHash)throw new InvalidDataException("Native DLL differs from build receipt");
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
        var modules=Modules();var kernel=modules.Single(m=>Path.GetFileName(m.Path).Equals("kernel32.dll",StringComparison.OrdinalIgnoreCase));
        if(Hash(File.ReadAllBytes(kernel.Path))!="4f7e85e01b521aa002692b1e868bee9a3a9ba19b815ce5d16d2e959fb0289feb")
            throw new InvalidDataException("This Windows loader build has not been validated");
        ulong entry=kernel.Base+ExportRva(kernel.Path,"LoadLibraryW");ulong cursor=entry;
        var snapshots=new List<(ulong Address,byte[] Data,uint Protection)>();bool verified=false;
        for(int hop=0;hop<5;hop++)
        {
            byte[] data=Read(cursor,32);var region=Query(cursor);
            if(region.State!=0x1000||region.Protection is not (0x20 or 0x40))throw new InvalidDataException("Loader relay is not executable");
            snapshots.Add((cursor,data,region.Protection));
            var steam=modules.FirstOrDefault(m=>Path.GetFileName(m.Path).Equals("gameoverlayrenderer64.dll",StringComparison.OrdinalIgnoreCase));
            if(steam.Path is not null&&cursor==steam.Base+0xad730&&
                Hash(File.ReadAllBytes(steam.Path))=="59057f983ae6f02b7eb2495bf65e0430129dd81dff8d9628c764bd1a07e23fee"&&
                data.AsSpan().SequenceEqual(Convert.FromHexString("48895c2410574883ec50803df7310e0000488bd9746d488b7c2458488bd14c8b")))
            {verified=true;break;}
            if(data[0]==0xe9)cursor=checked((ulong)((long)cursor+5+BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1))));
            else if(data[0]==0xff&&data[1]==0x25)
            {
                ulong slot=checked((ulong)((long)cursor+6+BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(2))));
                cursor=BinaryPrimitives.ReadUInt64LittleEndian(Read(slot,8));
            }
            else throw new InvalidDataException("Unknown public loader dispatch; no direct relay invocation is permitted");
        }
        if(!verified)throw new InvalidDataException("Public loader relay exceeds verified bounds");
        foreach(var snapshot in snapshots)
            if(!Read(snapshot.Address,32).AsSpan().SequenceEqual(snapshot.Data)||Query(snapshot.Address).Protection!=snapshot.Protection)
                throw new InvalidDataException("Public loader relay changed before dispatch");
        loaderSnapshots.Clear();loaderSnapshots.AddRange(snapshots);
        log($"Verified public LoadLibraryW: entry=0x{entry:x}, hops={snapshots.Count}; existing entry/relay preserved");return entry;
    }
    internal void Start(SeasonBridgeProtocol.Bootstrap input,byte[] sources,CancellationToken token,byte[]? sceneData=null)
    {
        token.ThrowIfCancellationRequested();SeasonTreeSources.Validate(sources);var modules=Modules();
        if(modules.Any(m=>Path.GetFileName(m.Path).Equals("renderdoc.dll",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Wrapped game contexts are not accepted");
        var resident=modules.Where(m=>Path.GetFileName(m.Path).Equals("SeasonBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(resident.Any(m=>!m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Another native bridge build is already resident");
        if(resident.Length==0)
        {
            ulong entry=LoaderEntry();IntPtr path=Allocate(Encoding.Unicode.GetBytes(dll+'\0'));
            foreach(var snapshot in loaderSnapshots)
                if(!Read(snapshot.Address,32).AsSpan().SequenceEqual(snapshot.Data)||Query(snapshot.Address).Protection!=snapshot.Protection)
                    throw new InvalidDataException("Public loader relay changed immediately before invocation");
            loader=Launch(entry,path);Wait(loader,5000);
        }
        token.ThrowIfCancellationRequested();
        var loaded=Modules().Single(m=>m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase));
        if(Hash(File.ReadAllBytes(dll))!=dllHash||GameMemory.ReadSeasonSnapshot(game).Identity.ModuleBase!=input.Module)
            throw new InvalidDataException("Native DLL/process identity changed before session start");
        input.Sources=(ulong)Allocate(sources).ToInt64();input.SourceBytes=(uint)sources.Length;
        if(sceneData is not null){SeasonSceneData.Validate(sceneData);input.SceneData=(ulong)Allocate(sceneData).ToInt64();input.SceneBytes=(uint)sceneData.Length;}
        log($"Native authenticated original material upload: bytes={input.SourceBytes}, assets={SeasonTreeSources.Assets.Length}");
        bootstrap=Allocate(SeasonBridgeProtocol.Bytes(input));
        worker=Launch(loaded.Base+ExportRva(dll,"SeasonBridgeSession"),bootstrap);
    }
    // The independently bundled gameplay worker reuses only the authenticated CPU loader and allocation lifetime.
    // It receives no render source, scene resource or GPU pointer, and has its own protocol/recovery receipt.
    internal void StartEnvironment(EnvironmentProtocol.Bootstrap input,CancellationToken token)
    {
        if(!environment)throw new InvalidOperationException("Environment worker contract required");
        token.ThrowIfCancellationRequested();var modules=Modules();
        var residents=modules.Where(m=>Path.GetFileName(m.Path).Equals("EnvironmentBridge.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(residents.Any(m=>!m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException(L.Seasons.RestartRequired);
        if(residents.Length==0)
        {
            ulong entry=LoaderEntry();IntPtr path=Allocate(Encoding.Unicode.GetBytes(dll+'\0'));
            foreach(var snapshot in loaderSnapshots)
                if(!Read(snapshot.Address,32).AsSpan().SequenceEqual(snapshot.Data)||Query(snapshot.Address).Protection!=snapshot.Protection)
                    throw new InvalidDataException("Public loader relay changed immediately before invocation");
            loader=Launch(entry,path);Wait(loader,5000);
        }
        token.ThrowIfCancellationRequested();
        var loaded=Modules().Single(m=>m.Path.Equals(dll,StringComparison.OrdinalIgnoreCase));
        if(Hash(File.ReadAllBytes(dll))!=dllHash||!GameMemory.IsSameGame(game)||input.Module!=game.ModuleBase)
            throw new InvalidDataException("Environment DLL/process identity changed before session start");
        bootstrap=Allocate(EnvironmentProtocol.Bytes(input));worker=Launch(loaded.Base+ExportRva(dll,"EnvironmentSession"),bootstrap);
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
            BinaryPrimitives.ReadUInt64LittleEndian(Read(game.ModuleBase+0x1387728,8))!=game.ModuleBase+0x14b600)
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
    internal static uint ExportRva(string file,string name)
    {
        byte[] data=File.ReadAllBytes(file);using var stream=new MemoryStream(data);using var pe=new PEReader(stream);
        var directory=pe.PEHeaders.PEHeader?.ExportTableDirectory??throw new InvalidDataException("Native PE header missing");
        int At(uint rva)
        {
            foreach(var section in pe.PEHeaders.SectionHeaders)
                if(rva>=(uint)section.VirtualAddress&&rva-(uint)section.VirtualAddress<(uint)section.SizeOfRawData)
                    return checked(section.PointerToRawData+(int)(rva-(uint)section.VirtualAddress));
            throw new InvalidDataException("Native export RVA is outside file data");
        }
        uint U(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at,4));int table=At((uint)directory.RelativeVirtualAddress);
        uint count=U(table+24),functions=U(table+28),names=U(table+32),ordinals=U(table+36);
        if(count>65536)throw new InvalidDataException("Native export count exceeds bounds");
        for(uint i=0;i<count;i++)
        {
            int text=At(U(At(names)+checked((int)i*4)));int end=Array.IndexOf(data,(byte)0,text,Math.Min(256,data.Length-text));
            if(end<0||Encoding.ASCII.GetString(data,text,end-text)!=name)continue;
            ushort ordinal=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(At(ordinals)+checked((int)i*2),2));
            uint rva=U(At(functions)+ordinal*4);
            if(rva>=(uint)directory.RelativeVirtualAddress&&rva-(uint)directory.RelativeVirtualAddress<(uint)directory.Size)
                throw new InvalidDataException("Forwarded native worker export is not accepted");
            return rva;
        }
        throw new InvalidDataException($"Native export missing: {name}");
    }
}
