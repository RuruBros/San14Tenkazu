namespace San14Tenkazu;

// What the city card writes: the city's own values (its durability in its castle building), its counties', and a
// new 태수, who governs by his status too.
internal static class CityEdits
{
    // What the card asks: the city's own fields (the castle's durability among them), each county's, and the new
    // 태수 (null when unchanged).
    internal sealed record Request(List<FieldChange> City, List<(int County, List<FieldChange> Changes)> Counties, int? Governor);

    internal static EditPlan Plan(GameSnapshot data, int id, Request request)
    {
        CityRecord city = data.Cities[id];
        string name = CityNames.ById[id];
        if (request.Governor is int pick && pick != city.Governor && !MayGovern(data, id, pick))
            return EditPlan.Refuse(L.Edits.NotGovernor(data.OfficerName(pick), name));
        var edits = new List<RecordEdit>();
        List<FieldChange> own = request.City.Where(c => c.Field.Record == RecordKind.City && city[c.Field] != c.Value).ToList();
        if (own.Count > 0) edits.Add(new RecordEdit(RecordKind.City, city.Address, city.Data, name, own));
        List<FieldChange> castleChanges = request.City.Where(c => c.Field.Record == RecordKind.Building).ToList();
        if (castleChanges.Count > 0 && data.CastleByCity.TryGetValue(id, out BuildingRecord? castle) &&
            castleChanges.Where(c => castle[c.Field] != c.Value).ToList() is { Count: > 0 } castleReal)
            edits.Add(new RecordEdit(RecordKind.Building, castle.Address, castle.Data, L.Edits.Castle(name), castleReal));
        foreach ((int index, List<FieldChange> changes) in request.Counties)
            if (data.County(index) is { } county && changes.Where(c => county[c.Field] != c.Value).ToList() is { Count: > 0 } real)
                edits.Add(new RecordEdit(RecordKind.County, county.Address, county.Data, $"{name}/{county.Name}", real));
        // 태수: the city names him, and he is 태수 by status; the one before goes back to 일반 (a ruler or 군단장 keeps his).
        if (request.Governor is int governor && governor != city.Governor)
        {
            if (edits.FirstOrDefault(e => e.Kind == RecordKind.City) is { } cityEdit) edits[edits.IndexOf(cityEdit)] = cityEdit with { Changes = [.. cityEdit.Changes, new FieldChange(CityFields.Governor, governor)] };
            else edits.Add(new RecordEdit(RecordKind.City, city.Address, city.Data, name, [new FieldChange(CityFields.Governor, governor)]));
            edits.AddRange(Statuses(data, city.Governor, governor));
        }
        return new EditPlan(edits, name);
    }

    // A new 태수 of a city or a gate is 태수 by status too; the one before goes back to 일반 (a ruler or 군단장 keeps his).
    internal static IEnumerable<RecordEdit> Statuses(GameSnapshot data, int before, int after)
    {
        if (data.OfficerById.GetValueOrDefault(before) is { Status: 3 } former)
            yield return new RecordEdit(RecordKind.Officer, former.Address, former.Data, former.Name, [new FieldChange(OfficerFields.StatusField, 4)]);
        if (data.OfficerById.GetValueOrDefault(after) is { Status: 4 } next)
            yield return new RecordEdit(RecordKind.Officer, next.Address, next.Data, next.Name, [new FieldChange(OfficerFields.StatusField, 3)]);
    }

    // Who may govern a city: one of its force's 태수 and 일반 officers who belongs to it and is in it now (a ruler or
    // 군단장 governs where he is; the card does not offer him).
    internal static bool MayGovern(GameSnapshot data, int city, int officer) => MayGovern(data, city, data.ForceOfCity(city), officer);

    // The same for any base of a force: a city or a gate.
    internal static bool MayGovern(GameSnapshot data, int baseId, int force, int officer) =>
        force > 0 && data.OfficerById.TryGetValue(officer, out OfficerRecord? o) && o.Status is 3 or 4 && o.BaseId == baseId &&
        o[OfficerFields.PlaceField] == baseId && data.ForceOfCorps(o.Corps) == force;
}

// What the gate card writes: the gate's own values (its durability in its building) and a new 태수, who governs by
// his status too, as in a city.
internal static class GateEdits
{
    // What the card asks: the gate's fields (GateFields) and the new 태수 (null when unchanged).
    internal sealed record Request(List<FieldChange> Changes, int? Governor);

    internal static EditPlan Plan(GameSnapshot data, int id, Request request)
    {
        if (data.Gates.FirstOrDefault(g => g.Id == id) is not { } gate) return EditPlan.Refuse(L.Edits.GateGone);
        string name = gate.Name;
        int? governor = request.Governor is int pick && pick != gate.Governor ? pick : null;
        if (governor is int next && !MayGovern(data, gate, next))
            return EditPlan.Refuse(L.Edits.NotGateGovernor(data.OfficerName(next), name));
        var edits = new List<RecordEdit>();
        List<FieldChange> own = request.Changes.Where(c => c.Field.Record == RecordKind.Gate && gate[c.Field] != c.Value).ToList();
        if (governor is int named) own.Add(new FieldChange(CityFields.GateGovernor, named));
        if (own.Count > 0) edits.Add(new RecordEdit(RecordKind.Gate, gate.Address, gate.Data, name, own));
        if (gate.Building is { } building &&
            request.Changes.Where(c => c.Field.Record == RecordKind.Building && building[c.Field] != c.Value).ToList() is { Count: > 0 } walls)
            edits.Add(new RecordEdit(RecordKind.Building, building.Address, building.Data, L.Edits.GateBuilding(name), walls));
        if (governor is int made) edits.AddRange(CityEdits.Statuses(data, gate.Governor, made));
        return new EditPlan(edits, name);
    }

    // Who may govern a gate: one of its force's 태수 and 일반 officers who belongs to it and is in it now.
    internal static bool MayGovern(GameSnapshot data, GateRecord gate, int officer) => CityEdits.MayGovern(data, gate.Id, data.ForceOfGate(gate), officer);
}
