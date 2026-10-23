namespace San14Tenkazu;

// Card tables stage inputs for their footer. Standalone list writes merge the snapshot and preserve card drafts.
internal sealed partial class EditorWindow
{
    private void ConfigureMemberEditing(OfficerTables tables, EditorCard card)
    {
        tables.Drafts = card.TableDrafts;
        tables.CanEditCell = (id, field) => CanEditCell(new OfficerTarget(id), field);
        tables.CellWriter = (id, field, value) => WriteCellAsync(new OfficerTarget(id), field, value);
        tables.ColumnWriter = (ids, field, value) => WriteColumnAsync(ids.Select(id => (EditorTarget)new OfficerTarget(id)).ToList(), field, value);
    }

    private void ConfigureCellEditing(GameTable table, Func<object, EditorTarget> target, EditorCard? card = null)
    {
        table.DraftTarget = target;
        table.Drafts = card?.TableDrafts ?? lists.TableDrafts;
        table.CanEditCell = (tag, field) => CanEditCell(target(tag), field);
        table.CellWriter = (tag, field, value) => WriteCellAsync(target(tag), field, value);
        table.ColumnWriter = (tags, field, value) => WriteColumnAsync(tags.Select(target).ToList(), field, value);
    }

    private bool CanEditCell(EditorTarget target, FieldDef field)
    {
        if (snapshot is not { } data || WriteBlock([GameChecks.GroupOf(field)]) is not null) return false;
        return target switch
        {
            CityTarget(int id) => data.IsCity(id) && (field == CityFields.Troops || field == CityFields.Gold || field == CityFields.Food),
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
        return PlanTargetEdits(data, target, [new(field, value)]);
    }

    private static EditPlan PlanTargetEdits(GameSnapshot data, EditorTarget target, List<FieldChange> changes)
    {
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

    private async Task<bool> WriteCardPlanAsync(EditorCard card, EditPlan own)
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        var changes = card.TableDrafts.Changes(out string error);
        if (changes is null) { status.Text = error; card.FocusInvalidTableInput(); return false; }
        if (changes.Any(row => row.Changes.Any(c => !CanEditCell(row.Target, c.Field))))
        {
            status.Text = L.Lists.BulkUnavailable;
            return false;
        }
        return await WritePlanAsync(PlanCardEdits(data, own, changes));
    }

    private async Task ApplyListDraftsAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return;
        var changes = lists.TableDrafts.Changes(out string error);
        if (changes is null) { status.Text = error; lists.FocusInvalidInput(); return; }
        if (changes.Any(row => row.Changes.Any(c => !CanEditCell(row.Target, c.Field))))
        { status.Text = L.Lists.BulkUnavailable; return; }
        EditPlan plan = PlanCardEdits(data, new([], L.Lists.DraftScope), changes);
        if (plan.Refused) { status.Text = plan.Refusal; return; }
        if (plan.Edits.Count > 0 && changes.Any(row => row.Target is PersonalityTarget) &&
            !ConfirmWrite(string.Join("\n", changes.Where(row => row.Target is PersonalityTarget)
                .Select(row => PlanTargetEdits(data, row.Target, row.Changes).Summary)))) return;
        if (plan.Edits.Count > 0 && !await WriteAsync([.. plan.Edits])) return;
        lists.TableDrafts.Clear();
        MergeSnapshot();
    }

    // Validate every target first, then merge changes to the same record (e.g. a new governor's status and ability).
    // GameMemory.Apply receives one batch, preserving its rollback and single-entry undo behavior.
    internal static EditPlan PlanCardEdits(GameSnapshot data, EditPlan own,
        IReadOnlyList<(EditorTarget Target, List<FieldChange> Changes)> tableChanges)
    {
        if (own.Refused) return own;
        var edits = own.Edits.ToList();
        foreach (var row in tableChanges)
        {
            foreach (var change in row.Changes)
                if (change.Value < change.Field.Min || change.Value > change.Field.Max)
                    return EditPlan.Refuse(change.Field.RangeMessage);
            EditPlan plan = PlanTargetEdits(data, row.Target, row.Changes);
            if (plan.Refused) return plan;
            edits.AddRange(plan.Edits);
        }
        var merged = new List<RecordEdit>();
        foreach (var group in edits.GroupBy(e => (e.Kind, e.Address)))
        {
            RecordEdit first = group.First();
            var fields = group.SelectMany(e => e.Changes).GroupBy(c => c.Field).ToList();
            if (group.Any(e => !e.Expected.SequenceEqual(first.Expected)) || fields.Any(f => f.Select(c => c.Value).Distinct().Count() > 1))
                return EditPlan.Refuse(L.Lists.BulkUnavailable);
            merged.Add(first with
            {
                Changes = fields.Select(f => f.First()).ToList(),
                Restarts = group.SelectMany(e => e.Restarts ?? []).Distinct().ToList(),
                MilitaryPoints = group.Select(e => e.MilitaryPoints).FirstOrDefault(g => g is not null),
                ExpectedRuntimeRules = group.Select(e => e.ExpectedRuntimeRules).FirstOrDefault(r => r is not null)
            });
        }
        return new(merged, own.Summary);
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
