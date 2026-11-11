#if LOCAL_DEVELOPER
using System.Text.Json;
using W = System.Windows;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private GameMemory.CompleteEditorPreparation? completeEditsPreparation;
    private CancellationTokenSource? completeEditsCancellation;
    private static readonly JsonSerializerOptions CompleteEditJson = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
    private bool CanPrepareCompleteEdits => CanRunProfiledFieldsEdit && !rulePolling && !runtimeRulesChecking &&
        !runtimeRulesChanging && !environmentChecking && !environmentChanging && !seasonStarting && !seasonStopping &&
        snapshot?.Session?.IsPlaying == true && completeEditsCancellation is null && Editors.All(card => card.Pending == 0) &&
        !RulesPending && runtimeRulesWindow?.HasPendingChanges != true && registeredEditor?.HasPending != true;

    private async Task PrepareCompleteEdits()
    {
        if (!CanPrepareCompleteEdits || developerMenu is not { } window || game is not { } identity || layout is not { } current) return;
        int? seededCommander = window.CompleteProvokeCommander;
        developerChanging = true; SetBusy(true, false); UpdateDeveloperControls();
        completeEditsPreparation = null;
        try
        {
            var prepared = await Task.Run(() => GameMemory.PrepareCompleteEditorTrials(identity, current));
            if (seededCommander is int commander) prepared = GameMemory.PrepareSeededProvocationTrial(prepared, commander);
            if (game != identity || layout is not { } now || !now.SameStorage(current) || HasPendingEdits)
                throw new InvalidOperationException(L.Errors.StateMoved);
            string directory = Path.Combine(seasonStorageRoot, "compatibility"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"complete-edit-targets-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(stream, GameMemory.CompleteEditorManifest(prepared), CompleteEditJson); stream.Flush(true);
            }
            completeEditsPreparation = prepared;
            window.CompleteEditsLog.Clear();
            window.AppendCompleteEdits(L.DeveloperUi.CompleteTargets(identity.ProcessId, prepared.Snapshot.Date,
                prepared.Snapshot.Hour, prepared.Plan.Trials.Length, path));
            foreach (var trial in prepared.Plan.Trials)
            {
                var descriptions = trial.Steps.SelectMany(s => s).SelectMany(e => e.Changes.Select(c =>
                    $"{e.Label}/{c.Field.Label}: {c.Field.Read(e.Expected)} → {c.Value} ({c.Field.Size} byte)"));
                window.AppendCompleteEdits(trial.Key + " · " + string.Join("; ", descriptions));
            }
            foreach (var absent in prepared.Plan.Missing) window.AppendCompleteEdits(L.DeveloperUi.CompleteMissing(absent.Key, absent.Reason));
        }
        catch (Exception error) { window.AppendCompleteEdits(L.DeveloperUi.ExecutionFailed + error.Message); AddLog(error.ToString()); }
        finally { developerChanging = false; SetBusy(false, false); UpdateDeveloperControls(); }
    }

    private async Task RunCompleteEdits()
    {
        if (!CanPrepareCompleteEdits || completeEditsPreparation is not { } prepared || developerMenu is not { } window ||
            game != prepared.Identity || layout is not { } current || !current.SameStorage(prepared.Layout)) return;
        if (W.MessageBox.Show(window, L.DeveloperUi.CompleteEditsConfirm, AppTitle, W.MessageBoxButton.YesNo,
            W.MessageBoxImage.Question, W.MessageBoxResult.No) != W.MessageBoxResult.Yes || !CanPrepareCompleteEdits) return;
        var uiEvidence = new { stage = "editor-ui", force = forceEditor.DeveloperFillState(),
            cityLoyalty = cityEditor.Officers.FillsEnabled, gateLoyalty = gateEditor.Officers.FillsEnabled, pending = HasPendingEdits };
        using var cancellation = new CancellationTokenSource(); completeEditsCancellation = cancellation;
        developerChanging = true; SetBusy(true, false); UpdateDeveloperControls();
        string? path = null;
        try
        {
            string directory = Path.Combine(seasonStorageRoot, "compatibility"); Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"complete-edit-roundtrip-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{prepared.Identity.ProcessId}-{Guid.NewGuid():N}.jsonl");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var output = new StreamWriter(stream) { AutoFlush = true };
            void Record(object row)
            {
                output.WriteLine(JsonSerializer.Serialize(row, CompleteEditJson)); stream.Flush(true);
                if (row is GameMemory.CompleteTrialEvidence evidence && evidence.Stage is "restored-byte-exact" or "recovery-failed")
                    window.Dispatcher.BeginInvoke(new Action(() => window.AppendCompleteEdits(evidence.Stage + " · " + evidence.Key)));
            }
            window.AppendCompleteEdits(L.DeveloperUi.CompleteStarted(path));
            Record(uiEvidence);
            try
            {
                var result = await Task.Run(() => GameMemory.RoundTripCompleteEditorFields(prepared, Record, cancellation.Token));
                window.AppendCompleteEdits(L.DeveloperUi.CompleteEditsResult(result.Passed, result.Missing, result.SnapshotDifferences, result.Cancelled) + " · " + path);
            }
            catch (Exception error)
            {
                try { Record(new { stage = "failed", error = error.ToString() }); } catch { /* Recovery ran before returning here. */ }
                throw;
            }
        }
        catch (Exception error)
        {
            window.AppendCompleteEdits(L.DeveloperUi.ExecutionFailed + error.Message + " · " + path);
            AddLog($"Complete editor trial failed: {error}; journal={path}");
        }
        finally
        {
            completeEditsPreparation = null; completeEditsCancellation = null;
            developerChanging = false; SetBusy(false, false); UpdateDeveloperControls();
        }
    }
}
#endif
