using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace San14Tenkazu.Tests;

internal sealed class TacticFixture : IRecordMemory
{
    internal readonly FormationFixture Formations = new();
    internal GameIdentity Identity => Formations.Identity;
    private readonly Dictionary<ulong, byte[]> segments = [];
    internal ulong Array = 0x305000000, Vectors = 0x306000000;
    internal int Writes;
    internal int FailWrite { get; set; }
    internal Action<int>? OnWrite { get; set; }
    internal TacticFixture(bool encoded = true) => Load(encoded);
    internal void Load(bool encoded = true)
    {
        byte[] pointers = new byte[201 * 8];
        for (int id = 0; id <= 200; ++id)
        {
            ulong at = Array + (ulong)(id * 0x90), vector = Vectors + (ulong)(id * 0x40);
            BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(id * 8), at);
            byte[] record = new byte[0x90], v = new byte[40];
            BinaryPrimitives.WriteUInt64LittleEndian(record, Identity.Classes.Tactic);
            Encoding.Unicode.GetBytes($"T{id}").CopyTo(record, 0x10);
            record[0x40] = 1; record[0x42] = 48; record[0x44] = 3;
            v.AsSpan().Fill(0xA5); v[0] = FormationRules.Encode(3, 1, id);
            foreach (var f in TacticRules.Fields)
            {
                int value = f.Id is "kind1" or "ability" ? 1 : f.Id.StartsWith("amount", StringComparison.Ordinal) ? 10 : 0;
                record[f.Plain] = (byte)value; v[f.Vector] = FormationRules.Encode(value, f.Rotate, id);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0x70), vector);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0x78), vector + (encoded ? 40UL : 0UL));
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0x80), vector + 40);
            segments[at] = record; segments[vector] = v;
        }
        segments[Formations.Root + TacticMemory.TableOffset] = pointers;
    }
    private (byte[] Bytes, int Offset)? Find(ulong address, int size)
    {
        var matches = segments.Where(s => address >= s.Key && address + (ulong)size <= s.Key + (ulong)s.Value.Length).ToArray();
        return matches.Length == 0 ? null : (matches.Single().Value, (int)(address - matches.Single().Key));
    }
    public byte[] Read(ulong address, int size) => Find(address, size) is { } s ? s.Bytes.AsSpan(s.Offset, size).ToArray() : Formations.Read(address, size);
    internal void Set(ulong address, byte[] bytes)
    {
        if (Find(address, bytes.Length) is { } s) bytes.CopyTo(s.Bytes, s.Offset); else Formations.Set(address, bytes);
    }
    public void Write(ulong address, byte[] bytes)
    {
        ++Writes;
        if (Writes == FailWrite) { Set(address, bytes[..Math.Max(1, bytes.Length / 2)]); throw new IOException("Injected partial write"); }
        Set(address, bytes); OnWrite?.Invoke(Writes);
    }
    internal TacticSnapshot Snapshot() => TacticMemory.Read(this, Identity);
    internal RuleProfile Profile() => Formations.Snapshot().Baseline(Identity.ExeHash).AddTactics(Snapshot());
}
