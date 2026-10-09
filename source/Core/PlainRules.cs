using System.Buffers.Binary;
using System.Security.Cryptography;

namespace San14Tenkazu;

// Tables whose Native consumers read plain scalar bytes. Encoded tables retain their own engines.
internal sealed record PlainRuleField(string Id, string Label, int Offset, FieldKind Kind, int Minimum, int Maximum)
{
    internal int StorageMinimum => Kind == FieldKind.Int16 ? short.MinValue : 0;
    internal int StorageMaximum => Kind switch { FieldKind.UInt16 => ushort.MaxValue, FieldKind.Int16 => short.MaxValue, _ => byte.MaxValue };
    internal FieldDef Input(RecordKind record) => new(Label, record, Offset, Kind, Minimum, Maximum);
    internal FieldDef Storage => new(Label, RecordKind.Rank, Offset, Kind, StorageMinimum, StorageMaximum);
}

internal sealed record PlainRuleSpec(string Table, ulong TableOffset, int Count, int Size, int NameChars,
    Func<GameClasses, ulong> Class, RecordKind RecordKind, PlainRuleField[] Fields, string Title, string Hint, string Search, string Initialized)
{
    internal PlainRuleField Field(string id) => Fields.Single(f => f.Id == id);
    internal bool ValidKey(RuleKey? key) => key is not null && key.Table == Table && key.Record > 0 && key.Record < Count && Fields.Any(f => f.Id == key.Field);
}

internal sealed record PlainRuleRecord(int Id, ulong Address, byte[] Data, PlainRuleSpec Spec)
{
    internal string Name => GameMemory.ReadName(Data, 0x10, Spec.NameChars);
    internal int Value(PlainRuleField field) => (int)field.Storage.Read(Data);
}

internal sealed record PlainRuleSnapshot(PlainRuleSpec Spec, ulong Root, ulong State, ulong Array, string Catalog, PlainRuleRecord[] Records)
{
    internal PlainRuleRecord Record(int id) => Records.Single(r => r.Id == id);
    internal Dictionary<RuleKey, int> Values => Records.SelectMany(r => Spec.Fields.Select(f => new KeyValuePair<RuleKey, int>(new(Spec.Table,r.Id,f.Id),r.Value(f)))).ToDictionary();
    internal bool SameStorage(PlainRuleSnapshot other) => Spec.Table == other.Spec.Table && Root == other.Root && State == other.State && Array == other.Array && Catalog == other.Catalog;
    internal void Check(RuleProfile profile, string build)
    {
        profile.Validate();
        if (!string.Equals(profile.Build,build,StringComparison.OrdinalIgnoreCase) || profile.Table(Spec.Table) is not { } saved
            || saved.Fingerprint != Catalog || !Records.Select(r => r.Id).SequenceEqual(saved.Records)) throw new InvalidDataException(L.Rules.Incompatible);
    }
}

internal static class PlainRuleMemory
{
    private static ulong Pointer(IRecordMemory m, ulong at) => BinaryPrimitives.ReadUInt64LittleEndian(m.Read(at,8));
    private static bool Address(ulong at) => at is >= 0x10000 and < 0x0000800000000000 && (at & 7) == 0;
    internal static PlainRuleSnapshot Read(IRecordMemory memory, GameIdentity game, PlainRuleSpec spec)
    {
        if (!game.Verified || spec.Class(game.Classes) == 0 || game.Classes.World == 0) throw new InvalidDataException(L.Rules.Incompatible);
        ulong root=Pointer(memory,game.ModuleBase+FormationMemory.RootRva);
        if (!Address(root)) throw new InvalidDataException(L.Rules.NotReady);
        ulong state=Pointer(memory,root+FormationMemory.StateOffset);
        if (!Address(state) || Pointer(memory,state)!=game.Classes.World) throw new InvalidDataException(L.Rules.NotReady);
        byte[] clock=memory.Read(state+0x34,5); int year=BinaryPrimitives.ReadUInt16LittleEndian(clock);
        if (year is <= 100 or >= 400 || clock[2] is < 1 or > 12 || clock[3] is < 1 or > 31) throw new InvalidDataException(L.Rules.NotReady);
        byte[] pointers=memory.Read(root+spec.TableOffset,spec.Count*8); ulong array=BinaryPrimitives.ReadUInt64LittleEndian(pointers);
        if (!Address(array)) throw new InvalidDataException(L.Rules.NotReady);
        using var catalog=new MemoryStream(); var records=new List<PlainRuleRecord>();
        for (int id=1;id<spec.Count;++id)
        {
            ulong at=BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(id*8));
            if (at!=array+(ulong)(id*spec.Size)) throw new InvalidDataException(L.Rules.NotReady);
            byte[] data=memory.Read(at,spec.Size);
            if (BinaryPrimitives.ReadUInt64LittleEndian(data)!=spec.Class(game.Classes)) throw new InvalidDataException(L.Rules.NotReady);
            catalog.WriteByte((byte)id); catalog.Write(data,0x10,spec.NameChars*2);
            var record=new PlainRuleRecord(id,at,data,spec);
            if (!GameMemory.IsPlaceholder(record.Name) && record.Name!="0") records.Add(record);
        }
        if (!memory.Read(root+spec.TableOffset,pointers.Length).AsSpan().SequenceEqual(pointers) || Pointer(memory,game.ModuleBase+FormationMemory.RootRva)!=root
            || Pointer(memory,root+FormationMemory.StateOffset)!=state) throw new InvalidOperationException(L.Rules.Stale);
        return new(spec,root,state,array,Convert.ToHexString(SHA256.HashData(catalog.ToArray())),records.ToArray());
    }

    internal static PlainRuleSnapshot Apply(IRecordMemory memory,GameIdentity game,PlainRuleSnapshot expected,IReadOnlyList<RuleChange> changes)
    {
        var spec=expected.Spec;
        if (changes.Select(c => c.Key).Distinct().Count()!=changes.Count || changes.Any(c => !spec.ValidKey(c.Key)
            || c.After<spec.Field(c.Key.Field).StorageMinimum || c.After>spec.Field(c.Key.Field).StorageMaximum)) throw new InvalidDataException(L.Rules.BadProfile);
        var fresh=Read(memory,game,spec);
        if (!fresh.SameStorage(expected)) throw new InvalidOperationException(L.Rules.Stale);
        foreach (RuleChange c in changes)
            if (fresh.Record(c.Key.Record).Value(spec.Field(c.Key.Field))!=c.Before || fresh.Record(c.Key.Record).Address!=expected.Record(c.Key.Record).Address)
                throw new InvalidOperationException(L.Rules.Stale);
        // Only the selected scalar fields are written; names, flags and padding are untouched.
        var writes=changes.Where(c => c.Before!=c.After).Select(c => {
            var r=fresh.Record(c.Key.Record); var f=spec.Field(c.Key.Field);
            return (At:r.Address+(ulong)f.Offset,Before:f.Storage.Encode(c.Before),After:f.Storage.Encode(c.After));
        }).ToArray();
        var attempted=new List<(ulong At,byte[] Before,byte[] After)>();
        void Guard() { if (!Read(memory,game,spec).SameStorage(fresh)) throw new InvalidOperationException(L.Rules.Stale); }
        try {
            foreach (var w in writes) {
                Guard(); if (!memory.Read(w.At,w.Before.Length).AsSpan().SequenceEqual(w.Before)) throw new InvalidOperationException(L.Rules.Stale);
                attempted.Add(w); memory.Write(w.At,w.After);
                if (!memory.Read(w.At,w.After.Length).AsSpan().SequenceEqual(w.After)) throw new IOException(L.Rules.WriteFailed);
            }
            Guard(); var after=Read(memory,game,spec);
            if (changes.Any(c => after.Values[c.Key]!=c.After)) throw new IOException(L.Rules.WriteFailed);
            return after;
        } catch (Exception failure) {
            var errors=new List<Exception> {failure};
            foreach (var w in attempted.AsEnumerable().Reverse()) try {
                Guard(); var now=memory.Read(w.At,w.Before.Length);
                if (now.Where((b,i) => b!=w.Before[i] && b!=w.After[i]).Any()) throw new IOException(L.Rules.Stale);
                memory.Write(w.At,w.Before);
                if (!memory.Read(w.At,w.Before.Length).AsSpan().SequenceEqual(w.Before)) throw new IOException(L.Rules.WriteFailed);
            } catch (Exception recovery) {errors.Add(recovery);}
            if (errors.Count>1) throw new AggregateException(L.Rules.RollbackFailed,errors);
            throw;
        }
    }
}
