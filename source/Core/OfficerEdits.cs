namespace San14Tenkazu;

// What the officer card writes for one officer: his own values, and what the game keeps on other records for them.
// 배우자 and 의형제 are kept on both sides, a 관직 has one holder in a force, a 명품 given changes the item's record,
// and an ability takes its experience along (WithExperience).
internal static class OfficerEdits
{
    // What the card asks: his own fields, and, null when unchanged, the new spouse (0 none) and the officer whose
    // 의형제 group to join (0 to leave his); and the 명품 given to him (their indexes).
    internal sealed record Request(List<FieldChange> Own, int? Spouse, int? SwornWith, IReadOnlyList<int> Items);

    internal static EditPlan Plan(GameSnapshot data, int officerId, Request request)
    {
        if (!data.OfficerById.TryGetValue(officerId, out OfficerRecord? self)) return EditPlan.Refuse(L.Edits.OfficerGone);

        // Each officer's changes, the last value set for a field winning; a value the game already holds is dropped.
        var changes = new Dictionary<int, List<FieldChange>>();
        void Set(int id, FieldDef field, long value)
        {
            if (!data.OfficerById.TryGetValue(id, out OfficerRecord? officer)) return;
            if (!changes.TryGetValue(id, out List<FieldChange>? list)) changes[id] = list = [];
            list.RemoveAll(c => c.Field == field);
            if (officer[field] != value) list.Add(new FieldChange(field, value));
        }
        long Of(int id, FieldDef field) => data.OfficerById.TryGetValue(id, out OfficerRecord? officer) ? officer[field] : 0;

        foreach (FieldChange change in request.Own) Set(self.Id, change.Field, change.Value);
        // 관직: no two officers of a force hold the same one, so whoever held it is left without.
        if (request.Own.FirstOrDefault(c => c.Field == OfficerFields.Office) is { } office && office.Value != OfficerFields.NoOffice)
            foreach (OfficerRecord other in data.OfficersOf(data.ForceOfCorps(self.Corps)).Where(o => o.Id != self.Id && o[OfficerFields.Office] == office.Value))
                Set(other.Id, OfficerFields.Office, OfficerFields.NoOffice);
        // 배우자: both point at each other; whoever either of them was married to is left without a spouse.
        if (request.Spouse is int spouse)
        {
            int before = (int)self[OfficerFields.Spouse];
            if (before != 0 && Of(before, OfficerFields.Spouse) == self.Id) Set(before, OfficerFields.Spouse, 0);
            if (spouse != 0)
            {
                int theirs = (int)Of(spouse, OfficerFields.Spouse);
                if (theirs != 0 && theirs != self.Id && Of(theirs, OfficerFields.Spouse) == spouse) Set(theirs, OfficerFields.Spouse, 0);
                Set(spouse, OfficerFields.Spouse, self.Id);
            }
            Set(self.Id, OfficerFields.Spouse, spouse);
        }
        // 의형제: joining takes the other officer's group (or makes one under his ID); a group left with one member ends.
        if (request.SwornWith is int with)
        {
            int before = (int)self[OfficerFields.Sworn];
            int after = with == 0 ? 0 : Of(with, OfficerFields.Sworn) is > 0 and var theirs ? (int)theirs : with;
            if (after != before)
            {
                Set(self.Id, OfficerFields.Sworn, after);
                if (with != 0 && Of(with, OfficerFields.Sworn) == 0) Set(with, OfficerFields.Sworn, after);
                List<OfficerRecord> rest = before == 0 ? [] : data.Officers.Where(o => o.Id != self.Id && o[OfficerFields.Sworn] == before).ToList();
                if (rest.Count == 1) Set(rest[0].Id, OfficerFields.Sworn, 0);
            }
        }

        List<RecordEdit> edits = changes.Where(pair => pair.Value.Count > 0)
            .Select(pair => (Officer: data.OfficerById[pair.Key], Changes: pair.Value))
            .OrderBy(pair => pair.Officer.Id != self.Id)
            .Select(pair => new RecordEdit(RecordKind.Officer, pair.Officer.Address, pair.Officer.Data, pair.Officer.Name, pair.Changes))
            .ToList();
        // A 명품 given: its holder becomes this officer, and one not found yet counts as found (and lies nowhere); the
        // game then adds it to its list of held ones (GameMemory.SyncOwnedItems), which its rules read.
        foreach (int index in request.Items)
        {
            if (data.Items.FirstOrDefault(i => i.Index == index) is not { } item) continue;
            List<FieldChange> moves = [new FieldChange(ItemFields.Owner, self.Id)];
            if (item.State != ItemFields.Found) moves.Add(new FieldChange(ItemFields.State, ItemFields.Found));
            if (item[ItemFields.Place] != 0) moves.Add(new FieldChange(ItemFields.Place, 0));
            edits.Add(new RecordEdit(RecordKind.Item, item.Address, item.Data, L.Edits.ItemLabel(item.Name), moves));
        }
        if (data.RuntimeRules is null && request.Own.Any(c => OfficerFields.Abilities.Contains(c.Field) || OfficerFields.Personalities.Contains(c.Field)))
            return EditPlan.Refuse(L.RuntimeErrors.RuntimeStateUnknown);
        return new EditPlan(WithExperience(edits, data.RuntimeRules?.Growth ?? false)
            .Select(e => e with { ExpectedRuntimeRules = e.Changes.Any(c => OfficerFields.Abilities.Contains(c.Field)
                || OfficerFields.Personalities.Contains(c.Field)) ? data.RuntimeRules : null }).ToList(), self.Name);
    }

    // The 습득 전법 slots for the common tactics picked: those he learned beyond the common ones stay first, in their
    // order (the card shows only the common 26), then the picked ones in order; five at most, the rest of the slots 0.
    internal static long[] TacticSlots(IEnumerable<long> slots, IEnumerable<int> picked)
    {
        List<long> tactics = [.. slots.Where(id => id > OfficerFields.CommonTactics), .. picked.Where(id => id is > 0 and <= OfficerFields.CommonTactics).Distinct().Order().Select(id => (long)id)];
        return Enumerable.Range(0, OfficerFields.Tactics.Length).Select(slot => slot < tactics.Count ? tactics[slot] : 0).ToArray();
    }

    // A 개성 slot is written as the game writes one: when the slot's 개성 changes, what the old one had going (in effect
    // for his unit, time left) is cleared, so the game judges the new one afresh instead of it taking the old one's
    // state. Taken from the record as it is just before the write (GameMemory.Apply), as the game lights and clears
    // these within seconds; the clearing is not undone, as undoing changes the slot again.
    internal static List<FieldChange> SlotStateCleared(ReadOnlySpan<byte> record, IEnumerable<FieldChange> changes)
    {
        int slots = 0;
        foreach (FieldChange change in changes)
            if (Array.IndexOf(OfficerFields.Personalities, change.Field) is var slot and >= 0 && change.Field.Read(record) != change.Value)
                slots |= 1 << slot;
        var cleared = new List<FieldChange>();
        long inEffect = OfficerFields.PersonalitiesInEffect.Read(record);
        if ((inEffect & slots) != 0) cleared.Add(new FieldChange(OfficerFields.PersonalitiesInEffect, inEffect & ~slots));
        for (int slot = 0; slot < OfficerFields.PersonalityTimeLeft.Length; slot++)
            if ((slots >> slot & 1) != 0 && OfficerFields.PersonalityTimeLeft[slot].Read(record) != 0)
                cleared.Add(new FieldChange(OfficerFields.PersonalityTimeLeft[slot], 0));
        return cleared;
    }

    // An officer's edited ability takes its experience along (OfficerFields.ExperienceFor), so the game finds the two
    // as it keeps them itself. The experience is logged and undone with the ability.
    internal static List<RecordEdit> WithExperience(IEnumerable<RecordEdit> edits, bool growth = false) =>
        edits.Select(edit => edit.Kind != RecordKind.Officer ? edit : edit with
        {
            Changes = [.. edit.Changes, .. edit.Changes.Where(c => OfficerFields.Abilities.Contains(c.Field))
                .Select(c => OfficerFields.ExperienceFor(edit.Expected, c.Field, c.Value, growth))
                .Where(c => c.Field.Read(edit.Expected) != c.Value)]
        }).ToList();
}
