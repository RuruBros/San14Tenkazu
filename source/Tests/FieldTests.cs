namespace San14Tenkazu.Tests;

public class FieldTests
{
    [Fact]
    public void EncodesWithinRangeAndReadsItBack()
    {
        var record = new byte[0x1F0];
        OfficerFields.Formations.Encode(54_321).CopyTo(record, OfficerFields.Formations.Offset);

        Assert.Equal(54_321, OfficerFields.Formations.Read(record));
    }

    [Fact]
    public void RefusesAValueOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OfficerFields.Compatibility.Encode(150));
        Assert.Throws<ArgumentOutOfRangeException>(() => CityFields.Scale.Encode(0));
    }

    // The DLC's and the player's own officers have IDs from 5000 (양 was 5105): a 정책 slot, the 군사 and the ties
    // take them (a 0~4095 range once refused 양 for a 지원 slot).
    [Fact]
    public void TakesTheIdsOfTheDlcsAndThePlayersOwnOfficers()
    {
        var force = new byte[0x200];
        var officer = new byte[0x1F0];
        foreach ((FieldDef field, byte[] record) in new (FieldDef, byte[])[]
                 { (ForceFields.PolicyOfficer(1, 0), force), (ForceFields.Strategist, force), (OfficerFields.Spouse, officer), (OfficerFields.Likes[7], officer) })
        {
            field.Encode(5105).CopyTo(record, field.Offset);
            Assert.Equal(5105, field.Read(record));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => OfficerFields.Spouse.Encode(ushort.MaxValue));
    }

    [Fact]
    public void KnowsWhichFieldsTieRecordsTogether()
    {
        Assert.True(LinkedFields.Contains(OfficerFields.Spouse));
        Assert.True(LinkedFields.Contains(OfficerFields.StatusField));
        Assert.True(LinkedFields.Contains(CityFields.Governor));
        // A 정책 slot made again is the same field.
        Assert.True(LinkedFields.Contains(ForceFields.PolicyOfficer(2, 3)));
        Assert.False(LinkedFields.Contains(CityFields.Gold));
        Assert.False(LinkedFields.Contains(OfficerFields.Loyalty));
    }

    [Theory]
    [InlineData(80, 90)]
    [InlineData(90, 80)]
    [InlineData(40, 100)]
    public void AnAbilityChangedTakesTheExperienceOfItsNewValue(int before, int after)
    {
        FieldDef ability = OfficerFields.Leadership;
        // An officer at `before`, 123 experience on his way to the next point.
        byte[] record = Officer(ability, before, gained: 123);
        Assert.True(OfficerFields.FitsExperience(record, ability));

        FieldChange changed = OfficerFields.ExperienceFor(record, ability, after);
        ability.Encode(after).CopyTo(record, ability.Offset);
        changed.Field.Encode(changed.Value).CopyTo(record, changed.Field.Offset);

        Assert.True(OfficerFields.FitsExperience(record, ability));
        Assert.Equal(changed.Value, changed.Field.Read(Officer(ability, after, gained: 123)));   // what he had gained stays
    }

    // An officer record with one ability at a value and its experience that far past the value's own.
    private static byte[] Officer(FieldDef ability, int value, int gained)
    {
        var record = new byte[0x1F0];
        ability.Encode(value).CopyTo(record, ability.Offset);
        FieldChange experience = OfficerFields.ExperienceFor(record, ability, value);
        experience.Field.Encode(experience.Value + gained).CopyTo(record, experience.Field.Offset);
        return record;
    }

    [Theory]
    [InlineData("12,345", 12_345)]
    [InlineData("１２３", 123)]
    [InlineData("　７　", 7)]
    public void ReadsTypedNumbersFullWidthFiguresToo(string typed, long value)
    {
        Assert.True(TypedNumber.TryParse(typed, out long read));
        Assert.Equal(value, read);
    }
}
