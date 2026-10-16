using System.Diagnostics;

namespace San14Tenkazu;

internal sealed record GamePreparationAccess(
    Func<bool> IsRunning, Action Launch, Func<GameIdentity?> Find,
    Func<GameIdentity, RuntimeRuleSettings?> Read,
    Action<GameIdentity, RuntimeRuleSettings, string> Prepare)
{
    internal static readonly GamePreparationAccess Live = new(GameMemory.IsRunning,
        () => Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamInstallation.AppId) { UseShellExecute = true }),
        () => GameMemory.IsRunning() ? GameMemory.FindGame() : null,
        GameMemory.ReadRuntimeRules, GameMemory.PrepareRuntimeRules);
}

// No save is opened by this flow. The user waits for verified preparation, then uses the game's Load menu.
internal sealed class GamePreparation(GamePreparationAccess access)
{
    internal async Task<GameIdentity> RunAsync(RuntimeRulePreference preference, Func<GameIdentity, string> receipt,
        Func<bool> intentUnchanged, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? wait = null, int attempts = 240)
    {
        preference.CheckBuild(GameBuildProfiles.Current.ExeSha256);
        cancellationToken.ThrowIfCancellationRequested();
        if (access.IsRunning()) throw new InvalidOperationException(L.Preparation.AlreadyRunning);
        if (!intentUnchanged()) throw new InvalidOperationException(L.Rules.ProfileMoved);
        DateTime started = DateTime.UtcNow;
        access.Launch();
        GameIdentity? observed = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!intentUnchanged()) throw new InvalidOperationException(L.Rules.ProfileMoved);
            GameIdentity? found = access.Find();
            if (found is { } identity)
            {
                if (identity.StartTimeUtc < started || observed is { } prior && prior != identity)
                    throw new InvalidOperationException(L.Errors.ProcessChanged);
                if (!identity.Verified) throw new InvalidOperationException(L.Preparation.Unsupported);
                preference.CheckBuild(identity.ExeHash);
                observed = identity;
                RuntimeRuleSettings? state = access.Read(identity);
                // Startup can still be unpacking code. Only the complete original guard set is accepted.
                if (state is not null)
                {
                    if (state != RuntimeRuleSettings.Original) throw new InvalidOperationException(L.Preparation.ChangedRules);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!intentUnchanged()) throw new InvalidOperationException(L.Rules.ProfileMoved);
                    access.Prepare(identity, preference.Settings, receipt(identity));
                    if (access.Find() != identity || access.Read(identity) != preference.Settings)
                        throw new InvalidOperationException(L.Preparation.VerificationFailed);
                    return identity;
                }
            }
            else if (observed is not null) throw new InvalidOperationException(L.Errors.ProcessChanged);
            await (wait?.Invoke(cancellationToken) ?? Task.Delay(500, cancellationToken));
        }
        throw new TimeoutException(L.Preparation.Timeout);
    }
}
