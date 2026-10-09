using System.Buffers.Binary;

namespace San14Tenkazu;

internal readonly record struct GameCalendarDate(int Year, int Month, int Day)
{
    // Intersect the snapshot guard (>100) with native 0x333480 (<352).
    // NativeCalendar (0x31CBA0) uses twelve 30-day months, including February.
    internal const int MinYear = 101, MaxYear = 351;
    internal bool IsValid => Year is >= MinYear and <= MaxYear && Month is >= 1 and <= 12 && Day is >= 1 and <= 30;
    internal byte[] Encode()
    {
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(GameCalendarDate), L.Calendar.Range);
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)Year);
        bytes[2] = (byte)Month; bytes[3] = (byte)Day;
        return bytes;
    }
    public override string ToString() => L.Calendar.Date(Year, Month, Day);
}

internal readonly record struct GameClock(GameCalendarDate Date, int Hour)
{
    internal static GameClock Read(ReadOnlySpan<byte> bytes)
    {
        var clock = new GameClock(new(BinaryPrimitives.ReadUInt16LittleEndian(bytes), bytes[2], bytes[3]), bytes[4]);
        if (!clock.Date.IsValid || clock.Hour is < 0 or > 23) throw new InvalidDataException(L.Calendar.InvalidClock);
        return clock;
    }
}

internal static class CalendarEdits
{
    internal const int DateOffset = 0x34;

    // Check the live pointer and class on every read, including rollback.
    internal static GameClock Read(IRecordMemory memory, ulong stateSlot, ulong state, ulong worldClass)
    {
        if (state == 0 || stateSlot == 0 || worldClass == 0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(stateSlot, 8)) != state ||
            BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(state, 8)) != worldClass)
            throw new InvalidOperationException(L.Errors.StateMoved);
        byte[] clock = memory.Read(state + DateOffset, 5);
        if (BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(stateSlot, 8)) != state)
            throw new InvalidOperationException(L.Errors.StateMoved);
        return GameClock.Read(clock);
    }

    internal static GameClock Apply(IRecordMemory memory, ulong stateSlot, ulong state, ulong worldClass,
        GameClock expected, GameCalendarDate target)
    {
        byte[] replacement = target.Encode();
        if (!expected.Date.IsValid || expected.Hour is < 0 or > 23) throw new InvalidDataException(L.Calendar.InvalidClock);
        if (Read(memory, stateSlot, state, worldClass) != expected) throw new InvalidOperationException(L.Calendar.Stale);
        if (target == expected.Date) return expected;
        byte[] original = expected.Date.Encode();
        try
        {
            // Four bytes only: hour, padding, player force and all other state stay outside this write.
            memory.Write(state + DateOffset, replacement);
            GameClock after = Read(memory, stateSlot, state, worldClass);
            if (after != new GameClock(target, expected.Hour)) throw new IOException(L.Calendar.WriteFailed);
            return after;
        }
        catch (Exception failure)
        {
            try
            {
                // Do not restore another save's state or overwrite a clock that has started advancing.
                if (BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(stateSlot, 8)) != state ||
                    BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(state, 8)) != worldClass ||
                    memory.Read(state + DateOffset + 4, 1)[0] != expected.Hour)
                    throw new InvalidOperationException(L.Calendar.Stale);
                memory.Write(state + DateOffset, original);
                if (Read(memory, stateSlot, state, worldClass) != expected) throw new IOException(L.Calendar.WriteFailed);
            }
            catch (Exception recovery)
            {
                throw new AggregateException(L.Calendar.RollbackFailed, failure, recovery);
            }
            throw;
        }
    }
}
