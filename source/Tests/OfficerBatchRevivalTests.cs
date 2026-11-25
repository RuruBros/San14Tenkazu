namespace San14Tenkazu.Tests;

public sealed class OfficerBatchRevivalTests
{
    // Force 1 holds city 1; officers 12~14 are dead, 15 has not appeared, 16 is a generated slot, 17 still leads a unit.
    private static GameSnapshot Data() => new TestGame().Force(1, 1, 1).City(1, 1)
        .Officer(1, "조조", 1, 1, 1)
        .Officer(12, "하진", 0, 0, 9).Officer(13, "고순", 0, 0, 9).Officer(14, "장각", 0, 0, 9)
        .Officer(15, "윤대목", 0, 0, 7).Officer(16, "임시", 0, 0, 9).Officer(17, "여포", 0, 0, 9).Leading(17)
        .Build().WithGenerated(16);

    private static OfficerBatchRevival.Options Options(BatchRevivalStatus status = BatchRevivalStatus.Free, int city = 0,
        int min = OfficerBatchRevival.DefaultMinYears, int max = OfficerBatchRevival.DefaultMaxYears) => new(status, city, min, max);

    [Fact]
    public void FindsDeadOfficersTheSingleRevivalAcceptsInNameOrder()
    {
        var data = Data();
        var dead = OfficerBatchRevival.Find(data, includeHidden: false);
        Assert.Equal(new[] { 13, 14, 12 }, dead.Officers.Select(o => o.Id));
        Assert.Equal(2, dead.Skipped);
        var all = OfficerBatchRevival.Find(data, includeHidden: true);
        Assert.Equal(new[] { 13, 15, 14, 12 }, all.Officers.Select(o => o.Id));
        Assert.All(all.Officers, o => Assert.False(OfficerRevival.Plan(data, new(o.Id, RevivalMode.Free, 1, 230)).Refused));
    }

    // 0 재야, 1 일반, 2 무작위 (BatchRevivalStatus is internal).
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RandomCitiesFollowTheStatusAndDeathYearsStayInRange(int choice)
    {
        var status = (BatchRevivalStatus)choice;
        var data = Data();
        int[] officers = OfficerBatchRevival.Find(data, includeHidden: true).Officers.Select(o => o.Id).ToArray();
        var draws = Enumerable.Range(0, 40).SelectMany(seed =>
            OfficerBatchRevival.Draw(data, officers, Options(status), new Random(seed)).Values).ToList();
        int year = data.Date!.Value.Year;
        Assert.All(draws, r => Assert.InRange(r.DeathYear, year + 20, year + 40));
        Assert.All(draws, r => Assert.True(data.IsCity(r.Base)));
        Assert.All(draws, r => Assert.False(OfficerRevival.Plan(data, r).Refused));
        Assert.All(draws.Where(r => r.Mode == RevivalMode.Serving), r => Assert.Equal(1, r.Base));
        Assert.Equal(status != BatchRevivalStatus.Serving, draws.Any(r => r.Mode == RevivalMode.Free));
        Assert.Equal(status != BatchRevivalStatus.Free, draws.Any(r => r.Mode == RevivalMode.Serving));
        if (status == BatchRevivalStatus.Free) Assert.True(draws.Select(r => r.Base).Distinct().Count() > 1);
        Assert.Contains(draws, r => r.DeathYear == year + 20);
        Assert.Contains(draws, r => r.DeathYear == year + 40);
    }

    [Fact]
    public void AFixedCitySendsEveryOfficerThereAndMustTakeEveryDrawnStatus()
    {
        var data = Data();
        Assert.Equal(OfficerBatchRevival.Cities(data, RevivalMode.Free).Count, OfficerBatchRevival.CityChoices(data, BatchRevivalStatus.Free).Count);
        Assert.Equal(new[] { 1 }, OfficerBatchRevival.CityChoices(data, BatchRevivalStatus.Serving).Select(c => c.Base));
        Assert.Equal(new[] { 1 }, OfficerBatchRevival.CityChoices(data, BatchRevivalStatus.Random).Select(c => c.Base));
        var draws = OfficerBatchRevival.Draw(data, [12, 13, 14], Options(BatchRevivalStatus.Random, city: 1), new Random(3));
        Assert.All(draws.Values, r => Assert.Equal(1, r.Base));
        Assert.Null(OfficerBatchRevival.Check(data, Options(BatchRevivalStatus.Free, city: 2)));
        Assert.Equal(L.Transfers.DestinationInvalid, OfficerBatchRevival.Check(data, Options(BatchRevivalStatus.Serving, city: 2)));
        Assert.Equal(L.Transfers.DestinationInvalid, OfficerBatchRevival.Check(data, Options(BatchRevivalStatus.Random, city: 2)));
    }

    [Theory]
    [InlineData(0, 40)] [InlineData(30, 20)] [InlineData(20, 797)]
    public void RefusesDeathYearsOutsideTheField(int min, int max)
    {
        var data = Data();
        Assert.Equal(796, OfficerBatchRevival.MaxYears(data));
        Assert.Equal(L.BatchRevival.InvalidYears(796), OfficerBatchRevival.Check(data, Options(min: min, max: max)));
        Assert.Throws<InvalidOperationException>(() => OfficerBatchRevival.Draw(data, [12], Options(min: min, max: max), new Random(1)));
        Assert.Null(OfficerBatchRevival.Check(data, Options(min: 1, max: 796)));
    }

    [Fact]
    public void ServingNeedsALivingRulersCity()
    {
        var data = new TestGame().Officer(12, "하진", 0, 0, 9).Build();
        Assert.Null(OfficerBatchRevival.Check(data, Options(BatchRevivalStatus.Free)));
        Assert.Equal(L.BatchRevival.NoCities, OfficerBatchRevival.Check(data, Options(BatchRevivalStatus.Serving)));
        Assert.Equal(L.BatchRevival.NoCities, OfficerBatchRevival.Check(data, Options(BatchRevivalStatus.Random)));
        Assert.Equal(L.Transfers.NoDate, OfficerBatchRevival.Check(new TestGame().WithoutDate().Build(), Options()));
    }

    [Fact]
    public void PicksTheAskedNumberFromAllCandidatesInTheirOrder()
    {
        int[] candidates = [40, 10, 30, 20, 50];
        var seen = new HashSet<int>();
        for (int seed = 0; seed < 30; seed++)
        {
            var picked = OfficerBatchRevival.Pick(candidates, 2, new Random(seed));
            Assert.Equal(2, picked.Distinct().Count());
            Assert.Equal(candidates.Where(picked.Contains), picked);
            seen.UnionWith(picked);
        }
        Assert.Equal(candidates.Order(), seen.Order());
        Assert.Equal(candidates, OfficerBatchRevival.Pick(candidates, 5, new Random(1)));
        Assert.Equal(candidates, OfficerBatchRevival.Pick(candidates, 99, new Random(1)));
        Assert.Empty(OfficerBatchRevival.Pick(candidates, 0, new Random(1)));
    }
}

internal static class GeneratedOfficerExtensions
{
    // The game's own generated-slot flag (+0x15E bit 0x4000), as OfficerRevival reads it.
    internal static GameSnapshot WithGenerated(this GameSnapshot data, int officer)
    {
        data.OfficerById[officer].Data[0x15F] |= 0x40;
        return data;
    }
}
