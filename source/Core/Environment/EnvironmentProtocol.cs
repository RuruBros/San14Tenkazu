using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace San14Tenkazu.EnvironmentRules;

internal static class EnvironmentProtocol
{
    internal const uint Magic=0x314E5645,Version=5;
    internal const int ClimateBytes=46*12*4,EventCount=1024;
    [StructLayout(LayoutKind.Sequential,Pack=8,CharSet=CharSet.Unicode)]
    internal struct Bootstrap
    {
        internal uint Magic,Version,Size,ControllerPid;
        internal ulong ControllerStartFileTime,Module,Generation;
        internal ulong WorldClass,UnitClass,TileClass,CountyClass,CityClass;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Pipe;
        internal Recovery Recovery;
    }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    internal struct Recovery
    {
        internal uint Magic,Version,Error,Complete;
        internal ulong Generation,Frames;
        internal uint Thread,Reserved;
        internal bool Confirmed(ulong generation)=>Magic==EnvironmentProtocol.Magic&&Version==EnvironmentProtocol.Version&&Generation==generation&&Complete==1;
    }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    internal struct Message
    {
        internal uint Magic,Version,Size,Command;
        internal ulong Generation,Sequence,Seed;
        internal uint Flags,Reserved;
        internal EnvironmentValues Values;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=ClimateBytes)]internal byte[] Climate;
    }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    internal struct Event
    {
        internal ulong Id;
        internal uint Date,Kind,City,Tile,Unit,Commander;
        internal int TroopsBefore,TroopsAfter,MoraleBefore,MoraleAfter,FoodBefore,FoodAfter;
    }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    internal struct Reply
    {
        internal uint Magic,Version,Size,Error;
        internal ulong Generation,Sequence,Applied,Frames,SpeedCalls,Slowed,LastEvent;
        internal uint Thread,Date,Flags,Reserved;
        internal EnvironmentValues Values;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=46)]internal byte[] Weather;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=EventCount)]internal Event[] Events;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=46*5)]internal uint[] RecentDates;
        internal ulong NoticesQueued;internal uint NoticesPending,NoticeDate,NoticeCity,NoticeKind;
        internal ulong TestId;internal uint TestMode,TestState,TestCity,TestKind,TestDate,TestError;
        internal bool DiagnosticSoundAttempted=>(Reserved&4)!=0;
        internal bool DiagnosticAudioMissing=>(Reserved&8)!=0;
        internal int DiagnosticSoundChannel=>(int)((Reserved>>8)&255)-1;
        internal int DiagnosticSoundId=>(int)((Reserved>>16)&255)-1;
        internal int DiagnosticSoundState=>(int)((Reserved>>24)&15);
    }
    internal static byte[] Bytes<T>(T value) where T:struct
    {
        int size=Marshal.SizeOf<T>();IntPtr memory=Marshal.AllocHGlobal(size);
        try{Marshal.StructureToPtr(value,memory,false);byte[] bytes=new byte[size];Marshal.Copy(memory,bytes,0,size);return bytes;}
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal static T Read<T>(byte[] bytes) where T:struct
    {
        if(bytes.Length!=Marshal.SizeOf<T>())throw new InvalidDataException("Environment protocol size differs");
        var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        try{return Marshal.PtrToStructure<T>(pinned.AddrOfPinnedObject());}finally{pinned.Free();}
    }
    internal static GameCalendarDate Date(uint value)=>new((int)(value&65535),(int)((value>>16)&255),(int)(value>>24));
}
