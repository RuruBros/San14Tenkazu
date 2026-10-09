namespace San14Tenkazu;

// The city and gate cards' [소속 주기]: a base no force holds goes to a force, through the game's own scenario-event
// command 거점 소유자 변경 (GameMemory.GiveBase). The game hands the base to the corps of the officer it is given (the
// ruler, so his own corps) and does what a change of owner does: the castle's force, the counties and the hexes of
// land around it, the force's regional advantages (with the game's own message), the 태수 of the corps' cities
// checked by its rule. A gate keeps corps 0, as the game's own gates do; its building and land say whose it is.
// The command hands the base over as a capture does (exe+0x26D590 with its fifth argument 0), so it also changes the
// base's own values: the castle's durability raised to 40% of full when lower, gold and food kept at
// max(10, 매력 / 3) + the force's bonus percent of what they were but at least 5,000 and 20,000, troops and wounded cut
// to that percent (read 2026-10-03, Reverse/notes/bases.md). KeptValues writes them back, so only the owner changes.
internal static class BaseGrants
{
    // A force a base may go to: a force with its ruler (not a tribe), and the cities it holds.
    internal readonly record struct Taker(int Force, int Ruler, int Cities);

    // The forces a base may go to, those with most cities first.
    internal static List<Taker> Takers(GameSnapshot data) =>
        data.Forces.Values.Where(f => f.Id <= GameMemory.MaxPlayerForce && Ruler(data, f.Id) is not null)
            .Select(f => new Taker(f.Id, f.RulerId, data.CitiesOf(f.Id).Count()))
            .OrderByDescending(t => t.Cities).ThenBy(t => t.Force)
            .ToList();

    // A force's ruler: the officer the force names, a ruler by status and in the force.
    internal static OfficerRecord? Ruler(GameSnapshot data, int force) =>
        data.Forces.TryGetValue(force, out ForceInfo info) && data.OfficerById.TryGetValue(info.RulerId, out OfficerRecord? ruler) &&
        ruler.Status == 1 && data.ForceOfCorps(ruler.Corps) == force ? ruler : null;

    // Whether the ID is a city's or a gate's: the bases the cards edit.
    internal static bool IsBase(GameSnapshot data, int baseId) =>
        baseId is >= 1 and <= GameMemory.CityCount || data.Gates.Any(g => g.Id == baseId);

    // The force that holds a city or a gate (0 none).
    internal static int ForceOf(GameSnapshot data, int baseId) =>
        baseId is >= 1 and <= GameMemory.CityCount ? data.ForceOfCity(baseId)
        : data.Gates.FirstOrDefault(g => g.Id == baseId) is { } gate ? data.ForceOfGate(gate) : 0;

    // The base's record as read: the address the game call checks.
    internal static ulong Address(GameSnapshot data, int baseId) =>
        baseId is >= 1 and <= GameMemory.CityCount ? data.Cities[baseId].Address : data.Gates.First(g => g.Id == baseId).Address;

    // Why the base cannot go to the force, or null when it can.
    internal static string? Refusal(GameSnapshot data, int baseId, int force)
    {
        if (!IsBase(data, baseId)) return L.Give.Gone;
        string place = CityNames.BaseName(baseId);
        if (ForceOf(data, baseId) is > 0 and var owner) return L.Give.HasForce(place, data.ForceName(owner));
        if (force is < 1 or > GameMemory.MaxPlayerForce || Ruler(data, force) is null) return L.Give.NoRuler(data.ForceName(force));
        return null;
    }

    // The base's own values the game changed as it handed the base over (before → after), written back as they were.
    internal static EditPlan KeptValues(GameSnapshot before, GameSnapshot after, int baseId)
    {
        if (baseId is >= 1 and <= GameMemory.CityCount)
        {
            CityRecord city = before.Cities[baseId];
            List<FieldChange> kept = [.. new[] { CityFields.Gold, CityFields.Food, CityFields.Troops, CityFields.Wounded }.Select(f => new FieldChange(f, city[f]))];
            if (before.CastleByCity.TryGetValue(baseId, out BuildingRecord? castle)) kept.Add(new FieldChange(CityFields.Durability, castle[CityFields.Durability]));
            return CityEdits.Plan(after, baseId, new CityEdits.Request(kept, [], null));
        }
        if (before.Gates.FirstOrDefault(g => g.Id == baseId) is not { } gate) return EditPlan.Refuse(L.Give.Gone);
        List<FieldChange> walls = [new FieldChange(GateFields.Troops, gate[GateFields.Troops])];
        if (gate.Building is { } building) walls.Add(new FieldChange(GateFields.Durability, building[GateFields.Durability]));
        return GateEdits.Plan(after, baseId, new GateEdits.Request(walls, null));
    }
}
