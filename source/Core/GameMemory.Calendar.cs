namespace San14Tenkazu;

internal static partial class GameMemory
{
    private static void RequireCalendarLayout(GameIdentity identity, GameLayout layout)
    {
        if (!identity.Verified || !identity.ExeHash.Equals(VerifiedExeHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.Calendar.VerifiedOnly);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        GameLayout current = ReadCurrentLayout(identity);
        if (!layout.SameStorage(current) || layout.State == 0) throw new InvalidOperationException(L.Errors.StateMoved);
    }

    internal static GameClock ReadCalendar(GameIdentity identity, GameLayout layout)
    {
        RequireCalendarLayout(identity, layout);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return CalendarEdits.Read(new ProcessMemory(handle), layout.Tables.State, layout.State, identity.Classes.World); }
        finally { CloseHandle(handle); }
    }

    internal static GameClock ChangeCalendar(GameIdentity identity, GameLayout layout, GameClock expected, GameCalendarDate target)
    {
        _ = target.Encode();
        RequireCalendarLayout(identity, layout);
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.Calendar.PauseFirst);
        RequireCalendarLayout(identity, layout);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return CalendarEdits.Apply(new ProcessMemory(handle), layout.Tables.State, layout.State, identity.Classes.World, expected, target); }
        finally { CloseHandle(handle); }
    }
}
