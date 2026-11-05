#if LOCAL_DEVELOPER
using System.Text.Json;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private bool CanRunProfiledRuleEdit => !developerChanging && !busy && !refreshing && !rulePolling &&
        !HasPendingEdits && !RulesPending && layout is not null &&
        game is { DataProfile.RuleGuards.IsDefaultOrEmpty: false } &&
        snapshot is { Pace: GamePace.Paused, Date: not null, PlayerForce: > 0, Session.IsPlaying: true };

    private async Task RunProfiledRuleEdit()
    {
        if (!CanRunProfiledRuleEdit || developerMenu is not { } window || game is not { } identity ||
            layout is not { } found || snapshot is not { } expected ||
            !Confirm(L.DeveloperUi.ProfiledRuleConfirm, defaultNo: true) ||
            !CanRunProfiledRuleEdit || game != identity || layout is not { } current || !current.SameStorage(found))
            return;

        developerChanging = true;
        SetBusy(true, scanning: false);
        UpdateDeveloperControls();
        string? path = null;
        try
        {
            string directory = Path.Combine(seasonStorageRoot, "compatibility");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"profiled-rules-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
            using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            void Record(object row) => output.WriteLine(JsonSerializer.Serialize(row));
            try
            {
                int passed = await Task.Run(() => GameMemory.RoundTripProfiledRules(identity, found, expected, Record));
                window.AppendCompatibility(L.DeveloperUi.ProfiledRuleResult(passed) + " " + path);
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
            AddLog($"Profiled rule edits: {error}; journal={path}");
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
