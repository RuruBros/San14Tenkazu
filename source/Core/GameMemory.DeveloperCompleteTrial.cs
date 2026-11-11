#if LOCAL_DEVELOPER
using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal sealed record CompleteEditTrial(string Key, string Label, IReadOnlyList<IReadOnlyList<RecordEdit>> Steps,
        bool SeededProvocation = false);
    internal sealed record CompleteEditMissing(string Key, string Reason);
    internal sealed record CompleteEditPlan(CompleteEditTrial[] Trials, CompleteEditMissing[] Missing);
    internal sealed record CompleteTrialImage(string Kind, ulong Address, string Label, string Before,
        string Expected, string Actual, string Sha256, int[] AllowedOffsets);
    internal sealed record CompleteTrialEvidence(string Stage, string Key, string Label, CompleteTrialImage[] Records, object[] Changes);
    private sealed record CompleteTrialRecord(RecordKind Kind, ulong Address, string Label, byte[] Before, FieldDef[] Allowed);

    // This journal is local-developer-only. It restores only writes actually attempted by this trial,
    // in reverse order, while the process is still frozen. It never writes an entire baseline record.
    private sealed class CompleteTrialMemory(IRecordMemory inner, GameClasses classes, CompleteTrialRecord[] records) : IRecordMemory
    {
        private sealed record Entry(CompleteTrialRecord Record, int Offset, byte[] Before, byte[] After)
        {
            internal bool Completed;
        }
        private readonly List<Entry> entries = [];
        public byte[] Read(ulong address, int length) => inner.Read(address, length);
        public void Write(ulong address, byte[] bytes)
        {
            var record = records.SingleOrDefault(r => r.Allowed.Any(f => r.Address + (ulong)f.Offset == address && f.Size == bytes.Length))
                ?? throw new InvalidOperationException("Trial attempted an unplanned write");
            var before = inner.Read(record.Address, record.Before.Length);
            Validate(record, before);
            int offset = checked((int)(address - record.Address));
            var after = (byte[])before.Clone(); bytes.CopyTo(after, offset);
            var entry = new Entry(record, offset, before, after);
            entries.Add(entry); // WriteProcessMemory can fail after a prefix was written.
            inner.Write(address, bytes);
            entry.Completed = true;
        }
        private void Validate(CompleteTrialRecord record, byte[] now)
        {
            ValidateSameRecord(classes, new(record.Kind, record.Address, record.Before, record.Label, []), now);
            var mask = record.Allowed.SelectMany(f => Enumerable.Range(f.Offset, f.Size)).ToHashSet();
            if (now.Where((value, offset) => !mask.Contains(offset) && value != record.Before[offset]).Any())
                throw new InvalidOperationException("Unplanned neighboring data changed: " + record.Label);
        }
        internal void Restore(Action guard)
        {
            if (entries.Count == 0) return;
            guard();
            var errors = new List<Exception>();
            foreach (var entry in entries.AsEnumerable().Reverse())
            {
                try
                {
                    var r = entry.Record;
                    byte[] now = inner.Read(r.Address, r.Before.Length);
                    Validate(r, now);
                    if (now.AsSpan().SequenceEqual(entry.Before)) continue;
                    bool matches = now.AsSpan().SequenceEqual(entry.After);
                    if (!matches && !entry.Completed)
                    {
                        // Only a prefix of this single attempted field can have reached memory.
                        int size = r.Allowed.Single(f => f.Offset == entry.Offset &&
                            !entry.Before.AsSpan(f.Offset, f.Size).SequenceEqual(entry.After.AsSpan(f.Offset, f.Size))).Size;
                        matches = Enumerable.Range(0, size + 1).Any(prefix =>
                        {
                            byte[] partial = (byte[])entry.Before.Clone();
                            entry.After.AsSpan(entry.Offset, prefix).CopyTo(partial.AsSpan(entry.Offset));
                            return now.AsSpan().SequenceEqual(partial);
                        });
                    }
                    if (!matches) throw new InvalidOperationException("Trial recovery context/value changed: " + r.Label);
                    var field = r.Allowed.Single(f => f.Offset == entry.Offset &&
                        !entry.Before.AsSpan(f.Offset, f.Size).SequenceEqual(entry.After.AsSpan(f.Offset, f.Size)));
                    inner.Write(r.Address + (ulong)field.Offset, entry.Before.AsSpan(field.Offset, field.Size).ToArray());
                    if (!inner.Read(r.Address, r.Before.Length).AsSpan().SequenceEqual(entry.Before))
                        throw new IOException("Trial recovery readback failed: " + r.Label);
                }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count != 0) throw new WriteRecoveryException(new IOException("Complete editor trial recovery incomplete"), errors);
        }
    }

    private static CompleteTrialRecord[] CompleteTrialRecords(GameSnapshot data, CompleteEditTrial trial)
    {
        var all = CompleteSnapshotRecords(data);
        var edits = trial.Steps.SelectMany(s => s).ToArray();
        var selected = new Dictionary<ulong, List<FieldDef>>();
        void Add(ulong address, IEnumerable<FieldDef> fields)
        {
            if (!selected.TryGetValue(address, out var current)) selected[address] = current = [];
            current.AddRange(fields);
        }
        foreach (var edit in edits)
        {
            Add(edit.Address, edit.Changes.Select(c => c.Field));
            if (edit.Kind == RecordKind.PlayRecord) Add(edit.Address, [PlayRecordFields.Changed]);
            if (edit.Kind == RecordKind.Officer)
                Add(edit.Address, OfficerEdits.SlotStateCleared(edit.Expected, edit.Changes).Select(c => c.Field));
            foreach (var restart in edit.Restarts ?? []) Add(restart.Officer, [OfficerFields.PersonalitiesInEffect]);
            if (edit.Kind == RecordKind.Personality && data.Personalities.SingleOrDefault(p => p.Address == edit.Address) is { } personality)
                foreach (var holder in PersonalityEdits.Holders(data, personality.Index)) Add(holder.Address, []);
            if (edit.MilitaryPoints is { } military)
            {
                Add(military.ForceAddress, []); Add(military.Caps.Address, []);
            }
        }
        // Watch the known preceding/following records too; do not probe arbitrary adjacent addresses.
        foreach (ulong address in selected.Keys.ToArray())
        {
            var record = all.Single(r => r.Address == address);
            var peers = all.Where(r => r.Kind == record.Kind).OrderBy(r => r.Address).ToArray();
            int index = Array.FindIndex(peers, r => r.Address == address);
            if (index > 0) Add(peers[index - 1].Address, []);
            if (index + 1 < peers.Length) Add(peers[index + 1].Address, []);
        }
        return selected.Select(pair => all.Single(r => r.Address == pair.Key) with
            { Allowed = pair.Value.Distinct().ToArray() }).OrderBy(r => r.Address).ToArray();
    }

    private static CompleteTrialRecord[] CompleteSnapshotRecords(GameSnapshot data)
    {
        var records = new List<CompleteTrialRecord>();
        void Add(RecordKind kind, ulong address, string label, byte[] bytes)
        { if (address != 0) records.Add(new(kind, address, label, (byte[])bytes.Clone(), [])); }
        foreach (var r in data.Cities.Concat(data.Tribes)) Add(RecordKind.City, r.Address, r.Name, r.Data);
        foreach (var r in data.Counties) Add(RecordKind.County, r.Address, r.Name, r.Data);
        foreach (var r in data.Gates) Add(RecordKind.Gate, r.Address, r.Name, r.Data);
        foreach (var r in data.Officers) Add(RecordKind.Officer, r.Address, r.Name, r.Data);
        foreach (var r in data.Units) Add(RecordKind.Unit, r.Address, $"unit:{r.Slot}/{r.Commander}", r.Data);
        foreach (var r in data.ForceRecords.Values) Add(RecordKind.Force, r.Address, data.ForceName(r.Id), r.Data);
        foreach (var r in data.CorpsRecords.Values) Add(RecordKind.Corps, r.Address, $"corps:{r.Id}", r.Data);
        if (data.MilitaryCaps is { } caps) Add(RecordKind.Corps, caps.Address, "military-caps (read-only)", caps.Data);
        foreach (var r in data.Items) Add(RecordKind.Item, r.Address, r.Name, r.Data);
        foreach (var r in data.Personalities) Add(RecordKind.Personality, r.Address, r.Name, r.Data);
        foreach (var r in data.CastleByCity.Values.Concat(data.Gates.Select(g => g.Building).OfType<BuildingRecord>())
            .Concat(data.Facilities.Select(f => f.Building).OfType<BuildingRecord>())) Add(RecordKind.Building, r.Address, $"building:{r.Index}", r.Data);
        if (data.Play is { } play) Add(RecordKind.PlayRecord, play.Address, "play-record", play.Data);
        return records.DistinctBy(r => r.Address).ToArray();
    }

    private static void CompleteTrialExpected(Dictionary<ulong, byte[]> images, IReadOnlyList<RecordEdit> edits)
    {
        foreach (var edit in edits.Where(e => e.Changes.Count > 0))
        {
            var bytes = images[edit.Address];
            var along = edit.Kind switch
            {
                RecordKind.Officer => OfficerEdits.SlotStateCleared(bytes, edit.Changes),
                RecordKind.PlayRecord => new List<FieldChange> { new(PlayRecordFields.Changed, 1) },
                _ => []
            };
            foreach (var change in edit.Changes.Concat(along)) change.Field.Encode(change.Value).CopyTo(bytes, change.Field.Offset);
            foreach (var restart in edit.Restarts ?? [])
            {
                var holder = images[restart.Officer];
                if (OfficerFields.Personalities[restart.Slot].Read(holder) == restart.Personality)
                    OfficerFields.PersonalitiesInEffect.Encode(OfficerFields.PersonalitiesInEffect.Read(holder) & ~(1L << restart.Slot))
                        .CopyTo(holder, OfficerFields.PersonalitiesInEffect.Offset);
            }
        }
    }

    internal static void RunCompleteEditTrial(GameSnapshot data, GameClasses classes, CompleteEditTrial trial,
        IRecordMemory memory, Action<GameSnapshot, IReadOnlyList<RecordEdit>, IRecordMemory> apply,
        Action guard, Action<CompleteTrialEvidence> record)
    {
        var scope = CompleteTrialRecords(data, trial);
        var expected = scope.ToDictionary(r => r.Address, r => (byte[])r.Before.Clone());
        var journal = new CompleteTrialMemory(memory, classes, scope);
        var changes = trial.Steps.SelectMany((step, index) => step.SelectMany(e => e.Changes.Select(c => (object)new
        { step = index, kind = e.Kind.ToString(), e.Address, e.Label, field = c.Field.Label, c.Field.Offset, c.Field.Size, before = c.Field.Read(e.Expected), after = c.Value }))).ToArray();
        void Evidence(string stage, bool check)
        {
            var images = scope.Select(r =>
            {
                byte[] actual = memory.Read(r.Address, r.Before.Length);
                return new CompleteTrialImage(r.Kind.ToString(), r.Address, r.Label, Convert.ToHexString(r.Before),
                    Convert.ToHexString(expected[r.Address]), Convert.ToHexString(actual), Convert.ToHexString(SHA256.HashData(actual)),
                    r.Allowed.SelectMany(f => Enumerable.Range(f.Offset, f.Size)).Distinct().Order().ToArray());
            }).ToArray();
            record(new(stage, trial.Key, trial.Label, images, changes));
            if (check && images.Any(r => r.Actual != r.Expected)) throw new IOException("Trial byte readback mismatch: " + trial.Key);
        }
        guard();
        Evidence("prepared", true); // Persist original records before the first attempted write.
        bool wrote = false;
        try
        {
            var working = data;
            int index = 0;
            foreach (var step in trial.Steps)
            {
                if (trial.SeededProvocation && index == 1)
                {
                    var edit = step.Single();
                    var unit = working.Units.Single(u => u.Address == edit.Address);
                    var product = UnitEdits.Plan(working, unit.Slot, unit.Commander, edit.Changes);
                    if (product.Refused || product.Edits.Count != 1 || !product.Edits[0].Changes.SequenceEqual(edit.Changes))
                        throw new InvalidOperationException(L.Errors.StateMoved);
                }
                CompleteTrialExpected(expected, step);
                wrote = true;
                apply(working, step, journal);
                working = working.With(step);
                Evidence($"apply-{index++}-readback", true);
            }
            Evidence("changed-readback", true);
            index = trial.Steps.Count - 1;
            foreach (var step in trial.Steps.Reverse())
            {
                var undo = Undo.Of(step);
                CompleteTrialExpected(expected, undo);
                apply(working, undo, journal);
                working = working.With(undo);
                Evidence($"undo-{index--}-readback", true);
            }
            Evidence("ordinary-undo-readback", true);
        }
        finally
        {
            if (wrote)
            {
                try { journal.Restore(guard); }
                catch
                {
                    try { Evidence("recovery-failed", false); } catch { /* Preserve the recovery failure. */ }
                    throw;
                }
                foreach (var r in scope) expected[r.Address] = r.Before;
                Evidence("restored-byte-exact", true);
            }
        }
    }
}
#endif
