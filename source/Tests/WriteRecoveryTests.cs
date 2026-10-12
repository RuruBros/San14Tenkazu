using System.ComponentModel;

namespace San14Tenkazu.Tests;

public sealed class WriteRecoveryTests
{
    private sealed class FaultMemory(GameSnapshot data) : IRecordMemory
    {
        internal readonly FakeMemory Inner = new(data);
        internal Action<ulong, int>? BeforeRead;
        internal Action<ulong, byte[], int>? BeforeWrite;
        internal Action<ulong, byte[], int>? AfterWrite;
        internal int Writes;
        public byte[] Read(ulong address, int length)
        {
            BeforeRead?.Invoke(address, length);
            return Inner.Read(address, length);
        }
        public void Write(ulong address, byte[] bytes)
        {
            int attempt = ++Writes;
            BeforeWrite?.Invoke(address, bytes, attempt);
            Inner.Write(address, bytes);
            AfterWrite?.Invoke(address, bytes, attempt);
        }
    }

    private static (GameSnapshot Data, UnitRecord Unit, FaultMemory Memory, EditPlan Plan) UnitEdit()
    {
        var data = new TestGame().Officer(1, "A", 1, 1, 4).Officer(2, "B", 1, 1, 4).Leading(1).Build();
        UnitRecord unit = data.Units.Single();
        UnitFields.Troops.Encode(1000).CopyTo(unit.Data, UnitFields.Troops.Offset);
        return (data, unit, new(data), UnitEdits.Plan(data, unit.Slot, 1, [new(UnitFields.Troops, 2000)]));
    }

    [Fact]
    public void ReplacementUnitIsNotOverwrittenDuringRollback()
    {
        var (_, unit, memory, plan) = UnitEdit();
        memory.AfterWrite = (_, _, attempt) =>
        {
            if (attempt != 1) return;
            memory.Inner.Set(unit.Address, new("Commander", RecordKind.Unit, UnitFields.CommanderOffset, FieldKind.UInt16, 0, 6000), 2);
            memory.Inner.Set(unit.Address, UnitFields.Troops, 777);
        };

        var error = Assert.Throws<WriteRecoveryException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));

        Assert.Equal(777, UnitFields.Troops.Read(memory.Inner[unit.Address]!));
        Assert.Equal(1, memory.Writes);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains((unit.Address + (ulong)UnitFields.Troops.Offset).ToString("X"), error.Message);
        Assert.Contains(UnitFields.Troops.Label, error.Message);
        Assert.DoesNotContain("모두 되돌", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrReusedRecordIsReportedWithoutRollbackWrites(bool reused)
    {
        var (_, unit, memory, plan) = UnitEdit();
        memory.AfterWrite = (_, _, attempt) =>
        {
            if (attempt != 1) return;
            if (reused) memory.Inner.Set(unit.Address, new("Vtable", RecordKind.Unit, 0, FieldKind.UInt32, 0, uint.MaxValue), 0x1234);
            else memory.Inner.Remove(unit.Address);
        };
        var error = Assert.Throws<WriteRecoveryException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        Assert.Equal(1, memory.Writes);
        Assert.Contains(L.Errors.RollbackIncomplete, error.Message);
    }

    [Fact]
    public void NewGameValueOnTheSameUnitIsPreserved()
    {
        var (_, unit, memory, plan) = UnitEdit();
        memory.AfterWrite = (_, _, attempt) =>
        {
            if (attempt == 1) memory.Inner.Set(unit.Address, UnitFields.Troops, 777);
        };
        var error = Assert.Throws<WriteRecoveryException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        Assert.Equal(777, UnitFields.Troops.Read(memory.Inner[unit.Address]!));
        Assert.Equal(1, memory.Writes);
        Assert.Contains(L.Errors.RollbackValueChanged, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrPartialWritesRestoreOnlyTheirOwnBytes(bool partial)
    {
        var (_, unit, memory, plan) = UnitEdit();
        var original = new Win32Exception(299, "Injected write failure");
        memory.BeforeWrite = (address, bytes, attempt) =>
        {
            if (attempt != 1) return;
            if (partial) memory.Inner.Write(address, bytes[..1]);
            throw original;
        };
        Assert.Same(original, Assert.Throws<Win32Exception>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits)));
        Assert.Equal(1000, UnitFields.Troops.Read(memory.Inner[unit.Address]!));
        Assert.Equal(partial ? 2 : 1, memory.Writes);
    }

    [Fact]
    public void FailedRollbackReportsTheFieldAndContinuesRestoringOtherFields()
    {
        var data = new TestGame().Force(1, 1, 1).City(6, 1, 2)
            .Officer(1, "Ruler", 1, 6, 1).Officer(2, "Former", 1, 6, 3).Officer(3, "Next", 1, 6, 4).Build();
        CityRecord city = data.Cities[6];
        var memory = new FaultMemory(data);
        var plan = CityEdits.Plan(data, 6, new([new(CityFields.Gold, 50000)], [], 3));
        memory.Inner.Set(data.OfficerById[2].Address, OfficerFields.Office, 10);
        ulong target = city.Address + (ulong)CityFields.Governor.Offset;
        memory.BeforeWrite = (address, _, attempt) =>
        {
            if (address == target && attempt > 2) throw new Win32Exception(299, "Injected recovery failure");
        };

        var error = Assert.Throws<WriteRecoveryException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));

        Assert.IsType<StaleEditException>(error.InnerExceptions[0]);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains("Injected recovery failure", error.Message);
        Assert.Contains(CityFields.Governor.Label, error.Message);
        Assert.Contains(target.ToString("X"), error.Message);
        Assert.Equal(3, CityFields.Governor.Read(memory.Inner[city.Address]!));
        Assert.Equal(4, OfficerFields.StatusField.Read(memory.Inner[data.OfficerById[3].Address]!));
        Assert.Equal(0, CityFields.Gold.Read(memory.Inner[city.Address]!));
        Assert.DoesNotContain("모두 되돌", error.Message);
    }

    [Fact]
    public void RollbackMustReadBackTheRestoredValue()
    {
        var (_, unit, memory, plan) = UnitEdit();
        var original = new IOException("Injected post-write read failure");
        bool failRead = true;
        memory.BeforeRead = (_, _) =>
        {
            if (memory.Writes == 1 && failRead) { failRead = false; throw original; }
        };
        memory.AfterWrite = (_, _, attempt) =>
        {
            if (attempt == 2) memory.Inner.Set(unit.Address, UnitFields.Troops, 777);
        };
        var error = Assert.Throws<WriteRecoveryException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        Assert.Same(original, error.InnerExceptions[0]);
        Assert.Equal(777, UnitFields.Troops.Read(memory.Inner[unit.Address]!));
        Assert.Contains(L.Errors.RollbackReadbackFailed, error.Message);
    }

    [Fact]
    public void ChangedLinkedContextIsNotRestoredOver()
    {
        var data = new TestGame().Officer(1, "Officer", 1, 6, 4).Build();
        OfficerRecord officer = data.OfficerById[1];
        var memory = new FaultMemory(data);
        var edit = new RecordEdit(RecordKind.Officer, officer.Address, officer.Data, officer.Name, [new(OfficerFields.Office, 10)]);
        bool failRead = true;
        memory.BeforeRead = (_, _) =>
        {
            if (memory.Writes == 1 && failRead) { failRead = false; throw new IOException("Post-write failure"); }
        };
        memory.AfterWrite = (_, _, attempt) =>
        {
            if (attempt == 1) memory.Inner.Set(officer.Address, OfficerFields.CorpsField, 2);
        };
        var error = Assert.Throws<WriteRecoveryException>(() => GameMemory.Apply(memory, TestGame.Classes, [edit]));
        Assert.Equal(1, memory.Writes);
        Assert.Equal(10, OfficerFields.Office.Read(memory.Inner[officer.Address]!));
        Assert.Contains(L.Errors.RollbackContextChanged(OfficerFields.CorpsField.Label), error.Message);
    }

    [Fact]
    public void RepeatedFieldsAndMultipleEditsOfOneRecordRestoreInReverseOrder()
    {
        var data = new TestGame().Build();
        CityRecord city = data.Cities[6], missing = data.Cities[7];
        var memory = new FaultMemory(data);
        var original = new IOException("Later record read failure");
        memory.BeforeRead = (address, _) => { if (address == missing.Address) throw original; };
        RecordEdit Edit(CityRecord target, params FieldChange[] changes) => new(RecordKind.City, target.Address, target.Data, "City", changes);
        Assert.Same(original, Assert.Throws<IOException>(() => GameMemory.Apply(memory, TestGame.Classes,
            [Edit(city, new(CityFields.Gold, 10), new(CityFields.Gold, 10), new(CityFields.Food, 20)),
             Edit(city, new FieldChange(CityFields.Gold, 30)), Edit(missing, new FieldChange(CityFields.Gold, 40))])));
        Assert.Equal(city.Data, memory.Inner[city.Address]);
    }

    [Fact]
    public void PersonalityRestartRecoveryValidatesItsOfficerAndKeepsOtherRecoveryRunning()
    {
        var data = new TestGame().Officer(1, "Officer", 1, 6, 4)
            .With(1, OfficerFields.Personalities[0], 30).With(1, OfficerFields.PersonalitiesInEffect, 1)
            .Personality(30, "Trait", "Description", (20, 10)).Build();
        OfficerRecord officer = data.OfficerById[1];
        PersonalityRecord personality = data.Personalities.Single();
        var memory = new FaultMemory(data);
        ulong restarted = officer.Address + (ulong)OfficerFields.PersonalitiesInEffect.Offset;
        memory.AfterWrite = (address, _, _) =>
        {
            if (address == restarted) memory.Inner.Set(officer.Address, new("Id", RecordKind.Officer, OfficerFields.IdOffset, FieldKind.UInt16, 0, 6000), 2);
        };
        memory.BeforeRead = (address, _) => { if (address == data.Cities[7].Address) throw new IOException("Later failure"); };
        RecordEdit[] edits = [new(RecordKind.Personality, personality.Address, personality.Data, "Trait", [new(PersonalityFields.Values[0], 20)], [new(officer.Address, 0, 30)]),
            new(RecordKind.City, data.Cities[7].Address, data.Cities[7].Data, "City", [new(CityFields.Gold, 1)])];

        var error = Assert.Throws<WriteRecoveryException>(() => GameMemory.Apply(memory, TestGame.Classes, edits));
        Assert.Equal(0, OfficerFields.PersonalitiesInEffect.Read(memory.Inner[officer.Address]!));
        Assert.Equal(10, PersonalityFields.Values[0].Read(memory.Inner[personality.Address]!));
        Assert.Contains(OfficerFields.PersonalitiesInEffect.Label, error.Message);
    }
}
