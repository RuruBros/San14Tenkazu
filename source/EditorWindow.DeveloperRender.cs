#if LOCAL_DEVELOPER
using System.Text.Json;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;
using System.Windows.Threading;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private sealed record ProfiledRenderTrial(GameIdentity Game, GameLayout Layout, SeasonSnapshot Scene,
        ulong SessionState, int Hour, string Path, long Started);
    private ProfiledRenderTrial? developerRenderTrial;
    private DispatcherTimer? developerRenderTimer;
    private bool developerRenderPolling;

    private bool CanRunProfiledRenderCheck => !developerChanging && !busy && !refreshing && !environmentChanging &&
        !HasPendingEdits && !RulesPending && developerRenderTrial is null && developerNoticeDiagnostic is null &&
        game is { } identity && GameBuildProfiles.FindNative(identity) == GameBuildProfiles.NovemberNative &&
        layout is not null && snapshot is { Pace: GamePace.Paused, Date: not null, Session.IsPlaying: true } &&
        (environmentController is null || environmentController.Status is { Phase: EnvironmentPhase.Off, Restored: true }) &&
        seasonController?.Status is { Phase: SeasonPhase.Off, OriginalRestored: true, Requested: false };

    private async Task RunProfiledRenderCheck()
    {
        if (!CanRunProfiledRenderCheck || developerMenu is not { } window || game is not { } identity ||
            layout is not { } found || snapshot is not { } expected ||
            !Confirm(L.DeveloperUi.ProfiledRenderConfirm, defaultNo: true) || !CanRunProfiledRenderCheck ||
            game != identity || layout is not { } current || !current.SameStorage(found)) return;
        developerChanging = true;
        UpdateDeveloperControls();
        try
        {
            if (expected.Session is not { Entries.Length: >= 3 } session)
                throw new InvalidOperationException(L.Errors.StateMoved);
            ulong sessionState = session.Entries[2].Address;
            var before = await Task.Run(() =>
            {
                if (GameMemory.ReadPace(identity, found) != GamePace.Paused)
                    throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
                var loaded = GameMemory.ReadSession(identity);
                if (!loaded.IsPlaying || loaded.Entries[2].Address != sessionState)
                    throw new InvalidOperationException(L.Errors.StateMoved);
                return GameMemory.ReadSeasonSnapshot(identity);
            });
            if (game != identity || expected.Date != (before.Date.Year, before.Date.Month, before.Date.Day))
                throw new InvalidOperationException(L.Errors.StateMoved);
            string directory = Path.Combine(seasonStorageRoot, "compatibility");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"profiled-render-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
            var trial = new ProfiledRenderTrial(identity, found, before, sessionState, expected.Hour, path, System.Diagnostics.Stopwatch.GetTimestamp());
            developerRenderTrial = trial;
            WriteProfiledRender("before", new { identity.ProcessId, identity.ExeHash, before.Date.Year, before.Date.Month, before.Date.Day, expected.Hour,
                before.Identity.World, before.Identity.Scene, before.Identity.Driver });
            const EnvironmentEffects visuals = EnvironmentEffects.RainVisual | EnvironmentEffects.SnowVisual;
            var settings = environmentSettings with { Seasonal = true, Effects = visuals };
            await ApplySavedEnvironmentAsync(settings);
            if (seasonController?.Status is not { Phase: SeasonPhase.On, Scope: SeasonScope.Full, Requested: true } render ||
                environmentController?.Status is not { Phase: EnvironmentPhase.On, Actual: visuals, Receipt: { } environment } ||
                render.Date != before.Date || System.Diagnostics.Stopwatch.GetElapsedTime(trial.Started).TotalSeconds >= 120)
                throw new InvalidOperationException(L.DeveloperUi.GameFrameRejected);
            WriteProfiledRender("on", new { before.Date.Year, before.Date.Month, before.Date.Day, render.Generation, environment.Frames,
                environment.Weather, environment.LastEvent, visualOnly = true });
            developerRenderTimer = new(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                async (_, _) => await PollProfiledRenderCheck(), Dispatcher);
            developerRenderTimer.Start();
            window.Append(L.DeveloperUi.ProfiledRenderOn + " " + path);
        }
        catch (Exception error)
        {
            window.Append(L.DeveloperUi.ExecutionFailed + error.Message);
            WriteProfiledRender("failed", new { error = error.ToString() });
            await StopProfiledRenderCheck("failed");
        }
        finally
        {
            developerChanging = false;
            UpdateDeveloperControls();
        }
    }

    private async Task PollProfiledRenderCheck()
    {
        if (developerRenderPolling || developerChanging || developerRenderTrial is not { } trial) return;
        developerRenderPolling = true;
        try
        {
            string? reason = null;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(trial.Started).TotalSeconds >= 120) reason = "deadline";
            else if (game != trial.Game || layout is not { } current || !current.SameStorage(trial.Layout) ||
                snapshot is not { Pace: GamePace.Paused, Session: { IsPlaying: true, Entries.Length: >= 3 } session } state ||
                state.Hour != trial.Hour || session.Entries[2].Address != trial.SessionState) reason = "session-or-clock";
            else
            {
                try
                {
                    var now = await Task.Run(() => GameMemory.ReadSeasonSnapshot(trial.Game));
                    if (now != trial.Scene) reason = "date-or-scene";
                }
                catch (Exception error) { reason = error.Message; }
            }
            if (reason is not null && developerRenderTrial == trial && !developerChanging)
            {
                developerChanging = true;
                try { await StopProfiledRenderCheck(reason); }
                finally { developerChanging = false; UpdateDeveloperControls(); }
            }
        }
        finally { developerRenderPolling = false; }
    }

    private async Task<bool> StopProfiledRenderCheck(string reason)
    {
        if (developerRenderTrial is null) return true;
        developerRenderTimer?.Stop();
        bool environment = await StopEnvironmentAsync(false);
        bool season = await StopSeasonsAsync(false);
        if (!environment || !season)
        {
            WriteProfiledRender("recovery-unconfirmed", new { reason, environment, season });
            developerMenu?.Append(L.Seasons.Unrestored);
            return false;
        }
        CompleteProfiledRenderCheck(reason);
        return true;
    }

    private void CompleteProfiledRenderCheck(string reason)
    {
        if (developerRenderTrial is not { } trial) return;
        developerRenderTimer?.Stop();
        developerRenderTimer = null;
        WriteProfiledRender("original-restored", new { reason, environmentRestored = true, rendererRestored = true, screenVerified = false });
        developerRenderTrial = null;
        environmentApplied = null;
        developerMenu?.Append(L.DeveloperUi.ProfiledRenderOff + " " + trial.Path);
    }

    private void WriteProfiledRender(string stage, object data)
    {
        if (developerRenderTrial is not { } trial) return;
        try { File.AppendAllText(trial.Path, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage, data }) + System.Environment.NewLine); }
        catch (Exception error) { AddLog(L.DeveloperUi.DiagnosticLogFailed + error.Message); }
    }
}
#endif
