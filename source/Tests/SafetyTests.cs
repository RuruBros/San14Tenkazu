namespace San14Tenkazu.Tests;

// The net under the rules: a write is checked against the facts tying records together before it is written
// (GameChecks.Broken), and an unverified build takes writes only as allowed (GameChecks.WriteBlock).
public class SafetyTests
{
    private const int 원소군 = 4, 마등군 = 9, 원소군단 = 4, 마등군단 = 9, 업 = 6, 양평 = 1, 안정 = 22, 모략 = 2;

    private static GameSnapshot Game() => new TestGame()
        .Force(원소군, ruler: 35, 원소군단).Force(마등군, ruler: 521, 마등군단)
        .City(업, 원소군단, governor: 35).City(양평, 원소군단, governor: 31).City(안정, 마등군단, governor: 521)
        .Officer(35, "원소", 원소군단, 업, status: 1).Officer(521, "마등", 마등군단, 안정, status: 1)
        .Officer(31, "염유", 원소군단, 양평, status: 3)
        .Officer(470, "수고", 원소군단, 업, status: 4, office: 69).Officer(168, "곽도", 원소군단, 업, status: 4).Officer(268, "봉기", 원소군단, 업, status: 4)
        .Officer(713, "전풍", 원소군단, 업, status: 4).Officer(300, "심배", 원소군단, 양평, status: 4)
        .Officer(211, "허공", 0, 안정, status: 5).Officer(212, "왕씨", 0, 안정, status: 5)
        .With(211, OfficerFields.Spouse, 212).With(212, OfficerFields.Spouse, 211)
        .Policy(원소군, 모략, (168, 10), (470, 0), (268, 0))
        .Strategist(원소군, 713)
        .Build();

    private static List<string> Broken(GameSnapshot data, EditPlan plan) => GameChecks.Broken(data, plan.Edits, GameCatalog.Empty);

    [Fact]
    public void TheRulesPlansLeaveEveryTieAsTheGameKeepsIt()
    {
        GameSnapshot data = Game();
        OfficerRecord Officer(int id) => data.OfficerById[id];
        EditPlan[] plans =
        [
            Transfers.Plan(data, Officer(31), Transfer.Move, 업),
            Transfers.Plan(data, Officer(470), Transfer.Defect, 안정),
            Transfers.Plan(data, Officer(713), Transfer.Defect, 안정),
            Transfers.Plan(data, Officer(31), Transfer.Dismiss, 0),
            Transfers.Plan(data, Officer(211), Transfer.Recruit, 업),
            CityEdits.Plan(data, 양평, new CityEdits.Request([], [], Governor: 300)),
            OfficerEdits.Plan(data, 168, new OfficerEdits.Request([new FieldChange(OfficerFields.Office, 69)], null, null, [])),
            OfficerEdits.Plan(data, 470, new OfficerEdits.Request([], Spouse: 212, null, [])),
        ];

        Assert.All(plans, plan => Assert.Empty(Broken(data, plan)));
    }

    [Fact]
    public void AWriteThatForgetsTheOtherSideIsCaught()
    {
        GameSnapshot data = Game();
        OfficerRecord 수고 = data.OfficerById[470];
        // 수고 married to 왕씨, written on his side only: 왕씨 still points at 허공, who points at her.
        var plan = new EditPlan([new RecordEdit(RecordKind.Officer, 수고.Address, 수고.Data, 수고.Name, [new FieldChange(OfficerFields.Spouse, 212)])], "");

        Assert.Contains(Broken(data, plan), fact => fact.StartsWith("배우자가 서로 가리킴"));
    }

    [Fact]
    public void AMoveThatForgetsTheCityIsCaught()
    {
        GameSnapshot data = Game();
        OfficerRecord 염유 = data.OfficerById[31];
        // 염유 leaves 양평 for 업 but 양평 still names him its 태수.
        var plan = new EditPlan([new RecordEdit(RecordKind.Officer, 염유.Address, 염유.Data, 염유.Name,
            [new FieldChange(OfficerFields.BaseField, 업), new FieldChange(OfficerFields.PlaceField, 업)])], "");

        Assert.Contains(Broken(data, plan), fact => fact.StartsWith("태수가 그 도시 장수"));
    }

    [Fact]
    public void ADefectionThatForgetsThePolicySlotIsCaught()
    {
        GameSnapshot data = Game();
        OfficerRecord 수고 = data.OfficerById[470];
        var plan = new EditPlan([new RecordEdit(RecordKind.Officer, 수고.Address, 수고.Data, 수고.Name,
            [new FieldChange(OfficerFields.CorpsField, 마등군단), new FieldChange(OfficerFields.BaseField, 안정), new FieldChange(OfficerFields.PlaceField, 안정)])], "");

        Assert.Contains(Broken(data, plan), fact => fact.StartsWith("정책 칸 장수가 그 세력 장수"));
    }

    private static readonly List<GameChecks.Check> Checks =
        [new(GameChecks.City, "규모 1~4", 46, 46, 1), new(GameChecks.Ties, "배우자가 서로 가리킴", 3, 26, 0.95, Tie: true)];

    [Fact]
    public void TheVerifiedBuildIsAlwaysWritten() =>
        Assert.Null(GameChecks.WriteBlock(verified: true, allowed: false, Checks, [GameChecks.Ties]));

    [Fact]
    public void AnUnverifiedBuildIsReadOnlyUntilAllowed() =>
        Assert.Contains("읽기 전용", GameChecks.WriteBlock(verified: false, allowed: false, Checks, [GameChecks.City]));

    [Fact]
    public void AnAllowedBuildTakesOnlyTheGroupsThatPassed()
    {
        Assert.Null(GameChecks.WriteBlock(verified: false, allowed: true, Checks, [GameChecks.City]));
        Assert.Contains(GameChecks.Ties, GameChecks.WriteBlock(verified: false, allowed: true, Checks, [GameChecks.City, GameChecks.Ties]));
    }

    [Fact]
    public void WhileTheGameRunsLinkedValuesWaitAndPlainOnesGoIn()
    {
        FieldChange[] linked = [new(OfficerFields.StatusField, 4)], plain = [new(CityFields.Gold, 50_000)];

        Assert.Contains("[정지]", GameChecks.PaceBlock(GamePace.Running, linked));
        Assert.Null(GameChecks.PaceBlock(GamePace.Running, plain));
        Assert.Null(GameChecks.PaceBlock(GamePace.Paused, linked));
        Assert.Null(GameChecks.PaceBlock(GamePace.Unknown, linked));   // not known: the stale check still guards the write
    }
}
