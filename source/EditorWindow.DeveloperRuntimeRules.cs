#if LOCAL_DEVELOPER
using System.Text.Json;
using W = System.Windows;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private bool CanRunProfiledRuntimeRules => CanRunProfiledAi && !runtimeRulesChecking && !runtimeRulesChanging &&
        runtimeRulesWindow?.HasPendingChanges != true && game is { } identity && RuntimeRuleBuildProfile.Find(identity) is not null &&
        snapshot is { OriginalAbilityTableVerified: true } data && data.RuntimeRules == RuntimeRuleSettings.Original;

    private async Task RunProfiledRuntimeRules()
    {
        if (!CanRunProfiledRuntimeRules || developerMenu is not { } window || game is not { } identity ||
            layout is not { } found || snapshot is not { } expected) return;
        developerChanging = true;
        SetBusy(true, scanning: false);
        UpdateDeveloperControls();
        string? path = null;
        try
        {
            if (W.MessageBox.Show(window, L.DeveloperUi.ProfiledRuntimeRulesConfirm, AppTitle,
                W.MessageBoxButton.YesNo, W.MessageBoxImage.Question, W.MessageBoxResult.No) != W.MessageBoxResult.Yes)
                return;
            if (game != identity || layout is not { } current || !current.SameStorage(found))
                throw new InvalidOperationException(L.Errors.StateMoved);
            string directory = Path.Combine(seasonStorageRoot, "compatibility");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"profiled-runtime-rules-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
            using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            void Record(object row) => output.WriteLine(JsonSerializer.Serialize(row));
            try
            {
                window.AppendCompatibility(L.DeveloperUi.ProfiledRuntimeRulesStarted + " " + path);
                int passed = await Task.Run(() => GameMemory.RoundTripProfiledRuntimeRules(identity, found, expected, Record));
                window.AppendCompatibility(L.DeveloperUi.ProfiledRuntimeRulesRestored(passed) + " " + path);
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
            AddLog($"Profiled runtime rules: {error}; journal={path}");
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
