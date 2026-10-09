using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AllianceEditTests
{
    private static GameSnapshot Game(int player = 45)
    {
        GameSnapshot made = new TestGame().Force(3, 2, 3).Force(45, 1, 45)
            .Officer(1, "설지", 45, 1, 1).Officer(2, "손책", 3, 2, 1).Build();
        return new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
            CastleByCity = made.CastleByCity, TileOwners = made.TileOwners, TileCounties = made.TileCounties,
            Forces = made.Forces, ForceRecords = made.ForceRecords, CorpsForce = made.CorpsForce,
            OfficerById = made.OfficerById, Units = made.Units, PlayerForce = player };
    }

    [Fact]
    public void PeriodAndPlayerFlagWritesMatchRecordedNativeSetter()
    {
        JsonElement vectors = Formulas.Vectors.Read("force_agreement.json.gz");
        int checkedCases = 0;
        foreach (JsonElement c in vectors.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            if (f.GetProperty("kind").GetString() != "agreement" || f.GetProperty("refresh").GetBoolean()) continue;
            int first = f.GetProperty("target").GetInt32(), second = f.GetProperty("other").GetInt32();
            if (first == second || first == 0) continue;
            int months = f.GetProperty("value").GetInt32();
            if (months is < 0 or > ForceFields.MaxAgreement) continue;
            GameSnapshot data = Game();
            foreach (JsonElement poke in f.GetProperty("pokes").EnumerateArray())
                if (poke[0].GetString() == "0xdf30")
                    Convert.FromHexString(poke[3].GetString()!).CopyTo(data.ForceRecords[poke[1].GetInt32()].Data, poke[2].GetInt32());
            EditPlan plan = ForceEdits.Plan(data, first, [], [], [(second, months)]);
            Assert.False(plan.Refused, plan.Refusal);
            GameSnapshot after = data.With(plan.Edits);
            foreach (JsonElement r in c.GetProperty("after").GetProperty("records").EnumerateArray())
            {
                int index = r[1].GetInt32(), partner = index == first ? second : first;
                byte[] expected = Convert.FromHexString(r[2].GetString()!);
                Assert.Equal(expected[ForceFields.AgreementOffset + partner - 1],
                    after.ForceRecords[index].Data[ForceFields.AgreementOffset + partner - 1]);
            }
            checkedCases++;
        }
        Assert.Equal(12, checkedCases);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(4)] [InlineData(12)] [InlineData(48)]
    public void BilateralPeriodsAndSeparateFlagsCanBeUndone(int months)
    {
        GameSnapshot data = Game();
        data.ForceRecords[45].Data[ForceFields.Agreement(3).Offset] = 0x82;
        data.ForceRecords[3].Data[ForceFields.Agreement(45).Offset] = 0x82;
        EditPlan plan = ForceEdits.Plan(data, 45, [], [(3, 80)], [(3, months)]);
        var memory = new FakeMemory(data);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        Assert.Equal(months, ForceFields.Agreement(3).Read(memory[data.ForceRecords[45].Address]!) & 0x7F);
        Assert.Equal(months, ForceFields.Agreement(45).Read(memory[data.ForceRecords[3].Address]!) & 0x7F);
        Assert.Equal(80, ForceFields.Friendship(45).Read(memory[data.ForceRecords[3].Address]!));
        Assert.Equal(months is >= 1 and <= 3 ? 0x80 : 0, ForceFields.Agreement(3).Read(memory[data.ForceRecords[45].Address]!) & 0x80);
        Assert.Equal(0x80, ForceFields.Agreement(45).Read(memory[data.ForceRecords[3].Address]!) & 0x80);
        GameMemory.Apply(memory, TestGame.Classes, Undo.Of(plan.Edits));
        Assert.Equal(data.ForceRecords[45].Data, memory[data.ForceRecords[45].Address]);
        Assert.Equal(data.ForceRecords[3].Data, memory[data.ForceRecords[3].Address]);
    }

    [Theory]
    [InlineData(46)] [InlineData(47)] [InlineData(48)] [InlineData(49)] [InlineData(50)] [InlineData(51)]
    public void TribalAndExternalForcesCannotReceiveOrCreateAlliances(int id)
    {
        GameSnapshot data = Game();
        ForceRecord tribal = new TestGame().Force(id, 4101).Build().ForceRecords[id];
        data.ForceRecords[id] = tribal;
        Assert.False(ForceFields.CanAlly(tribal));
        Assert.True(ForceEdits.Plan(data, 45, [], [], [(id, 12)]).Refused);
        Assert.True(ForceEdits.Plan(data, id, [], [], [(45, 12)]).Refused);
        Assert.False(ForceEdits.Plan(data, 45, [], [(id, 80)]).Refused);
    }

    [Fact]
    public void InvalidPairPeriodOrUnknownPlayerRefusesTheWholePlan()
    {
        GameSnapshot data = Game();
        foreach ((int other, int months) in new[] { (45, 12), (2, 12), (3, -1), (3, 49) })
        {
            EditPlan plan = ForceEdits.Plan(data, 45, [new(ForceFields.Color, 1)], [(3, 80)], [(other, months)]);
            Assert.True(plan.Refused);
            Assert.Empty(plan.Edits);
        }
        Assert.True(ForceEdits.Plan(Game(-1), 45, [], [], [(3, 12)]).Refused);
        data.ForceRecords[3].Data.AsSpan(ForceFields.RulerOffset, 2).Clear();
        Assert.True(ForceEdits.Plan(data, 45, [], [], [(3, 12)]).Refused);
    }

    [Fact]
    public void AGameChangeToEitherPeriodRollsBackAllWritesAndBlocksPartialUndo()
    {
        GameSnapshot data = Game();
        EditPlan plan = ForceEdits.Plan(data, 45, [new(ForceFields.Color, 1)], [(3, 80)], [(3, 12)]);
        var memory = new FakeMemory(data);
        memory.Set(data.ForceRecords[3].Address, ForceFields.Agreement(45), 4);
        Assert.Throws<StaleEditException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        Assert.Equal(data.ForceRecords[45].Data, memory[data.ForceRecords[45].Address]);
        memory.Set(data.ForceRecords[3].Address, ForceFields.Agreement(45), 0);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        memory.Set(data.ForceRecords[3].Address, ForceFields.Agreement(45), 11);
        Assert.NotEmpty(Undo.Compare(Undo.Of(plan.Edits), e => memory[e.Address]).LinkedMoved);
        Assert.NotNull(GameChecks.PaceBlock(GamePace.Running, plan.Edits.SelectMany(e => e.Changes)));
        Assert.Null(GameChecks.PaceBlock(GamePace.Paused, plan.Edits.SelectMany(e => e.Changes)));
    }

    [Fact]
    public void NativeSynchronizationDeduplicatesPairsAndRefusesMissingDirection()
    {
        GameSnapshot data = Game();
        EditPlan plan = ForceEdits.Plan(data, 45, [], [(3, 80)], [(3, 12)]);
        ulong[] addresses = new ulong[52];
        foreach (ForceRecord f in data.ForceRecords.Values) addresses[f.Id] = f.Address;
        GameMemory.AgreementPair pair = Assert.Single(GameMemory.AgreementPairs(addresses, plan.Edits));
        Assert.Equal(0, pair.Before);
        Assert.Equal(12, pair.Months);
        Assert.Throws<InvalidOperationException>(() => GameMemory.AgreementPairs(addresses, [plan.Edits[0]]));
    }
}
