namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static GameSessionObservation ReadSession(GameIdentity identity)
    {
        if (!identity.Verified || !identity.ExeHash.Equals(VerifiedExeHash, StringComparison.OrdinalIgnoreCase))
            return new(GameSessionPhase.Unknown, 0, 0, [], "Unverified executable: session layout is not known");
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return GameSessionReader.Read((address, length) => ReadBytes(handle, address, length), identity.ModuleBase); }
        finally { CloseHandle(handle); }
    }

    internal static GameSessionObservation RequirePlayingSession(GameIdentity identity)
    {
        GameSessionObservation session = ReadSession(identity);
        if (!session.IsPlaying) throw new GameSessionUnavailableException(session);
        return session;
    }
}
