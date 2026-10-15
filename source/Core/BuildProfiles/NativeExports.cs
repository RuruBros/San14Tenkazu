using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace San14Tenkazu;

internal static class NativeExports
{
    internal static uint Rva(byte[] data,string name)
    {
        using var stream=new MemoryStream(data);using var pe=new PEReader(stream);
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
