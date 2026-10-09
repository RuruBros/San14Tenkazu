using System.Text.RegularExpressions;

namespace San14Tenkazu;

// What the personality card writes: a 개성's condition (with its number and the second condition its units must
// meet) and its two effects' kinds and values, kept to what the game makes work (PersonalityRules); a value means
// what its kind makes of it: a percent, gold, or 1 for an effect that is only on. A 개성 is the game's own
// definition, saved with the game, so an edit reaches every officer who has it in that save.
internal static class PersonalityEdits
{
    // The officers who have the 개성 in one of their slots.
    internal static List<OfficerRecord> Holders(GameSnapshot data, int personality) =>
        data.Officers.Where(o => OfficerFields.Personalities.Any(f => o[f] == personality)).ToList();

    // A name for each effect kind, as the game words it for a positive value: the description of a 개성 that has that
    // effect alone, one with a positive value first, then one with no condition (or 자신부대 alone), then the first,
    // with whom it reaches and when taken out, as the 개성's condition says those (Condition): 19 is "모든능력상승"
    // from 상승, not 간웅's "자신부대를 제외한 … 자신세력부대의 모든능력상승". A negative value works the other way
    // (낭비's -20 on 군량소비감소). "효과 #N" for a kind that comes only with another.
    internal static Dictionary<int, string> EffectNames(GameSnapshot data)
    {
        var names = new Dictionary<int, string>();
        foreach (PersonalityRecord personality in data.Personalities
            .Where(p => p.Kind(0) != 0 && p.Kind(1) == 0 && p.Description.Length > 0)
            .OrderBy(p => p.Value(0) <= 0)
            .ThenBy(p => p.Condition is not (PersonalityFields.NoCondition or PersonalityFields.OwnUnit))
            .ThenBy(p => p.Index))
            names.TryAdd(personality.Kind(0), EffectOnly(personality.Description));
        foreach (int kind in data.Personalities.SelectMany(p => new[] { p.Kind(0), p.Kind(1) }).Where(k => k != 0)) names.TryAdd(kind, L.Terms.EffectNumber(kind));
        return names;
    }

    // How the game's descriptions word whom and when (their full-width commas read as ","): taken out of an effect's
    // name, as the condition shows them on its own.
    internal static readonly string[] ReachPatterns =
    [
        @"자신부대를 제외한 (특정)?범위 내의? 자신세력부대(\(남\))?(의|가)? ?",
        @"자신을 제외한 특정범위 내의? (아군무장이|자신세력부대의) ?",
        @"특정범위 내의? 적부대(\(남\))?(의|가)? ?",
        @"자신부대와 태수인 거점(의|이)? ?",
        @"자신부대(의|가)? ?",
        @"(격파|괴멸) 시, ?",
        @"마을 ?(위)?에 있으면 ",
        @"함선에 탑승한 경우, ?",
        @"\(함선,병기 외\)",
    ];
    private static readonly Regex Reach = new(string.Join("|", ReachPatterns));

    // An effect's name from a description: whom and when taken out, a space after each comma ("시, 기간단축").
    internal static string EffectOnly(string description) => AfterComma.Replace(Reach.Replace(description, "").Trim(',', '.', ' '), ", ");

    private static readonly Regex AfterComma = new(@",(?! )");

    // Whom the 개성's effects reach and when: its condition (with the troops where it takes them), "(함선·병기 제외)"
    // where the units it names must be no ship or siege.
    internal static string Condition(PersonalityRecord personality)
    {
        string text = ConditionText(personality.Condition, personality.ConditionValue);
        bool named = PersonalityRules.TargetOf(personality.Condition) is PersonalityRules.Target.Own or PersonalityRules.Target.Others;
        return named && personality.Filter == PersonalityRules.FilterNoShipsOrSiege && personality.Condition != PersonalityFields.OwnUnit
            ? $"{text} {L.Personality.NoShipsOrSiege}" : text;
    }

    // An effect kind as the card names it: "name (#N)", "효과 #N" for one without a name of its own, "없음" for none.
    internal static string KindName(IReadOnlyDictionary<int, string> names, int kind)
    {
        if (kind == 0) return L.Terms.None;
        string name = names.GetValueOrDefault(kind, L.Terms.EffectNumber(kind));
        return name == L.Terms.EffectNumber(kind) ? name : $"{name} (#{kind})";
    }

    // A condition from the PersonalityConditions table, 20 with its troops; "조건 #N" for one not in the table.
    internal static string ConditionText(int condition, long troops = -1) =>
        condition == PersonalityRules.Troops && troops >= 0 ? L.Personality.TroopsCondition(troops)
        : L.Table(ConditionTable, condition) is { Length: > 0 } known ? known : L.Personality.ConditionNumber(condition);

    internal const string ConditionTable = "PersonalityConditions", FilterTable = "PersonalityFilters";

    internal static string FilterText(int filter) =>
        filter == 0 ? L.Terms.None : L.Table(FilterTable, filter) is { Length: > 0 } known ? known : L.Personality.ConditionNumber(filter);

    // The kinds that are only on or off: every 개성 with them has the value 1 (저돌's 명령설정, 오환's 친오환, ...).
    internal static HashSet<int> Switches(GameSnapshot data) =>
        data.Personalities.SelectMany(p => new[] { (Kind: p.Kind(0), Value: p.Value(0)), (Kind: p.Kind(1), Value: p.Value(1)) })
            .Where(e => e.Kind != 0).GroupBy(e => e.Kind).Where(g => g.All(e => e.Value == 1)).Select(g => g.Key).ToHashSet();

    // What the card writes, in the order the summary tells it.
    private static readonly FieldDef[] Editable =
    [
        PersonalityFields.Condition, PersonalityFields.ConditionValue, PersonalityFields.Filter,
        PersonalityFields.Kinds[0], PersonalityFields.Values[0], PersonalityFields.Kinds[1], PersonalityFields.Values[1]
    ];

    // The 개성 as the card asks it to be, checked against what the game makes work. Its kinds are checked when they or
    // the condition change (a kind the game has where nothing was found to read it stays as it is). An effect slot
    // without a kind takes no value, an effect that is only switched on keeps 1, and under a 괴멸·격파 condition the
    // effects are the morale the event adds, marked as the game marks them.
    internal static EditPlan Plan(GameSnapshot data, int index, IReadOnlyList<FieldChange> changes)
    {
        if (data.Personalities.FirstOrDefault(p => p.Index == index) is not { } personality) return EditPlan.Refuse(L.Edits.PersonalityGone);
        if (changes.FirstOrDefault(c => !Editable.Contains(c.Field)) is { Field: { } other }) return EditPlan.Refuse(L.Edits.NotOnPersonalityCard(other.Label));
        var after = Editable.ToDictionary(f => f, f => personality[f]);
        foreach (FieldChange change in changes) after[change.Field] = change.Value;
        string name = personality.Name;
        PersonalityRules.Timing timing = personality.Timing;
        int condition = (int)after[PersonalityFields.Condition];
        bool conditionChanged = condition != personality.Condition;
        if (conditionChanged && PersonalityRules.ConditionProblem(timing, condition) is { Length: > 0 } conditionProblem)
            return EditPlan.Refuse(L.Edits.PersonalityCondition(name, conditionProblem));
        PersonalityRules.Target target = PersonalityRules.TargetOf(condition);

        HashSet<int> switches = Switches(data);
        for (int effect = 0; effect < PersonalityFields.Kinds.Length; effect++)
        {
            FieldDef kindField = PersonalityFields.Kinds[effect], valueField = PersonalityFields.Values[effect];
            if (target == PersonalityRules.Target.Event)
                after[kindField] = after[valueField] != 0 ? PersonalityRules.EventMorale : 0;
            int kind = (int)after[kindField];
            bool kindChanged = kind != personality.Kind(effect);
            bool check = kindChanged || conditionChanged && PersonalityRules.ReachOf(kind) != PersonalityRules.Reach.Unknown;
            if (check && PersonalityRules.KindProblem(timing, condition, kind) is { Length: > 0 } kindProblem)
                return EditPlan.Refuse(L.Edits.PersonalityKind(name, kindField.Label, kindProblem));
            bool valueAsked = after[valueField] != personality.Value(effect);
            if (kind == 0 && target != PersonalityRules.Target.Event)
            {
                if (!kindChanged && valueAsked) return EditPlan.Refuse(L.Edits.NoEffect(name, valueField.Label));
                after[valueField] = 0;
            }
            else if (switches.Contains(kind))
            {
                if (!kindChanged && valueAsked) return EditPlan.Refuse(L.Edits.SwitchOnly(name, valueField.Label));
                after[valueField] = 1;
            }
        }

        bool filterAsked = after[PersonalityFields.Filter] != personality.Filter;
        int filter = PersonalityRules.FilterFor(index, timing, condition, (int)after[PersonalityFields.Filter]);
        if (filterAsked && filter != after[PersonalityFields.Filter]) return EditPlan.Refuse(L.Edits.PersonalityFilter(name));
        after[PersonalityFields.Filter] = filter;

        switch (PersonalityRules.NumberOf(index, condition))
        {
            case PersonalityRules.Number.Troops when after[PersonalityFields.ConditionValue] is < 1 or > PersonalityRules.MaxTroops:
                return EditPlan.Refuse(L.Edits.PersonalityNumber(name, L.Personality.TroopsLabel, 1, PersonalityRules.MaxTroops));
            case PersonalityRules.Number.Range when after[PersonalityFields.ConditionValue] is < 1 or > PersonalityRules.MaxRange:
                return EditPlan.Refuse(L.Edits.PersonalityNumber(name, L.Personality.RangeLabel, 1, PersonalityRules.MaxRange));
            case PersonalityRules.Number.None:
                after[PersonalityFields.ConditionValue] = personality.ConditionValue;
                break;
        }

        Dictionary<int, string> kinds = EffectNames(data);
        string Shown(FieldDef field, long value, long troops) =>
            field == PersonalityFields.Condition ? ConditionText((int)value, troops)
            : field == PersonalityFields.Filter ? FilterText((int)value)
            : PersonalityFields.Kinds.Contains(field) ? KindName(kinds, (int)value)
            : value.ToString("N0");
        List<FieldChange> real = Editable.Where(f => after[f] != personality[f]).Select(f => new FieldChange(f, after[f])).ToList();
        if (real.Count == 0) return new EditPlan([], name);
        string parts = string.Join(", ", real.Select(c =>
            $"{c.Field.Label} {Shown(c.Field, personality[c.Field], personality.ConditionValue)} → {Shown(c.Field, c.Value, after[PersonalityFields.ConditionValue])}"));
        List<OfficerRecord> holders = Holders(data, index);
        int active = holders.Count(o => OfficerFields.IsActive(o.Status));
        // The game judges a lit 개성 again by its condition alone and only spreads one that is dark: one lit for its
        // own unit would stay so under a condition that names others while that holds. A new condition or second
        // condition so has the holders' slots go dark, to be judged afresh (GameMemory.Apply).
        bool relight = timing == PersonalityRules.Timing.WhileMet
            && (conditionChanged || after[PersonalityFields.Filter] != personality.Filter);
        List<SlotRestart>? restarts = relight
            ? holders.SelectMany(o => Enumerable.Range(0, OfficerFields.Personalities.Length).Where(s => o[OfficerFields.Personalities[s]] == index)
                .Select(s => new SlotRestart(o.Address, s, index))).ToList()
            : null;
        return new EditPlan([new RecordEdit(RecordKind.Personality, personality.Address, personality.Data, L.Edits.PersonalityLabel(name), real, restarts)],
            L.Edits.PersonalitySummary(name, parts, holders.Count, active));
    }
}

// What the item card writes: the ability a 명품 adds and by how much (its holder is given on the officer card). An
// item that adds no ability adds nothing (the game's own have 0 there), so 없음 takes the bonus to 0.
internal static class ItemEdits
{
    internal static EditPlan Plan(GameSnapshot data, int index, IReadOnlyList<FieldChange> changes)
    {
        if (data.Items.FirstOrDefault(i => i.Index == index) is not { } item) return EditPlan.Refuse(L.Edits.ItemGone);
        if (changes.Any(c => c.Field != ItemFields.Ability && c.Field != ItemFields.Bonus))
            return EditPlan.Refuse(L.Edits.ItemCardOnly);
        long ability = item.Ability, bonus = item.Bonus;
        foreach (FieldChange change in changes)
            if (change.Field == ItemFields.Ability) ability = change.Value;
            else bonus = change.Value;
        if (ability == 0) bonus = 0;
        var real = new List<FieldChange>();
        if (ability != item.Ability) real.Add(new FieldChange(ItemFields.Ability, ability));
        if (bonus != item.Bonus) real.Add(new FieldChange(ItemFields.Bonus, bonus));
        string Gives(long a, long b) => a == 0 ? L.Terms.None : $"{ItemFields.AbilityName((int)a)} +{b}";
        return new EditPlan(real.Count == 0 ? [] : [new RecordEdit(RecordKind.Item, item.Address, item.Data, L.Edits.ItemLabel(item.Name), real)],
            $"{item.Name}: {Gives(item.Ability, item.Bonus)} → {Gives(ability, bonus)}");
    }
}
