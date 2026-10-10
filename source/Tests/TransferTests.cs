namespace San14Tenkazu.Tests;

// The [소속] rules against the moves the game itself made (watched on 2026-10-02) and the ones tried on the running
// game then: 염유 moved within his force, 원담 dismissed, 허공 recruited, 마연 released, 수고 sent to 마등군 while in
// one of 원소군's 모략 slots.
public class TransferTests
{
    private const int 원소군 = 4, 마등군 = 9, 원소군단 = 4, 마등군단 = 9;
    private const int 업 = 6, 양평 = 1, 안정 = 22;
    private const int 모략 = 2;

    // 원소군 (원소 35) holds 업 and 양평 (염유 31 its 태수); 마등군 (마등 521) holds 안정. 수고 470 sits in 원소군's 모략
    // slots between 168 and 268, 전풍 713 is its 군사; 허공 211 is 재야 in 안정, 마연 758 a prisoner in 업.
    private static TestGame Game() => new TestGame()
        .Force(원소군, ruler: 35, 원소군단).Force(마등군, ruler: 521, 마등군단)
        .City(업, 원소군단, governor: 35).City(양평, 원소군단, governor: 31).City(안정, 마등군단, governor: 521)
        .Officer(35, "원소", 원소군단, 업, status: 1).Officer(521, "마등", 마등군단, 안정, status: 1)
        .Officer(31, "염유", 원소군단, 양평, status: 3)
        .Officer(470, "수고", 원소군단, 업, status: 4, loyalty: 97, office: 69)
        .Officer(168, "곽도", 원소군단, 업, status: 4).Officer(268, "봉기", 원소군단, 업, status: 4)
        .Officer(713, "전풍", 원소군단, 업, status: 4)
        .Officer(211, "허공", 0, 안정, status: 5, loyalty: 0)
        .Officer(758, "마연", 0, 업, status: 6, loyalty: 0)
        .Policy(원소군, 모략, (168, 10), (470, 0), (268, 0))
        .Strategist(원소군, 713);

    private static (GameSnapshot Data, EditPlan Plan, Written After) Plan(TestGame game, int officer, Transfer kind, int target = 0)
    {
        GameSnapshot data = game.Build();
        EditPlan plan = Transfers.Plan(data, data.OfficerById[officer], kind, target);
        return (data, plan, new Written(plan));
    }

    [Fact]
    public void AGeneralMovesToAnotherBaseOfHisForceAndNothingElseChanges()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Game(), 470, Transfer.Move, 양평);
        OfficerRecord 수고 = data.OfficerById[470];

        Assert.False(plan.Refused);
        Assert.Equal(양평, after[수고, OfficerFields.BaseField]);
        Assert.Equal(양평, after[수고, OfficerFields.PlaceField]);
        Assert.Equal(4, after[수고, OfficerFields.StatusField]);
        Assert.Equal(97, after[수고, OfficerFields.Loyalty]);
        Assert.Single(plan.Edits);
        Assert.Contains("업 → 양평", plan.Summary);
    }

    [Fact]
    public void A태수WhoMovesBecomesAGeneralAndHisCityHasNone()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Game(), 31, Transfer.Move, 업);

        Assert.Equal(4, after[data.OfficerById[31], OfficerFields.StatusField]);
        Assert.Equal(0, after[data.Cities[양평], CityFields.Governor]);
        Assert.Contains("양평 태수 없음", plan.Summary);
    }

    [Fact]
    public void AnOfficerSentToAnotherForceServesItAsTheGameRecruitsAndLeavesHisOldPolicySlot()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Game(), 470, Transfer.Defect, 안정);
        OfficerRecord 수고 = data.OfficerById[470];
        ForceRecord 원소 = data.ForceRecords[원소군];

        Assert.Equal(마등군단, after[수고, OfficerFields.CorpsField]);
        Assert.Equal(안정, after[수고, OfficerFields.BaseField]);
        Assert.Equal(안정, after[수고, OfficerFields.PlaceField]);
        Assert.Equal(4, after[수고, OfficerFields.StatusField]);
        Assert.Equal(OfficerFields.NoOffice, after[수고, OfficerFields.Office]);
        Assert.Equal(Transfers.LoyaltyOnJoining, after[수고, OfficerFields.Loyalty]);
        Assert.Equal(203, after[수고, OfficerFields.JoinedYear]);
        Assert.Equal(OfficerFields.RecruitedService, after[수고, OfficerFields.Service]);
        // (168, 470, 268) with (10, 0, 0) days: the officers after him move up, their days with them.
        Assert.Equal([168, 268, 0], Enumerable.Range(0, 3).Select(slot => after[원소, ForceFields.PolicyOfficer(모략, slot)]));
        Assert.Equal([10, 0, 0], Enumerable.Range(0, 3).Select(slot => after[원소, ForceFields.PolicyDays(모략, slot)]));
        Assert.Contains("모략 정책 칸에서 빠짐", plan.Summary);
    }

    [Fact]
    public void A군사SentAwayLeavesHisForceWithout()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Game(), 713, Transfer.Defect, 안정);

        Assert.Equal(0, after[data.ForceRecords[원소군], ForceFields.Strategist]);
        Assert.Contains("군사 없음", plan.Summary);
    }

    [Fact]
    public void ADismissedOfficerStaysInHisBaseAndWillNotServeHisRulerForAYear()
    {
        (GameSnapshot data, _, Written after) = Plan(Game(), 470, Transfer.Dismiss);
        OfficerRecord 수고 = data.OfficerById[470];

        Assert.Equal(0, after[수고, OfficerFields.CorpsField]);
        Assert.Equal(5, after[수고, OfficerFields.StatusField]);
        Assert.Equal(업, after[수고, OfficerFields.PlaceField]);
        Assert.Equal(0, after[수고, OfficerFields.Loyalty]);
        Assert.Equal(OfficerFields.NoOffice, after[수고, OfficerFields.Office]);
        Assert.Equal(35, after[수고, OfficerFields.FormerLord]);
        Assert.Equal(OfficerFields.Dismissed, after[수고, OfficerFields.FormerLordMonths]);
        Assert.Equal(0, after[수고, OfficerFields.JoinedYear]);
    }

    [Fact]
    public void A재야OfficerRecruitedJoinsTheBaseChosen()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Game(), 211, Transfer.Recruit, 업);
        OfficerRecord 허공 = data.OfficerById[211];

        Assert.Equal(원소군단, after[허공, OfficerFields.CorpsField]);
        Assert.Equal(업, after[허공, OfficerFields.BaseField]);
        Assert.Equal(4, after[허공, OfficerFields.StatusField]);
        Assert.Equal(Transfers.LoyaltyOnJoining, after[허공, OfficerFields.Loyalty]);
        Assert.Equal(203, after[허공, OfficerFields.JoinedYear]);
        Assert.Single(plan.Edits);   // he left no force: no slots or posts to clear
    }

    [Fact]
    public void AReleasedPrisonerWillNotServeTheRulerWhoHeldHimForTwoMonths()
    {
        (GameSnapshot data, _, Written after) = Plan(Game(), 758, Transfer.Release);
        OfficerRecord 마연 = data.OfficerById[758];

        Assert.Equal(5, after[마연, OfficerFields.StatusField]);
        Assert.Equal(35, after[마연, OfficerFields.FormerLord]);
        Assert.Equal(OfficerFields.Released, after[마연, OfficerFields.FormerLordMonths]);
    }

    [Theory]
    [InlineData(35, (int)Transfer.Defect, 안정, "군주")]
    [InlineData(35, (int)Transfer.Dismiss, 0, "군주")]
    [InlineData(470, (int)Transfer.Move, 안정, "같은 세력")]
    [InlineData(470, (int)Transfer.Defect, 양평, "다른 세력")]
    [InlineData(470, (int)Transfer.Recruit, 안정, "재야나 포로")]
    [InlineData(211, (int)Transfer.Dismiss, 0, "군단장·태수·일반")]
    [InlineData(470, (int)Transfer.Release, 0, "포로만")]
    [InlineData(470, (int)Transfer.Move, 0, "거점을 고르세요")]
    public void RefusesWhatTheGameWouldNotDo(int officer, int kind, int target, string reason)
    {
        (_, EditPlan plan, _) = Plan(Game(), officer, (Transfer)kind, target);

        Assert.True(plan.Refused);
        Assert.Empty(plan.Edits);
        Assert.Contains(reason, plan.Refusal);
    }

    [Fact]
    public void ARulerMovesWithinHisForceWithoutLosingHisTitleOrMainCorps()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Game(), 35, Transfer.Move, 양평);
        OfficerRecord ruler = data.OfficerById[35];
        Assert.False(plan.Refused);
        Assert.Equal(1, after[ruler, OfficerFields.StatusField]);
        Assert.Equal(원소군단, after[ruler, OfficerFields.CorpsField]);
        Assert.Equal(양평, after[ruler, OfficerFields.BaseField]);
        Assert.Equal(양평, after[ruler, OfficerFields.PlaceField]);
        Assert.Equal(ruler[OfficerFields.Office], after[ruler, OfficerFields.Office]);
        Assert.Single(plan.Edits);
        Assert.True(Transfers.RequiresNative(data, ruler, Transfer.Move));
        Assert.Contains(L.Transfers.RulerMoves, plan.Summary);
    }

    [Fact]
    public void ARulerKeepsHisMainCorpsWhenMovingToADelegatedCity()
    {
        var game = Game().Force(원소군, 35, 원소군단, 12).City(양평, 12);
        (GameSnapshot data, EditPlan plan, Written after) = Plan(game, 35, Transfer.Move, 양평);
        Assert.False(plan.Refused);
        Assert.Equal(원소군단, after[data.OfficerById[35], OfficerFields.CorpsField]);
        Assert.Equal(1, after[data.OfficerById[35], OfficerFields.StatusField]);
    }

    [Fact]
    public void ARulerCannotMoveOutsideHisForceOrChangeStatusThroughTheRegularMode()
    {
        (GameSnapshot data, EditPlan plan, _) = Plan(Game(), 35, Transfer.Move, 안정);
        Assert.True(plan.Refused);
        Assert.Contains(L.Transfers.SameForceOnly, plan.Refusal);
        Assert.True(OfficerStatusChanges.Plan(data, new(35, OfficerStatusMode.Regular, 원소군, 양평)).Plan.Refused);
    }

    [Fact]
    public void ARulerCannotUseAGateAsHisDestination()
    {
        (_, EditPlan plan, _) = Plan(Game().Gate(52, 원소군단), 35, Transfer.Move, 52);
        Assert.True(plan.Refused);
        Assert.Equal(L.Transfers.RulerCityOnly, plan.Refusal);
    }

    [Theory]
    [InlineData((int)Transfer.Dismiss, 0)]
    public void LeavesAnOfficerLeadingAUnitWhereHeIs(int kind, int target)
    {
        (_, EditPlan plan, _) = Plan(Leading수고(), 470, (Transfer)kind, target);

        Assert.Contains("출진", plan.Refusal);
    }

    // As 주창 on the running game (2026-10-03): corps and base go to the new force, and the unit with him; his place
    // stays the unit's, as the game keeps it while he is out.
    [Fact]
    public void ACommanderOutWithHisUnitIsSentToAnotherForceAndStaysWithIt()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Leading수고(), 470, Transfer.Defect, 안정);
        OfficerRecord 수고 = data.OfficerById[470];

        Assert.False(plan.Refused);
        Assert.Equal(마등군단, after[수고, OfficerFields.CorpsField]);
        Assert.Equal(안정, after[수고, OfficerFields.BaseField]);
        Assert.Equal(UnitPlace, after[수고, OfficerFields.PlaceField]);
        Assert.Equal(Transfers.LoyaltyOnJoining, after[수고, OfficerFields.Loyalty]);
        Assert.True(Transfers.RequiresNative(data, 수고, Transfer.Defect));
        Assert.Contains("새 소속 거점으로 귀환", plan.Summary);
    }

    [Fact]
    public void ACommanderMovingWithinHisForceKeepsHisUnitPlaceUntilNativeReturn()
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Leading수고(), 470, Transfer.Move, 양평);
        OfficerRecord officer = data.OfficerById[470];
        Assert.False(plan.Refused);
        Assert.Equal(양평, after[officer, OfficerFields.BaseField]);
        Assert.Equal(UnitPlace, after[officer, OfficerFields.PlaceField]);
        Assert.Equal(97, after[officer, OfficerFields.Loyalty]);
        Assert.True(Transfers.RequiresNative(data, officer, Transfer.Move));
    }

    [Theory]
    [InlineData((int)Transfer.Move, 양평)]
    [InlineData((int)Transfer.Defect, 안정)]
    public void ACorpsLeaderTransfersAsAGeneralWithNativeSuccession(int kind, int home)
    {
        (GameSnapshot data, EditPlan plan, Written after) = Plan(Game().Officer(470, "수고", 원소군단, 업, status: 2), 470, (Transfer)kind, home);
        OfficerRecord officer = data.OfficerById[470];
        Assert.False(plan.Refused);
        Assert.Equal(4, after[officer, OfficerFields.StatusField]);
        Assert.Equal(home, after[officer, OfficerFields.BaseField]);
        Assert.Contains("군단장직 해제", plan.Summary);
        Assert.True(Transfers.RequiresNative(data, officer, (Transfer)kind));
    }

    [Fact]
    public void ACorpsLeaderStillCannotBeDismissed()
    {
        (_, EditPlan plan, _) = Plan(Game().Officer(470, "수고", 원소군단, 업, status: 2), 470, Transfer.Dismiss);
        Assert.True(plan.Refused);
        Assert.Contains("군단장", plan.Refusal);
    }

    [Fact]
    public void ReturningACommanderToHisCurrentHomeStillHasAnIdentityToGuard()
    {
        (GameSnapshot data, EditPlan plan, _) = Plan(Leading수고(), 470, Transfer.Move, 업);
        Assert.False(plan.Refused);
        Assert.Single(plan.Edits);
        Assert.Empty(plan.Edits[0].Changes);
        Assert.True(Transfers.RequiresNative(data, data.OfficerById[470], Transfer.Move));
    }

    [Theory]
    [InlineData((int)Transfer.Defect, 안정)]
    [InlineData((int)Transfer.Dismiss, 0)]
    public void LeavesAnOfficerAwayFromHisBaseWhereHeIs(int kind, int target)
    {
        (_, EditPlan plan, _) = Plan(Game().Officer(470, "수고", 원소군단, 업, status: 4, place: 595), 470, (Transfer)kind, target);

        Assert.Contains("거점 밖", plan.Refusal);
    }

    // 수고 out with a unit: his place is the unit (61 + its slot), as the game keeps it.
    private const int UnitPlace = 79;
    private static TestGame Leading수고() => Game().Officer(470, "수고", 원소군단, 업, status: 4, loyalty: 97, office: 69, place: UnitPlace).Leading(470);

    [Fact]
    public void WritesNothingWithoutTheGamesDate()
    {
        (_, EditPlan plan, _) = Plan(Game().WithoutDate(), 211, Transfer.Recruit, 업);

        Assert.Contains("날짜", plan.Refusal);
    }

    [Fact]
    public void OffersEachForceWithARulerAndItsBases()
    {
        List<Transfers.ForceBases> targets = Transfers.Targets(Game().Build());

        Assert.Equal(["마등군", "원소군"], targets.Select(t => t.Name));
        Assert.Equal([양평, 업], targets.Single(t => t.Force == 원소군).Bases.Select(b => b.Base));
    }
}
