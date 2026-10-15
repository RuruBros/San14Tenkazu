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

    private void StageLoyalty(EditorCard card, IEnumerable<int> officers)
    {
        if (busy || snapshot is not { } data) return;
        foreach (int id in officers)
            if (data.OfficerById.TryGetValue(id, out var officer) && OfficerFields.IsVassal(officer.Status))
            {
                var number = card.TableDrafts.Get(new OfficerTarget(id), OfficerFields.Loyalty, officer[OfficerFields.Loyalty]);
                if (number.IsValid && number.Value < 150) number.Set(150);
            }
    }

    private void StageUnits(IEnumerable<(int Slot, int Commander)> units, bool heal)
    {
        if (busy || snapshot is not { } data) return;
        foreach (var id in units)
        {
            if (data.Unit(id.Slot, id.Commander) is not { } unit) continue;
            var target = new UnitTarget(id.Slot, id.Commander);
            NumberField Number(FieldDef field) => forceEditor.TableDrafts.Get(target, field, unit[field]);
            if (heal)
            {
                var troops = Number(UnitFields.Troops);
                var wounded = Number(UnitFields.Wounded);
                if (!troops.IsValid || !wounded.IsValid || wounded.Value == 0) continue;
                troops.Set(Math.Min(UnitFields.Troops.Max, troops.Value + wounded.Value));
                wounded.Set(0);
            }
            else
            {
                var morale = Number(UnitFields.Morale);
                if (morale.IsValid && morale.Value < UnitFields.MoraleCap) morale.Set(UnitFields.MoraleCap);
            }
        }
    }
}
