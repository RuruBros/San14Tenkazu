using System.Reflection;

namespace San14Tenkazu.Tests;

// Derive coverage from the field definitions, so adding a field cannot silently
// leave the November registry behind. The explicit exceptions are implicit writes
// in SlotStateCleared and Apply, rather than editable inputs.
internal static class EditorCoverageFixture
{
    internal static readonly FieldDef[] Implicit = [OfficerFields.PersonalitiesInEffect,
        .. OfficerFields.PersonalityTimeLeft, PlayRecordFields.Changed];
    internal static readonly FieldDef[] All = new[] { typeof(CityFields), typeof(GateFields), typeof(FacilityFields),
            typeof(CountyFields), typeof(OfficerFields), typeof(AiTraits), typeof(UnitFields), typeof(ForceFields),
            typeof(ItemFields), typeof(PersonalityFields), typeof(CorpsFields), typeof(PlayRecordFields) }
        .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        .SelectMany(info => info.GetValue(null) switch
        {
            FieldDef field => new[] { field }, IEnumerable<FieldDef> fields => fields, _ => []
        })
        .Concat(Enumerable.Range(1, 51).Select(ForceFields.Friendship))
        .Concat(Enumerable.Range(1, GameMemory.MaxPlayerForce).Select(ForceFields.Agreement))
        .Concat(Enumerable.Range(0, 5).SelectMany(field => Enumerable.Range(0, 7)
            .SelectMany(slot => new[] { ForceFields.PolicyOfficer(field, slot), ForceFields.PolicyDays(field, slot) })))
        .Distinct().OrderBy(field => field.Record).ThenBy(field => field.Offset).ThenBy(field => field.Label).ToArray();

    internal static GameSnapshot Snapshot()
    {
        var fixture = new TestGame().Force(1, 1, 1).Force(2, 2, 2)
            .MilitaryPoints(1, 1, 1, 1, 500, 1).MilitaryPoints(2, 2, 1, 2, 500, 1)
            .City(1, 1, 3).City(2, 2, 2).Gate(52, 1, 4)
            .Officer(1, "군주1", 1, 1, 1).Officer(2, "군주2", 2, 2, 1)
            .Officer(3, "태수", 1, 1, 3).Officer(4, "관문태수", 1, 52, 3)
            .Officer(5, "장수", 1, 1, 4).Officer(6, "장수2", 2, 2, 4)
            .Leading(5).Item(1, "명품", 5, ability: 1, bonus: 10)
            .Personality(1, "개성", "", (20, 10), (21, 5), condition: PersonalityRules.Troops,
                conditionValue: 1000, filter: PersonalityRules.FilterArmy)
            .Play(1);
        foreach (int officer in Enumerable.Range(1, 6))
            fixture.With(officer, OfficerFields.Ideology, 1).With(officer, OfficerFields.PolicyLevel, 1).With(officer, OfficerFields.Birth, 150)
                .With(officer, OfficerFields.Debut, 170).With(officer, OfficerFields.Death, 250);
        for (int city = 1; city <= GameMemory.CityCount; city++) fixture.Castle(city, 2000);
        var data = fixture.Build(new(Traits9: true), 1, originalAbilityTableVerified: true, pace: GamePace.Paused, hour: 0);
        foreach (int city in data.CityIds) Put(data.Cities[city].Data, CityFields.Scale, 1);
        foreach (var unit in data.Units)
        {
            Put(unit.Data, UnitFields.Troops, 1000); Put(unit.Data, UnitFields.Formation, 1);
            foreach (var time in UnitFields.BoostTimes) Put(unit.Data, time, 10);
            foreach (var time in UnitFields.Ailments) Put(unit.Data, time, 10);
        }
        // Nonzero neighbors expose accidental two-byte writes at these byte fields.
        data.Cities[1].Data[CityFields.Morale.Offset + 1] = 0xA5;
        data.Gates[0].Data[GateFields.Morale.Offset + 1] = 0xA5;
        data.OfficerById[5].Data[OfficerFields.Compatibility.Offset + 1] = 0xA5;
        foreach (var kind in PersonalityFields.Kinds) data.Personalities[0].Data[kind.Offset + 1] = 0xA5;
        return data;
    }

    internal static void Put(byte[] bytes, FieldDef field, long value) => field.Encode(value).CopyTo(bytes, field.Offset);
}
