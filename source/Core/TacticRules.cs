using System.Buffers.Binary;
using System.Security.Cryptography;

namespace San14Tenkazu;

internal sealed record TacticField(string Id, string Label, int Plain, int Vector, int Rotate, int Maximum, int Slot)
{
    internal FieldDef Input => new(Label, RecordKind.Tactic, Plain, FieldKind.Byte, Id == "ability" ? 1 : 0, Maximum);
}

internal static class TacticRules
{
    // Unmodified Native getters: kind 16D510, range 16D5A0, amount 16D630,
    // level 16D6C0 and ability 16CDA0. Slots are exactly 0 and 1.
    internal static readonly TacticField[] Fields = [
        .. Enumerable.Range(0, 2).SelectMany(slot => new TacticField[] {
            new($"kind{slot + 1}", L.Rules.EffectKind, 0x62 + slot, 4 + slot * 4, 7, 23, slot),
            new($"range{slot + 1}", L.Rules.EffectRange, 0x64 + slot, 12 + slot * 4, 1, 255, slot),
            new($"amount{slot + 1}", L.Rules.EffectAmount, 0x66 + slot, 20 + slot * 4, 3, 255, slot),
            new($"level{slot + 1}", L.Rules.EffectLevel, 0x68 + slot, 28 + slot * 4, 5, 10, slot) }),
        new("ability", L.Rules.AbilityBasis, 0x6A, 36, 7, 2, -1)];
    internal static TacticField Field(string id) => Fields.Single(f => f.Id == id);
    internal static bool ValidKey(RuleKey? key) => key is { Table: "tactic", Record: >= 1 and <= 200 } && Fields.Any(f => f.Id == key.Field);
    internal static RuleKey Key(int id, TacticField field) => new("tactic", id, field.Id);
    internal static string KindName(int kind) => L.Table("TacticKinds", kind);
    internal static string AmountLabel(int kind) => kind switch {
        2 or 3 or 4 or 17 or 23 or >= 5 and <= 14 or 21 or 22 => L.Rules.Duration,
        1 => L.Rules.DamageFactor, 15 or 16 => L.Rules.MoraleAmount,
        18 => L.Rules.SiegeAmount, 20 => L.Rules.HealFactor, _ => L.Rules.EffectAmount };
}

internal sealed record TacticRecord(int Id, ulong Address, byte[] Data, ulong VectorAddress, byte[] Vector)
{
    internal string Name => GameMemory.ReadName(Data, 0x10, 5);
    internal bool Named => !GameMemory.IsPlaceholder(Name);
    internal int Value(TacticField field) => Vector.Length == 40 ? FormationRules.Decode(Vector[field.Vector], field.Rotate, Id) : Data[field.Plain];
    internal bool MirrorsAgree => TacticRules.Fields.All(f => Value(f) == Data[f.Plain])
        && (Vector.Length != 40 || FormationRules.Decode(Vector[0], 1, Id) == Data[0x44]);
}

internal sealed record TacticSnapshot(ulong Root, ulong State, ulong Array, TacticRecord[] Records, string Catalog)
{
    internal TacticRecord Record(int id) => Records.Single(r => r.Id == id);
    internal Dictionary<RuleKey, int> Values => Records.SelectMany(r => TacticRules.Fields.Select(f => new KeyValuePair<RuleKey, int>(TacticRules.Key(r.Id, f), r.Value(f)))).ToDictionary();
    internal void Check(RuleProfile profile, string build)
    {
        profile.Validate();
        if (!string.Equals(profile.Build, build, StringComparison.OrdinalIgnoreCase) || profile.Table("tactic") is not { } c
            || c.Fingerprint != Catalog || !c.Records.SequenceEqual(Records.Select(r => r.Id))) throw new InvalidDataException(L.Rules.Incompatible);
    }
}

// Native index 22E0A0 is (record - table[0])/0x90. Resolve every pointer again;
// never use a profile address or a partially constructed nonempty vector as fallback.
internal static class TacticMemory
{
    internal const ulong TableOffset = 0x774D0;
    private static ulong Pointer(IRecordMemory m, ulong at) => BinaryPrimitives.ReadUInt64LittleEndian(m.Read(at, 8));
    private static bool Address(ulong at) => at is >= 0x10000 and < 0x0000800000000000 && (at & 7) == 0;
    internal static TacticSnapshot Read(IRecordMemory memory, GameIdentity game)
    {
        if (!game.Verified || game.Classes.Tactic == 0 || game.Classes.World == 0) throw new InvalidDataException(L.Rules.Incompatible);
        ulong root = Pointer(memory, game.ModuleBase + FormationMemory.RootRva);
        if (!Address(root)) throw new InvalidDataException(L.Rules.NotReady);
        byte[] pointers = memory.Read(root + TableOffset, 201 * 8);
        ulong array = BinaryPrimitives.ReadUInt64LittleEndian(pointers), state = Pointer(memory, root + FormationMemory.StateOffset);
        if (!Address(array) || !Address(state) || Pointer(memory, state) != game.Classes.World) throw new InvalidDataException(L.Rules.NotReady);
        byte[] clock = memory.Read(state + 0x34, 5);
        int year = BinaryPrimitives.ReadUInt16LittleEndian(clock);
        if (year is <= 100 or >= 400 || clock[2] is < 1 or > 12 || clock[3] is < 1 or > 31) throw new InvalidDataException(L.Rules.NotReady);
        var records = new List<TacticRecord>();
        using var catalog = new MemoryStream();
        for (int id = 1; id <= 200; ++id)
        {
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(id * 8));
            if (address != array + (ulong)(id * 0x90)) throw new InvalidDataException(L.Rules.NotReady);
            byte[] data = memory.Read(address, 0x90);
            if (BinaryPrimitives.ReadUInt64LittleEndian(data) != game.Classes.Tactic) throw new InvalidDataException(L.Rules.NotReady);
            catalog.WriteByte((byte)id); catalog.Write(data, 0x10, 10); catalog.WriteByte(data[0x40]);
            ulong start = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x70)), end = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x78)), cap = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x80));
            bool empty = start == end && (start == 0 && cap == 0 || Address(start) && cap >= end);
            if (!empty && (!Address(start) || end != start + 40 || cap < end || cap - start > 4096)) throw new InvalidDataException(L.Rules.NotReady);
            byte[] vector = empty ? [] : memory.Read(start, 40);
            var record = new TacticRecord(id, address, data, start, vector);
            if (record.Named) records.Add(record);
        }
        if (records.Count == 0) throw new InvalidDataException(L.Rules.NotReady);
        if (Pointer(memory, game.ModuleBase + FormationMemory.RootRva) != root || Pointer(memory, root + FormationMemory.StateOffset) != state
            || !memory.Read(root + TableOffset, pointers.Length).AsSpan().SequenceEqual(pointers)) throw new InvalidDataException(L.Rules.Stale);
        return new(root, state, array, records.ToArray(), Convert.ToHexString(SHA256.HashData(catalog.ToArray())));
    }

    internal static TacticSnapshot Apply(IRecordMemory memory, GameIdentity game, TacticSnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        if (changes.Select(c => c.Key).Distinct().Count() != changes.Count || changes.Any(c => !TacticRules.ValidKey(c.Key) || c.After is < 0 or > 255)) throw new InvalidDataException(L.Rules.BadProfile);
        TacticSnapshot fresh = Read(memory, game);
        if (fresh.Root != expected.Root || fresh.State != expected.State || fresh.Array != expected.Array || fresh.Catalog != expected.Catalog) throw new InvalidOperationException(L.Rules.Stale);
        foreach (RuleChange c in changes)
        {
            TacticRecord r = fresh.Record(c.Key.Record), old = expected.Record(c.Key.Record); TacticField f = TacticRules.Field(c.Key.Field);
            if (r.Address != old.Address || r.VectorAddress != old.VectorAddress || r.Vector.Length != old.Vector.Length
                || r.Value(f) != c.Before || r.Data[f.Plain] != old.Data[f.Plain] || !r.MirrorsAgree) throw new InvalidOperationException(L.Rules.Stale);
        }
        var writes = new List<(ulong At, byte[] Before, byte[] After)>();
        foreach (var group in changes.Where(c => c.Before != c.After).GroupBy(c => c.Key.Record))
        {
            TacticRecord r = fresh.Record(group.Key);
            byte[] plain = r.Data.AsSpan(0x62, 9).ToArray(), vector = r.Vector.ToArray();
            foreach (RuleChange c in group)
            {
                TacticField f = TacticRules.Field(c.Key.Field); plain[f.Plain - 0x62] = (byte)c.After;
                if (vector.Length == 40) vector[f.Vector] = FormationRules.Encode(c.After, f.Rotate, r.Id);
            }
            writes.Add((r.Address + 0x62, r.Data.AsSpan(0x62, 9).ToArray(), plain));
            if (vector.Length == 40) writes.Add((r.VectorAddress, r.Vector, vector));
        }
        var attempted = new List<(ulong At, byte[] Before, byte[] After)>();
        void Guard()
        {
            TacticSnapshot now = Read(memory, game);
            if (now.Root != fresh.Root || now.State != fresh.State || now.Array != fresh.Array || now.Catalog != fresh.Catalog
                || now.Records.Any(r => r.VectorAddress != fresh.Record(r.Id).VectorAddress || r.Vector.Length != fresh.Record(r.Id).Vector.Length)) throw new InvalidOperationException(L.Rules.Stale);
        }
        try
        {
            foreach (var write in writes)
            {
                Guard();
                if (!memory.Read(write.At, write.Before.Length).AsSpan().SequenceEqual(write.Before)) throw new InvalidOperationException(L.Rules.Stale);
                attempted.Add(write); memory.Write(write.At, write.After);
                if (!memory.Read(write.At, write.After.Length).AsSpan().SequenceEqual(write.After)) throw new IOException(L.Rules.WriteFailed);
            }
            Guard(); TacticSnapshot result = Read(memory, game);
            if (changes.Any(c => result.Record(c.Key.Record).Value(TacticRules.Field(c.Key.Field)) != c.After || !result.Record(c.Key.Record).MirrorsAgree)) throw new IOException(L.Rules.WriteFailed);
            return result;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var write in attempted.AsEnumerable().Reverse())
                try
                {
                    Guard(); byte[] now = memory.Read(write.At, write.Before.Length);
                    if (now.Where((b, i) => b != write.Before[i] && b != write.After[i]).Any()) throw new IOException(L.Rules.Stale);
                    memory.Write(write.At, write.Before);
                    if (!memory.Read(write.At, write.Before.Length).AsSpan().SequenceEqual(write.Before)) throw new IOException(L.Rules.WriteFailed);
                }
                catch (Exception rollback) { errors.Add(rollback); }
            if (errors.Count > 1) throw new AggregateException(L.Rules.RollbackFailed, errors);
            throw;
        }
    }
}
