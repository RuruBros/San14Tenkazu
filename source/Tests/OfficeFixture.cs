using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace San14Tenkazu.Tests;

internal sealed class OfficeFixture : IRecordMemory
{
    internal readonly PolicyFixture Policies = new();
    internal GameIdentity Identity => Policies.Identity;
    private readonly Dictionary<ulong, byte[]> segments = [];
    internal ulong Array = 0x310000000, Vectors = 0x311000000;
    internal int Writes, FailWrite;
    internal Action<int>? OnWrite { get; set; }
    internal OfficeFixture(bool encoded = true) => Load(encoded);
    internal void Load(bool encoded = true)
    {
        byte[] pointers = new byte[256 * 8];
        for (int id = 0; id < 256; ++id)
        {
            ulong at = Array + (ulong)(id * 0x58); BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(id * 8), at);
            byte[] data = new byte[0x58]; BinaryPrimitives.WriteUInt64LittleEndian(data, Identity.Classes.Post);
            if (id is 1 or 2 or 73) Encoding.Unicode.GetBytes(id == 73 ? "없음" : $"O{id}").CopyTo(data, 0x10);
            data[0x36] = 0xA1; data[0x3C] = 0xA2; data[0x3D] = 0xA3;
            byte[] vector = new byte[20]; vector.AsSpan().Fill(0xA5);
            foreach (OfficeField f in OfficeRules.Fields)
            {
                int value = f.Width == 2 ? id == 73 ? 3000 : 18000 : f.Id == "grade" ? 2 : 5;
                if (f.Width == 2) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(f.Plain), (ushort)value); else data[f.Plain] = (byte)value;
                if (f.Vector >= 0) {
                    if (f.Width == 2) BinaryPrimitives.WriteUInt16LittleEndian(vector.AsSpan(f.Vector), OfficeRules.EncodeWord(value, id));
                    else vector[f.Vector] = FormationRules.Encode(value, f.Rotate, id);
                }
            }
            ulong start = Vectors + (ulong)(id * 64);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x40), start);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x48), start + (encoded ? 20UL : 0));
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x50), start + 20);
            segments[at] = data; segments[start] = vector;
        }
        segments[Policies.Tactics.Formations.Root + OfficeMemory.TableOffset] = pointers;
    }
    private (byte[] Bytes, int Offset)? Find(ulong address, int count)
    {
        var found = segments.Where(s => address >= s.Key && address + (ulong)count <= s.Key + (ulong)s.Value.Length).ToArray();
        return found.Length == 0 ? null : (found.Single().Value, (int)(address - found.Single().Key));
    }
    public byte[] Read(ulong address, int count) => Find(address, count) is { } s ? s.Bytes.AsSpan(s.Offset, count).ToArray() : Policies.Read(address, count);
    internal void Set(ulong address, byte[] bytes) { if (Find(address, bytes.Length) is { } s) bytes.CopyTo(s.Bytes, s.Offset); else Policies.Set(address, bytes); }
    public void Write(ulong address, byte[] bytes)
    {
        ++Writes;
        if (Writes == FailWrite) { Set(address, bytes[..Math.Max(1, bytes.Length / 2)]); throw new IOException("Injected office partial write"); }
        Set(address, bytes); OnWrite?.Invoke(Writes);
    }
    internal OfficeSnapshot Snapshot() => OfficeMemory.Read(this, Identity);
    internal RuleProfile Profile() => Policies.Profile().AddOffices(Snapshot());
}
