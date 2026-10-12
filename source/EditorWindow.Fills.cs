namespace San14Tenkazu;

// The fills on the cards that write to many at once, after asking: [충성 150] over a city's, a gate's or a force's officers
// of one status, [부상병을 병사로] and [사기 130] over a force's units. Undone as any write.
internal sealed partial class EditorWindow
{
    private void WireFills()
    {
        cityEditor.Officers.LoyaltyClicked += (officers, status) => _ = RaiseLoyaltyAsync(officers, $"{CityNames.ById[cityEditor.City]} {status}");
        gateEditor.Officers.LoyaltyClicked += (officers, status) => _ = RaiseLoyaltyAsync(officers, $"{CityNames.BaseName(gateEditor.Gate)} {status}");
        forceEditor.Members.LoyaltyClicked += (officers, status) => _ = RaiseLoyaltyAsync(officers, $"{ForceNameOf(forceEditor.Force)} {status}");
        forceEditor.HealClicked += units => _ = FillUnitsAsync(L.Fills.HealUnits(ForceNameOf(forceEditor.Force)), data => Fills.HealUnits(data, units));
        forceEditor.MoraleClicked += units => _ = FillUnitsAsync(L.Fills.RaiseMorale(ForceNameOf(forceEditor.Force), UnitFields.MoraleCap), data => Fills.RaiseMorale(data, units));
    }

    private string ForceNameOf(int force) => snapshot?.ForceName(force) ?? "";

    // Asks before a fill writes, naming the first few of those it changes.
    private bool ConfirmFill(string what, List<RecordEdit> edits)
    {
        if (edits.Count == 0)
        {
            status.Text = L.Fills.NothingToChange(what);
            return false;
        }
        string names = string.Join(", ", edits.Take(8).Select(e => e.Label)) + (edits.Count > 8 ? (" " + L.Fills.AndMore(edits.Count - 8)) : "");
        return ConfirmWrite(L.Fills.WritesTo(what, edits.Count, names));
    }

    private async Task RaiseLoyaltyAsync(IEnumerable<int> officers, string who)
    {
        if (busy || snapshot is not { } data) return;
        List<RecordEdit> edits = Fills.RaiseOfficers(data, officers, [OfficerFields.Loyalty]);
        if (ConfirmFill(L.Fills.Loyalty150(who), edits) && await WriteAsync(edits)) AfterWrite();
    }

    private async Task FillUnitsAsync(string what, Func<GameSnapshot, List<RecordEdit>> fill)
    {
        if (busy || snapshot is not { } data) return;
        List<RecordEdit> edits = fill(data);
        if (ConfirmFill(what, edits) && await WriteAsync(edits)) AfterWrite();
    }
}
