using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace San14Tenkazu.Seasons;

internal static class SeasonBridgeProtocol
{
    internal const uint Magic=0x53425331, Version=5;
    internal enum Command:uint { Hello=1, Apply=2, Heartbeat=3, Stop=4, ApplyTreeColor=5, TreeColorStatus=6, SceneStatus=7 }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Recovery
    {
        internal uint Magic,Version,Size,Error;
        internal ulong Generation,ActualDraws;
        internal uint RenderThread,CallbacksDrained,OriginalGpuState,DispatchDetached,ProtectionRestored,ResourcesReleased;
        internal readonly bool Valid(ulong generation)=>Magic==SeasonBridgeProtocol.Magic&&Version==SeasonBridgeProtocol.Version&&Size==56&&Generation==generation;
        internal readonly SeasonRestoreReceipt Receipt=>new(Generation,CallbacksDrained==1,OriginalGpuState==1,
            DispatchDetached==1,ProtectionRestored==1,ResourcesReleased==1);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    internal struct Bootstrap
    {
        internal uint Magic,Version,Size,Reserved;
        internal ulong Generation,ControllerStartFileTime;
        internal uint ControllerPid,LeaseMs;
        internal ulong Module,World,State,Manager,Scene,Driver,Device,Context;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] internal string Pipe;
        internal Recovery Recovery;
        internal ulong Sources;
        internal uint SourceBytes,SourceReserved;
        internal ulong SceneData;
        internal uint SceneBytes,SceneReserved;
    }
    internal readonly record struct Reply(uint Error,ulong Generation,ulong Sequence,ulong ActualDraws,uint RenderThread,
        bool Rendered,SeasonDate Date,uint Capabilities,ulong MaterialEpoch,ulong CompletedEpoch,ulong TreeDraws,
        ulong Swaps,ulong Restores,ulong GpuBytes,uint Generated,uint AlphaPixels,uint ColorScope,uint GpuStateVerified,uint[] Selection);
    internal static byte[] Encode(Command command,ulong generation,ulong sequence,SeasonParameters? parameters=null)
    {
        if(generation==0||sequence==0)throw new ArgumentOutOfRangeException(nameof(generation));
        byte[] data=new byte[64];var span=data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span,Magic);BinaryPrimitives.WriteUInt32LittleEndian(span[4..],Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..],64);BinaryPrimitives.WriteUInt32LittleEndian(span[12..],(uint)command);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..],generation);BinaryPrimitives.WriteUInt64LittleEndian(span[24..],sequence);
        if(parameters is {} p)
        {
            if(!p.Date.IsValid||!p.Weights.IsValid)throw new InvalidDataException("Invalid native season parameters");
            BinaryPrimitives.WriteUInt32LittleEndian(span[32..],(uint)p.Date.Year);
            BinaryPrimitives.WriteUInt32LittleEndian(span[36..],(uint)p.Date.Month);
            BinaryPrimitives.WriteUInt32LittleEndian(span[40..],(uint)p.Date.Day);
            double[] weights=[p.Weights.Spring,p.Weights.Summer,p.Weights.Autumn,p.Weights.Winter];
            for(int i=0;i<4;i++)BinaryPrimitives.WriteSingleLittleEndian(span[(48+i*4)..],(float)weights[i]);
        }
        return data;
    }
    internal static Reply Decode(ReadOnlySpan<byte> data,ulong generation,ulong sequence)
    {
        if(data.Length!=192||BinaryPrimitives.ReadUInt32LittleEndian(data)!=Magic||BinaryPrimitives.ReadUInt32LittleEndian(data[4..])!=Version||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..])!=192||BinaryPrimitives.ReadUInt64LittleEndian(data[16..])!=generation||
            BinaryPrimitives.ReadUInt64LittleEndian(data[24..])!=sequence)throw new InvalidDataException("Native season reply identity differs");
        uint flag=BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        if(flag>1||!SeasonDate.TryCreate((int)BinaryPrimitives.ReadUInt32LittleEndian(data[48..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[52..]),(int)BinaryPrimitives.ReadUInt32LittleEndian(data[56..]),out var date))
            throw new InvalidDataException("Native season date/render receipt is invalid");
        uint[] selection=new uint[16];for(int i=0;i<16;i++)selection[i]=BinaryPrimitives.ReadUInt32LittleEndian(data[(128+i*4)..]);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),generation,sequence,
            BinaryPrimitives.ReadUInt64LittleEndian(data[32..]),BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),flag==1,date,
            BinaryPrimitives.ReadUInt32LittleEndian(data[60..]),BinaryPrimitives.ReadUInt64LittleEndian(data[64..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[72..]),BinaryPrimitives.ReadUInt64LittleEndian(data[80..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[88..]),BinaryPrimitives.ReadUInt64LittleEndian(data[96..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[104..]),BinaryPrimitives.ReadUInt32LittleEndian(data[112..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[116..]),BinaryPrimitives.ReadUInt32LittleEndian(data[120..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[124..]),selection);
    }
    internal static byte[] Bytes<T>(T value)where T:struct
    {
        int size=Marshal.SizeOf<T>();IntPtr buffer=Marshal.AllocHGlobal(size);
        try{Marshal.StructureToPtr(value,buffer,false);byte[] data=new byte[size];Marshal.Copy(buffer,data,0,size);return data;}
        finally{Marshal.FreeHGlobal(buffer);}
    }
    internal static T Read<T>(byte[] bytes)where T:struct
    {
        if(bytes.Length!=Marshal.SizeOf<T>())throw new InvalidDataException("Native receipt size differs");
        IntPtr buffer=Marshal.AllocHGlobal(bytes.Length);
        try{Marshal.Copy(bytes,0,buffer,bytes.Length);return Marshal.PtrToStructure<T>(buffer);}
        finally{Marshal.FreeHGlobal(buffer);}
    }
}
