namespace San14Tenkazu.Tests;

// What the cards' [변경 적용] writes along with the values typed: the ties the game keeps on both sides, the one
// holder of an office, the 태수's status, an item's record, an ability's experience.
public class CardEditTests
{
    private const int 조조군 = 1, 손책군 = 2, 조조군단 = 1, 손책군단 = 2;
    private const int 진류 = 15, 허창 = 16;
    private const int 정동장군 = 20;

    // 조조군 holds 진류 (태수 하후돈) and 허창; 하후연 is 정동장군. 유비 and 감부인 are married, as are 장비 and 하후씨;
    // 유비, 관우 and 장비 are one 의형제 group (유비's ID); 순욱 and 순유 another. 적토마 is with 여포, 청홍검 not found yet.
    private static TestGame Game() => new TestGame()
        .Force(조조군, ruler: 1, 조조군단).Force(손책군, ruler: 2, 손책군단)
        .City(진류, 조조군단, governor: 10).City(허창, 조조군단, governor: 1)
        .Officer(1, "조조", 조조군단, 허창, status: 1).Officer(2, "손책", 손책군단, 0, status: 1)
        .Officer(10, "하후돈", 조조군단, 진류, status: 3).Officer(11, "하후연", 조조군단, 진류, status: 4, office: 정동장군)
        .Officer(12, "조인", 조조군단, 진류, status: 4)
        .Officer(20, "유비", 0, 허창, status: 5).Officer(21, "감부인", 0, 허창, status: 5)
        .Officer(22, "관우", 0, 허창, status: 5).Officer(23, "장비", 0, 허창, status: 5).Officer(24, "하후씨", 0, 허창, status: 5)
        .Officer(30, "순욱", 조조군단, 허창, status: 4).Officer(31, "순유", 조조군단, 허창, status: 4)
        .Officer(40, "여포", 0, 진류, status: 5)
        .With(20, OfficerFields.Spouse, 21).With(21, OfficerFields.Spouse, 20)
        .With(23, OfficerFields.Spouse, 24).With(24, OfficerFields.Spouse, 23)
        .With(20, OfficerFields.Sworn, 20).With(22, OfficerFields.Sworn, 20).With(23, OfficerFields.Sworn, 20)
        .With(30, OfficerFields.Sworn, 30).With(31, OfficerFields.Sworn, 30)
        .Item(1, "적토마", owner: 40).Item(2, "청홍검", owner: 0, state: ItemFields.Hidden, place: 77)
        .Castle(진류, durability: 3000);

    private static (GameSnapshot Data, EditPlan Plan, Written After) Officer(TestGame game, int officer, OfficerEdits.Request request)
    {
        GameSnapshot data = game.Build();
        EditPlan plan = OfficerEdits.Plan(data, officer, request);
        return (data, plan, new Written(plan));
    }

    private static OfficerEdits.Request Ask(List<FieldChange>? own = null, int? spouse = null, int? sworn = null, params int[] items) =>
        new(own ?? [], spouse, sworn, items);

    [Fact]
    public void MarryingWritesBothSidesAndLeavesTheFormerSpousesWithout()
    {
        // 유비 marries 하후씨: 감부인 (his) and 장비 (hers) are left without a spouse.
        (GameSnapshot data, _, Written after) = Officer(Game(), 20, Ask(spouse: 24));

        Assert.Equal(24, after[data.OfficerById[20], OfficerFields.Spouse]);
        Assert.Equal(20, after[data.OfficerById[24], OfficerFields.Spouse]);
        Assert.Equal(0, after[data.OfficerById[21], OfficerFields.Spouse]);
        Assert.Equal(0, after[data.OfficerById[23], OfficerFields.Spouse]);
    }

    [Fact]
    public void DivorcingLeavesBothWithout()
    {
        (GameSnapshot data, _, Written after) = Officer(Game(), 20, Ask(spouse: 0));

        Assert.Equal(0, after[data.OfficerById[20], OfficerFields.Spouse]);
        Assert.Equal(0, after[data.OfficerById[21], OfficerFields.Spouse]);
    }

    [Fact]
    public void JoiningASwornGroupTakesItsNumber()
    {
        // 조인 joins 유비's group through 관우: the group keeps 유비's ID.
        (GameSnapshot data, _, Written after) = Officer(Game(), 12, Ask(sworn: 22));

        Assert.Equal(20, after[data.OfficerById[12], OfficerFields.Sworn]);
    }

    [Fact]
    public void SwearingWithSomeoneOutsideAnyGroupMakesOneUnderHisNumber()
    {
        (GameSnapshot data, _, Written after) = Officer(Game(), 12, Ask(sworn: 10));

        Assert.Equal(10, after[data.OfficerById[12], OfficerFields.Sworn]);
        Assert.Equal(10, after[data.OfficerById[10], OfficerFields.Sworn]);
    }

    [Fact]
    public void AGroupLeftWithOneMemberEnds()
    {
        // 순유 leaves 순욱's group: 순욱 is no longer sworn to anyone.
        (GameSnapshot data, _, Written after) = Officer(Game(), 31, Ask(sworn: 0));

        Assert.Equal(0, after[data.OfficerById[31], OfficerFields.Sworn]);
        Assert.Equal(0, after[data.OfficerById[30], OfficerFields.Sworn]);
    }

    [Fact]
    public void AnOfficeGivenIsTakenFromWhoeverHeldItInTheForce()
    {
        (GameSnapshot data, _, Written after) = Officer(Game(), 12, Ask([new FieldChange(OfficerFields.Office, 정동장군)]));

        Assert.Equal(정동장군, after[data.OfficerById[12], OfficerFields.Office]);
        Assert.Equal(OfficerFields.NoOffice, after[data.OfficerById[11], OfficerFields.Office]);
    }

    [Fact]
    public void AnItemGivenChangesHandsAndOneNotFoundYetCountsAsFound()
    {
        (GameSnapshot data, _, Written after) = Officer(Game(), 12, Ask(items: [1, 2]));
        ItemRecord 적토마 = data.Items.Single(i => i.Index == 1), 청홍검 = data.Items.Single(i => i.Index == 2);

        Assert.Equal(12, after[적토마, ItemFields.Owner]);
        Assert.Equal(12, after[청홍검, ItemFields.Owner]);
        Assert.Equal(ItemFields.Found, after[청홍검, ItemFields.State]);
        Assert.Equal(0, after[청홍검, ItemFields.Place]);
    }

    [Fact]
    public void AnAbilityTypedTakesItsExperienceAlong()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Officer(Game(), 12, Ask([new FieldChange(OfficerFields.Leadership, 95)]));
        OfficerRecord 조인 = data.OfficerById[12];

        Assert.Equal(95, after[조인, OfficerFields.Leadership]);
        Assert.Contains(plan.Edits.Single().Changes, c => OfficerFields.IsExperience(c.Field));
    }

    [Fact]
    public void TacticsLearnedBeyondTheCommonOnesStayFirst()
    {
        // He learned 137 and has 8 and 14; 14 is taken off and 3 picked.
        Assert.Equal([137, 3, 8, 0, 0], OfficerEdits.TacticSlots([8, 14, 137, 0, 0], [8, 3]));
        // Five at most.
        Assert.Equal([137, 1, 2, 3, 4], OfficerEdits.TacticSlots([137, 0, 0, 0, 0], [1, 2, 3, 4, 5]));
    }

    [Fact]
    public void ValuesTheGameAlreadyHoldsAreNotWritten()
    {
        (_, EditPlan plan, _) = Officer(Game(), 12, Ask([new FieldChange(OfficerFields.Loyalty, 90)]));

        Assert.Empty(plan.Edits);
    }

    [Fact]
    public void ANew태수GovernsByStatusAndTheOneBeforeBecomesAGeneral()
    {
        GameSnapshot data = Game().Build();
        EditPlan plan = CityEdits.Plan(data, 진류, new CityEdits.Request([], [], Governor: 11));
        var after = new Written(plan);

        Assert.Equal(11, after[data.Cities[진류], CityFields.Governor]);
        Assert.Equal(3, after[data.OfficerById[11], OfficerFields.StatusField]);
        Assert.Equal(4, after[data.OfficerById[10], OfficerFields.StatusField]);
    }

    [Fact]
    public void ARulersCityKeepsHisStatusWhenAnotherIsMade태수()
    {
        GameSnapshot data = Game().Build();
        EditPlan plan = CityEdits.Plan(data, 허창, new CityEdits.Request([], [], Governor: 30));
        var after = new Written(plan);

        Assert.Equal(1, after[data.OfficerById[1], OfficerFields.StatusField]);
        Assert.Equal(3, after[data.OfficerById[30], OfficerFields.StatusField]);
    }

    [Fact]
    public void Only태수OrGeneralsOfTheForceInTheCityMayGovernIt()
    {
        GameSnapshot data = Game().Officer(13, "조홍", 조조군단, 허창, status: 4, place: 595).Build();

        Assert.True(CityEdits.Plan(data, 진류, new CityEdits.Request([], [], Governor: 12)).Edits.Count > 0);
        Assert.True(CityEdits.Plan(data, 진류, new CityEdits.Request([], [], Governor: 30)).Refused);   // 순욱 is in 허창
        Assert.True(CityEdits.Plan(data, 진류, new CityEdits.Request([], [], Governor: 40)).Refused);   // 여포 is 재야
        Assert.True(CityEdits.Plan(data, 허창, new CityEdits.Request([], [], Governor: 13)).Refused);   // 조홍 is away
    }

    [Fact]
    public void ACitysDurabilityIsWrittenToItsCastle()
    {
        GameSnapshot data = Game().Build();
        EditPlan plan = CityEdits.Plan(data, 진류, new CityEdits.Request([new FieldChange(CityFields.Durability, 5000), new FieldChange(CityFields.Gold, 50_000)], [], null));
        var after = new Written(plan);

        Assert.Equal(5000, after[data.CastleByCity[진류], CityFields.Durability]);
        Assert.Equal(50_000, after[data.Cities[진류], CityFields.Gold]);
    }

    // 호뢰관 of 조조군: 장료 its 태수 and 악진 there; 우금 belongs to it but is away, 조인 is in 진류.
    private const int 호뢰관 = 53;

    private static GameSnapshot WithGate() => Game().Gate(호뢰관, 조조군단, governor: 50, durability: 8000)
        .Officer(50, "장료", 조조군단, 호뢰관, status: 3).Officer(51, "악진", 조조군단, 호뢰관, status: 4)
        .Officer(52, "우금", 조조군단, 호뢰관, status: 4, place: 595).Build();

    [Fact]
    public void AGatesDurabilityIsWrittenToItsBuildingAndTheRestToItsRecord()
    {
        GameSnapshot data = WithGate();
        GateRecord gate = data.Gates.Single();
        EditPlan plan = GateEdits.Plan(data, 호뢰관, new GateEdits.Request([new FieldChange(GateFields.Durability, 3000), new FieldChange(GateFields.Troops, 20_000),
            new FieldChange(GateFields.Morale, 120)], null));
        var after = new Written(plan);

        Assert.Equal(3000, after[gate.Building!, GateFields.Durability]);
        Assert.Equal(20_000, after[gate, GateFields.Troops]);
        Assert.Equal(120, after[gate, GateFields.Morale]);
        Assert.Equal(8000, after[gate, GateFields.BaseDurability]);
    }

    [Fact]
    public void AGatesNew태수Is태수ByStatusAndTheOneBeforeGoesBackTo일반()
    {
        GameSnapshot data = WithGate();
        EditPlan plan = GateEdits.Plan(data, 호뢰관, new GateEdits.Request([], Governor: 51));
        var after = new Written(plan);

        Assert.Equal(51, after[data.Gates.Single(), CityFields.GateGovernor]);
        Assert.Equal(3, after[data.OfficerById[51], OfficerFields.StatusField]);
        Assert.Equal(4, after[data.OfficerById[50], OfficerFields.StatusField]);
    }

    [Fact]
    public void Only태수OrGeneralsOfTheForceInTheGateMayGovernIt()
    {
        GameSnapshot data = WithGate();

        Assert.True(GateEdits.Plan(data, 호뢰관, new GateEdits.Request([], Governor: 52)).Refused);   // 우금 is away
        Assert.True(GateEdits.Plan(data, 호뢰관, new GateEdits.Request([], Governor: 12)).Refused);   // 조인 is in 진류
        Assert.False(GateEdits.Plan(data, 호뢰관, new GateEdits.Request([], Governor: 51)).Refused);
    }

    [Fact]
    public void FriendshipIsWrittenInBothForces()
    {
        GameSnapshot data = Game().Build();
        EditPlan plan = ForceEdits.Plan(data, 조조군, [], [(손책군, 80)]);
        var after = new Written(plan);

        Assert.Equal(80, after[data.ForceRecords[조조군], ForceFields.Friendship(손책군)]);
        Assert.Equal(80, after[data.ForceRecords[손책군], ForceFields.Friendship(조조군)]);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(149)]
    [InlineData(150)]
    [InlineData(200)]
    public void LoyaltyFillsOnlyThoseServingARulerAndOnlyUpwards(int loyalty)
    {
        GameSnapshot data = Game().With(12, OfficerFields.Loyalty, loyalty)
            .Officer(50, "포로", 0, 진류, status: 6).Build();
        List<RecordEdit> edits = Fills.RaiseOfficers(data, [1, 10, 11, 12, 20, 50], [OfficerFields.Loyalty]);

        // Rulers, free officers, prisoners and loyalty already at or above 150 stay.
        string[] expected = loyalty < 150 ? ["하후돈", "하후연", "조인"] : ["하후돈", "하후연"];
        Assert.Equal(expected, edits.Select(e => e.Label));
        Assert.All(edits, edit => Assert.Equal(new FieldChange(OfficerFields.Loyalty, 150), Assert.Single(edit.Changes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(150)]
    [InlineData(200)]
    public void Loyalty100RaisesOnlyVassalsBelow100(int loyalty)
    {
        GameSnapshot data = Game().With(12, OfficerFields.Loyalty, loyalty)
            .Officer(50, "포로", 0, 진류, status: 6).Build();
        List<RecordEdit> edits = Fills.RaiseOfficers(data, [1, 10, 11, 12, 20, 50], [OfficerFields.Loyalty], 100);

        string[] expected = loyalty < 100 ? ["하후돈", "하후연", "조인"] : ["하후돈", "하후연"];
        Assert.Equal(expected, edits.Select(e => e.Label));
        Assert.All(edits, edit => Assert.Equal(new FieldChange(OfficerFields.Loyalty, 100), Assert.Single(edit.Changes)));
    }
}
