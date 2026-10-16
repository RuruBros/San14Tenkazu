#if LOCAL_DEVELOPER
using System.Text.Json;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private async Task ReadDeveloperTraitList()
    {
        if (developerChanging || busy || developerMenu is not { } window) return;
        var identity = game;
        TraitListEvidence? evidence = null;
        string? error = null;
        developerChanging = true; SetBusy(true, false); UpdateDeveloperControls();
        window.AppendTraitDiagnostic(L.DeveloperUi.TraitReadStarted);
        try
        {
            try
            {
                if (identity is not { Verified: true } verified || seasonClosing || seasonDisconnecting)
                    throw new InvalidOperationException(L.DeveloperUi.VerifiedConnectionRequired);
                evidence = await Task.Run(() => GameMemory.ReadTraitListDiagnostic(verified));
                error = evidence.Error;
            }
            catch (Exception failure) { error = failure.Message; }
            string directory = Path.Combine(Program.DataFolder, "runtime-rules", "diagnostics");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"trait-list-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                await JsonSerializer.SerializeAsync(file, new { formatVersion = 1, utc = DateTime.UtcNow, readOnly = true,
                    identity?.ProcessId, identity?.StartTimeUtc, identity?.ExeHash,
                    consistent = evidence?.Consistent ?? false, error, evidence }, new JsonSerializerOptions { WriteIndented = true });
            string summary = evidence is { Consistent: true }
                ? L.DeveloperUi.TraitComparison($"{evidence.ColumnCount}", $"{evidence.Differences.Length}")
                : L.DeveloperUi.UnconfirmedTraitEvidence + error;
            window.AppendTraitDiagnostic(summary);
            if (evidence is { Consistent: true })
                foreach (var diff in evidence.Differences.Take(20))
                    window.AppendTraitDiagnostic(L.DeveloperUi.TraitDifference($"{diff.ColumnIndex}", $"{diff.RecordOffset}", $"{diff.Rva}", $"{diff.ExpectedHex}", $"{diff.ActualHex}"));
            if (evidence?.Differences.Length > 20) window.AppendTraitDiagnostic(L.DeveloperUi.RemainingDifferencesSaved);
            window.AppendTraitDiagnostic(L.DeveloperUi.DiagnosticFile + path);
            AddLog(L.DeveloperUi.TraitDiagnostic + summary + " · " + path);
        }
        catch (Exception failure)
        { window.AppendTraitDiagnostic(L.DeveloperUi.DiagnosticSaveFailed + failure.Message); AddLog(L.DeveloperUi.TraitSaveFailed + failure.Message); }
        finally { developerChanging = false; SetBusy(false, false); UpdateDeveloperControls(); }
    }
}
#endif
