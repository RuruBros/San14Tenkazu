using System.Buffers.Binary;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests;

public class CalendarTests
{
    private static readonly GameClock Before = new(new(197, 10, 11), 14);
    private static readonly GameCalendarDate Target = new(200, 2, 30);

    private sealed class ClockMemory : IRecordMemory
    {
        internal const ulong Slot = 0x100, State = 0x1000, Class = 0xABCDE;
        internal readonly byte[] StateBytes = Enumerable.Repeat((byte)0xA5, 0x80).ToArray();
        internal ulong StatePointer = State;
        internal int Writes;
        internal Action<ClockMemory>? AfterFirstWrite;
        internal bool FailRecovery;
        internal ClockMemory()
        {
            BinaryPrimitives.WriteUInt64LittleEndian(StateBytes, Class);
            Before.Date.Encode().CopyTo(StateBytes, CalendarEdits.DateOffset);
            StateBytes[CalendarEdits.DateOffset + 4] = (byte)Before.Hour;
        }
        public byte[] Read(ulong address, int length) => address == Slot
            ? BitConverter.GetBytes(StatePointer)
            : StateBytes.AsSpan(checked((int)(address - State)), length).ToArray();
        public void Write(ulong address, byte[] bytes)
        {
            ++Writes;
            Assert.Equal(State + CalendarEdits.DateOffset, address);
            Assert.Equal(4, bytes.Length);
            if (Writes > 1 && FailRecovery) throw new IOException("Recovery write failed");
            bytes.CopyTo(StateBytes, checked((int)(address - State)));
            if (Writes == 1) AfterFirstWrite?.Invoke(this);
        }
        internal GameClock Apply(GameClock? expected = null, GameCalendarDate? target = null) =>
            CalendarEdits.Apply(this, Slot, State, Class, expected ?? Before, target ?? Target);
    }

    [Fact]
    public void EveryAllowedDateAlsoPassesTheVerifiedNativeDateValidator()
    {
        for (int year = GameCalendarDate.MinYear; year <= GameCalendarDate.MaxYear; ++year)
            for (int month = 1; month <= 12; ++month)
                foreach (int day in new[] { 1, 30 })
                {
                    var bytes = new byte[6];
                    new GameCalendarDate(year, month, day).Encode().CopyTo(bytes, 0);
                    bytes[4] = 23;
                    Assert.True(CommandTravel.DateValid(new Rec(0, bytes), 0));
                }
    }

    [Theory]
    [InlineData(100, 1, 1)] [InlineData(352, 1, 1)] [InlineData(200, 0, 1)]
    [InlineData(200, 13, 1)] [InlineData(200, 2, 0)] [InlineData(200, 2, 31)]
    public void InvalidTargetsAreRefusedWithoutWriting(int year, int month, int day)
    {
        var memory = new ClockMemory();
        Assert.Throws<ArgumentOutOfRangeException>(() => memory.Apply(target: new(year, month, day)));
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void DateChangePreservesHourPaddingPlayerAndEveryOtherStateByte()
    {
        var memory = new ClockMemory();
        byte[] expected = (byte[])memory.StateBytes.Clone();
        Target.Encode().CopyTo(expected, CalendarEdits.DateOffset);
        Assert.Equal(new GameClock(Target, Before.Hour), memory.Apply());
        Assert.Equal(expected, memory.StateBytes);
        Assert.Equal(1, memory.Writes);
    }

    [Fact]
    public void MovingClockAndChangedStateAreRefusedBeforeAnyWrite()
    {
        var hour = new ClockMemory(); hour.StateBytes[0x38]++;
        Assert.Throws<InvalidOperationException>(() => hour.Apply()); Assert.Equal(0, hour.Writes);
        var day = new ClockMemory(); day.StateBytes[0x37]++;
        Assert.Throws<InvalidOperationException>(() => day.Apply()); Assert.Equal(0, day.Writes);
        var state = new ClockMemory { StatePointer = 0x2000 };
        Assert.Throws<InvalidOperationException>(() => state.Apply()); Assert.Equal(0, state.Writes);
        var kind = new ClockMemory(); kind.StateBytes[0]++;
        Assert.Throws<InvalidOperationException>(() => kind.Apply()); Assert.Equal(0, kind.Writes);
    }

    [Fact]
    public void UnchangedDateDoesNotWrite()
    {
        var memory = new ClockMemory();
        Assert.Equal(Before, memory.Apply(target: Before.Date));
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void FailedAndPartialWritesRestoreTheOriginalDate()
    {
        foreach (Action<ClockMemory> fault in new Action<ClockMemory>[]
            { m => m.StateBytes[0x37] = 0, m => { m.StateBytes[0x35] = 0xFF; throw new IOException("Partial write"); } })
        {
            var memory = new ClockMemory { AfterFirstWrite = fault };
            byte[] original = (byte[])memory.StateBytes.Clone();
            Assert.ThrowsAny<Exception>(() => memory.Apply());
            Assert.Equal(original, memory.StateBytes);
            Assert.Equal(2, memory.Writes);
        }
    }

    [Fact]
    public void RecoveryFailureIsReportedWithTheOriginalFailure()
    {
        var memory = new ClockMemory { AfterFirstWrite = m => m.StateBytes[0x37] = 0, FailRecovery = true };
        var error = Assert.Throws<AggregateException>(() => memory.Apply());
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(L.Calendar.RollbackFailed, error.Message.Split(" (")[0]);
    }

    [Fact]
    public void RecoveryNeverWritesToAReplacedStateOrAnAdvancingClock()
    {
        foreach (Action<ClockMemory> change in new Action<ClockMemory>[]
            { m => m.StatePointer = 0x2000, m => ++m.StateBytes[0x38] })
        {
            var memory = new ClockMemory { AfterFirstWrite = change };
            Assert.Throws<AggregateException>(() => memory.Apply());
            Assert.Equal(1, memory.Writes);
        }
    }
}
