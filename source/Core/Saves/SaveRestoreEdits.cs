using System.Text.Json;

namespace San14Tenkazu;

// A verified editor deletion is evidence, unlike the loader's all-zero trait signature alone.
// Keep it across editor restarts, scoped to the same process, officer storage and record state.
internal sealed class SaveRestoreEdits
{
    internal sealed record Scope(int ProcessId, DateTime StartTimeUtc, ulong ModuleBase, string Build,
        ulong OfficerArray, int OfficerCount, ulong State)
    {
        internal static Scope For(GameIdentity identity, GameLayout layout) => new(identity.ProcessId, identity.StartTimeUtc,
            identity.ModuleBase, identity.ExeHash, layout.OfficerArray, layout.OfficerCount, layout.State);
    }
    internal sealed record Entry(ulong Address, byte[] Record);
    internal sealed record Receipt(int Version, Scope Owner, Entry[] Entries);

    private readonly string path;
    private readonly Scope scope;
    private readonly Dictionary<ulong, byte[]> records;

    internal static string PathFor(string runtimeReceipt) => runtimeReceipt + ".officer-edits.json";

    internal SaveRestoreEdits(string path, GameIdentity identity, GameLayout layout)
    {
        this.path = path; scope = Scope.For(identity, layout);
        if (!File.Exists(path)) { records = []; return; }
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (receipt is not { Version: 1, Owner: not null, Entries: not null }
            || receipt.Entries.Any(e => e is null || e.Record is null || e.Record.Length != GameMemory.OfficerStride)
            || receipt.Entries.Select(e => e.Address).Distinct().Count() != receipt.Entries.Length)
            throw new InvalidDataException("Invalid officer edit recovery receipt.");
        if (receipt.Entries.Any(e => e.Address <= receipt.Owner.OfficerArray
            || (e.Address - receipt.Owner.OfficerArray) % GameMemory.OfficerStride != 0
            || (e.Address - receipt.Owner.OfficerArray) / GameMemory.OfficerStride >= (ulong)Math.Max(0, receipt.Owner.OfficerCount)
            || e.Record.AsSpan(SaveRestore.TraitOffset, SaveRestore.TraitBytes).IndexOfAnyExcept((byte)0) >= 0))
            throw new InvalidDataException("Invalid officer edit recovery target.");
        records = receipt.Owner == scope ? receipt.Entries.ToDictionary(e => e.Address, e => e.Record) : [];
    }

    internal bool PreservesTraits(ulong address, byte[] record) => records.TryGetValue(address, out var edited) && SameState(edited, record);

    private static bool SameState(byte[] edited, byte[] record)
    {
        if (record.Length != GameMemory.OfficerStride || !edited.AsSpan(0, 8).SequenceEqual(record.AsSpan(0, 8))
            || !edited.AsSpan(SaveRestore.TraitOffset, SaveRestore.TraitBytes).SequenceEqual(record.AsSpan(SaveRestore.TraitOffset, SaveRestore.TraitBytes))) return false;
        foreach (var (stream, offset, length) in GameSaveFile.Segments)
            // Runtime growth-mode transactions legitimately rescale experience as well as
            // abilities. Those writes are not evidence of loading a different officer state.
            if (stream != GameSaveFile.AbilityStream && stream != GameSaveFile.TraitStream && stream != GameSaveFile.ExperienceStream
                && !edited.AsSpan(offset, length).SequenceEqual(record.AsSpan(offset, length))) return false;
        return true;
    }

    // Only an actually observed different state retires the evidence. Merely reopening the
    // editor, retrying reconciliation or changing runtime settings is not a game load signal.
    internal void Observe(Func<ulong, byte[]?> read)
    {
        var next = new Dictionary<ulong, byte[]>(records);
        foreach (var (address, edited) in records)
            if (read(address) is not { } current || !SameState(edited, current)) next.Remove(address);
        if (next.Count != records.Count) Save(next);
    }

    // Called inside Apply's rollback boundary, after the whole batch's writes/readbacks pass.
    // Failure to persist the evidence fails and rolls back that edit batch as well.
    internal void Record(IRecordMemory memory, IReadOnlyList<RecordEdit> edits)
    {
        var next = new Dictionary<ulong, byte[]>(records);
        bool changed = false;
        foreach (var group in edits.Where(e => e.Kind == RecordKind.Officer && e.Changes.Count > 0).GroupBy(e => e.Address))
        {
            var edit = group.First();
            var changes = group.SelectMany(e => e.Changes).GroupBy(c => c.Field).Select(g => g.Last()).ToArray();
            bool traits = changes.Any(c => OfficerFields.Personalities.Contains(c.Field));
            if (!traits && !next.ContainsKey(edit.Address)) continue;
            if (edit.Address <= scope.OfficerArray || (edit.Address - scope.OfficerArray) % GameMemory.OfficerStride != 0
                || (edit.Address - scope.OfficerArray) / GameMemory.OfficerStride >= (ulong)Math.Max(0, scope.OfficerCount))
                throw new InvalidOperationException("Officer edit is outside the current recovery storage.");
            byte[] after = memory.Read(edit.Address, GameMemory.OfficerStride);
            if (!after.AsSpan(0, 8).SequenceEqual(edit.Expected.AsSpan(0, 8))
                || !after.AsSpan(OfficerFields.IdOffset, 2).SequenceEqual(edit.Expected.AsSpan(OfficerFields.IdOffset, 2)))
                throw new InvalidOperationException("Officer changed before recording its edit.");
            foreach (var change in changes)
                if (change.Field.Read(after) != change.Value) throw new InvalidOperationException("Officer changed before recording its edit.");
            if (after.AsSpan(SaveRestore.TraitOffset, SaveRestore.TraitBytes).IndexOfAnyExcept((byte)0) < 0)
            {
                // Preserve a prior deletion through another successful edit of the same officer.
                // An address reused for another officer cannot inherit that deletion.
                if (traits || next.TryGetValue(edit.Address, out var prior)
                    && prior.AsSpan(OfficerFields.IdOffset, 2).SequenceEqual(after.AsSpan(OfficerFields.IdOffset, 2)))
                    next[edit.Address] = after;
                else next.Remove(edit.Address);
            }
            else next.Remove(edit.Address);
            changed = true;
        }
        if (changed) Save(next);
    }

    private void Save(Dictionary<ulong, byte[]> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Receipt(1, scope, next.Select(p => new Entry(p.Key, p.Value)).ToArray()));
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
            records.Clear(); foreach (var item in next) records.Add(item.Key, item.Value);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
