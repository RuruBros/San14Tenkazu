using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace San14Tenkazu.Tests;

internal sealed class FormationFixture : IRecordMemory
{
    internal readonly GameIdentity Identity = new(0, DateTime.UnixEpoch, 0x140000000, new string('A', 64), true, TestGame.Classes);
    private readonly Dictionary<ulong, byte[]> segments = [];
    internal ulong Root = 0x300000000, Array = 0x301000000, Vectors = 0x302000000, State = 0x303000000;
    internal int Writes;
    internal int FailWrite { get; set; }
    internal Action<int>? OnWrite { get; set; }
    internal FormationFixture(bool encoded = true) { Load(encoded); }
    internal void Load(bool encoded = true)
    {
        segments[Identity.ModuleBase + FormationMemory.RootRva] = BitConverter.GetBytes(Root);
        byte[] pointers = new byte[21 * 8];
        for (int i = 0; i <= 20; i++) BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(i * 8), Array + (ulong)(i * 0xC8));
        segments[Root + FormationMemory.TableOffset] = pointers;
        segments[Root + FormationMemory.StateOffset] = BitConverter.GetBytes(State);
        segments[State] = BitConverter.GetBytes(Identity.Classes.World);
        segments[State + 0x34] = [203, 0, 3, 2, 0];
        for (int id = 1; id <= 20; id++)
        {
            byte[] r = new byte[0xC8], v = new byte[32];
            BinaryPrimitives.WriteUInt64LittleEndian(r, Identity.Classes.Formation);
            Encoding.Unicode.GetBytes($"F{id}").CopyTo(r, 0x10);
            r[0x7E] = 1;
            r[0x80] = 41; r[0x81] = 42; r[0x82] = 43;
            for (int k = 0; k < v.Length; k++) v[k] = (byte)(0xA0 + k);
            foreach (FormationField f in FormationRules.Fields) { r[f.Plain] = 10; v[f.Vector] = FormationRules.Encode(10, f.Rotate, id); }
            ulong vector = Vectors + (ulong)(id * 0x40);
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0xA8), vector);
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0xB0), vector + (encoded ? 32UL : 0UL));
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0xB8), vector + 32);
            segments[Array + (ulong)(id * 0xC8)] = r;
            segments[vector] = v;
        }
    }
    private (byte[] Bytes, int Offset) Find(ulong address, int size)
    {
        var segment = segments.Single(s => address >= s.Key && address + (ulong)size <= s.Key + (ulong)s.Value.Length);
        return (segment.Value, (int)(address - segment.Key));
    }
    public byte[] Read(ulong address, int size) { var s = Find(address, size); return s.Bytes.AsSpan(s.Offset, size).ToArray(); }
    public void Write(ulong address, byte[] bytes)
    {
        ++Writes;
        if (Writes == FailWrite)
        {
            Set(address, bytes[..Math.Max(1, bytes.Length / 2)]);
            throw new IOException("Injected partial write");
        }
        Set(address, bytes);
        OnWrite?.Invoke(Writes);
    }
    internal void Set(ulong address, byte[] bytes) { var s = Find(address, bytes.Length); bytes.CopyTo(s.Bytes, s.Offset); }
    internal FormationSnapshot Snapshot() => FormationMemory.Read(this, Identity);
}
