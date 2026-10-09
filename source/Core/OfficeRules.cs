using System.Buffers.Binary;
using System.Security.Cryptography;

namespace San14Tenkazu;

internal sealed record OfficeField(string Id, string Label, int Plain, int Width, int Vector, int Rotate, int Minimum, int Maximum)
{
    internal int StorageMax => Width == 2 ? ushort.MaxValue : byte.MaxValue;
    internal FieldDef Input => new(Label, RecordKind.Office, Plain, Width == 2 ? FieldKind.UInt16 : FieldKind.Byte, Minimum, Maximum);
}

internal static class OfficeRules
{
    // Native capacity 92550, bonus 1EE270; grade is the direct byte compared in appointment paths.
    // 1..9 is the first editable grade range verified in named game records, not a Native clamp.
    internal static readonly OfficeField[] Fields = [
        new("capacity", L.Rules.OfficeCapacity, 0x34, 2, 0, 13, 0, ushort.MaxValue),
        new("leadership", L.Rules.OfficeLeadership, 0x38, 1, 4, 5, 0, byte.MaxValue),
        new("strength", L.Rules.OfficeStrength, 0x39, 1, 8, 5, 0, byte.MaxValue),
        new("intelligence", L.Rules.OfficeIntelligence, 0x3A, 1, 12, 5, 0, byte.MaxValue),
        new("politics", L.Rules.OfficePolitics, 0x3B, 1, 16, 5, 0, byte.MaxValue),
        new("grade", L.Rules.OfficeGrade, 0x37, 1, -1, 0, 1, 9)];
    internal static OfficeField Field(string id) => Fields.Single(f => f.Id == id);
    internal static bool ValidKey(RuleKey? key) => key is { Table: "office", Record: >= 1 and <= 255 } && Fields.Any(f => f.Id == key.Field);
    internal static RuleKey Key(int id, OfficeField field) => new("office", id, field.Id);
    internal static ushort EncodeWord(int value, int id) { int x = (value ^ id) & ushort.MaxValue; return (ushort)((x >> 13) | (x << 3)); }
    internal static int DecodeWord(ushort value, int id) => (ushort)(((value << 13) | (value >> 3)) ^ id);
}

internal sealed record OfficeRecord(int Id, ulong Address, byte[] Data, ulong VectorAddress, byte[] Vector)
{
    internal string Name => GameMemory.ReadName(Data, 0x10, 5);
    // The verified Korean game marks unused CPostData records with this exact stored name.
    internal bool Named => Name is not ("0" or "무효") && !GameMemory.IsPlaceholder(Name);
    internal int Plain(OfficeField f) => f.Width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(f.Plain)) : Data[f.Plain];
    internal int Value(OfficeField f) => f.Vector < 0 || Vector.Length == 0 ? Plain(f) : f.Width == 2
        ? OfficeRules.DecodeWord(BinaryPrimitives.ReadUInt16LittleEndian(Vector.AsSpan(f.Vector)), Id)
        : FormationRules.Decode(Vector[f.Vector], f.Rotate, Id);
    internal bool MirrorsAgree => OfficeRules.Fields.All(f => Value(f) == Plain(f));
}

internal sealed record OfficeSnapshot(ulong Root, ulong State, ulong Array, string Catalog, OfficeRecord[] Records)
{
    internal OfficeRecord Record(int id) => Records.Single(r => r.Id == id);
    internal Dictionary<RuleKey, int> Values => Records.SelectMany(r => OfficeRules.Fields.Select(f => new KeyValuePair<RuleKey, int>(OfficeRules.Key(r.Id, f), r.Value(f)))).ToDictionary();
    internal bool SameStorage(OfficeSnapshot other) => Root == other.Root && State == other.State && Array == other.Array && Catalog == other.Catalog;
    internal void Check(RuleProfile profile, string build)
    {
        profile.Validate();
        if (!string.Equals(profile.Build, build, StringComparison.OrdinalIgnoreCase) || profile.Table("office") is not { } saved
            || Catalog != saved.Fingerprint || !Records.Select(r => r.Id).SequenceEqual(saved.Records)) throw new InvalidDataException(L.Rules.Incompatible);
    }
}

internal static class OfficeMemory
{
    internal const ulong TableOffset = 0x784F8;
    private static ulong Pointer(IRecordMemory m, ulong at) => BinaryPrimitives.ReadUInt64LittleEndian(m.Read(at, 8));
    private static bool Address(ulong at) => at is >= 0x10000 and < 0x0000800000000000 && (at & 7) == 0;
    internal static OfficeSnapshot Read(IRecordMemory memory, GameIdentity game)
    {
        if (!game.Verified || game.Classes.Post == 0 || game.Classes.World == 0) throw new InvalidDataException(L.Rules.Incompatible);
        ulong root = Pointer(memory, game.ModuleBase + FormationMemory.RootRva);
        if (!Address(root)) throw new InvalidDataException(L.Rules.NotReady);
        ulong state = Pointer(memory, root + FormationMemory.StateOffset);
        if (!Address(state) || Pointer(memory, state) != game.Classes.World) throw new InvalidDataException(L.Rules.NotReady);
        byte[] clock = memory.Read(state + 0x34, 5); int year = BinaryPrimitives.ReadUInt16LittleEndian(clock);
        if (year is <= 100 or >= 400 || clock[2] is < 1 or > 12 || clock[3] is < 1 or > 31) throw new InvalidDataException(L.Rules.NotReady);
        byte[] pointers = memory.Read(root + TableOffset, 256 * 8);
        ulong array = BinaryPrimitives.ReadUInt64LittleEndian(pointers);
        if (!Address(array)) throw new InvalidDataException(L.Rules.NotReady);
        var rows = new List<OfficeRecord>(); using var catalog = new MemoryStream();
        for (int id = 1; id < 256; ++id)
        {
            ulong at = BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(id * 8));
            if (at != array + (ulong)(id * 0x58)) throw new InvalidDataException(L.Rules.NotReady);
            byte[] data = memory.Read(at, 0x58);
            if (BinaryPrimitives.ReadUInt64LittleEndian(data) != game.Classes.Post) throw new InvalidDataException(L.Rules.NotReady);
            catalog.WriteByte((byte)id); catalog.Write(data, 0x10, 10);
            var row = new OfficeRecord(id, at, data, 0, []);
            if (!row.Named) continue;
            ulong begin = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x40)), end = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x48)), cap = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x50));
            bool empty = begin == end && (begin == 0 && cap == 0 || Address(begin) && cap >= end && cap - begin <= 4096);
            if (!empty && (!Address(begin) || end != begin + 20 || cap < end || cap - begin > 4096)) throw new InvalidDataException(L.Rules.NotReady);
            rows.Add(row with { VectorAddress = begin, Vector = empty ? [] : memory.Read(begin, 20) });
        }
        if (!memory.Read(root + TableOffset, pointers.Length).AsSpan().SequenceEqual(pointers)
            || Pointer(memory, game.ModuleBase + FormationMemory.RootRva) != root || Pointer(memory, root + FormationMemory.StateOffset) != state) throw new InvalidOperationException(L.Rules.Stale);
        return new(root, state, array, Convert.ToHexString(SHA256.HashData(catalog.ToArray())), rows.ToArray());
    }

    internal static OfficeSnapshot Apply(IRecordMemory memory, GameIdentity game, OfficeSnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        // Restore retains the exact verified storage value, including a baseline outside the first UI range.
        if (changes.Select(c => c.Key).Distinct().Count() != changes.Count || changes.Any(c => !OfficeRules.ValidKey(c.Key)
            || c.After < 0 || c.After > OfficeRules.Field(c.Key.Field).StorageMax)) throw new InvalidDataException(L.Rules.BadProfile);
        OfficeSnapshot fresh = Read(memory, game);
        if (!fresh.SameStorage(expected)) throw new InvalidOperationException(L.Rules.Stale);
        foreach (RuleChange c in changes)
        {
            OfficeRecord r = fresh.Record(c.Key.Record), old = expected.Record(c.Key.Record); OfficeField f = OfficeRules.Field(c.Key.Field);
            if (r.Address != old.Address || r.VectorAddress != old.VectorAddress || r.Vector.Length != old.Vector.Length || r.Value(f) != c.Before
                || !r.Data.AsSpan(f.Plain, f.Width).SequenceEqual(old.Data.AsSpan(f.Plain, f.Width)) || !r.MirrorsAgree) throw new InvalidOperationException(L.Rules.Stale);
        }
        var writes = new List<(ulong At, byte[] Before, byte[] After)>();
        foreach (var group in changes.Where(c => c.Before != c.After).GroupBy(c => c.Key.Record))
        {
            OfficeRecord r = fresh.Record(group.Key); byte[] plain = r.Data.AsSpan(0x34, 8).ToArray(), vector = r.Vector.ToArray();
            foreach (RuleChange c in group)
            {
                OfficeField f = OfficeRules.Field(c.Key.Field);
                if (f.Width == 2) BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(f.Plain - 0x34), (ushort)c.After);
                else plain[f.Plain - 0x34] = (byte)c.After;
                if (vector.Length == 20 && f.Vector >= 0)
                {
                    if (f.Width == 2) BinaryPrimitives.WriteUInt16LittleEndian(vector.AsSpan(f.Vector), OfficeRules.EncodeWord(c.After, r.Id));
                    else vector[f.Vector] = FormationRules.Encode(c.After, f.Rotate, r.Id);
                }
            }
            writes.Add((r.Address + 0x34, r.Data.AsSpan(0x34, 8).ToArray(), plain));
            if (vector.Length == 20 && !vector.AsSpan().SequenceEqual(r.Vector)) writes.Add((r.VectorAddress, r.Vector, vector));
        }
        var attempted = new List<(ulong At, byte[] Before, byte[] After)>();
        void Guard()
        {
            OfficeSnapshot now = Read(memory, game);
            if (!now.SameStorage(fresh) || now.Records.Any(r => r.Address != fresh.Record(r.Id).Address
                || r.VectorAddress != fresh.Record(r.Id).VectorAddress || r.Vector.Length != fresh.Record(r.Id).Vector.Length)) throw new InvalidOperationException(L.Rules.Stale);
        }
        try
        {
            foreach (var w in writes)
            {
                Guard(); if (!memory.Read(w.At, w.Before.Length).AsSpan().SequenceEqual(w.Before)) throw new InvalidOperationException(L.Rules.Stale);
                attempted.Add(w); memory.Write(w.At, w.After);
                if (!memory.Read(w.At, w.After.Length).AsSpan().SequenceEqual(w.After)) throw new IOException(L.Rules.WriteFailed);
            }
            Guard(); OfficeSnapshot result = Read(memory, game);
            if (changes.Any(c => result.Record(c.Key.Record).Value(OfficeRules.Field(c.Key.Field)) != c.After || !result.Record(c.Key.Record).MirrorsAgree)) throw new IOException(L.Rules.WriteFailed);
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
