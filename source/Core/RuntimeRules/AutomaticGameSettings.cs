namespace San14Tenkazu;

internal sealed record AutomaticGameAccess(Func<GameIdentity?> Find, Func<GameIdentity, bool> Same,
    Func<GameIdentity, RuntimeRuleSettings?> ReadRules, Func<GameIdentity, bool> DataReady,
    Func<GameIdentity, RuntimeRuleSettings, string, bool> Prepare)
{
    internal static readonly AutomaticGameAccess Live = new(
        () => GameMemory.IsRunning() ? GameMemory.FindGame() : null, GameMemory.IsSameGame,
        GameMemory.ReadRuntimeRules, id => GameMemory.TryReadCurrentLayout(id) is not null,
        GameMemory.TryPrepareRuntimeRulesAutomatically);
}

internal enum AutomaticGamePhase { Absent, Starting, RulesApplied, Connect, Unsupported }
internal sealed record AutomaticGameObservation(GameIdentity? Identity, AutomaticGamePhase Phase);

// Called serially. Cache EXE verification per process, never per polling interval.
// Readiness is an observed table state, not proof that no load has previously occurred.
internal sealed class AutomaticGameSettings(AutomaticGameAccess access)
{
    private GameIdentity? observed;
    internal GameIdentity? Observed => observed;
    private RuntimeRulePreference? prepared;
    internal AutomaticGameObservation Poll(RuntimeRulePreference? preference, Func<GameIdentity, string> receipt,
        Func<bool> intentCurrent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (observed is not { } cached || !access.Same(cached))
        { observed = access.Find(); prepared = null; }
        if (observed is not { } identity) return new(null, AutomaticGamePhase.Absent);
        if (!identity.Verified) return new(identity, AutomaticGamePhase.Unsupported);
        preference?.CheckBuild(identity.ExeHash);
        if (access.DataReady(identity)) return new(identity, AutomaticGamePhase.Connect);
        if (preference is null) return new(identity, AutomaticGamePhase.Starting);
        var rules = access.ReadRules(identity);
        if (rules is null) return new(identity, AutomaticGamePhase.Starting);
        if (rules == preference.Settings) { prepared = preference; return new(identity, AutomaticGamePhase.RulesApplied); }
        if (prepared == preference) throw new InvalidDataException(L.Preparation.ChangedRules);
        // Only an original startup state is eligible for code-only preparation. A loaded
        // world uses the ordinary paused transaction, including experience conversion.
        if (rules != RuntimeRuleSettings.Original && (prepared is null || rules != prepared.Settings))
            throw new InvalidDataException(L.Preparation.ChangedRules);
        cancellationToken.ThrowIfCancellationRequested();
        if (!intentCurrent()) throw new InvalidOperationException(L.Rules.ProfileMoved);
        if (!access.Same(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        if (!access.Prepare(identity, preference.Settings, receipt(identity)))
            return new(identity, AutomaticGamePhase.Connect);
        if (!access.Same(identity) || access.ReadRules(identity) != preference.Settings)
            throw new InvalidDataException(L.Preparation.VerificationFailed);
        prepared = preference;
        return new(identity, AutomaticGamePhase.RulesApplied);
    }
}
