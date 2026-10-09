using System.Diagnostics;

namespace San14Tenkazu.Tests;

// A test that reads the running game (never writes): skipped when the game is not running.
public sealed class LiveGameFactAttribute : FactAttribute
{
    public LiveGameFactAttribute()
    {
        Process[] games = Process.GetProcessesByName("SAN14CE_KO");
        foreach (Process game in games) game.Dispose();
        if (games.Length == 0) Skip = "게임이 실행 중이 아닙니다.";
    }
}

// A live test that searches the whole of the game's memory (tens of seconds): only with SAN14_SCAN=1.
public sealed class LiveScanFactAttribute : FactAttribute
{
    public LiveScanFactAttribute()
    {
        if (new LiveGameFactAttribute().Skip is { } skip) Skip = skip;
        else if (Environment.GetEnvironmentVariable("SAN14_SCAN") != "1") Skip = "메모리 전체 검색 시험은 SAN14_SCAN=1일 때만 돌립니다.";
    }
}

// The core against the running game, read only: connecting, the tables, a snapshot, the facts every save keeps.
public class LiveGameTests
{
    [LiveGameFact]
    public void ConnectsReadsAndEveryFactHolds()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameLayout layout = GameMemory.Resolve(identity, null, CancellationToken.None);
        GameSnapshot data = GameMemory.ReadSnapshot(identity, layout);
        List<GameChecks.Check> checks = GameChecks.Run(data, GameMemory.ReadCatalog(identity, layout));

        Assert.True(identity.Verified, $"검증하지 않은 실행 파일입니다: {identity.ExeHash}");
        Assert.True(data.Officers.Length > 1000);
        Assert.All(checks, check => Assert.True(check.Passed, $"[{check.Group}] {check}"));
    }

    [LiveGameFact]
    public void FindsByClassTheTablesTheVerifiedBuildHasAtKnownPlaces()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameLayout layout = GameMemory.Resolve(identity, null, CancellationToken.None);

        Assert.Equal(layout.Tables, GameMemory.DiscoverTables(identity, layout.Tables.City));
    }

    // The play record: at its place on the verified build, found again by its header on a build taken as unverified
    // (which reads no DLC), and holding the bits of every tutorial it holds cleared, as the game sets them together.
    [LiveGameFact]
    public void FindsThePlayRecordWhereTheGameKeepsIt()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameLayout layout = GameMemory.Resolve(identity, null, CancellationToken.None);
        GameSnapshot data = GameMemory.ReadSnapshot(identity, layout);
        GameSnapshot unverified = GameMemory.ReadSnapshot(identity with { Verified = false }, layout);

        PlayRecord play = Assert.IsType<PlayRecord>(data.Play);
        Assert.Equal(play.Address, unverified.Play?.Address);
        Assert.NotNull(data.DlcOfficers);
        Assert.Null(unverified.DlcOfficers);
        int[] fromCleared = Enumerable.Range(AncientOfficers.First, AncientOfficers.Last - AncientOfficers.First + 1)
            .Where(id => AncientOfficers.SourceOf(id) is AncientOfficers.Tutorial t && (play.Tutorials >> (t.Number - 1) & 1) != 0).ToArray();
        Assert.All(fromCleared, id => Assert.Equal(AncientOfficers.Access.Unlocked, AncientOfficers.AccessOf(id, play.Unlocks, null)));
    }

    // An unverified build connects through the static pointers kept from its first connection: no search.
    [LiveGameFact]
    public void AnUnverifiedBuildGoesStraightToTheTablesThroughThePointersKept()
    {
        GameIdentity verified = GameMemory.FindGame();
        GameLayout expected = GameMemory.Resolve(verified, null, CancellationToken.None);
        var clock = Stopwatch.StartNew();

        GameLayout layout = GameMemory.Resolve(verified with { Verified = false }, null, CancellationToken.None, [new BasePointer(0x277DF18, 0xDD38)]);

        Assert.Equal(expected.Tables, layout.Tables);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed.TotalSeconds:N1}초 걸렸습니다.");
    }

    // Its first connection searches memory once and finds, among the exe's static pointers to the city table, the one
    // the verified build uses.
    [LiveScanFact]
    public void AnUnverifiedBuildsFirstConnectionFindsTheTablesAndTheGamesOwnPointer()
    {
        GameIdentity verified = GameMemory.FindGame();
        GameLayout expected = GameMemory.Resolve(verified, null, CancellationToken.None);

        GameLayout layout = GameMemory.Resolve(verified with { Verified = false }, null, CancellationToken.None);

        Assert.Equal(expected.Tables, layout.Tables);
        Assert.Contains(new BasePointer(0x277DF18, 0xDD38), layout.Pointers);
    }

    // Whether the game runs, read from its clock, agrees with two reads of the game a second apart (what the date chip
    // shows). The game's clock stands a moment now and then, and the player may pause or resume meanwhile: tried
    // three times.
    [LiveGameFact]
    public void ThePaceReadFromTheClockAgreesWithTwoReadsOfTheGame()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameLayout layout = GameMemory.Resolve(identity, null, CancellationToken.None);
        var seen = new List<(GamePace Clock, GamePace Reads)>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GamePace pace = GameMemory.ReadPace(identity, layout);
            GameSnapshot first = GameMemory.ReadSnapshot(identity, layout);
            Thread.Sleep(1000);
            GameSnapshot second = GameMemory.ReadSnapshot(identity, layout, first);
            Assert.NotEqual(GamePace.Unknown, pace);
            Assert.Equal(GamePace.Unknown, first.Pace);
            if (pace == second.Pace) return;
            seen.Add((pace, second.Pace));
        }
        Assert.Fail("세 번 모두 달랐습니다: " + string.Join(", ", seen));
    }

    // The 개성 the game has, with their effects: 절약 cuts 군량 consumption (kind 20), and some effects only switch on.
    [LiveGameFact]
    public void ReadsThePersonalitiesWithTheirEffects()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameSnapshot data = GameMemory.ReadSnapshot(identity, GameMemory.Resolve(identity, null, CancellationToken.None));

        Assert.True(data.Personalities.Length >= 200, $"개성 {data.Personalities.Length}개");
        PersonalityRecord 절약 = data.Personalities.Single(p => p.Name == "절약");
        Assert.Equal(20, 절약.Kind(0));
        Assert.Contains("군량", PersonalityEdits.EffectNames(data)[20]);
        Assert.Contains(data.Personalities.Single(p => p.Name == "저돌").Kind(0), PersonalityEdits.Switches(data));
        // 효웅 and 무염 reach enemy units in range; 19 is named without whom it reaches.
        Assert.Equal("범위 안의 적 부대 (함선·병기 제외)", PersonalityEdits.Condition(data.Personalities.Single(p => p.Name == "효웅")));
        Assert.Equal("범위 안의 적 부대", PersonalityEdits.Condition(data.Personalities.Single(p => p.Name == "무염")));
        Assert.Equal("모든능력상승", PersonalityEdits.EffectNames(data)[19]);
    }

    // The rules read from the game's code take every 개성 the game has as it has it (but the kinds nothing was found
    // to read), and every kind it uses has a reach; a 개성 lit by an attack has condition 30.
    [LiveGameFact]
    public void EveryPersonalityTheGameHasFitsTheRulesReadFromItsCode()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameSnapshot data = GameMemory.ReadSnapshot(identity, GameMemory.Resolve(identity, null, CancellationToken.None));
        var unfit = new List<string>();
        foreach (PersonalityRecord p in data.Personalities.Where(p => p.Kind(0) != 0 || p.Kind(1) != 0))
        {
            if (p.Timing != PersonalityRules.Timing.AfterAttack && PersonalityRules.ConditionProblem(p.Timing, p.Condition) is { Length: > 0 } why)
                unfit.Add($"{p.Name}: {why}");
            if (p.Timing == PersonalityRules.Timing.AfterAttack && p.Condition != PersonalityRules.Attacked) unfit.Add($"{p.Name}: 조건 {p.Condition}");
            foreach (int kind in new[] { p.Kind(0), p.Kind(1) }.Where(k => PersonalityRules.ReachOf(k) != PersonalityRules.Reach.Unknown))
                if (PersonalityRules.KindProblem(p.Timing, p.Condition, kind) is { Length: > 0 } problem) unfit.Add($"{p.Name} #{kind}: {problem}");
            if (p.Timing == PersonalityRules.Timing.WhileMet && PersonalityRules.TargetOf(p.Condition) is PersonalityRules.Target.Own or PersonalityRules.Target.Others
                && !PersonalityRules.Filters.Contains(p.Filter))
                unfit.Add($"{p.Name}: 대상 부대 {p.Filter}");
        }
        Assert.Empty(unfit);
        var unknown = data.Personalities.SelectMany(p => new[] { p.Kind(0), p.Kind(1) }).Where(k => k != 0 && PersonalityRules.ReachOf(k) == PersonalityRules.Reach.Unknown)
            .ToHashSet();
        Assert.Subset(new HashSet<int> { 16, 23, 52, 88, 89, 90, 91, 92, 93, 102 }, unknown);
    }

    // The 진형 the game has, named as the editor names them, with their 기동 (추행 the fastest on land); every officer's
    // 진형 bits are land formations, and every army fights in one on land.
    [LiveGameFact]
    public void ReadsTheFormationsWithTheirMobility()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameLayout layout = GameMemory.Resolve(identity, null, CancellationToken.None);
        GameSnapshot data = GameMemory.ReadSnapshot(identity, layout);
        GameCatalog catalog = GameMemory.ReadCatalog(identity, layout);

        Assert.All(UnitFields.LandFormations, f => Assert.Equal(UnitFields.FormationName(f), catalog.Formation(f).Name));
        Assert.Equal(L.Terms.Mobility(26), catalog.Formation(7).Description);
        long allowed = UnitFields.LandFormations.Sum(f => 1L << f);
        Assert.All(data.Officers, o => Assert.Equal(0, o[OfficerFields.Formations] & ~allowed));
        Assert.All(data.Units.Where(u => u.IsArmy), u => Assert.Contains(u.Formation, UnitFields.LandFormations));
    }
}
