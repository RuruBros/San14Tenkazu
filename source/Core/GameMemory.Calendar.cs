namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static bool SupportsCalendar(GameIdentity identity) =>
        identity.Verified ||
        GameCommandProfile.Find(identity)?.Functions.ContainsKey(0x333480) == true;

    private static void RequireCalendarLayout(GameIdentity identity, GameLayout layout)
    {
        if (!SupportsCalendar(identity) || identity.Verified && !identity.ExeHash.Equals(VerifiedExeHash, StringComparison.OrdinalIgnoreCase))
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
        if (identity.DataProfile is not null)
            return WithProfiledEditSession(identity, layout, (process, _) =>
            {
                if (!CommandCodeMatches(process, identity, 0x333480, []) ||
                    !CommandCodeMatches(process, identity, 0x31CBA0, []))
                    throw new InvalidDataException(L.Connection.EditCodeChanged);
                RequireEditPage(process, layout.State + CalendarEdits.DateOffset, 5, PageReadWrite);
                return CalendarEdits.Apply(new ProcessMemory(process), layout.Tables.State, layout.State, identity.Classes.World, expected, target);
            });
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.Calendar.PauseFirst);
        RequireCalendarLayout(identity, layout);
        IntPtr handle = OpenGameWrite(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, identity);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return CalendarEdits.Apply(new ProcessMemory(handle), layout.Tables.State, layout.State, identity.Classes.World, expected, target); }
        finally { CloseHandle(handle); }
    }
}
