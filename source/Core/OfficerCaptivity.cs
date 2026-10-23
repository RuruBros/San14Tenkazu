namespace San14Tenkazu;

// 0x2E0CF0 with unit/notification/dialog/officer zero and an explicit captor force.
// Native capture removes appointments and reevaluates corps; the editor never fabricates those writes.
internal static class OfficerCaptivity
{
    internal sealed record Target(int Force, string Name, int Base, string BaseName);
    internal sealed record Request(int Officer, int Force);

    internal static IReadOnlyList<Target> Targets(GameSnapshot data) => data.ForceRecords.Values
        .Where(f => f.Id is >= 1 and <= GameMemory.MaxPlayerForce &&
            data.OfficerById.TryGetValue(f.Ruler, out var ruler) && ruler.Status == 1 &&
            data.ForceOfCorps(ruler.Corps) == f.Id && BaseForce(data, ruler.BaseId) == f.Id)
        .OrderBy(f => data.ForceName(f.Id), StringComparer.CurrentCulture)
        .Select(f => new Target(f.Id, data.ForceName(f.Id), data.OfficerById[f.Ruler].BaseId,
            data.BaseName(data.OfficerById[f.Ruler].BaseId))).ToArray();

    internal static int BaseForce(GameSnapshot data, int id) => id switch
    {
        _ when data.IsCity(id) => data.ForceOfCity(id),
        >= GameMemory.GateFirst and <= GameMemory.GateLast => data.Gates.FirstOrDefault(g => g.Id == id) is { } gate
            ? data.ForceOfGate(gate) : 0,
        _ => 0
    };

    internal static EditPlan Plan(GameSnapshot data, Request request)
    {
        if (!data.OfficerById.TryGetValue(request.Officer, out var officer) || officer.Status is not (2 or 3 or 4 or 5 or 6))
            return EditPlan.Refuse(L.StatusChange.CaptureOnly);
        if ((BitConverter.ToUInt16(officer.Data, 0x15E) & 0x4000) != 0)
            return EditPlan.Refuse(L.StatusChange.Generated);
        if (data.Units.Any(u => u.Commander == officer.Id)) return EditPlan.Refuse(L.Transfers.OnCampaign);
        if (officer.Status != 6 && officer[OfficerFields.PlaceField] != officer.BaseId)
            return EditPlan.Refuse(L.Transfers.OutsideBase);
        if (data.Date is null) return EditPlan.Refuse(L.Transfers.NoDate);
        Target? target = Targets(data).FirstOrDefault(t => t.Force == request.Force);
        if (target is null) return EditPlan.Refuse(L.Transfers.DestinationInvalid);
        if (officer.Status == 6 && BaseForce(data, officer.BaseId) == target.Force && officer.BaseId == target.Base
            && officer[OfficerFields.PlaceField] <= GameMemory.TribeLast)
            return EditPlan.Refuse(L.StatusChange.Unchanged);
        return new EditPlan([], L.StatusChange.CaptureSummary(officer.Name, OfficerFields.StatusName(officer.Status),
            target.Name, target.BaseName) + "\n" + L.StatusChange.CaptureNote + "\n" + L.StatusChange.UndoNote);
    }
}
