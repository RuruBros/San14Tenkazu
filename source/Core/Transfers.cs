namespace San14Tenkazu;

// What the officer card's [소속] page asks for.
internal enum Transfer { Move, Defect, Dismiss, Recruit, Release }

// Whom an officer serves: moving him to another base of his force, sending him to another force, letting him go
// (재야), recruiting a 재야 officer or a prisoner, and releasing a prisoner. Each is written as the game itself writes
// it (watched on 2026-10-02 over 20 months of the game's own moves), with what goes with it: the force he leaves
// loses him as 군사 and from its 정책 slots, and the base he leaves loses him as 태수.
internal static class Transfers
{
    internal const int LoyaltyOnJoining = 100;

    // A force with a ruler and the bases it holds (cities and gates), for the officer card's pickers.
    internal sealed record ForceBases(int Force, string Name, IReadOnlyList<(int Base, string Name)> Bases);

    // A base the officer can be sent to: a city (1~46) or a gate (52~61), with its corps and force.
    private readonly record struct Post(int Base, string Name, int Corps, int Force);

    private static List<Post> Posts(GameSnapshot data)
    {
        var posts = new List<Post>();
        for (int id = 1; id <= GameMemory.CityCount; id++)
        {
            CityRecord city = data.Cities[id];
            if (data.ForceOfCorps(city.Corps) is > 0 and var force) posts.Add(new Post(id, CityNames.ById[id], city.Corps, force));
        }
        foreach (GateRecord gate in data.Gates)
            if (data.ForceOfCorps(gate.Corps) is > 0 and var force) posts.Add(new Post(gate.Id, gate.Name, gate.Corps, force));
        return posts;
    }

    internal static List<ForceBases> Targets(GameSnapshot data) =>
        Posts(data).GroupBy(p => p.Force)
            .Where(g => data.Forces.TryGetValue(g.Key, out ForceInfo info) && info.RulerId is not (0 or 0xFFFF))
            .OrderBy(g => data.ForceName(g.Key), StringComparer.CurrentCulture)
            .Select(g => new ForceBases(g.Key, data.ForceName(g.Key), g.Select(p => (p.Base, p.Name)).ToList()))
            .ToList();

    // Active commanders require read-only route preflight and same-frame native membership/return processing.
    // A unit's force follows its commander immediately; preserving its old order is unsafe for transports.
    internal static string Blocker(GameSnapshot data, OfficerRecord officer, Transfer kind)
    {
        int status = officer.Status;
        bool serving = status is 2 or 3 or 4;
        bool leading = Leading(data, officer);
        return kind switch
        {
            Transfer.Move or Transfer.Defect or Transfer.Dismiss when status == 1 => L.Transfers.RulerStays,
            Transfer.Dismiss when status == 2 => L.Transfers.CorpsLeaderStays,
            Transfer.Move or Transfer.Defect or Transfer.Dismiss when !serving => L.Transfers.OnlyVassals,
            Transfer.Dismiss when leading => L.Transfers.OnCampaign,
            Transfer.Move or Transfer.Defect or Transfer.Dismiss when !leading && officer[OfficerFields.PlaceField] != officer.BaseId => L.Transfers.OutsideBase,
            Transfer.Recruit when status is not (5 or 6) => L.Transfers.RecruitOnly,
            Transfer.Release when status != 6 => L.Transfers.ReleaseOnly,
            _ => ""
        };
    }

    private static bool Leading(GameSnapshot data, OfficerRecord officer) => data.Units.Any(u => u.Commander == officer.Id);

    // The writes a transfer takes, with a summary to confirm; refused with the reason when it cannot be done.
    internal static EditPlan Plan(GameSnapshot data, OfficerRecord officer, Transfer kind, int target)
    {
        if (Blocker(data, officer, kind) is { Length: > 0 } blocked) return EditPlan.Refuse(blocked);
        if (data.Date is not { } date) return EditPlan.Refuse(L.Transfers.NoDate);
        Post? to = kind is Transfer.Dismiss or Transfer.Release ? null : Posts(data).Cast<Post?>().FirstOrDefault(p => p!.Value.Base == target);
        if (kind is Transfer.Move or Transfer.Defect or Transfer.Recruit && to is null) return EditPlan.Refuse(L.Transfers.PickBase);
        int force = data.ForceOfCorps(officer.Corps);
        if (kind == Transfer.Move && to!.Value.Force != force) return EditPlan.Refuse(L.Transfers.SameForceOnly);
        if (kind == Transfer.Defect && to!.Value.Force == force) return EditPlan.Refuse(L.Transfers.OtherForceOnly);

        var edits = new List<RecordEdit>();
        var own = new List<FieldChange>();
        var notes = new List<string>();
        void Set(FieldDef field, long value)
        {
            if (officer[field] != value) own.Add(new FieldChange(field, value));
        }
        // A commander out with his unit stays where he is: his place is the unit (61 + its slot) until he comes back.
        // The corps goes with the base, or the game sets it back from the base's corps at the next 순.
        bool leading = Leading(data, officer);
        void Join(Post post)
        {
            Set(OfficerFields.CorpsField, post.Corps);
            Set(OfficerFields.BaseField, post.Base);
            if (!leading) Set(OfficerFields.PlaceField, post.Base);
            Set(OfficerFields.StatusField, 4);
            Set(OfficerFields.Office, OfficerFields.NoOffice);
            Set(OfficerFields.Loyalty, LoyaltyOnJoining);
            Set(OfficerFields.FormerLord, 0);
            Set(OfficerFields.FormerLordMonths, 0);
            Set(OfficerFields.PrisonerOf, 0);
            Set(OfficerFields.Captivity, 0);
            Set(OfficerFields.JoinedYear, date.Year);
            Set(OfficerFields.Service, OfficerFields.RecruitedService);
            Set(OfficerFields.ServiceValue, 0);
        }
        void GoFree(int lord, int months)
        {
            Set(OfficerFields.CorpsField, 0);
            Set(OfficerFields.StatusField, 5);
            Set(OfficerFields.Office, OfficerFields.NoOffice);
            Set(OfficerFields.Loyalty, 0);
            Set(OfficerFields.PlaceField, officer.BaseId);
            Set(OfficerFields.FormerLord, lord);
            Set(OfficerFields.FormerLordMonths, months);
            Set(OfficerFields.PrisonerOf, 0);
            Set(OfficerFields.Captivity, 0);
            Set(OfficerFields.JoinedYear, 0);
            Set(OfficerFields.Service, 0);
            Set(OfficerFields.ServiceValue, 0);
        }

        string from = $"{data.ForceName(force)} {CityNames.BaseName(officer.BaseId)}";
        string summary;
        switch (kind)
        {
            case Transfer.Move:
                Set(OfficerFields.CorpsField, to!.Value.Corps);
                Set(OfficerFields.BaseField, to.Value.Base);
                if (!leading) Set(OfficerFields.PlaceField, to.Value.Base);
                if (officer.Status is 2 or 3) Set(OfficerFields.StatusField, 4);
                summary = L.Transfers.MoveSummary(officer.Name, CityNames.BaseName(officer.BaseId), to.Value.Name);
                break;
            case Transfer.Defect:
                Join(to!.Value);
                summary = L.Transfers.DefectSummary(officer.Name, from, data.ForceName(to.Value.Force), to.Value.Name, LoyaltyOnJoining);
                break;
            case Transfer.Dismiss:
                GoFree(data.Forces.TryGetValue(force, out ForceInfo lord) ? lord.RulerId : 0, OfficerFields.Dismissed);
                summary = L.Transfers.DismissSummary(officer.Name, from, CityNames.BaseName(officer.BaseId), OfficerFields.Dismissed);
                break;
            case Transfer.Recruit:
                Join(to!.Value);
                summary = L.Transfers.RecruitSummary(officer.Name, OfficerFields.StatusName(officer.Status), data.ForceName(to.Value.Force), to.Value.Name, LoyaltyOnJoining);
                break;
            default:
                int holder = BaseForce(data, officer.BaseId);
                GoFree(data.Forces.TryGetValue(holder, out ForceInfo keeper) ? keeper.RulerId : 0, OfficerFields.Released);
                summary = L.Transfers.ReleaseSummary(officer.Name, CityNames.BaseName(officer.BaseId));
                break;
        }
        if (officer.Status == 2 && kind is Transfer.Move or Transfer.Defect)
            notes.Add(L.Transfers.CorpsLeaderReleased);
        if (leading && kind is Transfer.Move or Transfer.Defect)
            notes.Add(L.Transfers.UnitReturns);
        if (RequiresNative(data, officer, kind)) notes.Add(L.Transfers.NativeUndoNote);
        if (own.Count > 0 || RequiresNative(data, officer, kind)) edits.Add(new RecordEdit(RecordKind.Officer, officer.Address, officer.Data, officer.Name, own));

        // The base he leaves (or, as a prisoner, is held in) no longer has him as 태수.
        if (kind != Transfer.Recruit || officer.Status == 6)
            if (GovernorEdit(data, officer.BaseId, officer.Id, 0) is { } governor)
            {
                edits.Add(governor);
                notes.Add(L.Transfers.NoGovernor(CityNames.BaseName(officer.BaseId)));
            }
        // The force he leaves no longer has him as 군사 or in its 정책 slots.
        if (kind is Transfer.Defect or Transfer.Dismiss || kind == Transfer.Recruit && force > 0 && force != to!.Value.Force)
            if (LeaveForceEdit(data, force, officer.Id, notes) is { } leave) edits.Add(leave);
        return new EditPlan(edits, notes.Count == 0 ? summary : L.Transfers.WithNotes(summary, string.Join(", ", notes)));
    }

    internal static bool RequiresNative(GameSnapshot data, OfficerRecord officer, Transfer kind) =>
        kind is Transfer.Move or Transfer.Defect && (officer.Status == 2 || Leading(data, officer));

    // The force of a base: a city's, a gate's or a tribe's.
    private static int BaseForce(GameSnapshot data, int baseId) => baseId switch
    {
        >= 1 and <= GameMemory.CityCount => data.ForceOfCity(baseId),
        >= GameMemory.GateFirst and <= GameMemory.GateLast => data.Gates.FirstOrDefault(g => g.Id == baseId) is { } gate ? data.ForceOfCorps(gate.Corps) : 0,
        _ => 0
    };

    // A base's 태수 set to someone else, when it is the given officer now; null otherwise.
    private static RecordEdit? GovernorEdit(GameSnapshot data, int baseId, int from, int to)
    {
        if (baseId is >= 1 and <= GameMemory.CityCount && data.Cities[baseId] is var city && city[CityFields.Governor] == from)
            return new RecordEdit(RecordKind.City, city.Address, city.Data, CityNames.ById[baseId], [new FieldChange(CityFields.Governor, to)]);
        if (data.Gates.FirstOrDefault(g => g.Id == baseId) is { } gate && gate[CityFields.GateGovernor] == from)
            return new RecordEdit(RecordKind.Gate, gate.Address, gate.Data, gate.Name, [new FieldChange(CityFields.GateGovernor, to)]);
        return null;
    }

    // The force's record without the officer: no longer its 군사, and out of its 정책 slots (the rest move up).
    private static RecordEdit? LeaveForceEdit(GameSnapshot data, int force, int officer, List<string> notes)
    {
        if (!data.ForceRecords.TryGetValue(force, out ForceRecord? record)) return null;
        var changes = new List<FieldChange>();
        if (record[ForceFields.Strategist] == officer)
        {
            changes.Add(new FieldChange(ForceFields.Strategist, 0));
            notes.Add(L.Transfers.NoStrategist(data.ForceName(force)));
        }
        for (int field = 0; field < ForceFields.PolicyFields.Length; field++)
        {
            var slots = Enumerable.Range(0, ForceFields.PolicySlots)
                .Select(slot => (Officer: record[ForceFields.PolicyOfficer(field, slot)], Days: record[ForceFields.PolicyDays(field, slot)])).ToList();
            if (!slots.Any(s => s.Officer == officer)) continue;
            var kept = slots.Where(s => s.Officer != 0 && s.Officer != officer).ToList();
            for (int slot = 0; slot < ForceFields.PolicySlots; slot++)
            {
                (long who, long days) = slot < kept.Count ? kept[slot] : (0, 0);
                if (who != slots[slot].Officer) changes.Add(new FieldChange(ForceFields.PolicyOfficer(field, slot), who));
                if (days != slots[slot].Days) changes.Add(new FieldChange(ForceFields.PolicyDays(field, slot), days));
            }
            notes.Add(L.Transfers.LeftPolicySlot(data.ForceName(force), ForceFields.PolicyFields[field].Name));
        }
        return changes.Count == 0 ? null : new RecordEdit(RecordKind.Force, record.Address, record.Data, data.ForceName(force), changes);
    }
}
