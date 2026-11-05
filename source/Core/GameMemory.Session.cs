namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static GameSessionObservation ReadSession(GameIdentity identity)
    {
        var layout = identity.Verified && identity.ExeHash.Equals(VerifiedExeHash, StringComparison.OrdinalIgnoreCase)
            ? GameSessionReader.Original : identity.DataProfile?.Session;
        if (layout is null)
            return new(GameSessionPhase.Unsupported, 0, 0, [], "Executable has no registered session layout");
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return GameSessionReader.Read((address, length) => ReadBytes(handle, address, length), identity.ModuleBase, layout); }
        finally { CloseHandle(handle); }
    }

    internal static GameSessionObservation RequirePlayingSession(GameIdentity identity)
    {
        GameSessionObservation session = ReadSession(identity);
        if (!session.IsPlaying) throw new GameSessionUnavailableException(session);
        return session;
    }
}
