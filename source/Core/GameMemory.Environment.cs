using System.Security.Cryptography;
using San14Tenkazu.EnvironmentRules;
namespace San14Tenkazu;

internal static partial class GameMemory
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="CreateRemoteThread",SetLastError=true)]
    private static extern IntPtr StartEnvironmentRestore(IntPtr process,IntPtr attributes,UIntPtr stack,IntPtr start,IntPtr parameter,uint flags,out uint tid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="WaitForSingleObject")]
    private static extern uint WaitEnvironmentRestore(IntPtr thread,uint milliseconds);
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="GetExitCodeThread",SetLastError=true)]
    private static extern bool EnvironmentRestoreResult(IntPtr thread,out uint result);
    private static void RestoreEnvironmentDispatch(IntPtr handle,GameIdentity identity,ulong stub)
    {
        var target=EnvironmentNativeBundle.Export(identity,"EnvironmentRestoreDispatch");if(!CodeIs(handle,target.Address,target.Code))throw new InvalidDataException(L.Calls.WhyCode);
        IntPtr args=VirtualAllocEx(handle,IntPtr.Zero,(UIntPtr)16,MemCommit|MemReserve,PageReadWrite);if(args==IntPtr.Zero)throw AccessError(L.Errors.NoWriteAccess);
        IntPtr worker=IntPtr.Zero;bool completed=false;
        try{
            WriteBytes(handle,(ulong)args.ToInt64(),BitConverter.GetBytes(identity.ModuleBase).Concat(BitConverter.GetBytes(stub)).ToArray());
            worker=StartEnvironmentRestore(handle,IntPtr.Zero,UIntPtr.Zero,(IntPtr)unchecked((long)target.Address),args,0,out _);
            if(worker==IntPtr.Zero)throw AccessError(L.Errors.NoWriteAccess);
            if(WaitEnvironmentRestore(worker,5000)!=0)throw new IOException("Environment dispatch restoration did not finish");completed=true;
            if(!EnvironmentRestoreResult(worker,out uint result)||result!=0)throw new IOException("Environment dispatch restoration rejected: "+result);
        }finally{if(worker!=IntPtr.Zero)CloseHandle(worker);if(completed||worker==IntPtr.Zero)VirtualFreeEx(handle,args,UIntPtr.Zero,MemRelease);}
    }
    internal static (int Count,ulong DialogClass,string Text) ReadEnvironmentNotification(GameIdentity identity)
    {
        IntPtr handle=OpenProcess(ProcessQueryInformation|ProcessVmRead,false,identity.ProcessId);if(handle==IntPtr.Zero)throw AccessError(L.Errors.NoWriteAccess);
        try{
            ulong manager=identity.ModuleBase+0x1b620b0,count=BitConverter.ToUInt64(ReadBytes(handle,manager+0x10,8)),items=BitConverter.ToUInt64(ReadBytes(handle,manager+0x20,8));
            if(count>128)throw new InvalidDataException("Native state count differs");int notices=0;ulong dialogClass=0;string text="";
            for(ulong i=0;i<count;i++){
                ulong state=BitConverter.ToUInt64(ReadBytes(handle,items+i*8,8));if(state==0||BitConverter.ToUInt64(ReadBytes(handle,state,8))!=identity.ModuleBase+0x13acdb8)continue;
                notices++;byte[] tail=new byte[32];if(!TryRead(handle,state+0x470,tail))continue;
                ulong dialog=BitConverter.ToUInt64(tail),head=BitConverter.ToUInt64(tail,8),length=BitConverter.ToUInt64(tail,16);byte[] pointer=new byte[8];
                if(dialog!=0&&TryRead(handle,dialog,pointer))dialogClass=BitConverter.ToUInt64(pointer)-identity.ModuleBase;
                if(length==0||head==0||!TryRead(handle,head,pointer))continue;ulong node=BitConverter.ToUInt64(pointer);
                if(node==0||node==head||!TryRead(handle,node+16,pointer))continue;ulong message=BitConverter.ToUInt64(pointer);byte[] contents=new byte[400];
                if(message!=0&&TryRead(handle,message,contents))text=System.Text.Encoding.Unicode.GetString(contents).Split('\0')[0];
            }return(notices,dialogClass,text);
        }finally{CloseHandle(handle);}
    }
    // Evaluate the actual native movement calculation on the game frame; this does not advance or move a unit.
    internal static int ReadEnvironmentMovement(GameIdentity identity,GameLayout layout,UnitRecord unit,bool environment)
    {
        if(!identity.Verified||!IsSameGame(identity)||!layout.SameStorage(ReadCurrentLayout(identity)))throw new InvalidOperationException(L.Errors.StateMoved);
        IntPtr handle=OpenProcess(ProcessQueryInformation|ProcessVmRead|ProcessVmWrite|ProcessVmOperation,false,identity.ProcessId);
        if(handle==IntPtr.Zero)throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            if(unit.Address<layout.UnitArray||unit.Address>=layout.UnitArray+(ulong)(layout.UnitCount*UnitStride)||
                (unit.Address-layout.UnitArray)%UnitStride!=0||!ReadBytes(handle,unit.Address,UnitStride).AsSpan().SequenceEqual(unit.Data))
                throw new InvalidOperationException(L.Errors.StateMoved);
            var target=environment?EnvironmentNativeBundle.Export(identity,"EnvironmentMovement"):(identity.ModuleBase+0x269150,ReadBytes(handle,identity.ModuleBase+0x269150,32));
            if(!environment)
            {
                byte[] body=ReadBytes(handle,identity.ModuleBase+0x269150,131);
                if(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()!=EnvironmentNativeBundle.Targets[1].Hash)throw new InvalidDataException(L.Calls.WhyCode);
            }
            var result=RunOnFrame(handle,identity,_=>new FrameCall(target.Item1,target.Item2,unit.Address,0,[],false));
            if(result.Why is {} error)throw new IOException(error);return result.Result;
        }
        finally{CloseHandle(handle);}
    }
}
