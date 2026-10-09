namespace San14Tenkazu.Tests;

// What the unit card may write: a 진형 the game's own setter would take, a 능력 변화 level that the game will keep (it
// has time left, written first), and a 도발 only lowered (whom the unit answers cannot be chosen).
public class UnitEditTests
{
    private const int 마등 = 521, 선비투장 = 4137, 마등군 = 9, 마등군단 = 9, 안정 = 22;
    private const int 추행 = 7, 몽충 = 12, 사이 = 14, 수송 = 19;

    private static GameSnapshot Game(int commander = 마등) => new TestGame()
        .Force(마등군, ruler: 마등, 마등군단).City(안정, 마등군단, governor: 마등)
        .Officer(마등, "마등", 마등군단, 안정, status: 1).Officer(선비투장, "선비투장", 마등군단, 안정, status: 4)
        .Leading(commander)
        .Build();

    private static EditPlan Plan(GameSnapshot data, params FieldChange[] changes)
    {
        UnitRecord unit = data.Units.Single();
        return UnitEdits.Plan(data, unit.Slot, unit.Commander, changes);
    }

    private static void Set(GameSnapshot data, FieldDef field, long value) => field.Encode(value).CopyTo(data.Units.Single().Data, field.Offset);

    [Fact]
    public void ALevelGoesInAfterItsTime()
    {
        GameSnapshot data = Game();
        FieldDef level = UnitFields.BoostLevels[0], time = UnitFields.BoostTimes[0];

        EditPlan plan = Plan(data, new FieldChange(level, 10), new FieldChange(time, 240));

        Assert.False(plan.Refused);
        Assert.Equal([time, level], Assert.Single(plan.Edits).Changes.Select(c => c.Field));
    }

    [Fact]
    public void ALevelWithNoTimeLeftIsRefused()
    {
        GameSnapshot data = Game();

        EditPlan plan = Plan(data, new FieldChange(UnitFields.BoostLevels[3], -4));

        Assert.True(plan.Refused);
        Assert.Contains(L.Terms.BoostDefense, plan.Refusal);
    }

    [Fact]
    public void ALevelMayChangeWhileTimeIsLeftAndGoWhenTheTimeDoes()
    {
        GameSnapshot data = Game();
        Set(data, UnitFields.BoostLevels[1], 3);
        Set(data, UnitFields.BoostTimes[1], 20);

        Assert.False(Plan(data, new FieldChange(UnitFields.BoostLevels[1], 10)).Refused);
        Assert.False(Plan(data, new FieldChange(UnitFields.BoostLevels[1], 0), new FieldChange(UnitFields.BoostTimes[1], 0)).Refused);
        Assert.True(Plan(data, new FieldChange(UnitFields.BoostTimes[1], 0)).Refused);
    }

    [Fact]
    public void A도발IsOnlyLowered()
    {
        GameSnapshot data = Game();
        FieldDef provoked = UnitFields.Ailments[UnitFields.Provoked];
        Set(data, provoked, 12);

        Assert.True(Plan(data, new FieldChange(provoked, 30)).Refused);
        Assert.False(Plan(data, new FieldChange(provoked, 0)).Refused);
        Assert.False(Plan(data, new FieldChange(UnitFields.Ailments[0], 40), new FieldChange(UnitFields.Ailments[1], 8)).Refused);
    }

    [Fact]
    public void AnArmyTakesALandFormationAndSaiOnlyUnderATribeOfficer()
    {
        Assert.False(Plan(Game(), new FieldChange(UnitFields.Formation, 추행)).Refused);
        Assert.True(Plan(Game(), new FieldChange(UnitFields.Formation, 몽충)).Refused);
        Assert.True(Plan(Game(), new FieldChange(UnitFields.Formation, 사이)).Refused);
        Assert.False(Plan(Game(선비투장), new FieldChange(UnitFields.Formation, 사이)).Refused);
    }

    [Fact]
    public void ATransportKeepsItsFormation()
    {
        GameSnapshot data = Game();
        data.Units.Single().Data[UnitFields.KindOffset] = UnitFields.TransportKind;
        data.Units.Single().Data[UnitFields.FormationOffset] = 수송;

        EditPlan plan = Plan(data, new FieldChange(UnitFields.Formation, 추행));

        Assert.True(plan.Refused);
        Assert.Equal(L.Edits.TransportFormation, plan.Refusal);
    }

    [Fact]
    public void ALevelIsASignedByte()
    {
        var unit = new byte[GameMemory.UnitStride];
        FieldDef level = UnitFields.BoostLevels[0];

        level.Encode(-7).CopyTo(unit, level.Offset);

        Assert.Equal(0xF9, unit[level.Offset]);
        Assert.Equal(-7, level.Read(unit));
        Assert.Throws<ArgumentOutOfRangeException>(() => level.Encode(UnitFields.BoostLevelMax + 1));
    }

    // Bit k is formation k, as the game tests it; 사이 is offered to tribe officers only.
    [Fact]
    public void AnOfficersFormationsAreBits()
    {
        const long 하후연 = 0xCC;   // 봉시, 안행, 장사, 추행

        Assert.Equal([2, 3, 6, 7], Enumerable.Range(1, 20).Where(f => OfficerFields.HasFormation(하후연, f)));
        Assert.DoesNotContain(사이, OfficerFields.FormationsFor(마등));
        Assert.Contains(사이, OfficerFields.FormationsFor(선비투장));
    }
}
