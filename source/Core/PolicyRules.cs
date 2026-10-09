using System.Buffers.Binary;
using System.Security.Cryptography;

namespace San14Tenkazu;

internal sealed record PolicyField(string Table, string Id, string Label, int Plain, int Slot)
{
    internal int Width => Table == "policy" ? 1 : 2;
    internal int Maximum => Width == 1 ? 100 : ushort.MaxValue;
    internal FieldDef Input => new(Label, RecordKind.Policy, Plain, Width == 1 ? FieldKind.Byte : FieldKind.UInt16, 0, Maximum);
}

internal static class PolicyRules
{
    internal static readonly PolicyField[] Effects = Enumerable.Range(0, 8).Select(i => new PolicyField("policy", $"effect{i + 1}", L.Rules.PolicySlot(i + 1), 0x98 + i, i)).ToArray();
    internal static readonly PolicyField[] Levels = Enumerable.Range(0, 10).Select(i => new PolicyField("policyeffect", $"level{i + 1}", L.Rules.PolicyLevel(i + 1), 0x82 + i * 2, i)).ToArray();
    internal static PolicyField[] Fields(string table) => table switch { "policy" => Effects, "policyeffect" => Levels, _ => throw new InvalidDataException(L.Rules.BadProfile) };
    internal static PolicyField Field(RuleKey key) => Fields(key.Table).Single(f => f.Id == key.Field);
    internal static bool ValidKey(RuleKey? key) => key is not null && (key.Table == "policy" && key.Record is >= 1 and <= 120 && Effects.Any(f => f.Id == key.Field)
        || key.Table == "policyeffect" && key.Record is >= 1 and <= 100 && Levels.Any(f => f.Id == key.Field));
    internal static RuleKey Key(int id, PolicyField field) => new(field.Table, id, field.Id);
    internal static ushort Encode(int value, int id) { int x = value ^ id; return (ushort)((x >> 11) | (x << 5)); }
    internal static int Decode(ushort value, int id) => (ushort)(((value << 11) | (value >> 5)) ^ id);
}

internal sealed record PolicyRecord(string Table, int Id, ulong Address, byte[] Data, ulong VectorAddress, byte[] Vector)
{
    internal string Name => GameMemory.ReadName(Data, 0x10, 6);
    internal bool Named => Name != "0" && !GameMemory.IsPlaceholder(Name);
    internal int Value(PolicyField field) => field.Width == 1 ? Data[field.Plain] : Vector.Length == 40
        ? PolicyRules.Decode(BinaryPrimitives.ReadUInt16LittleEndian(Vector.AsSpan(field.Slot * 4)), Id)
        : BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(field.Plain));
    internal bool MirrorsAgree => Table == "policy" || PolicyRules.Levels.All(f => Value(f) == BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(f.Plain)));
}

internal sealed record PolicySnapshot(ulong Root, ulong State, ulong PolicyArray, ulong EffectArray, PolicyRecord[] Records, RuleCatalog[] Catalogs)
{
    internal PolicyRecord Record(string table, int id) => Records.Single(r => r.Table == table && r.Id == id);
    internal Dictionary<RuleKey, int> Values => Records.SelectMany(r => PolicyRules.Fields(r.Table).Select(f => new KeyValuePair<RuleKey, int>(PolicyRules.Key(r.Id, f), r.Value(f)))).ToDictionary();
    internal bool SameStorage(PolicySnapshot other) => Root == other.Root && State == other.State && PolicyArray == other.PolicyArray && EffectArray == other.EffectArray
        && Catalogs.Zip(other.Catalogs).All(p => p.First.Table == p.Second.Table && p.First.Fingerprint == p.Second.Fingerprint)
        && Catalogs.Length == other.Catalogs.Length;
    internal void Check(RuleProfile profile, string build)
    {
        profile.Validate();
        if (!string.Equals(profile.Build, build, StringComparison.OrdinalIgnoreCase) || Catalogs.Any(c => profile.Table(c.Table) is not { } saved
            || c.Fingerprint != saved.Fingerprint || !c.Records.SequenceEqual(saved.Records))) throw new InvalidDataException(L.Rules.Incompatible);
    }
}

// CPolicyData +98..9F are direct effect IDs. CPolicyEffectData has ten ushort mirrors
// at +82..94 and a 40-byte dword vector at +98/A0/A8. Native 22C590 rotates 16 bits by 11.
internal static class PolicyMemory
{
    internal const ulong PolicyTableOffset = 0x78D50, EffectTableOffset = 0x80880;
    private static ulong Pointer(IRecordMemory m, ulong at) => BinaryPrimitives.ReadUInt64LittleEndian(m.Read(at, 8));
    private static bool Address(ulong at) => at is >= 0x10000 and < 0x0000800000000000 && (at & 7) == 0;
    internal static PolicySnapshot Read(IRecordMemory memory, GameIdentity game)
    {
        if (!game.Verified || game.Classes.Policy == 0 || game.Classes.PolicyEffect == 0 || game.Classes.World == 0) throw new InvalidDataException(L.Rules.Incompatible);
        ulong root = Pointer(memory, game.ModuleBase + FormationMemory.RootRva);
        if (!Address(root)) throw new InvalidDataException(L.Rules.NotReady);
        ulong state = Pointer(memory, root + FormationMemory.StateOffset);
        if (!Address(state) || Pointer(memory, state) != game.Classes.World) throw new InvalidDataException(L.Rules.NotReady);
        byte[] clock = memory.Read(state + 0x34, 5);
        int year = BinaryPrimitives.ReadUInt16LittleEndian(clock);
        if (year is <= 100 or >= 400 || clock[2] is < 1 or > 12 || clock[3] is < 1 or > 31) throw new InvalidDataException(L.Rules.NotReady);
        var rows = new List<PolicyRecord>(); var catalogs = new List<RuleCatalog>(); var arrays = new List<ulong>();
        foreach (var t in new[] { (Table: "policy", Offset: PolicyTableOffset, Size: 0xA8, Count: 121, Class: game.Classes.Policy),
                                 (Table: "policyeffect", Offset: EffectTableOffset, Size: 0xB0, Count: 101, Class: game.Classes.PolicyEffect) })
        {
            byte[] pointers = memory.Read(root + t.Offset, t.Count * 8);
            ulong array = BinaryPrimitives.ReadUInt64LittleEndian(pointers); arrays.Add(array);
            if (!Address(array)) throw new InvalidDataException(L.Rules.NotReady);
            using var catalog = new MemoryStream(); var ids = new List<int>();
            for (int id = 1; id < t.Count; ++id)
            {
                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(id * 8));
                if (address != array + (ulong)(id * t.Size)) throw new InvalidDataException(L.Rules.NotReady);
                byte[] data = memory.Read(address, t.Size);
                if (BinaryPrimitives.ReadUInt64LittleEndian(data) != t.Class) throw new InvalidDataException(L.Rules.NotReady);
                catalog.WriteByte((byte)id); catalog.Write(data, 0x10, 12);
                ulong start = 0; byte[] vector = [];
                if (t.Table == "policyeffect")
                {
                    start = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x98));
                    ulong end = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0xA0)), cap = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0xA8));
                    bool empty = start == end && (start == 0 && cap == 0 || Address(start) && cap >= end);
                    if (!empty && (!Address(start) || end != start + 40 || cap < end || cap - start > 4096)) throw new InvalidDataException(L.Rules.NotReady);
                    vector = empty ? [] : memory.Read(start, 40);
                }
                var r = new PolicyRecord(t.Table, id, address, data, start, vector);
                if (r.Named) { rows.Add(r); ids.Add(id); }
            }
            if (ids.Count == 0) throw new InvalidDataException(L.Rules.NotReady);
            catalogs.Add(new(t.Table, t.Table + "-v1", Convert.ToHexString(SHA256.HashData(catalog.ToArray())), ids.ToArray()));
            if (!memory.Read(root + t.Offset, pointers.Length).AsSpan().SequenceEqual(pointers)) throw new InvalidDataException(L.Rules.Stale);
        }
        if (Pointer(memory, game.ModuleBase + FormationMemory.RootRva) != root || Pointer(memory, root + FormationMemory.StateOffset) != state) throw new InvalidDataException(L.Rules.Stale);
        return new(root, state, arrays[0], arrays[1], rows.ToArray(), catalogs.ToArray());
    }

    internal static PolicySnapshot Apply(IRecordMemory memory, GameIdentity game, PolicySnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        // Restoring a verified byte baseline may include Native-ignored IDs 101..255.
        if (changes.Select(c => c.Key).Distinct().Count() != changes.Count || changes.Any(c => !PolicyRules.ValidKey(c.Key)
            || c.After < 0 || c.After > (c.Key.Table == "policy" ? byte.MaxValue : ushort.MaxValue))) throw new InvalidDataException(L.Rules.BadProfile);
        PolicySnapshot fresh = Read(memory, game);
        if (!fresh.SameStorage(expected)) throw new InvalidOperationException(L.Rules.Stale);
        foreach (RuleChange c in changes)
        {
            PolicyRecord r = fresh.Record(c.Key.Table, c.Key.Record), old = expected.Record(c.Key.Table, c.Key.Record); PolicyField f = PolicyRules.Field(c.Key);
            if (r.Address != old.Address || r.VectorAddress != old.VectorAddress || r.Vector.Length != old.Vector.Length || r.Value(f) != c.Before
                || !r.Data.AsSpan(f.Plain, f.Width).SequenceEqual(old.Data.AsSpan(f.Plain, f.Width)) || !r.MirrorsAgree) throw new InvalidOperationException(L.Rules.Stale);
        }
        var writes = new List<(ulong At, byte[] Before, byte[] After)>();
        foreach (var group in changes.Where(c => c.Before != c.After).GroupBy(c => (c.Key.Table, c.Key.Record)))
        {
            PolicyRecord r = fresh.Record(group.Key.Table, group.Key.Record);
            int at = r.Table == "policy" ? 0x98 : 0x82, count = r.Table == "policy" ? 8 : 20;
            byte[] plain = r.Data.AsSpan(at, count).ToArray(), vector = r.Vector.ToArray();
            foreach (RuleChange c in group)
            {
                PolicyField f = PolicyRules.Field(c.Key);
                if (f.Width == 1) plain[f.Plain - at] = (byte)c.After;
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(f.Plain - at), (ushort)c.After);
                    if (vector.Length == 40) BinaryPrimitives.WriteUInt16LittleEndian(vector.AsSpan(f.Slot * 4), PolicyRules.Encode(c.After, r.Id));
                }
            }
            writes.Add((r.Address + (ulong)at, r.Data.AsSpan(at, count).ToArray(), plain));
            if (vector.Length == 40) writes.Add((r.VectorAddress, r.Vector, vector));
        }
        var attempted = new List<(ulong At, byte[] Before, byte[] After)>();
        void Guard()
        {
            PolicySnapshot now = Read(memory, game);
            if (!now.SameStorage(fresh) || now.Records.Any(r => r.Address != fresh.Record(r.Table, r.Id).Address
                || r.VectorAddress != fresh.Record(r.Table, r.Id).VectorAddress || r.Vector.Length != fresh.Record(r.Table, r.Id).Vector.Length)) throw new InvalidOperationException(L.Rules.Stale);
        }
        try
        {
            foreach (var w in writes)
            {
                Guard(); if (!memory.Read(w.At, w.Before.Length).AsSpan().SequenceEqual(w.Before)) throw new InvalidOperationException(L.Rules.Stale);
                attempted.Add(w); memory.Write(w.At, w.After);
                if (!memory.Read(w.At, w.After.Length).AsSpan().SequenceEqual(w.After)) throw new IOException(L.Rules.WriteFailed);
            }
            Guard(); PolicySnapshot result = Read(memory, game);
            if (changes.Any(c => result.Record(c.Key.Table, c.Key.Record).Value(PolicyRules.Field(c.Key)) != c.After
                || !result.Record(c.Key.Table, c.Key.Record).MirrorsAgree)) throw new IOException(L.Rules.WriteFailed);
            return result;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var w in attempted.AsEnumerable().Reverse())
                try
                {
                    Guard(); byte[] now = memory.Read(w.At, w.Before.Length);
                    if (now.Where((b, i) => b != w.Before[i] && b != w.After[i]).Any()) throw new IOException(L.Rules.Stale);
                    memory.Write(w.At, w.Before);
                    if (!memory.Read(w.At, w.Before.Length).AsSpan().SequenceEqual(w.Before)) throw new IOException(L.Rules.WriteFailed);
                }
                catch (Exception recovery) { errors.Add(recovery); }
            if (errors.Count > 1) throw new AggregateException(L.Rules.RollbackFailed, errors);
            throw;
        }
    }
}
