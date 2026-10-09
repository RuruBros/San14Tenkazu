using static San14Tenkazu.AncientOfficers;

namespace San14Tenkazu.Tests;

// 고대무장 (AncientOfficers): who waits for a bit of the play record, who comes with a DLC, what sets each bit, and the
// write that lets them appear (GameMemory.Apply on the play record, with its change mark) and its undo.
public class AncientOfficerTests
{
    private static IEnumerable<int> All => Enumerable.Range(First, Last - First + 1);

    // The bits of these officers, with bit 0 that the game's records hold besides.
    private static ulong BitsOf(IEnumerable<int> ids) => ids.Aggregate(1UL, (bits, id) => bits | 1UL << Bit(id)!.Value);

    private static IEnumerable<int> FromTutorials(params int[] numbers) =>
        All.Where(id => SourceOf(id) is Tutorial t && numbers.Contains(t.Number));

    [Fact]
    public void ThirtySixWaitForABitFiveComeWithADlcAndTheRestAlwaysMay()
    {
        int[] bits = All.Select(Bit).OfType<int>().ToArray();
        Assert.Equal(36, bits.Length);
        Assert.Equal(36, bits.Distinct().Count());
        Assert.All(bits, bit => Assert.InRange(bit, 1, 63));
        Assert.Equal(5, All.Count(ComesWithDlc));
        Assert.DoesNotContain(All, id => ComesWithDlc(id) && Bit(id) is not null);
        Assert.Equal(47, All.Count(id => AccessOf(id, 0, new HashSet<int>()) == Access.Always));
    }

    [Theory]
    [InlineData(2003, 2)]
    [InlineData(2004, 3)]
    [InlineData(2018, 17)]
    [InlineData(2028, 27)]
    [InlineData(2068, 45)]
    [InlineData(2077, 54)]
    [InlineData(2083, 55)]
    [InlineData(2088, 60)]
    public void ABitIsWhereTheGameKeepsIt(int id, int bit) => Assert.Equal(bit, Bit(id));

    [Theory]
    [InlineData(2000)]
    [InlineData(2001)]
    [InlineData(2013)]
    [InlineData(2050)]
    [InlineData(2067)]
    [InlineData(2078)]
    [InlineData(2082)]
    [InlineData(2089)]
    public void OthersHaveNoBit(int id) => Assert.Null(Bit(id));

    // The two play records read on 2026-10-03: the player's own, who had cleared tutorials 1~6 and nothing more, and
    // one shared online (2020) by a player who had cleared everything there was then (not tutorials 7 and 8).
    [Fact]
    public void TheTablesGiveTheBitsOfRealRecords()
    {
        Assert.Equal(0x0034_5100_C802_642DUL, BitsOf(FromTutorials(1, 2, 3, 4, 5, 6)));
        Assert.Equal(0x007F_F100_C803_EFFDUL, BitsOf(All.Where(id => Bit(id) is not null).Except(FromTutorials(7, 8))));
    }

    [Fact]
    public void EveryOfficerWhoWaitsHasWhatSetsHisBit()
    {
        Assert.All(All.Where(id => Bit(id) is not null), id => Assert.NotNull(SourceOf(id)));
        Assert.All(All.Where(id => Bit(id) is null), id => Assert.Null(SourceOf(id)));
        Assert.Equal(L.Ancient.Tutorial(1), Condition(2004));
        Assert.Equal(L.Ancient.Scenario(L.Table(ScenarioTable, 8)), Condition(2016));
        Assert.Equal(L.Ancient.Chronicle(L.Table(ChronicleTable, 2077)), Condition(2077));
        Assert.Equal(L.Ancient.ChronicleS, Condition(ChronicleSReward));
        Assert.Equal(L.Ancient.WithDlc, Condition(2078));
        Assert.Equal("", Condition(2001));
        Assert.All(Enumerable.Range(1, 8), n => Assert.NotEqual("", L.Table(ScenarioTable, n)));
        Assert.All(All.Select(SourceOf).OfType<Chronicle>(), c => Assert.NotEqual("", L.Table(ChronicleTable, c.Officer)));
    }

    [Fact]
    public void WhetherOneMayAppearFollowsHisBitOrHisDlc()
    {
        ulong unlocks = 1UL << 3;   // 장량's
        Assert.Equal(Access.Unlocked, AccessOf(2004, unlocks, null));
        Assert.Equal(Access.Locked, AccessOf(2006, unlocks, null));
        Assert.Equal(Access.Always, AccessOf(2001, 0, null));
        Assert.Equal(Access.Dlc, AccessOf(2078, ulong.MaxValue, null));
        Assert.Equal(Access.DlcHeld, AccessOf(2078, 0, new HashSet<int> { 2078 }));
        Assert.Equal(Access.DlcMissing, AccessOf(2079, ulong.MaxValue, new HashSet<int> { 2078 }));
    }

    private static GameSnapshot Game(ulong unlocks) => new TestGame()
        .Officer(2004, "장량", 0, 0, status: 7).Officer(2006, "소하", 0, 0, status: 7).Officer(2088, "상우춘", 0, 0, status: 7)
        .Play(unlocks).Build();

    [Fact]
    public void UnlockingSetsTheBitsOfThoseLockedAndKeepsTheRest()
    {
        GameSnapshot data = Game(1UL | 1UL << 3);

        EditPlan plan = Unlock(data, [2004, 2006, 2088, 2001, 2078]);

        RecordEdit edit = Assert.Single(plan.Edits);
        Assert.Equal(RecordKind.PlayRecord, edit.Kind);
        Assert.Equal(data.Play!.Address, edit.Address);
        Assert.Equal(new[] { new FieldChange(PlayRecordFields.Unlocks1, 1 | 1 << 3 | 1 << 5), new FieldChange(PlayRecordFields.Unlocks2, 1 << 28) }, edit.Changes);
        Assert.Equal(L.Ancient.UnlockSummary(2, "소하, 상우춘"), plan.Summary);
    }

    [Fact]
    public void AWordWithItsTopBitGoesInAsTheGameReadsIt()
    {
        EditPlan plan = Unlock(new TestGame().Play(1).Build(), All);

        Assert.Equal(new[] { new FieldChange(PlayRecordFields.Unlocks1, unchecked((int)0xC803_EFFD)), new FieldChange(PlayRecordFields.Unlocks2, 0x1FFF_F100) },
            Assert.Single(plan.Edits).Changes);
    }

    [Fact]
    public void NothingIsWrittenWhenNoneIsLockedOrThereIsNoRecord()
    {
        EditPlan none = Unlock(Game(BitsOf([2004, 2006, 2088])), [2004, 2006, 2088]);
        Assert.Empty(none.Edits);
        Assert.Equal(L.Ancient.NothingLocked, none.Summary);
        Assert.True(Unlock(new TestGame().Build(), [2004]).Refused);
    }

    [Fact]
    public void TheWriteRaisesTheChangeMarkAndUndoPutsTheBitsBack()
    {
        GameSnapshot data = Game(1);
        var memory = new FakeMemory(data);
        EditPlan plan = Unlock(data, [2006]);

        Assert.Empty(GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        byte[] after = memory[data.Play!.Address]!;
        Assert.Equal(1UL | 1UL << 5, BitConverter.ToUInt64(after, PlayRecordFields.Unlocks1.Offset));
        Assert.Equal(1, PlayRecordFields.Changed.Read(after));

        Assert.Empty(GameMemory.Apply(memory, TestGame.Classes, Undo.Of(plan.Edits)));
        Assert.Equal(1UL, BitConverter.ToUInt64(memory[data.Play.Address]!, PlayRecordFields.Unlocks1.Offset));
    }

    [Fact]
    public void ARecordThatNoLongerOpensAsOneIsNotWritten()
    {
        GameSnapshot data = Game(1);
        var memory = new FakeMemory(data);
        EditPlan plan = Unlock(data, [2006]);
        memory.Write(data.Play!.Address + 4, [5, 0, 0, 0]);   // another version of the record

        Assert.Throws<InvalidOperationException>(() => GameMemory.Apply(memory, TestGame.Classes, plan.Edits));
        Assert.Equal(1UL, BitConverter.ToUInt64(memory[data.Play.Address]!, PlayRecordFields.Unlocks1.Offset));
        Assert.Equal(0, PlayRecordFields.Changed.Read(memory[data.Play.Address]!));
    }

    [Fact]
    public void TheRecordIsToldByItsHeaderItsMarkAndItsRanks()
    {
        byte[] good = Game(0).Play!.Data;
        Assert.True(GameMemory.IsPlayRecord(good));

        byte[] Changed(int offset, byte value)
        {
            byte[] data = (byte[])good.Clone();
            data[offset] = value;
            return data;
        }
        Assert.False(GameMemory.IsPlayRecord(Changed(4, 5)));
        Assert.False(GameMemory.IsPlayRecord(Changed(PlayRecordFields.Changed.Offset, 2)));
        Assert.False(GameMemory.IsPlayRecord(Changed(PlayRecordFields.RanksOffset + 299, 5)));
        Assert.False(GameMemory.IsPlayRecord(good.AsSpan(0, PlayRecordFields.Size - 1)));
    }

    [Fact]
    public void TheChecksPassARecordAsTheGameKeepsIt()
    {
        GameSnapshot data = new TestGame().Play(BitsOf(FromTutorials(1, 2, 3, 4, 5, 6)), tutorials: 0x3F).Build();

        GameChecks.Check check = Assert.Single(GameChecks.Run(data, GameCatalog.Empty), c => c.Group == GameChecks.PlayRecord);
        Assert.Equal(check.Total, check.Fit);
    }
}
