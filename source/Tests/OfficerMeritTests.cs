namespace San14Tenkazu.Tests;

public class OfficerMeritTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    [InlineData(65535)]
    public void ApplyingAndUndoingMeritPreservesEveryOtherOfficerByte(int value)
    {
        GameSnapshot data = new TestGame().Force(1, 1, 1).Officer(12, "조인", 1, 1, 4)
            .With(12, OfficerFields.Merit, 1234).Build();
        OfficerRecord officer = data.OfficerById[12];
        // The adjacent bytes include the separate 공훈 steps (+0x1C2/+0x1C3).
        officer.Data[0x1BE] = 0xA5; officer.Data[0x1BF] = 0x5A;
        officer.Data[0x1C2] = 4; officer.Data[0x1C3] = 2;
        byte[] before = (byte[])officer.Data.Clone();
        var memory = new FakeMemory(data);
        EditPlan plan = OfficerEdits.Plan(data, officer.Id,
            new OfficerEdits.Request([new FieldChange(OfficerFields.Merit, value)], null, null, []));

        Assert.Equal(OfficerFields.Merit, Assert.Single(Assert.Single(plan.Edits).Changes).Field);
        Assert.Empty(GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        byte[] expected = (byte[])before.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(0x1C0), (ushort)value);
        Assert.Equal(expected, memory[officer.Address]);

        Undo.Check undo = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);
        Assert.Empty(undo.LinkedMoved); Assert.Empty(undo.Moved);
        Assert.Empty(GameMemory.Apply(memory, TestGame.Classes, undo.Edits));
        Assert.Equal(before, memory[officer.Address]);
    }
}
