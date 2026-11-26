namespace San14Tenkazu;

internal static class EditorValueRanges
{
    internal static int? OfficialCityFill(long scale, FieldDef field)
    {
        if (CityFields.Caps(scale) is not { } caps) return null;
        return field == CityFields.Gold ? caps.Gold : field == CityFields.Food ? caps.Food
            : field == CityFields.Troops ? caps.Troops : null;
    }

    internal static bool UnconfirmedEffect(long kind) => kind < 0 || kind > PersonalityRules.LastKind;

    // An extended event definition may also use its other slot differently from an official morale event.
    internal static bool NonstandardEventSlot(int condition, int kind, long value) =>
        PersonalityRules.TargetOf(condition) == PersonalityRules.Target.Event
        && kind != (value == 0 ? 0 : PersonalityRules.EventMorale);

    internal static bool IsFormationCombat(RuleKey key) => key is { Table: "formation", Field: "attack" or "defense" };

    internal static bool UnconfirmedFormation(RuleKey key, int value) =>
        IsFormationCombat(key) && (value < 0 || value > FormationRules.Field(key.Field).Maximum);

    internal static bool PreserveFormation(RuleProfile profile, RuleKey key, int current) =>
        IsFormationCombat(key) && (UnconfirmedFormation(key, current) || UnconfirmedFormation(key, profile.Default(key))
            || UnconfirmedFormation(key, profile.Desired(key)));

    internal static RuleChange[] FormationRestore(FormationRecord record, RuleProfile profile)
    {
        var changes = new List<RuleChange>();
        foreach (var field in FormationRules.Fields)
        {
            var key = FormationRules.Key(record.Id, field);
            int current = record.Value(field);
            if (!PreserveFormation(profile, key, current))
                changes.Add(new(key, current, profile.Default(key)));
        }
        return [.. changes];
    }
}
