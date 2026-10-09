namespace San14Tenkazu.Tests;

// [소속 주기] (BaseGrants): which forces may take a base no force holds, which bases may be given, and the values the
// game changes as it hands a base over, written back.
public class BaseGrantTests
{
    private const int 조조군 = 1, 손책군 = 2, 유표군 = 3, 강족 = 46;
    private const int 조조군단 = 1, 손책군단 = 2, 유표군단 = 3, 강족군단 = 4;
    private const int 진류 = 15, 허창 = 16, 오 = 26, 시상 = 28, 백수관 = 58;

    // A city's corps, as the game's own write sets it.
    private static readonly FieldDef CityCorps = new("군단", RecordKind.City, CityFields.CorpsOffset, FieldKind.UInt16, 0, ushort.MaxValue);

    // 조조군 holds 진류 and 허창, 손책군 holds 오; 유표's record says he is 일반, so 유표군 has no ruler; the 강족 are a tribe.
    // 시상 and 백수관 belong to no one.
    private static GameSnapshot Game() => new TestGame()
        .Force(조조군, ruler: 1, 조조군단).Force(손책군, ruler: 2, 손책군단).Force(유표군, ruler: 3, 유표군단).Force(강족, ruler: 4, 강족군단)
        .City(진류, 조조군단).City(허창, 조조군단, governor: 1).City(오, 손책군단, governor: 2)
        .Officer(1, "조조", 조조군단, 허창, status: 1).Officer(2, "손책", 손책군단, 오, status: 1)
        .Officer(3, "유표", 유표군단, 0, status: 4).Officer(4, "미당", 강족군단, 0, status: 1)
        .Castle(시상, durability: 1000)
        .Gate(백수관, 0, durability: 2500)
        .Build();

    [Fact]
    public void ForcesWithTheirRulerTakeBasesAndTheBiggestComeFirst()
    {
        GameSnapshot data = Game();

        Assert.Equal([조조군, 손책군], BaseGrants.Takers(data).Select(t => t.Force));
        Assert.Equal(2, BaseGrants.Takers(data)[0].Cities);
    }

    [Fact]
    public void OnlyABaseNoForceHoldsIsGivenAndOnlyToAForceWithItsRuler()
    {
        GameSnapshot data = Game();

        Assert.Null(BaseGrants.Refusal(data, 시상, 손책군));
        Assert.Null(BaseGrants.Refusal(data, 백수관, 조조군));
        Assert.Equal(L.Give.HasForce("허창", data.ForceName(조조군)), BaseGrants.Refusal(data, 허창, 손책군));
        Assert.NotNull(BaseGrants.Refusal(data, 시상, 유표군));    // no ruler
        Assert.NotNull(BaseGrants.Refusal(data, 시상, 강족));      // a tribe
        Assert.Equal(L.Give.Gone, BaseGrants.Refusal(data, 52, 조조군));   // no such gate here
    }

    // 시상 went to 손책군 by the game's 거점 소유자 변경, which raised its gold and food as a capture does: they go back,
    // the corps the game gave it stays.
    [Fact]
    public void TheCitysValuesTheGameChangedAreWrittenBack()
    {
        GameSnapshot made = Game();
        CityRecord city = made.Cities[시상];
        GameSnapshot before = made.With([new RecordEdit(RecordKind.City, city.Address, city.Data, "시상",
            [new FieldChange(CityFields.Gold, 1500), new FieldChange(CityFields.Food, 8000)])]);
        GameSnapshot after = before.With([new RecordEdit(RecordKind.City, city.Address, before.Cities[시상].Data, "시상",
            [new FieldChange(CityFields.Gold, 5000), new FieldChange(CityFields.Food, 20_000), new FieldChange(CityCorps, 손책군단)])]);

        EditPlan plan = BaseGrants.KeptValues(before, after, 시상);
        var written = new Written(plan);

        RecordEdit edit = Assert.Single(plan.Edits);
        Assert.Equal(after.Cities[시상].Data, edit.Expected);
        Assert.Equal([CityFields.Gold, CityFields.Food], edit.Changes.Select(c => c.Field));
        Assert.Equal(1500, written[after.Cities[시상], CityFields.Gold]);
        Assert.Equal(8000, written[after.Cities[시상], CityFields.Food]);
        Assert.Equal(손책군, after.ForceOfCity(시상));
    }

    [Fact]
    public void TheGatesTroopsAndDurabilityTheGameChangedAreWrittenBack()
    {
        GameSnapshot made = Game();
        GateRecord gate = made.Gates.Single();
        GameSnapshot before = made.With([new RecordEdit(RecordKind.Gate, gate.Address, gate.Data, "백수관", [new FieldChange(GateFields.Troops, 1801)])]);
        GameSnapshot after = before.With([
            new RecordEdit(RecordKind.Gate, gate.Address, before.Gates.Single().Data, "백수관", [new FieldChange(GateFields.Troops, 594)]),
            new RecordEdit(RecordKind.Building, gate.Building!.Address, gate.Building.Data, "백수관", [new FieldChange(GateFields.Durability, 2600)])]);

        var written = new Written(BaseGrants.KeptValues(before, after, 백수관));

        Assert.Equal(1801, written[after.Gates.Single(), GateFields.Troops]);
        Assert.Equal(2500, written[after.Gates.Single().Building!, GateFields.Durability]);
    }

    // Nothing the game left as it was is written.
    [Fact]
    public void ValuesTheGameLeftAloneAreNotWritten()
    {
        GameSnapshot data = Game();

        Assert.Empty(BaseGrants.KeptValues(data, data, 시상).Edits);
        Assert.Empty(BaseGrants.KeptValues(data, data, 백수관).Edits);
    }
}
