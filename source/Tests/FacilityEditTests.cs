namespace San14Tenkazu.Tests;

public class FacilityEditTests
{
    public static IEnumerable<object[]> Kinds => Enumerable.Range(3, 15).Select(kind => new object[] { kind, kind % 2 == 0 });
    private static GameSnapshot Game(int kind = 3, bool built = true) => new TestGame().Facility(70, kind, 22000, 4, 120, built).Build();
    private static EditPlan Plan(GameSnapshot data, long value) => FacilityEdits.Plan(data, 70, data.Facilities.Single().Kind, 22000, 4, value);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryFacilityKindWritesOnlyItsDurabilityAndCanBeUndone(int kind, bool built)
    {
        GameSnapshot data = Game(kind, built);
        BuildingRecord building = data.Facilities.Single().Building!;
        var memory = new FakeMemory(data);
        EditPlan plan = Plan(data, 9999);

        Assert.False(plan.Refused);
        RecordEdit edit = Assert.Single(plan.Edits);
        Assert.Equal(RecordKind.Building, edit.Kind);
        Assert.Equal(building.Address, edit.Address);
        Assert.Equal(FacilityFields.Durability, Assert.Single(edit.Changes).Field);
        Assert.Empty(GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        byte[] expected = (byte[])building.Data.Clone();
        FacilityFields.Durability.Encode(9999).CopyTo(expected, 0x14);
        Assert.Equal(expected, memory[building.Address]);

        GameSnapshot after = data.With(plan.Edits);
        Assert.Equal(9999, after.Facilities.Single().Durability);
        Assert.Equal(built, after.Facilities.Single().Built);
        Assert.Equal(120, data.Facilities.Single().Durability);
        Assert.Equal(120, FacilityFields.Durability.Read(data.DataAt(RecordKind.Building, building.Address)!));
        Assert.Equal(expected, after.DataAt(RecordKind.Building, building.Address));

        Undo.Check undo = Undo.Compare(Undo.Of(plan.Edits), e => after.DataAt(e.Kind, e.Address));
        Assert.Empty(undo.Missing);
        Assert.Empty(undo.Moved);
        GameMemory.Apply(memory, TestGame.Classes, undo.Edits);
        Assert.Equal(building.Data, memory[building.Address]);
    }

    [Fact]
    public void AnUnchangedDurabilityDoesNotCreateAWrite() => Assert.Empty(Plan(Game(), 120).Edits);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void ZeroAndValuesOutsideTheGameSetterRangeAreRejected(long value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(Game(), value));

    [Theory]
    [InlineData(71, 3, 22000, 4)]
    [InlineData(70, 5, 22000, 4)]
    [InlineData(70, 3, 22001, 4)]
    [InlineData(70, 3, 22000, 9)]
    public void ACardForAnotherSlotKindTileOrForceDoesNotWrite(int index, int kind, int tile, int force)
    {
        EditPlan plan = FacilityEdits.Plan(Game(), index, kind, tile, force, 300);
        Assert.True(plan.Refused);
        Assert.Empty(plan.Edits);
    }

    [Theory]
    [InlineData(0x10, 22001)]
    [InlineData(0x12, 5)]
    [InlineData(0x13, 9)]
    [InlineData(0x14, 0)]
    public void AReplacedOrDestroyedFacilityBetweenReadAndWriteIsRejected(int offset, int value)
    {
        GameSnapshot data = Game();
        BuildingRecord building = data.Facilities.Single().Building!;
        var memory = new FakeMemory(data);
        memory.Set(building.Address, new FieldDef("Changed", RecordKind.Building, offset, offset is 0x10 or 0x14 ? FieldKind.UInt16 : FieldKind.Byte, 0, ushort.MaxValue), value);
        byte[] before = memory[building.Address]!;

        Assert.Throws<InvalidOperationException>(() => GameMemory.Apply(memory, TestGame.Classes, Plan(data, 400).Edits));
        Assert.Equal(before, memory[building.Address]);
        Undo.Check undo = Undo.Compare(Undo.Of(Plan(data, 400).Edits), e => memory[e.Address]);
        Assert.Single(undo.Missing);
        Assert.Empty(undo.Edits);
    }

    [Fact]
    public void DamageBetweenReadAndWriteIsReportedAndWrittenOver()
    {
        GameSnapshot data = Game();
        BuildingRecord building = data.Facilities.Single().Building!;
        var memory = new FakeMemory(data);
        memory.Set(building.Address, FacilityFields.Durability, 60);

        Assert.Single(GameMemory.Apply(memory, TestGame.Classes, Plan(data, 400).Edits));
        Assert.Equal(400, FacilityFields.Durability.Read(memory[building.Address]!));
    }
}
