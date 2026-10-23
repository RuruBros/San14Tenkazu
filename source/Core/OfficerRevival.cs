namespace San14Tenkazu;

internal enum RevivalMode { Serving, Free }

// Appearance/revival retains editable values, including birth/debut years. Erased history is not reconstructed.
internal static class OfficerRevival
{
    internal sealed record Destination(int Base, string Name, int Corps, int Force);
    internal sealed record Request(int Officer, RevivalMode Mode, int Base, int DeathYear);

    internal static IReadOnlyList<Destination> Targets(GameSnapshot data, RevivalMode mode)
    {
        IEnumerable<Destination> bases = data.CityIds.Select(id => data.Cities[id]).Select(c =>
            new Destination(c.CityId, data.CityName(c.CityId), c.Corps, data.ForceOfCorps(c.Corps)))
            .Concat(data.Gates.Select(g => new Destination(g.Id, g.Name, g.Corps, data.ForceOfCorps(g.Corps))));
        return bases.Where(b => mode == RevivalMode.Free || b.Corps is >= 1 and <= 51 &&
            b.Force is >= 1 and <= GameMemory.MaxPlayerForce && data.Forces.TryGetValue(b.Force, out var force) &&
            data.OfficerById.TryGetValue(force.RulerId, out var ruler) && ruler.Status == 1)
            .OrderBy(b => b.Base).ToArray();
    }

    internal static EditPlan Plan(GameSnapshot data, Request request)
    {
        if (!Enum.IsDefined(request.Mode)) return EditPlan.Refuse(L.Revival.InvalidMode);
        if (!data.OfficerById.TryGetValue(request.Officer, out var officer) || officer.Status is not (7 or 9))
            return EditPlan.Refuse(L.Revival.InactiveOnly);
        if ((BitConverter.ToUInt16(officer.Data, 0x15E) & 0x4000) != 0)
            return EditPlan.Refuse(L.Revival.GeneratedOfficer);
        if (data.Units.Any(u => u.Commander == officer.Id)) return EditPlan.Refuse(L.Revival.HasUnit);
        if (data.Date is not { } date) return EditPlan.Refuse(L.Transfers.NoDate);
        if (request.DeathYear <= date.Year || request.DeathYear > OfficerFields.Death.Max)
            return EditPlan.Refuse(L.Revival.DeathYearAfter(date.Year));
        var target = Targets(data, request.Mode).FirstOrDefault(b => b.Base == request.Base);
        if (target is null) return EditPlan.Refuse(L.Transfers.DestinationInvalid);

        bool serving = request.Mode == RevivalMode.Serving;
        var changes = new List<FieldChange>();
        void Set(FieldDef field, long value) { if (officer[field] != value) changes.Add(new(field, value)); }
        Set(OfficerFields.Death, request.DeathYear);
        Set(OfficerFields.CorpsField, serving ? target.Corps : 0);
        Set(OfficerFields.BaseField, target.Base);
        Set(OfficerFields.PlaceField, target.Base);
        Set(OfficerFields.StatusField, serving ? 4 : 5);
        Set(OfficerFields.Office, OfficerFields.NoOffice);
        Set(OfficerFields.Loyalty, serving ? Transfers.LoyaltyOnJoining : 0);
        Set(OfficerFields.FormerLord, 0);
        Set(OfficerFields.FormerLordMonths, 0);
        Set(OfficerFields.PrisonerOf, 0);
        Set(OfficerFields.Captivity, 0);
        Set(OfficerFields.JoinedYear, serving ? date.Year : 0);
        Set(OfficerFields.Service, serving ? OfficerFields.RecruitedService : 0);
        Set(OfficerFields.ServiceValue, 0);
        Set(OfficerFields.PersonalitiesInEffect, 0);
        foreach (var field in OfficerFields.PersonalityTimeLeft) Set(field, 0);
        string where = serving ? data.ForceName(target.Force) + " " + target.Name : target.Name;
        string summary = L.Revival.Summary(officer.Name, OfficerFields.StatusName(officer.Status),
            serving ? L.Terms.StatusRegular : L.Terms.StatusFree, where, request.DeathYear) + "\n" +
            (officer.Status == 7 ? L.Revival.AppearanceNote : L.Revival.ValuesNote) + "\n" + L.Revival.UndoNote;
        return new EditPlan([new(RecordKind.Officer, officer.Address, officer.Data, officer.Name, changes)], summary);
    }
}
