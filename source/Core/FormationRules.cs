using System.Buffers.Binary;
using System.Security.Cryptography;

namespace San14Tenkazu;

internal sealed record FormationField(string Id, string Label, int Plain, int Vector, int Rotate, int Maximum)
{
    internal int StorageMax => 255;
    internal FieldDef Input => new(Label, RecordKind.Formation, Plain, FieldKind.Byte, 0, Maximum);
}

internal static class FormationRules
{
    internal static readonly int[] EditableIds = Enumerable.Range(1, 20).ToArray();
    // Native getters: 2B0210, 2B2720, 2B2690, 2A9DC0, 2B27B0. Only these five verified scalars.
    internal static readonly FormationField[] Fields = [
        new("mobility", L.Rules.Mobility, 0x7F, 0, 5, 30),
        new("attack", L.Rules.Attack, 0x83, 12, 1, 45),
        new("siege", L.Rules.Siege, 0x84, 16, 3, 70),
        new("defense", L.Rules.Defense, 0x86, 24, 7, 40),
        new("break", L.Rules.Break, 0x85, 20, 5, 60)];
    internal static FormationField Field(string id) => Fields.Single(f => f.Id == id);
    internal static bool ValidKey(RuleKey? key) => key is { Table: "formation" } && EditableIds.Contains(key.Record) && Fields.Any(f => f.Id == key.Field);
    internal static RuleKey Key(int id, FormationField field) => new("formation", id, field.Id);
    internal static byte Decode(byte encoded, int rotate, int id) => (byte)(((encoded << rotate) | (encoded >> (8 - rotate))) ^ id);
    internal static byte Encode(int raw, int rotate, int id)
    {
        int value = (raw ^ id) & 255;
        return (byte)((value >> rotate) | (value << (8 - rotate)));
    }
    internal static int Effective(int raw, FormationField field, bool encoded) => encoded ? Math.Clamp(raw, 1, field.Maximum) : raw;
}

internal sealed record FormationRecord(int Id, ulong Address, byte[] Data, ulong VectorAddress, byte[] Vector)
{
    internal string Name => GameMemory.ReadName(Data, 0x10, 5);
    internal int Value(FormationField field) => Vector.Length == 32
        ? FormationRules.Decode(Vector[field.Vector], field.Rotate, Id) : Data[field.Plain];
    internal bool MirrorsAgree => FormationRules.Fields.All(f => Value(f) == Data[f.Plain]);
}

internal sealed record FormationSnapshot(ulong Root, ulong State, ulong Array, FormationRecord[] Records, string Catalog)
{
    internal FormationRecord Record(int id) => Records.Single(r => r.Id == id);
    internal Dictionary<RuleKey, int> Values => Records.SelectMany(r => FormationRules.Fields.Select(f =>
        new KeyValuePair<RuleKey, int>(FormationRules.Key(r.Id, f), r.Value(f)))).ToDictionary();
    internal RuleProfile Baseline(string build)
    {
        if (Records.Any(r => !r.MirrorsAgree)) throw new InvalidDataException(L.Rules.WriteFailed);
        RuleProfile profile = RuleProfile.Create(build, Catalog, Values.Select(v => new RuleDefault(v.Key, v.Value)));
        profile.Validate();
        return profile;
    }
    internal void Check(RuleProfile profile, string build)
    {
        profile.Validate();
        if (!string.Equals(profile.Build, build, StringComparison.OrdinalIgnoreCase) || profile.Catalog != Catalog)
            throw new InvalidDataException(L.Rules.Incompatible);
    }
}

// Resolves the game's current root/table on every read, including after loading another save. Nothing here
// accepts pointers from a saved profile. The native ID getter derives ID from the contiguous 0xC8 array.
internal static class FormationMemory
{
    internal const ulong RootRva = 0x277DF18, TableOffset = 0x77428, StateOffset = 0x871E8;
    private static ulong Pointer(IRecordMemory memory, ulong at) => BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(at, 8));
    private static bool Address(ulong at) => at is >= 0x10000 and < 0x0000800000000000 && (at & 7) == 0;
    internal static FormationSnapshot Read(IRecordMemory memory, GameIdentity game)
    {
        if (!game.Verified || game.Classes.Formation == 0 || game.Classes.World == 0) throw new InvalidDataException(L.Rules.Incompatible);
        ulong root = Pointer(memory, game.ModuleBase + RootRva);
        if (!Address(root)) throw new InvalidDataException(L.Rules.NotReady);
        byte[] pointers = memory.Read(root + TableOffset, 21 * 8);
        ulong array = BinaryPrimitives.ReadUInt64LittleEndian(pointers);
        ulong state = Pointer(memory, root + StateOffset);
        if (!Address(array) || !Address(state) || Pointer(memory, state) != game.Classes.World) throw new InvalidDataException(L.Rules.NotReady);
        byte[] clock = memory.Read(state + 0x34, 5);
        int year = BinaryPrimitives.ReadUInt16LittleEndian(clock);
        if (year is <= 100 or >= 400 || clock[2] is < 1 or > 12 || clock[3] is < 1 or > 31) throw new InvalidDataException(L.Rules.NotReady);
        var records = new List<FormationRecord>();
        using var catalog = new MemoryStream();
        for (int id = 1; id <= 20; id++)
        {
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(id * 8));
            if (address != array + (ulong)(id * 0xC8)) throw new InvalidDataException(L.Rules.NotReady);
            byte[] data = memory.Read(address, 0xC8);
            if (BinaryPrimitives.ReadUInt64LittleEndian(data) != game.Classes.Formation) throw new InvalidDataException(L.Rules.NotReady);
            ulong start = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0xA8)), end = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0xB0));
            ulong cap = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0xB8));
            // The native fallback uses plain bytes when the vector is empty. Reject malformed nonempty vectors.
            bool empty = start == end && (start == 0 && cap == 0 || Address(start) && cap >= end);
            if (!empty && (!Address(start) || end != start + 32 || cap < end || cap - start > 4096)) throw new InvalidDataException(L.Rules.NotReady);
            byte[] vector = empty ? [] : memory.Read(start, 32);
            records.Add(new(id, address, data, start, vector));
            catalog.WriteByte((byte)id);
            catalog.Write(data, 0x10, 10); // Name and kind identify the catalog, not values changed by this editor.
            catalog.WriteByte(data[0x7E]);
        }
        if (Pointer(memory, game.ModuleBase + RootRva) != root || Pointer(memory, root + StateOffset) != state
            || !memory.Read(root + TableOffset, pointers.Length).AsSpan().SequenceEqual(pointers)) throw new InvalidDataException(L.Rules.Stale);
        return new(root, state, array, records.ToArray(), Convert.ToHexString(SHA256.HashData(catalog.ToArray())));
    }

    internal static FormationSnapshot Apply(IRecordMemory memory, GameIdentity game, FormationSnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        if (changes.Select(c => c.Key).Distinct().Count() != changes.Count || changes.Any(c => !FormationRules.ValidKey(c.Key)
            || c.After < 0 || c.After > FormationRules.Field(c.Key.Field).StorageMax)) throw new InvalidDataException(L.Rules.BadProfile);
        FormationSnapshot fresh = Read(memory, game);
        if (fresh.Root != expected.Root || fresh.State != expected.State || fresh.Array != expected.Array || fresh.Catalog != expected.Catalog)
            throw new InvalidOperationException(L.Rules.Stale);
        foreach (RuleChange change in changes)
        {
            FormationRecord r = fresh.Record(change.Key.Record), old = expected.Record(change.Key.Record);
            FormationField f = FormationRules.Field(change.Key.Field);
            if (r.Address != old.Address || r.VectorAddress != old.VectorAddress || r.Vector.Length != old.Vector.Length
                || r.Value(f) != change.Before || r.Data[f.Plain] != old.Data[f.Plain] || r.Data[f.Plain] != r.Value(f)) throw new InvalidOperationException(L.Rules.Stale);
        }
        var writes = new List<(ulong At, byte[] Before, byte[] After)>();
        foreach (var group in changes.Where(c => c.Before != c.After).GroupBy(c => c.Key.Record))
        {
            FormationRecord r = fresh.Record(group.Key);
            byte[] plain = r.Data.AsSpan(0x7F, 8).ToArray(), vector = r.Vector.ToArray();
            foreach (RuleChange c in group)
            {
                FormationField f = FormationRules.Field(c.Key.Field);
                plain[f.Plain - 0x7F] = (byte)c.After;
                if (vector.Length == 32) vector[f.Vector] = FormationRules.Encode(c.After, f.Rotate, r.Id);
            }
            writes.Add((r.Address + 0x7F, r.Data.AsSpan(0x7F, 8).ToArray(), plain));
            if (vector.Length == 32) writes.Add((r.VectorAddress, r.Vector, vector));
        }
        var attempted = new List<(ulong At, byte[] Before, byte[] After)>();
        void Guard()
        {
            FormationSnapshot now = Read(memory, game);
            if (now.Root != fresh.Root || now.State != fresh.State || now.Array != fresh.Array || now.Catalog != fresh.Catalog
                || now.Records.Any(r => r.VectorAddress != fresh.Record(r.Id).VectorAddress || r.Vector.Length != fresh.Record(r.Id).Vector.Length))
                throw new InvalidOperationException(L.Rules.Stale);
        }
        try
        {
            foreach (var write in writes)
            {
                Guard();
                if (!memory.Read(write.At, write.Before.Length).AsSpan().SequenceEqual(write.Before)) throw new InvalidOperationException(L.Rules.Stale);
                attempted.Add(write); // Includes a failed/partial write so rollback covers it too.
                memory.Write(write.At, write.After);
                if (!memory.Read(write.At, write.After.Length).AsSpan().SequenceEqual(write.After)) throw new IOException(L.Rules.WriteFailed);
            }
            Guard();
            FormationSnapshot result = Read(memory, game);
            if (changes.Any(c => result.Record(c.Key.Record).Value(FormationRules.Field(c.Key.Field)) != c.After
                || result.Record(c.Key.Record).Data[FormationRules.Field(c.Key.Field).Plain] != c.After)) throw new IOException(L.Rules.WriteFailed);
            return result;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var write in attempted.AsEnumerable().Reverse())
                try
                {
                    Guard(); // Never restore bytes into an old table/vector after a load.
                    byte[] now = memory.Read(write.At, write.Before.Length);
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
