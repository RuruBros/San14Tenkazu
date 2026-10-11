namespace San14Tenkazu;

// An editable table cell changes one field through the same plans as its editor card. Other pending inputs in
// the card survive: a cell write merges the snapshot instead of reopening (and clearing) that card.
internal sealed partial class EditorWindow
{
    private void ConfigureMemberEditing(OfficerTables tables)
    {
        tables.CanEditCell = (id, field) => CanEditCell(new OfficerTarget(id), field);
        tables.CellWriter = (id, field, value) => WriteCellAsync(new OfficerTarget(id), field, value);
        tables.ColumnWriter = (ids, field, value) => WriteColumnAsync(ids.Select(id => (EditorTarget)new OfficerTarget(id)).ToList(), field, value);
    }

    private void ConfigureCellEditing(GameTable table, Func<object, EditorTarget> target)
    {
        table.CanEditCell = (tag, field) => CanEditCell(target(tag), field);
        table.CellWriter = (tag, field, value) => WriteCellAsync(target(tag), field, value);
        table.ColumnWriter = (tags, field, value) => WriteColumnAsync(tags.Select(target).ToList(), field, value);
    }

    private bool CanEditCell(EditorTarget target, FieldDef field)
    {
        if (snapshot is not { } data || WriteBlock([GameChecks.GroupOf(field)]) is not null) return false;
        return target switch
        {
            CityTarget(int id) => id is >= 1 and <= GameMemory.CityCount && id < data.Cities.Length && (field == CityFields.Troops || field == CityFields.Gold || field == CityFields.Food),
            GateTarget(int id) => data.Gates.Any(g => g.Id == id) && (field == GateFields.Troops || field == GateFields.Morale || field == GateFields.Durability),
            OfficerTarget(int id) => data.OfficerById.ContainsKey(id) && (field == OfficerFields.Loyalty || OfficerFields.Abilities.Contains(field)),
            UnitTarget(int slot, int commander) => data.Unit(slot, commander) is not null && UnitFields.All.Contains(field),
            PersonalityTarget(int id) => data.Personalities.FirstOrDefault(p => p.Index == id) is { } p
                && Array.IndexOf(PersonalityFields.Values, field) is int effect and >= 0
                && p.Kind(effect) != 0 && !PersonalityEdits.Switches(data).Contains(p.Kind(effect)),
            _ => false
        };
    }

    private async Task<bool> WriteCellAsync(EditorTarget target, FieldDef field, long value)
    {
        if (!CanWrite(out GameSnapshot data) || !CanEditCell(target, field)) return false;
        if (value < field.Min || value > field.Max)
        {
            status.Text = field.RangeMessage;
            return false;
        }
        EditPlan plan = PlanCellEdits(data, target, field, value);
        if (plan.Refused) { status.Text = plan.Refusal; return false; }
        if (plan.Edits.Count == 0) { status.Text = L.Window.NothingChanged; return true; }
        if (target is PersonalityTarget && !ConfirmWrite(plan.Summary)) return false;
        if (!await WriteAsync([.. plan.Edits])) return false;
        MergeSnapshot();
        return true;
    }

    internal static EditPlan PlanCellEdits(GameSnapshot data, EditorTarget target, FieldDef field, long value)
    {
        if (value < field.Min || value > field.Max) return EditPlan.Refuse(field.RangeMessage);
        List<FieldChange> changes = [new(field, value)];
        return target switch
        {
            CityTarget(int id) => CityEdits.Plan(data, id, new(changes, [], null)),
            GateTarget(int id) => GateEdits.Plan(data, id, new(changes, null)),
            OfficerTarget(int id) => OfficerEdits.Plan(data, id, new(changes, null, null, [])),
            UnitTarget(int slot, int commander) => UnitEdits.Plan(data, slot, commander, changes),
            PersonalityTarget(int id) => PersonalityEdits.Plan(data, id, changes),
            _ => throw new InvalidOperationException()
        };
    }

    // Plan every row before writing any of them. The batch is one entry in the undo history.
    internal static EditPlan PlanColumnEdits(GameSnapshot data, IReadOnlyList<EditorTarget> targets, FieldDef field, long value)
    {
        var edits = new List<RecordEdit>();
        foreach (EditorTarget target in targets.Distinct())
        {
            EditPlan plan = PlanCellEdits(data, target, field, value);
            if (plan.Refused) return plan;
            edits.AddRange(plan.Edits);
        }
        return new EditPlan(edits, L.Lists.BulkValue(field.Label, value));
    }

    private async Task<bool> WriteColumnAsync(IReadOnlyList<EditorTarget> targets, FieldDef field, long value)
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        if (targets.Any(target => !CanEditCell(target, field)))
        {
            status.Text = L.Lists.BulkUnavailable;
            return false;
        }
        EditPlan plan = PlanColumnEdits(data, targets, field, value);
        if (plan.Refused) { status.Text = plan.Refusal; return false; }
        if (plan.Edits.Count == 0) { status.Text = L.Window.NothingChanged; return true; }
        if (!ConfirmFill(plan.Summary, [.. plan.Edits])) return false;
        if (!await WriteAsync([.. plan.Edits])) return false;
        MergeSnapshot();
        return true;
    }
}
