#if LOCAL_DEVELOPER
using System.Text.Json;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private bool CanRunProfiledAi => !developerChanging && !busy && !refreshing && !HasPendingEdits &&
        !RulesPending && !environmentChanging && layout is not null &&
        game is { } identity && AiBuildProfile.IsNovember(identity) &&
        snapshot is { Pace: GamePace.Paused, PlayerForce: > 0, Session.IsPlaying: true } &&
        (environmentController is null || environmentController.Status is { Phase: EnvironmentRules.EnvironmentPhase.Off, Restored: true }) &&
        seasonController?.Status is { Phase: Seasons.SeasonPhase.Off, OriginalRestored: true, Requested: false };

    private Task RunProfiledAiEdit() => RunProfiledAiDiagnostic(extension: false);
    private Task RunProfiledAiExtension() => RunProfiledAiDiagnostic(extension: true);

    private async Task RunProfiledAiDiagnostic(bool extension)
    {
        if (!CanRunProfiledAi || developerMenu is not { } window || game is not { } identity ||
            layout is not { } found || snapshot is not { } expected ||
            !Confirm(extension ? L.DeveloperUi.ProfiledAiExtensionConfirm : L.DeveloperUi.ProfiledAiConfirm, defaultNo: true) ||
            !CanRunProfiledAi || game != identity || layout is not { } current || !current.SameStorage(found))
            return;
        developerChanging = true;
        SetBusy(true, scanning: false);
        UpdateDeveloperControls();
        string? path = null;
        try
        {
            string directory = Path.Combine(seasonStorageRoot, "compatibility");
            Directory.CreateDirectory(directory);
            string prefix = extension ? "profiled-ai-extension" : "profiled-ai";
            path = Path.Combine(directory, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
            using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            void Record(object row) => output.WriteLine(JsonSerializer.Serialize(row));
            try
            {
                if (extension)
                {
                    var trees = LoadAiTrees(quiet: true) ?? throw new InvalidOperationException(L.Ai.TreeChanged);
                    string stamp = aiStamp;
                    await Task.Run(() => GameMemory.RoundTripNovemberAiExtension(identity, found, trees, stamp, Record));
                    window.AppendCompatibility(L.DeveloperUi.ProfiledAiExtensionRestored + " " + path);
                }
                else
                {
                    int passed = await Task.Run(() => GameMemory.RoundTripProfiledAi(identity, found, expected, Record));
                    window.AppendCompatibility(L.DeveloperUi.ProfiledAiResult(passed) + " " + path);
                }
            }
            catch (Exception error)
            {
                Record(new { stage = "failed", error = error.ToString() });
                throw;
            }
        }
        catch (Exception error)
        {
            window.AppendCompatibility(L.DeveloperUi.ExecutionFailed + error.Message + (path is null ? "" : " · " + path));
            AddLog($"Profiled AI edits: {error}; journal={path}");
        }
        finally
        {
            developerChanging = false;
            SetBusy(false, scanning: false);
            UpdateDeveloperControls();
        }
    }
}
#endif
