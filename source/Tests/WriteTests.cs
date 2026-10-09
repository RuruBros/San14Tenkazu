namespace San14Tenkazu.Tests;

// The write itself (GameMemory.Apply) and its undo (Undo), on a made-up game's memory: what is written, what is
// refused when the game moved on between the read and the write, and what undoing finds afterwards.
public class WriteTests
{
    private const int 원소군 = 4, 마등군 = 9, 원소군단 = 4, 마등군단 = 9, 업 = 6, 양평 = 1, 안정 = 22, 모략 = 2;

    private static GameSnapshot Game() => new TestGame()
        .Force(원소군, ruler: 35, 원소군단).Force(마등군, ruler: 521, 마등군단)
        .City(업, 원소군단, governor: 35).City(양평, 원소군단).City(안정, 마등군단, governor: 521)
        .Officer(35, "원소", 원소군단, 업, status: 1).Officer(521, "마등", 마등군단, 안정, status: 1)
        .Officer(470, "수고", 원소군단, 업, status: 4).Officer(168, "곽도", 원소군단, 업, status: 4).Officer(268, "봉기", 원소군단, 업, status: 4)
        .Policy(원소군, 모략, (168, 10), (470, 0), (268, 0))
        .Leading(521)
        .Build();

    private static IReadOnlyList<string> Apply(FakeMemory memory, EditPlan plan) => GameMemory.Apply(memory, TestGame.Classes, plan.Edits);

    private static EditPlan Defect(GameSnapshot data) => Transfers.Plan(data, data.OfficerById[470], Transfer.Defect, 안정);

    [Fact]
    public void WritesThePlanAndReportsNothingWhenTheGameStoodStill()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);

        IReadOnlyList<string> overwritten = Apply(memory, Defect(data));

        Assert.Empty(overwritten);
        Assert.Equal(안정, OfficerFields.BaseField.Read(memory[data.OfficerById[470].Address]));
        Assert.Equal(268, ForceFields.PolicyOfficer(모략, 1).Read(memory[data.ForceRecords[원소군].Address]));
    }

    [Fact]
    public void AGatesValuesGoToItsRecordAndItsBuilding()
    {
        GameSnapshot data = new TestGame().Force(원소군, ruler: 35, 원소군단).Gate(52, 원소군단, durability: 6000).Build();
        var memory = new FakeMemory(data);
        GateRecord gate = data.Gates.Single();

        IReadOnlyList<string> overwritten = Apply(memory, GateEdits.Plan(data, 52,
            new GateEdits.Request([new FieldChange(GateFields.Troops, 9000), new FieldChange(GateFields.Durability, 2500)], null)));

        Assert.Empty(overwritten);
        Assert.Equal(9000, GateFields.Troops.Read(memory[gate.Address]));
        Assert.Equal(2500, GateFields.Durability.Read(memory[gate.Building!.Address]));
    }

    [Fact]
    public void APlainValueTheGameChangedMeanwhileIsWrittenOverAndReported()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        CityRecord city = data.Cities[업];
        memory.Set(city.Address, CityFields.Gold, 12_500);   // the month's income came in

        IReadOnlyList<string> overwritten = Apply(memory, CityEdits.Plan(data, 업, new CityEdits.Request([new FieldChange(CityFields.Gold, 50_000)], [], null)));

        Assert.Equal(50_000, CityFields.Gold.Read(memory[city.Address]));
        Assert.Contains("쓰기 직전 12,500", Assert.Single(overwritten));
    }

    [Fact]
    public void ALinkedChangeIsRefusedWhenTheGameMovedTheRecordAndNothingOfTheWriteStays()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        ForceRecord 원소 = data.ForceRecords[원소군];
        // The plan writes 수고 first, then 원소군's slots; a day passed and the first slot's policy is a day nearer.
        memory.Set(원소.Address, ForceFields.PolicyDays(모략, 0), 9);

        var error = Assert.Throws<StaleEditException>(() => Apply(memory, Defect(data)));

        Assert.Contains("모략 정책 1 준비 10 → 9", error.Message);
        Assert.Equal(data.OfficerById[470].Data[1..], memory[data.OfficerById[470].Address]![1..]);   // 수고 put back as he was
        Assert.Equal(470, ForceFields.PolicyOfficer(모략, 1).Read(memory[원소.Address]));
    }

    [Fact]
    public void AnotherUnitInTheSlotIsNotWritten()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        UnitRecord unit = data.Units.Single();
        memory.Set(unit.Address, new FieldDef("대장", RecordKind.Unit, UnitFields.CommanderOffset, FieldKind.UInt16, 0, 4095), 35);   // 마등's unit fell, 원소's took the slot

        Assert.Throws<InvalidOperationException>(() => Apply(memory, UnitEdits.Plan(data, unit.Slot, 521, [new FieldChange(UnitFields.Morale, 130)])));
        Assert.Equal(unit.Data[UnitFields.Morale.Offset], memory[unit.Address]![UnitFields.Morale.Offset]);
    }

    [Fact]
    public void TwoEditsOfOneRecordInOneWriteDoNotRefuseEachOther()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        OfficerRecord 수고 = data.OfficerById[470];
        RecordEdit first = new(RecordKind.Officer, 수고.Address, 수고.Data, 수고.Name, [new FieldChange(OfficerFields.StatusField, 3)]);
        RecordEdit second = new(RecordKind.Officer, 수고.Address, 수고.Data, 수고.Name, [new FieldChange(OfficerFields.Office, 20)]);

        GameMemory.Apply(memory, TestGame.Classes, [first, second]);

        Assert.Equal(3, OfficerFields.StatusField.Read(memory[수고.Address]));
        Assert.Equal(20, OfficerFields.Office.Read(memory[수고.Address]));
    }

    [Fact]
    public void UndoPutsEveryRecordBackAsItWas()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        EditPlan plan = Defect(data);
        Apply(memory, plan);

        Undo.Check check = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);
        GameMemory.Apply(memory, TestGame.Classes, check.Edits);

        Assert.Empty(check.LinkedMoved);
        Assert.Empty(check.Moved);
        Assert.Equal(data.OfficerById[470].Data, memory[data.OfficerById[470].Address]);
        Assert.Equal(data.ForceRecords[원소군].Data, memory[data.ForceRecords[원소군].Address]);
    }

    [Fact]
    public void UndoStopsWhenTheGameMovedALinkedFieldSince()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        EditPlan plan = Defect(data);
        Apply(memory, plan);
        memory.Set(data.OfficerById[470].Address, OfficerFields.PlaceField, 595);   // he went out on a task

        Undo.Check check = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);

        Assert.Contains("수고 있는 곳: 쓴 뒤 22 → 지금 595", Assert.Single(check.LinkedMoved));
    }

    [Fact]
    public void UndoAsksAboutAPlainValueTheGameChangedSinceAndCanLeaveIt()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        CityRecord city = data.Cities[업];
        EditPlan plan = CityEdits.Plan(data, 업, new CityEdits.Request([new FieldChange(CityFields.Gold, 60_000), new FieldChange(CityFields.Food, 210_000)], [], null));
        Apply(memory, plan);
        memory.Set(city.Address, CityFields.Gold, 61_000);

        Undo.Check check = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);
        GameMemory.Apply(memory, TestGame.Classes, check.KeepingMoved());

        Assert.Contains("금: 쓴 뒤 60,000 → 지금 61,000", Assert.Single(check.Moved).Note);
        Assert.Equal(61_000, CityFields.Gold.Read(memory[city.Address]));   // left as the game has it
        Assert.Equal(0, CityFields.Food.Read(memory[city.Address]));        // put back
    }

    [Fact]
    public void UndoSkipsARecordThatIsGone()
    {
        GameSnapshot data = Game();
        var memory = new FakeMemory(data);
        UnitRecord unit = data.Units.Single();
        EditPlan plan = UnitEdits.Plan(data, unit.Slot, 521, [new FieldChange(UnitFields.Morale, 130)]);
        Apply(memory, plan);
        memory.Remove(unit.Address);

        Undo.Check check = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);

        Assert.Equal(["마등 부대"], check.Missing);
        Assert.Empty(check.Edits);
    }

    // 수고 leads a unit: his 1st and 2nd 개성 (절약, 둔전) are in effect, the 1st with 5 left of a timed effect.
    private const int 절약 = 30, 둔전 = 32, 낭비 = 31;

    private static (GameSnapshot Data, FakeMemory Memory, ulong Address) InBattle()
    {
        GameSnapshot data = new TestGame()
            .Force(원소군, ruler: 35, 원소군단).City(업, 원소군단, governor: 35)
            .Officer(35, "원소", 원소군단, 업, status: 1).Officer(470, "수고", 원소군단, 업, status: 4)
            .With(470, OfficerFields.Personalities[0], 절약).With(470, OfficerFields.Personalities[1], 둔전)
            .With(470, OfficerFields.PersonalitiesInEffect, 0b11).With(470, OfficerFields.PersonalityTimeLeft[0], 5)
            .Build();
        return (data, new FakeMemory(data), data.OfficerById[470].Address);
    }

    private static EditPlan Personality(GameSnapshot data, int slot, int personality) =>
        OfficerEdits.Plan(data, 470, new OfficerEdits.Request([new FieldChange(OfficerFields.Personalities[slot], personality)], null, null, []));

    [Fact]
    public void AChangedPersonalityGoesInWithWhatTheOldOneHadGoingCleared()
    {
        (GameSnapshot data, FakeMemory memory, ulong address) = InBattle();

        IReadOnlyList<string> overwritten = Apply(memory, Personality(data, 0, 낭비));

        byte[] after = memory[address]!;
        Assert.Equal(낭비, OfficerFields.Personalities[0].Read(after));
        Assert.Equal(0b10, OfficerFields.PersonalitiesInEffect.Read(after));   // 둔전 stays in effect
        Assert.Equal(0, OfficerFields.PersonalityTimeLeft[0].Read(after));
        Assert.Empty(overwritten);
    }

    [Fact]
    public void WhatIsInEffectIsTakenAsTheGameHasItJustBeforeTheWrite()
    {
        (GameSnapshot data, FakeMemory memory, ulong address) = InBattle();
        memory.Set(address, OfficerFields.PersonalitiesInEffect, 0b100);   // after the read, 절약 and 둔전 ended, the 3rd lit

        Apply(memory, Personality(data, 0, 낭비));

        Assert.Equal(0b100, OfficerFields.PersonalitiesInEffect.Read(memory[address]!));
    }

    [Fact]
    public void ASlotLeftAsItWasKeepsWhatItHasGoing()
    {
        (GameSnapshot data, FakeMemory memory, ulong address) = InBattle();
        memory.Set(address, OfficerFields.Personalities[0], 낭비);   // the game itself changed it after the read

        Apply(memory, Personality(data, 0, 낭비));

        Assert.Equal(0b11, OfficerFields.PersonalitiesInEffect.Read(memory[address]!));
        Assert.Equal(5, OfficerFields.PersonalityTimeLeft[0].Read(memory[address]!));
    }

    [Fact]
    public void UndoingAPersonalityClearsWhatTheNewOneHadGoingWithoutAsking()
    {
        (GameSnapshot data, FakeMemory memory, ulong address) = InBattle();
        EditPlan plan = Personality(data, 0, 낭비);
        Apply(memory, plan);
        memory.Set(address, OfficerFields.PersonalitiesInEffect, 0b11);   // the game lit 낭비 after the write
        memory.Set(address, OfficerFields.PersonalityTimeLeft[0], 9);

        Undo.Check check = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);
        GameMemory.Apply(memory, TestGame.Classes, check.Edits);

        Assert.Empty(check.Moved);
        byte[] after = memory[address]!;
        Assert.Equal(절약, OfficerFields.Personalities[0].Read(after));
        Assert.Equal(0b10, OfficerFields.PersonalitiesInEffect.Read(after));
        Assert.Equal(0, OfficerFields.PersonalityTimeLeft[0].Read(after));
    }
}
