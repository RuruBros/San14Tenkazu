namespace San14Tenkazu;

// The force card's own values and bilateral friendships/alliance periods. Agreement bytes retain the separate
// high bit according to 0x2EBCC0; GameMemory.SyncForceAgreements runs the native transition and refresh bodies.
internal static class ForceEdits
{
    internal static EditPlan Plan(GameSnapshot data, int force, IReadOnlyList<FieldChange> changes,
        IReadOnlyList<(int Force, int Value)> friendships, IReadOnlyList<(int Force, int Value)>? alliances = null,
        IReadOnlyList<MilitaryPointRequest>? militaryPoints = null)
    {
        if (!data.ForceRecords.TryGetValue(force, out ForceRecord? record)) return EditPlan.Refuse(L.Edits.ForceGone);
        alliances ??= [];
        var own = new List<FieldChange>(changes);
        var others = new Dictionary<int, List<FieldChange>>();
        foreach ((int other, int months) in alliances)
        {
            if (force == other || !ForceFields.CanAlly(record) || !data.ForceRecords.TryGetValue(other, out ForceRecord? theirs)
                || !ForceFields.CanAlly(theirs)) return EditPlan.Refuse(L.Force.AllianceUnavailable);
            if (months is < 0 or > ForceFields.MaxAgreement) return EditPlan.Refuse(L.Force.AllianceError(data.ForceName(other), ForceFields.MaxAgreement));
            if (data.PlayerForce < 0) return EditPlan.Refuse(L.Force.AlliancePlayerUnknown);
            int Value(ForceRecord r, int target)
            {
                int flag = (int)r[ForceFields.Agreement(target)] & 0x80;
                if (r.Id == data.PlayerForce && months is < 1 or > 3) flag = 0;
                return flag | months;
            }
            own.Add(new FieldChange(ForceFields.Agreement(other), Value(record, other)));
            others[other] = [new FieldChange(ForceFields.Agreement(force), Value(theirs, force))];
        }
        foreach ((int other, int value) in friendships)
        {
            own.Add(new FieldChange(ForceFields.Friendship(other), value));
            if (data.ForceRecords.TryGetValue(other, out ForceRecord? theirs))
            {
                if (!others.TryGetValue(other, out List<FieldChange>? fields)) others[other] = fields = [];
                fields.Add(new FieldChange(ForceFields.Friendship(force), value));
            }
        }
        var edits = new List<RecordEdit> { new(RecordKind.Force, record.Address, record.Data, data.ForceName(force), own) };
        foreach ((int other, List<FieldChange> fields) in others)
        {
            ForceRecord theirs = data.ForceRecords[other];
            edits.Add(new RecordEdit(RecordKind.Force, theirs.Address, theirs.Data, data.ForceName(other), fields));
        }
        if (militaryPoints is { Count: > 0 })
        {
            if (data.MilitaryCaps is not { Valid: true } caps || militaryPoints.Select(p => p.Corps).Distinct().Count() != militaryPoints.Count)
                return EditPlan.Refuse(L.Force.MilitaryUnavailable);
            int rank = (int)(own.LastOrDefault(c => c.Field == ForceFields.Rank) is { Field: not null } change ? change.Value : record[ForceFields.Rank]);
            int maximum = caps.ForRank(rank);
            foreach (var request in militaryPoints)
            {
                if (!data.CorpsRecords.TryGetValue(request.Corps, out var corps) || corps.Force != force || corps.Number != request.Number || corps.Leader != request.Leader)
                    return EditPlan.Refuse(L.Force.MilitaryUnavailable);
                if (request.Value < 0 || request.Value > maximum) return EditPlan.Refuse(L.Force.MilitaryRange(maximum));
                if (request.Value == corps.MilitaryPoints) continue;
                edits.Add(new(RecordKind.Corps, corps.Address, corps.Data, L.Force.MilitaryCorps(corps.Number, data.OfficerName(corps.Leader)),
                    [new(CorpsFields.MilitaryPoints, request.Value)], MilitaryPoints: new(record.Address, force, record.Ruler, caps)));
            }
        }
        return new EditPlan(edits, data.ForceName(force));
    }
}

// What the unit card writes: the unit's own values, while it is still on the map (a slot is used again by another
// unit once this one is gone, so it is found by its commander too). A 능력 변화 level goes with time left (the game
// sets a level with none back to 0), and its time is written first; a 도발 is only lowered.
internal static class UnitEdits
{
    internal static EditPlan Plan(GameSnapshot data, int slot, int commander, IReadOnlyList<FieldChange> changes)
    {
        if (data.Unit(slot, commander) is not { } unit) return EditPlan.Refuse(L.Edits.UnitGone);
        List<FieldChange> real = changes.Where(c => unit[c.Field] != c.Value).ToList();
        long After(FieldDef field) => real.LastOrDefault(c => c.Field == field) is { Field: not null } change ? change.Value : unit[field];
        bool Changes(FieldDef field) => real.Any(c => c.Field == field);
        if (Changes(UnitFields.Formation) && !UnitFields.MayUseFormation(unit.Kind, unit.Commander, After(UnitFields.Formation)))
            return EditPlan.Refuse(unit.IsArmy ? L.Edits.NotLandFormation : L.Edits.TransportFormation);
        for (int boost = 0; boost < UnitFields.BoostLevels.Length; boost++)
        {
            FieldDef level = UnitFields.BoostLevels[boost], time = UnitFields.BoostTimes[boost];
            if ((Changes(level) || Changes(time)) && After(level) != 0 && After(time) == 0)
                return EditPlan.Refuse(L.Edits.BoostWithoutTime(UnitFields.BoostName(boost)));
        }
        if (After(UnitFields.Ailments[UnitFields.Provoked]) > unit[UnitFields.Ailments[UnitFields.Provoked]])
            return EditPlan.Refuse(L.Edits.ProvokeOnlyLowered);
        real = real.OrderBy(c => UnitFields.BoostLevels.Contains(c.Field)).ToList();
        return new EditPlan(real.Count == 0 ? [] : [new RecordEdit(RecordKind.Unit, unit.Address, unit.Data, L.Terms.UnitOf(data.CommanderName(commander)), real)],
            L.Terms.UnitOf(data.CommanderName(commander)));
    }
}
