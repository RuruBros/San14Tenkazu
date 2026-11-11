#if LOCAL_DEVELOPER
namespace San14Tenkazu;

internal static partial class GameMemory
{
    // Explicit fixture setup, never used by the ordinary suite or the product unit editor.
    // The entire sequence is executed by the existing single-freeze trial transaction.
    internal static CompleteEditorPreparation PrepareSeededProvocationTrial(CompleteEditorPreparation prepared, int commander)
    {
        var data = prepared.Snapshot;
        var unit = data.Units.SingleOrDefault(u => u.Commander == commander && data.ForceOfUnit(u) == data.PlayerForce)
            ?? throw new InvalidOperationException(L.Edits.UnitGone);
        var field = UnitFields.Ailments[UnitFields.Provoked];
        if (data.PlayerForce <= 0 || unit[field] != 0) throw new InvalidOperationException(L.Errors.StateMoved);
        var setup = new RecordEdit(RecordKind.Unit, unit.Address, unit.Data, L.Terms.UnitOf(data.CommanderName(commander)), [new(field, 1)]);
        var seeded = data.With([setup]);
        var decrease = UnitEdits.Plan(seeded, unit.Slot, commander, [new(field, 0)]);
        if (decrease.Refused || decrease.Edits.Count != 1 || decrease.Edits[0].Changes.Count != 1 ||
            decrease.Edits[0].Changes[0] != new FieldChange(field, 0))
            throw new InvalidOperationException(L.Errors.StateMoved);
        return prepared with { Plan = new([new("unit-25", L.DeveloperUi.CompleteSeededProvocation,
            [new[] { setup }, decrease.Edits], SeededProvocation: true)], []) };
    }

    internal static CompleteEditPlan PlanCompleteEditorTrials(GameSnapshot data, GameCatalog catalog)
    {
        var trials = new List<CompleteEditTrial>();
        var missing = new List<CompleteEditMissing>();
        var profile = GameBuildProfiles.FindData(GameNativeFactsNovember2026.ExeSha256)!;
        int player = data.PlayerForce;
        var officers = data.OfficersOf(player).Where(o => o.Status is 3 or 4).OrderBy(o => o.Id).ToArray();
        var units = data.Units.Where(u => data.ForceOfUnit(u) == player).OrderBy(u => u.Slot).ToArray();
        string? Problem(GameSnapshot current, IReadOnlyList<RecordEdit> edits)
        {
            var fields = edits.SelectMany(e => e.Changes).Select(c => c.Field).ToArray();
            if (ProfiledEdits.Block(profile, fields) is { } block) return block;
            if (GameChecks.WriteBlock(false, true, GameChecks.Run(current, catalog), ProfiledEdits.CheckedGroups(fields)) is { } checks) return checks;
            var broken = GameChecks.Broken(current, edits, catalog);
            return broken.Count == 0 ? null : string.Join("; ", broken);
        }
        void Add(string key, string label, IEnumerable<EditPlan> candidates, FieldDef? required = null)
        {
            string reason = L.DeveloperUi.CompleteNoTarget;
            foreach (var plan in candidates)
            {
                if (plan.Refused) { reason = plan.Refusal!; continue; }
                var edits = plan.Edits.Where(e => e.Changes.Count > 0).ToArray();
                if (edits.Length == 0 || required is { } field && !edits.Any(e => e.Changes.Any(c => c.Field == field && c.Value != c.Field.Read(e.Expected)))) continue;
                if (Problem(data, edits) is { } problem) { reason = problem; continue; }
                trials.Add(new(key, label, [edits])); return;
            }
            missing.Add(new(key, reason));
        }
        static IEnumerable<long> Near(FieldDef field, byte[] bytes) => new[] { field.Read(bytes) - 1, field.Read(bytes) + 1 }
            .Where(value => value >= field.Min && value <= field.Max);
        IEnumerable<long> OfficerValues(OfficerRecord officer, FieldDef field)
        {
            if (field == OfficerFields.Ideology) return catalog.Ideologies.Select(n => (long)n.Id);
            if (field == OfficerFields.Policy) return catalog.Policies.Select(n => (long)n.Id);
            if (field == OfficerFields.UniqueTactic || OfficerFields.Tactics.Contains(field))
                return catalog.Tactics.Select(n => (long)n.Id).Prepend(0);
            if (field == OfficerFields.Formations) return OfficerFields.FormationsFor(officer.Id)
                .Where(id => catalog.Formations.Any(n => n.Id == id)).Select(id => officer[field] ^ (1L << id));
            if (OfficerFields.Likes.Contains(field) || OfficerFields.Dislikes.Contains(field))
                return officers.Where(o => o.Id != officer.Id).Select(o => (long)o.Id).Prepend(0);
            return Near(field, officer.Data);
        }
        IEnumerable<EditPlan> OfficerPlans(FieldDef field) => officers.SelectMany(officer => OfficerValues(officer, field)
            .Where(value => value != officer[field] && value >= field.Min && value <= field.Max)
            .Select(value => OfficerEdits.Plan(data, officer.Id, new([new(field, value)], null, null, []))));
        FieldDef[] own = [OfficerFields.Gender, OfficerFields.Compatibility, OfficerFields.Ideology, OfficerFields.Policy,
            OfficerFields.PolicyLevel, .. AiTraits.Fields, OfficerFields.UniqueTactic, .. OfficerFields.Tactics,
            OfficerFields.Formations, .. OfficerFields.Likes, .. OfficerFields.Dislikes];
        foreach (var field in own) Add($"officer-{field.Offset:X}", field.Label, OfficerPlans(field), field);
        foreach (var field in new[] { CityFields.Morale })
            Add($"city-{field.Offset:X}", field.Label, data.CitiesOf(player).Order().SelectMany(id => Near(field, data.Cities[id].Data)
                .Select(value => CityEdits.Plan(data, id, new([new(field, value)], [], null)))), field);
        foreach (var field in new[] { GateFields.Morale, GateFields.BaseDurability })
            Add($"gate-{field.Offset:X}", field.Label, data.GatesOf(player).OrderBy(g => g.Id).SelectMany(g => Near(field, g.Data)
                .Select(value => GateEdits.Plan(data, g.Id, new([new(field, value)], null)))), field);
        foreach (var field in new[] { UnitFields.Formation }.Concat(UnitFields.Timed))
        {
            IEnumerable<EditPlan> UnitPlans()
            {
                foreach (var unit in units)
                {
                    var values = field == UnitFields.Formation ? UnitFields.LandFormations.Where(id => catalog.Formations.Any(n => n.Id == id)).Select(id => (long)id)
                        : Near(field, unit.Data);
                    foreach (long value in values.Where(v => v != unit[field]))
                    {
                        var changes = new List<FieldChange> { new(field, value) };
                        int boost = Array.IndexOf(UnitFields.BoostLevels, field);
                        if (boost >= 0 && value != 0 && unit[UnitFields.BoostTimes[boost]] == 0)
                            changes.Add(new(UnitFields.BoostTimes[boost], 1));
                        yield return UnitEdits.Plan(data, unit.Slot, unit.Commander, changes);
                    }
                }
            }
            Add($"unit-{field.Offset:X}", field.Label, UnitPlans(), field);
        }
        foreach (var field in new[] { ItemFields.Ability, ItemFields.Bonus })
            Add($"item-{field.Offset:X}", field.Label, data.Items.Where(i => data.OfficerById.TryGetValue(i.Owner, out var o) &&
                    data.ForceOfCorps(o.Corps) == player).OrderBy(i => i.Index)
                .SelectMany(item => (field == ItemFields.Ability ? Enumerable.Range(0, 6).Select(id => (long)id) : Near(field, item.Data))
                    .Where(v => v >= field.Min && v <= field.Max && v != item[field])
                    .Select(value => ItemEdits.Plan(data, item.Index, [new(field, value)]))), field);
        if (data.ForceRecords.TryGetValue(player, out var force))
        {
            Add("force-rank", ForceFields.Rank.Label, catalog.Ranks.Where(r => r.Id != force[ForceFields.Rank])
                .Select(r => ForceEdits.Plan(data, player, [new(ForceFields.Rank, r.Id)], [])), ForceFields.Rank);
            Add("force-friendship", L.DeveloperUi.CompleteFriendship, data.ForceRecords.Values.Where(f => f.Id != player && ForceFields.CanAlly(f)).OrderBy(f => f.Id)
                .SelectMany(other => Near(ForceFields.Friendship(other.Id), force.Data)
                    .Select(value => ForceEdits.Plan(data, player, [], [(other.Id, (int)value)]))));
            Add("corps-military", CorpsFields.MilitaryPoints.Label, data.CorpsRecords.Values.Where(c => c.Force == player).OrderBy(c => c.Id)
                .SelectMany(c => Near(CorpsFields.MilitaryPoints, c.Data).Select(value => ForceEdits.Plan(data, player, [], [],
                    militaryPoints: [new(c.Id, c.Number, c.Leader, (int)value)]))), CorpsFields.MilitaryPoints);
            Add("force-strategist", ForceFields.Strategist.Label, officers.Select(o => (long)o.Id).Prepend(0)
                .Where(id => id != force[ForceFields.Strategist])
                .Select(id => ForceEdits.Plan(data, player, [new(ForceFields.Strategist, id)], [])), ForceFields.Strategist);
            for (int field = 0; field < ForceFields.PolicyFields.Length; field++)
            {
                int group = field;
                IEnumerable<EditPlan> PolicyPlans(bool daysOnly)
                {
                    for (int slot = 0; slot < ForceFields.PolicySlots; slot++)
                    {
                        var holder = ForceFields.PolicyOfficer(group, slot); var days = ForceFields.PolicyDays(group, slot);
                        if (force[holder] != 0)
                        {
                            if (!daysOnly) yield return ForceEdits.Plan(data, player, [new(holder, 0), new(days, 0)], []);
                            else foreach (long value in Near(days, force.Data)) yield return ForceEdits.Plan(data, player, [new(days, value)], []);
                        }
                        else if (!daysOnly && Enumerable.Range(0, slot).All(s => force[ForceFields.PolicyOfficer(group, s)] != 0))
                            foreach (var officer in officers.Where(o => o.Id != force.Ruler && o.Id != force[ForceFields.Strategist] &&
                                ForceFields.PolicyFieldOf((int)o[OfficerFields.Policy]) == group &&
                                !Enumerable.Range(0, ForceFields.PolicySlots).Any(s => force[ForceFields.PolicyOfficer(group, s)] == o.Id)))
                                yield return ForceEdits.Plan(data, player, [new(holder, officer.Id), new(days, 1)], []);
                    }
                }
                Add($"force-policy-{group}-holder", L.DeveloperUi.CompletePolicyHolder(ForceFields.PolicyFields[group].Name), PolicyPlans(false));
                Add($"force-policy-{group}-days", L.DeveloperUi.CompletePolicyDays(ForceFields.PolicyFields[group].Name), PolicyPlans(true));
            }
        }
        else foreach (string key in new[] { "force-rank", "force-friendship", "corps-military", "force-strategist" }
            .Concat(Enumerable.Range(0, 5).SelectMany(group => new[] { $"force-policy-{group}-holder", $"force-policy-{group}-days" })))
                missing.Add(new(key, L.Edits.ForceGone));
        Add("officer-spouse", L.DeveloperUi.CompleteLinked(OfficerFields.Spouse.Label), officers.SelectMany(o => officers.Where(other => other.Id != o.Id)
            .Select(other => OfficerEdits.Plan(data, o.Id, new([], other.Id, null, [])))), OfficerFields.Spouse);
        Add("officer-sworn", L.DeveloperUi.CompleteLinked(OfficerFields.Sworn.Label), officers.SelectMany(o => officers.Where(other => other.Id != o.Id)
            .Select(other => OfficerEdits.Plan(data, o.Id, new([], null, other.Id, [])))), OfficerFields.Sworn);
        foreach (var kind in new[] { Transfer.Move, Transfer.Defect, Transfer.Dismiss, Transfer.Recruit, Transfer.Release })
        {
            IEnumerable<EditPlan> TransferPlans()
            {
                var candidates = kind is Transfer.Recruit or Transfer.Release
                    ? data.Officers.Where(o => o.Status is 5 or 6 && Transfers.Targets(data).Any(p => p.Force == player && p.Bases.Any(b => b.Base == o.BaseId))).OrderBy(o => o.Id)
                    : officers.OrderBy(o => o.Id);
                foreach (var officer in candidates)
                {
                    if (Transfers.RequiresNative(data, officer, kind)) continue;
                    foreach (int target in kind is Transfer.Dismiss or Transfer.Release ? new[] { 0 } : Transfers.Targets(data).SelectMany(p => p.Bases.Select(b => b.Base)))
                        yield return Transfers.Plan(data, officer, kind, target);
                }
            }
            Add("transfer-" + kind, L.DeveloperUi.CompleteTransfer(kind), TransferPlans(),
                kind is Transfer.Move or Transfer.Defect ? OfficerFields.BaseField : OfficerFields.StatusField);
        }
        Add("city-governor", L.DeveloperUi.CompleteCityGovernor, data.CitiesOf(player).Order().SelectMany(id => officers.Where(o => o.BaseId == id)
            .Select(o => CityEdits.Plan(data, id, new([], [], o.Id)))), CityFields.Governor);
        Add("gate-governor", L.DeveloperUi.CompleteGateGovernor, data.GatesOf(player).OrderBy(g => g.Id).SelectMany(g => officers.Where(o => o.BaseId == g.Id)
            .Select(o => GateEdits.Plan(data, g.Id, new([], o.Id)))), CityFields.GateGovernor);
        FieldDef[] personalityFields = [PersonalityFields.Condition, PersonalityFields.ConditionValue, PersonalityFields.Filter,
            .. PersonalityFields.Kinds, .. PersonalityFields.Values];
        bool HasLitHolder(PersonalityRecord p) => PersonalityEdits.Holders(data, p.Index).Any(o =>
            Enumerable.Range(0, OfficerFields.Personalities.Length).Any(slot => o[OfficerFields.Personalities[slot]] == p.Index &&
                (o[OfficerFields.PersonalitiesInEffect] >> slot & 1) != 0));
        foreach (var field in personalityFields)
        {
            IEnumerable<EditPlan> PersonalityPlans()
            {
                bool restartField = field == PersonalityFields.Condition || field == PersonalityFields.Filter;
                foreach (var p in data.Personalities.Where(p => PersonalityEdits.Holders(data, p.Index).Any(o => data.ForceOfCorps(o.Corps) == player))
                    .OrderByDescending(p => restartField && p.Timing == PersonalityRules.Timing.WhileMet && HasLitHolder(p))
                    .ThenByDescending(p => restartField && p.Timing == PersonalityRules.Timing.WhileMet).ThenBy(p => p.Index))
                {
                    IEnumerable<long> values = field == PersonalityFields.Condition ? PersonalityRules.Offered.Select(v => (long)v)
                        : field == PersonalityFields.Filter ? PersonalityRules.Filters.Select(v => (long)v)
                        : PersonalityFields.Kinds.Contains(field) ? data.Personalities.SelectMany(other => new[] { (long)other.Kind(0), other.Kind(1) }).Distinct().Order()
                        : Near(field, p.Data);
                    foreach (long value in values.Where(v => v >= field.Min && v <= field.Max && v != p[field]))
                        yield return PersonalityEdits.Plan(data, p.Index, [new(field, value)]);
                }
            }
            Add($"personality-{field.Offset:X}", field.Label, PersonalityPlans(), field);
        }
        if (!trials.SelectMany(t => t.Steps).SelectMany(s => s).SelectMany(e => e.Restarts ?? [])
            .Any(r => data.Officers.Any(o => o.Address == r.Officer && (o[OfficerFields.PersonalitiesInEffect] >> r.Slot & 1) != 0)))
            missing.Add(new("personality-holder-restart", L.DeveloperUi.CompleteNoTarget));
        for (int word = 0; word < 2; word++)
        {
            string key = "ancient-word-" + word;
            if (data.Play is not { } play) { missing.Add(new(key, L.Ancient.NoPlayRecord)); continue; }
            var field = word == 0 ? PlayRecordFields.Unlocks1 : PlayRecordFields.Unlocks2;
            int id = Enumerable.Range(AncientOfficers.First, AncientOfficers.Last - AncientOfficers.First + 1)
                .First(id => AncientOfficers.Bit(id) is { } bit && bit / 32 == word);
            int bit = AncientOfficers.Bit(id)!.Value;
            var steps = new List<IReadOnlyList<RecordEdit>>();
            var locked = data;
            if ((play.Unlocks >> bit & 1) != 0)
            {
                long value = unchecked((int)((uint)field.Read(play.Data) & ~(1u << (bit % 32))));
                var setup = new RecordEdit(RecordKind.PlayRecord, play.Address, play.Data, L.DeveloperUi.CompleteUnlockSetup, [new(field, value)]);
                steps.Add([setup]); locked = data.With([setup]);
            }
            var unlock = AncientOfficers.Unlock(locked, [id]);
            if (unlock.Refused || unlock.Edits.Count == 0 || Problem(locked, unlock.Edits) is { })
                missing.Add(new(key, unlock.Refusal ?? L.DeveloperUi.CompleteUnlockRefused));
            else { steps.Add(unlock.Edits); trials.Add(new(key, field.Label + $" (officer:{id}, bit:{bit})", steps)); }
        }
        var ids = data.OfficersOf(player).Select(o => o.Id).ToArray();
        var unitIds = units.Select(u => (u.Slot, u.Commander)).ToArray();
        Add("fill-loyalty", L.DeveloperUi.CompleteLoyaltyFill, [new(Fills.RaiseOfficers(data, ids, [OfficerFields.Loyalty]), L.DeveloperUi.CompleteLoyaltyFill)]);
        Add("fill-heal", L.Force.Heal, [new(Fills.HealUnits(data, unitIds), L.Force.Heal)]);
        Add("fill-morale", L.Force.Morale(UnitFields.MoraleCap), [new(Fills.RaiseMorale(data, unitIds), L.Force.Morale(UnitFields.MoraleCap))]);
        return new(trials.ToArray(), missing.ToArray());
    }
}
#endif
