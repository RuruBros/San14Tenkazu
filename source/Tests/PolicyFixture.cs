using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace San14Tenkazu.Tests;

internal sealed class PolicyFixture : IRecordMemory
{
    internal readonly TacticFixture Tactics = new();
    internal GameIdentity Identity => Tactics.Identity;
    private readonly Dictionary<ulong, byte[]> segments = [];
    internal ulong PolicyArray = 0x307000000, EffectArray = 0x308000000, Vectors = 0x309000000;
    internal int Writes, FailWrite;
    internal Action<int>? OnWrite { get; set; }
    internal PolicyFixture(bool encoded = true) => Load(encoded);
    internal void Load(bool encoded = true)
    {
        foreach (var t in new[] { (Table: "policy", Array: PolicyArray, Size: 0xA8, Count: 121, Class: Identity.Classes.Policy, Offset: PolicyMemory.PolicyTableOffset),
            (Table: "policyeffect", Array: EffectArray, Size: 0xB0, Count: 101, Class: Identity.Classes.PolicyEffect, Offset: PolicyMemory.EffectTableOffset) })
        {
            byte[] pointers = new byte[t.Count * 8];
            for (int id = 0; id < t.Count; ++id)
            {
                ulong at = t.Array + (ulong)(id * t.Size); BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(id * 8), at);
                byte[] data = new byte[t.Size]; BinaryPrimitives.WriteUInt64LittleEndian(data, t.Class);
                if (id is 1 or 2) Encoding.Unicode.GetBytes($"P{id}").CopyTo(data, 0x10);
                if (t.Table == "policy") data[0x98] = 1;
                else
                {
                    byte[] v = new byte[40]; v.AsSpan().Fill(0xA5);
                    foreach (var f in PolicyRules.Levels)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(f.Plain), (ushort)((f.Slot + 1) * 2));
                        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(f.Slot * 4), PolicyRules.Encode((f.Slot + 1) * 2, id));
                    }
                    ulong vector = Vectors + (ulong)(id * 64);
                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x98), vector);
                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0xA0), vector + (encoded ? 40UL : 0));
                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0xA8), vector + 40);
                    segments[vector] = v;
                }
                segments[at] = data;
            }
            segments[Tactics.Formations.Root + t.Offset] = pointers;
        }
    }
    private (byte[] Bytes, int Offset)? Find(ulong address, int count)
    {
        var found = segments.Where(s => address >= s.Key && address + (ulong)count <= s.Key + (ulong)s.Value.Length).ToArray();
        return found.Length == 0 ? null : (found.Single().Value, (int)(address - found.Single().Key));
    }
    public byte[] Read(ulong address, int count) => Find(address, count) is { } s ? s.Bytes.AsSpan(s.Offset, count).ToArray() : Tactics.Read(address, count);
    internal void Set(ulong address, byte[] bytes)
    {
        if (Find(address, bytes.Length) is { } s) bytes.CopyTo(s.Bytes, s.Offset); else Tactics.Set(address, bytes);
    }
    public void Write(ulong address, byte[] bytes)
    {
        ++Writes;
        if (Writes == FailWrite) { Set(address, bytes[..Math.Max(1, bytes.Length / 2)]); throw new IOException("Injected policy partial write"); }
        Set(address, bytes); OnWrite?.Invoke(Writes);
    }
    internal PolicySnapshot Snapshot() => PolicyMemory.Read(this, Identity);
    internal RuleProfile Profile() => Tactics.Profile().AddPolicies(Snapshot());
}
