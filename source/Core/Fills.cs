namespace San14Tenkazu;

// The fills under the lists: one value written to every listed record that is below it.
internal static class Fills
{
    // Officers' loyalty raised to 150 or abilities to 100 where lower (abilities with their experience). Loyalty only for
    // those serving a ruler: rulers (a fixed marker), free officers and prisoners keep theirs.
    internal static List<RecordEdit> RaiseOfficers(GameSnapshot data, IEnumerable<int> officers, IReadOnlyList<FieldDef> which)
    {
        bool loyalty = which.Contains(OfficerFields.Loyalty);
        var edits = new List<RecordEdit>();
        foreach (int id in officers)
        {
            if (!data.OfficerById.TryGetValue(id, out OfficerRecord? officer)) continue;
            if (loyalty && !OfficerFields.IsVassal(officer.Status)) continue;
            var changes = which.Select(f => new FieldChange(f, f == OfficerFields.Loyalty ? 150 : 100))
                .Where(change => officer[change.Field] < change.Value).ToList();
            if (changes.Count > 0) edits.Add(new RecordEdit(RecordKind.Officer, officer.Address, officer.Data, officer.Name, changes));
        }
        return OfficerEdits.WithExperience(edits);
    }

    // Units' wounded back among their troops (up to the troops' limit).
    internal static List<RecordEdit> HealUnits(GameSnapshot data, IEnumerable<(int Slot, int Commander)> units) =>
        units.Select(u => data.Unit(u.Slot, u.Commander)).OfType<UnitRecord>().Where(unit => unit[UnitFields.Wounded] > 0)
            .Select(unit => new RecordEdit(RecordKind.Unit, unit.Address, unit.Data, L.Terms.UnitOf(data.CommanderName(unit.Commander)),
                [new FieldChange(UnitFields.Troops, Math.Min(UnitFields.Troops.Max, unit[UnitFields.Troops] + unit[UnitFields.Wounded])), new FieldChange(UnitFields.Wounded, 0)]))
            .ToList();

    // Units' morale raised to the usual top.
    internal static List<RecordEdit> RaiseMorale(GameSnapshot data, IEnumerable<(int Slot, int Commander)> units) =>
        units.Select(u => data.Unit(u.Slot, u.Commander)).OfType<UnitRecord>().Where(unit => unit[UnitFields.Morale] < UnitFields.MoraleCap)
            .Select(unit => new RecordEdit(RecordKind.Unit, unit.Address, unit.Data, L.Terms.UnitOf(data.CommanderName(unit.Commander)),
                [new FieldChange(UnitFields.Morale, UnitFields.MoraleCap)]))
            .ToList();
}
