namespace San14Tenkazu;

// [되돌리기]: the edits that put a write back, and what the game has changed since the write.
internal static class Undo
{
    // The values a write replaced, as edits that put them back. Each expects its record as the write left it (the
    // values read before it with every change of the write on top), so undoing finds what the game changed since.
    internal static List<RecordEdit> Of(IReadOnlyList<RecordEdit> written)
    {
        var after = new Dictionary<ulong, byte[]>();
        foreach (RecordEdit e in written)
        {
            if (!after.TryGetValue(e.Address, out byte[]? image)) after[e.Address] = image = (byte[])e.Expected.Clone();
            foreach (FieldChange c in e.Changes) c.Field.Encode(c.Value).CopyTo(image, c.Field.Offset);
        }
        return written.Where(e => e.Changes.Count > 0)
            .Select(e => new RecordEdit(e.Kind, e.Address, after[e.Address], e.Label, e.Changes.Select(c => new FieldChange(c.Field, c.Field.Read(e.Expected))).ToList(),
                e.Restarts, e.MilitaryPoints))
            .ToList();
    }

    // What undoing finds in the records as they are now. Missing: records gone (a unit, or its slot taken by
    // another). LinkedMoved: linked fields the game changed since (he moved, a slot filled), which stop the undo, as
    // putting the rest back would no longer fit. Moved: plain values it changed, to ask about.
    internal sealed record Check(List<RecordEdit> Edits, List<string> Missing, List<string> LinkedMoved, List<(RecordEdit Edit, FieldChange Change, string Note)> Moved)
    {
        // The edits without the plain values the game changed since: those keep what the game has now.
        internal List<RecordEdit> KeepingMoved() =>
            Edits.Select(e => e with { Changes = e.Changes.Where(c => !Moved.Any(m => m.Edit == e && m.Change == c)).ToList() })
                .Where(e => e.Changes.Count > 0).ToList();
    }

    // fresh gives a record's bytes now, null when it is gone.
    internal static Check Compare(IReadOnlyList<RecordEdit> undo, Func<RecordEdit, byte[]?> fresh)
    {
        var check = new Check([], [], [], []);
        foreach (RecordEdit edit in undo)
        {
            if (fresh(edit) is not { } now || !GameMemory.IsSameRecord(edit.Kind, now, edit.Expected))
            {
                check.Missing.Add(edit.Label);
                continue;
            }
            IReadOnlyList<FieldDef> linked = LinkedFields.Of(edit.Kind);
            check.LinkedMoved.AddRange(edit.Changes.Where(c => ForceFields.IsAgreement(c.Field)
                && c.Field.Read(now) != c.Field.Read(edit.Expected))
                .Select(c => L.Edits.MovedAfterWrite(edit.Label, c.Field.Label, c.Field.Read(edit.Expected), c.Field.Read(now))));
            if (edit.Changes.Any(c => linked.Contains(c.Field)))
                check.LinkedMoved.AddRange(linked.Where(f => f.Read(now) != f.Read(edit.Expected))
                    .Select(f => L.Edits.MovedAfterWrite(edit.Label, f.Label, f.Read(edit.Expected), f.Read(now))));
            foreach (FieldChange change in edit.Changes)
                if (!linked.Contains(change.Field) && change.Field.Read(now) is var value && value != change.Field.Read(edit.Expected) && value != change.Value)
                    check.Moved.Add((edit, change, L.Edits.MovedSinceWrite(edit.Label, change.Field.Label, change.Field.Read(edit.Expected), value, change.Value)));
            check.Edits.Add(edit);
        }
        return check;
    }
}
