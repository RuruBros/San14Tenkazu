namespace San14Tenkazu.Tests;

public sealed class OfficerRevivalTests
{
    private static GameSnapshot Data(int status = 9) => new TestGame().Force(1, 1, 1).City(1, 1)
        .Officer(1, "조조", 1, 1, 1).Officer(12, "하진", 0, 0, status)
        .With(12, OfficerFields.Birth, 211).With(12, OfficerFields.Debut, 230)
        .With(12, OfficerFields.Death, 190).With(12, OfficerFields.Strength, 88)
        .With(12, OfficerFields.Personalities[0], 12).Build();

    [Theory]
    [InlineData(9, 0, 4, 1, 100, 203, 2)]
    [InlineData(9, 1, 5, 0, 0, 0, 0)]
    [InlineData(7, 0, 4, 1, 100, 203, 2)]
    [InlineData(7, 1, 5, 0, 0, 0, 0)]
    public void RestoresMembershipAndPreservesEditableValues(int before, int mode, int status, int corps, int loyalty,
        int joined, int service)
    {
        var data = Data(before);
        var plan = OfficerRevival.Plan(data, new(12, (RevivalMode)mode, 1, 220));
        Assert.False(plan.Refused);
        var after = data.With(plan.Edits).OfficerById[12];
        Assert.Equal(status, after.Status);
        Assert.Equal(corps, after.Corps);
        Assert.Equal(1, after.BaseId);
        Assert.Equal(1, after[OfficerFields.PlaceField]);
        Assert.Equal(220, after[OfficerFields.Death]);
        Assert.Equal(211, after[OfficerFields.Birth]);
        Assert.Equal(230, after[OfficerFields.Debut]);
        Assert.Equal(loyalty, after[OfficerFields.Loyalty]);
        Assert.Equal(joined, after[OfficerFields.JoinedYear]);
        Assert.Equal(service, after[OfficerFields.Service]);
        Assert.Equal(OfficerFields.NoOffice, after[OfficerFields.Office]);
        Assert.Equal(88, after[OfficerFields.Strength]);
        Assert.Equal(12, after[OfficerFields.Personalities[0]]);
        Assert.Single(plan.Edits);
        Assert.Contains(L.Revival.UndoNote, plan.Summary);
        Assert.Contains(OfficerFields.StatusName(before) + " → " + OfficerFields.StatusName(status), plan.Summary);
        Assert.Contains(before == 7 ? L.Revival.AppearanceNote : L.Revival.ValuesNote, plan.Summary);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(8)]
    public void RejectsEveryActiveStatus(int status) => Assert.True(OfficerRevival.Plan(Data(status), new(12, RevivalMode.Free, 1, 220)).Refused);

    [Fact]
    public void RejectsUnusedSlot()
    {
        var data = Data();
        data.OfficerById[12].Data[OfficerFields.StatusOffset] = 0;
        Assert.True(OfficerRevival.Plan(data, new(12, RevivalMode.Free, 1, 220)).Refused);
    }

    [Theory]
    [InlineData(7)] [InlineData(9)]
    public void FreeRevivalAllowsAnUnownedBaseButServingRequiresALivingRuler(int status)
    {
        var data = Data(status);
        Assert.False(OfficerRevival.Plan(data, new(12, RevivalMode.Free, 2, 220)).Refused);
        Assert.True(OfficerRevival.Plan(data, new(12, RevivalMode.Serving, 2, 220)).Refused);
        var withoutRuler = data.With([new(RecordKind.Officer, data.OfficerById[1].Address, data.OfficerById[1].Data,
            "조조", [new(OfficerFields.StatusField, 9)])]);
        Assert.True(OfficerRevival.Plan(withoutRuler, new(12, RevivalMode.Serving, 1, 220)).Refused);
    }

    [Theory]
    [InlineData(7, 190)] [InlineData(7, 203)] [InlineData(7, 1000)]
    [InlineData(9, 190)] [InlineData(9, 203)] [InlineData(9, 1000)]
    public void RejectsExpiredAndOutOfRangeLifespans(int status, int year) => Assert.True(OfficerRevival.Plan(Data(status), new(12, RevivalMode.Free, 1, year)).Refused);

    [Theory]
    [InlineData(7)] [InlineData(9)]
    public void RejectsGeneratedSlotsAndActiveCommanders(int status)
    {
        var data = Data(status);
        data.OfficerById[12].Data[0x15F] = 0x40;
        Assert.True(OfficerRevival.Plan(data, new(12, RevivalMode.Free, 1, 220)).Refused);
        var withUnit = new TestGame().Force(1, 1, 1).Officer(1, "조조", 1, 1, 1)
            .Officer(12, "하진", 0, 0, status).Leading(12).Build();
        Assert.True(OfficerRevival.Plan(withUnit, new(12, RevivalMode.Free, 1, 220)).Refused);
    }
}
