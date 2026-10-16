namespace San14Tenkazu;

// Card shortcuts fill drafts. The footer validates and writes them together with the other card inputs.
internal sealed partial class EditorWindow
{
    private void WireFills()
    {
        cityEditor.Officers.LoyaltyClicked += (officers, _) => StageLoyalty(cityEditor, officers);
        gateEditor.Officers.LoyaltyClicked += (officers, _) => StageLoyalty(gateEditor, officers);
        forceEditor.Members.LoyaltyClicked += (officers, _) => StageLoyalty(forceEditor, officers);
        forceEditor.HealClicked += units => StageUnits(units, heal: true);
        forceEditor.MoraleClicked += units => StageUnits(units, heal: false);
    }

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

    // The fill rules (whom, and to what) stay in Fills: its edits are staged as drafts instead of written.
    private void StageLoyalty(EditorCard card, IEnumerable<int> officers)
    {
        if (busy || snapshot is not { } data) return;
        var ids = officers.ToList();
        Dictionary<ulong, OfficerRecord> byAddress = ids.Select(id => data.OfficerById.GetValueOrDefault(id)).OfType<OfficerRecord>()
            .ToDictionary(o => o.Address);
        foreach (RecordEdit edit in Fills.RaiseOfficers(data, ids, [OfficerFields.Loyalty]))
            Stage(card, new OfficerTarget(byAddress[edit.Address].Id), byAddress[edit.Address].Data, edit, raiseOnly: true);
    }

    private void StageUnits(IEnumerable<(int Slot, int Commander)> units, bool heal)
    {
        if (busy || snapshot is not { } data) return;
        var ids = units.ToList();
        var byAddress = ids.Select(id => (Id: id, Unit: data.Unit(id.Slot, id.Commander))).Where(p => p.Unit is not null)
            .ToDictionary(p => p.Unit!.Address, p => p);
        foreach (RecordEdit edit in heal ? Fills.HealUnits(data, ids) : Fills.RaiseMorale(data, ids))
        {
            var (id, unit) = byAddress[edit.Address];
            Stage(forceEditor, new UnitTarget(id.Slot, id.Commander), unit!.Data, edit, raiseOnly: !heal);
        }
    }

    // One record's fill into the card's drafts: a draft the user typed higher than a raise keeps its value, and a
    // draft that does not read as a number is left for the footer to report.
    private static void Stage(EditorCard card, EditorTarget target, byte[] record, RecordEdit edit, bool raiseOnly)
    {
        var numbers = edit.Changes.Select(change => (change, Number: card.TableDrafts.Get(target, change.Field, change.Field.Read(record)))).ToList();
        if (numbers.Any(n => !n.Number.IsValid)) return;
        foreach ((FieldChange change, NumberField number) in numbers)
            if (!raiseOnly || number.Value < change.Value) number.Set(change.Value);
    }
}
