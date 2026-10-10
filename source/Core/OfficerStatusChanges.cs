namespace San14Tenkazu;

internal enum OfficerStatusMode { Regular, Free, Prisoner }

internal static class OfficerStatusChanges
{
    internal sealed record Request(int Officer, OfficerStatusMode Mode, int Force = 0, int Base = 0);
    internal sealed record Change(EditPlan Plan, Transfer? Transfer = null, OfficerCaptivity.Request? Capture = null);

    internal static Change Plan(GameSnapshot data, Request request)
    {
        if (!Enum.IsDefined(request.Mode)) return new(EditPlan.Refuse(L.StatusChange.InvalidMode));
        if (!data.OfficerById.TryGetValue(request.Officer, out var officer)) return new(EditPlan.Refuse(L.StatusChange.Unavailable));
        if (officer.Status == 1) return new(EditPlan.Refuse(L.Transfers.RulerStays));
        if (request.Mode == OfficerStatusMode.Prisoner)
        {
            var capture = new OfficerCaptivity.Request(request.Officer, request.Force);
            return new(OfficerCaptivity.Plan(data, capture), Capture: capture);
        }
        Transfer kind;
        if (request.Mode == OfficerStatusMode.Free)
        {
            if (officer.Status == 5) return new(EditPlan.Refuse(L.StatusChange.Unchanged));
            kind = officer.Status == 6 ? Transfer.Release : Transfer.Dismiss;
        }
        else
        {
            if (!Transfers.Targets(data).Any(f => f.Force == request.Force && f.Bases.Any(b => b.Base == request.Base)))
                return new(EditPlan.Refuse(L.Transfers.DestinationInvalid));
            if (officer.Status == 4 && officer.BaseId == request.Base && data.ForceOfCorps(officer.Corps) == request.Force)
                return new(EditPlan.Refuse(L.StatusChange.Unchanged));
            kind = officer.Status is 5 or 6 ? Transfer.Recruit
                : data.ForceOfCorps(officer.Corps) == request.Force ? Transfer.Move : Transfer.Defect;
        }
        return new(Transfers.Plan(data, officer, kind, request.Base), kind);
    }
}
