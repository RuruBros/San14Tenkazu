namespace San14Tenkazu.Tests;

public class MilitaryPointTests
{
    private static GameSnapshot Game() => new TestGame().Force(4, 35, 4, 7).Force(9, 521, 9)
        .MilitaryPoints(4, 4, 1, 35, 203, 4).MilitaryPoints(7, 4, 2, 199, 504, 4)
        .MilitaryPoints(9, 9, 1, 521, 2179).Build();
    private static EditPlan Plan(GameSnapshot data, int value) => ForceEdits.Plan(data, 4, [], [], [], [new(4, 1, 35, value)]);

    [Theory]
    [InlineData(0, 50000)] [InlineData(1, 200000)] [InlineData(2, 200000)] [InlineData(3, 200000)]
    [InlineData(4, 150000)] [InlineData(5, 150000)] [InlineData(6, 100000)] [InlineData(7, 100000)]
    [InlineData(8, 50000)] [InlineData(9, 50000)] [InlineData(10, 200000)] [InlineData(11, 200000)]
    [InlineData(12, 200000)] [InlineData(13, 50000)]
    public void CapMatchesOriginalNativeRankSwitch(int rank, int expected) => Assert.Equal(expected, Game().MilitaryCaps!.ForRank(rank));

    [Fact]
    public void WritesOnlySelectedBalanceAndUndoRestoresIt()
    {
        var data = Game(); var memory = new FakeMemory(data); var plan = Plan(data, 1000);
        Assert.False(plan.Refused);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        byte[] expected = (byte[])data.CorpsRecords[4].Data.Clone();
        CorpsFields.MilitaryPoints.Encode(1000).CopyTo(expected, 0x24);
        Assert.Equal(expected, memory[data.CorpsRecords[4].Address]);
        foreach (int id in new[] { 7, 9 }) Assert.Equal(data.CorpsRecords[id].Data, memory[data.CorpsRecords[id].Address]);
        Assert.Equal(1000, data.With(plan.Edits).CorpsRecords[4].MilitaryPoints);
        Assert.Equal(data.MilitaryCaps!.Data, memory[data.MilitaryCaps.Address]);
        var undo = Undo.Compare(Undo.Of(plan.Edits), e => memory[e.Address]);
        Assert.Empty(undo.Missing); Assert.Empty(undo.LinkedMoved); Assert.Empty(undo.Moved);
        GameMemory.Apply(memory, TestGame.Classes, undo.Edits);
        Assert.Equal(data.CorpsRecords[4].Data, memory[data.CorpsRecords[4].Address]);
    }

    [Theory]
    [InlineData(-1)] [InlineData(150001)]
    public void RejectsOutsideCurrentCap(int value) => Assert.True(Plan(Game(), value).Refused);

    [Fact]
    public void UsesNewRankWhenRankAndBalanceAreEditedTogether()
    {
        var data = Game(); var memory = new FakeMemory(data);
        var plan = ForceEdits.Plan(data, 4, [new(ForceFields.Rank, 1)], [], [], [new(4, 1, 35, 200000)]);
        Assert.False(plan.Refused); GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        Assert.Equal(200000, CorpsFields.MilitaryPoints.Read(memory[data.CorpsRecords[4].Address]!));
        GameMemory.Apply(memory, TestGame.Classes, Undo.Of(plan.Edits));
        Assert.Equal(4, ForceFields.Rank.Read(memory[data.ForceRecords[4].Address]!));
        Assert.Equal(203, CorpsFields.MilitaryPoints.Read(memory[data.CorpsRecords[4].Address]!));
    }

    [Fact]
    public void RefusesForeignCorpsChangedLeaderAndDuplicateRequests()
    {
        var data = Game();
        foreach (var requests in new MilitaryPointRequest[][] { [new(9, 1, 521, 1000)], [new(4, 1, 199, 1000)], [new(4, 1, 35, 1000), new(4, 1, 35, 1001)] })
            Assert.True(ForceEdits.Plan(data, 4, [], [], [], requests).Refused);
    }

    [Fact]
    public void CapChangeBetweenReadAndWriteRefusesAndRollsBackOtherFields()
    {
        var data = Game(); var memory = new FakeMemory(data);
        var plan = ForceEdits.Plan(data, 4, [new(ForceFields.Country, 2)], [], [], [new(4, 1, 35, 150000)]);
        var cap = new FieldDef("cap", RecordKind.Force, 20, FieldKind.Int32, 0, int.MaxValue);
        memory.Set(data.MilitaryCaps!.Address, cap, 100000);
        Assert.Throws<InvalidOperationException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        Assert.Equal(data.ForceRecords[4].Data, memory[data.ForceRecords[4].Address]);
        Assert.Equal(data.CorpsRecords[4].Data, memory[data.CorpsRecords[4].Address]);
    }

    [Theory]
    [InlineData(0x10, 9)] [InlineData(0x11, 2)] [InlineData(0x12, 199)]
    public void SlotReassignmentBetweenReadAndWriteIsRefused(int offset, int value)
    {
        var data = Game(); var memory = new FakeMemory(data); var plan = Plan(data, 1000);
        memory.Set(data.CorpsRecords[4].Address, new("identity", RecordKind.Corps, offset, FieldKind.Byte, 0, 255), value);
        Assert.Throws<InvalidOperationException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        Assert.Equal(203, CorpsFields.MilitaryPoints.Read(memory[data.CorpsRecords[4].Address]!));
    }

    [Fact]
    public void UndoChecksTheCurrentRankCap()
    {
        var data = Game(); var memory = new FakeMemory(data); var plan = Plan(data, 1000);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        memory.Set(data.MilitaryCaps!.Address, new("cap", RecordKind.Force, 20, FieldKind.Int32, 0, int.MaxValue), 100);
        Assert.Throws<InvalidOperationException>(() => GameMemory.Apply(memory, TestGame.Classes, Undo.Of(plan.Edits)));
        Assert.Equal(1000, CorpsFields.MilitaryPoints.Read(memory[data.CorpsRecords[4].Address]!));
    }
}
