namespace San14Tenkazu;

// Kept distinct from StaleEditException: the UI may only say "all undone" after a verified rollback.
internal sealed class WriteRecoveryException(Exception failure, IReadOnlyList<Exception> recovery)
    : AggregateException(L.Errors.RollbackIncomplete, new[] { failure }.Concat(recovery));

internal static partial class GameMemory
{
    private sealed class WriteRecovery(IRecordMemory memory, GameClasses classes)
    {
        private sealed class Entry(RecordEdit record, FieldDef field, byte[] before, byte[] replacement,
            IReadOnlyList<FieldDef> guards)
        {
            internal readonly RecordEdit Record = record with { Expected = (byte[])before.Clone() };
            internal readonly FieldDef Field = field;
            internal readonly byte[] Old = before.AsSpan(field.Offset, field.Size).ToArray();
            internal readonly byte[] New = replacement;
            internal readonly IReadOnlyList<FieldDef> Guards = guards;
            internal ulong Address => Record.Address + (ulong)Field.Offset;
            internal bool Completed;
        }

        private readonly List<Entry> entries = [];

        internal void Write(RecordEdit record, byte[] working, FieldChange change, IReadOnlyList<FieldDef>? guards = null)
        {
            byte[] replacement = change.Field.Encode(change.Value);
            var entry = new Entry(record, change.Field, working, replacement, guards ?? []);
            entries.Add(entry); // A failed WriteProcessMemory can have written a prefix.
            memory.Write(entry.Address, replacement);
            entry.Completed = true;
            // Later fields of the same record must recover in reverse from the state we actually attempted.
            replacement.CopyTo(working, change.Field.Offset);
        }

        private byte[] ReadTarget(Entry entry)
        {
            byte[] now = memory.Read(entry.Record.Address, entry.Record.Expected.Length);
            ValidateSameRecord(classes, entry.Record, now);
            // Do not restore links, or relight a personality slot, after their context has moved.
            IEnumerable<FieldDef> guards = entry.Guards;
            if (LinkedFields.Contains(entry.Field)) guards = guards.Concat(LinkedFields.Of(entry.Record.Kind));
            foreach (FieldDef guard in guards)
            {
                if (guard.Offset < entry.Field.Offset + entry.Field.Size && entry.Field.Offset < guard.Offset + guard.Size) continue;
                if (guard.Read(now) != guard.Read(entry.Record.Expected))
                    throw new InvalidOperationException(L.Errors.RollbackContextChanged(guard.Label));
            }
            return now;
        }

        internal void Restore(Exception failure)
        {
            var errors = new List<Exception>();
            foreach (Entry entry in entries.AsEnumerable().Reverse())
            {
                try
                {
                    byte[] now = ReadTarget(entry);
                    byte[] value = now.AsSpan(entry.Field.Offset, entry.Field.Size).ToArray();
                    if (value.AsSpan().SequenceEqual(entry.Old)) continue; // Failed before writing, or already restored.
                    if (!value.AsSpan().SequenceEqual(entry.New) && (entry.Completed ||
                        value.Where((b, i) => b != entry.Old[i] && b != entry.New[i]).Any()))
                        throw new InvalidOperationException(L.Errors.RollbackValueChanged);
                    if (entry.Record.Kind == RecordKind.Corps)
                        MilitaryIntelligence.Check(memory, classes,
                            entry.Record with { Changes = [new(entry.Field, entry.Field.Read(entry.Record.Expected))] }, now);
                    memory.Write(entry.Address, entry.Old);
                    byte[] restored = ReadTarget(entry);
                    if (!restored.AsSpan(entry.Field.Offset, entry.Field.Size).SequenceEqual(entry.Old))
                        throw new InvalidOperationException(L.Errors.RollbackReadbackFailed);
                }
                catch (Exception error)
                {
                    errors.Add(new InvalidOperationException(L.Errors.RollbackField(
                        entry.Record.Label, entry.Field.Label, entry.Address.ToString("X"), error.Message), error));
                }
            }
            if (errors.Count > 0) throw new WriteRecoveryException(failure, errors);
        }
    }
}
