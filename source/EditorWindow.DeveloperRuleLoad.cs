#if LOCAL_DEVELOPER
using System.Text.Json;

namespace San14Tenkazu;

internal sealed record LoadedRuleValue(RuleKey Key, int File, int Game);
internal sealed record LoadedRulesReport(GameClock Clock, string FileSha256, int Compared, LoadedRuleValue[] Differences,
    string[] UnidentifiedRows, string[] UnavailableTables);

internal static class LoadedRuleComparison
{
    internal static LoadedRulesReport Capture(Func<FormationFileData> readFile, Func<RuleReadState> readGame,
        Func<GameClock> pausedClock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GameClock clock = pausedClock();
        var firstFile = readFile();
        Guard();
        var first = readGame();
        if (first.Formation.Records.Any(r => !r.MirrorsAgree) || first.Tactic?.Records.Any(r => !r.MirrorsAgree) == true
            || first.Policy?.Records.Any(r => !r.MirrorsAgree) == true) throw new InvalidOperationException(L.Rules.Stale);
        Guard();
        var file = readFile();
        Guard();
        var second = readGame();
        Guard();
        if (firstFile.Sha256 != file.Sha256 || !first.SameValues(second))
            throw new InvalidOperationException(L.Rules.Stale);
        var differences = new List<LoadedRuleValue>();
        var missing = new List<string>();
        var unavailable = new List<string>();
        int compared = 0;
        if (file.Rows.Length == 0) unavailable.Add("formation");
        else foreach (var r in second.Formation.Records)
        {
            var row = file.Rows[r.Id - 1];
            if (!r.MirrorsAgree) throw new InvalidOperationException(L.Rules.Stale);
            if (row.NameBytes != Convert.ToHexString(r.Data, 0x10, 10) || row.Kind != r.Data[0x7e])
            { missing.Add($"formation/{r.Id}"); continue; }
            foreach (var f in FormationRules.Fields) Observe(FormationRules.Key(r.Id, f), row.Values[f.Id], r.Value(f));
        }
        foreach (var layout in FileRuleLayout.All)
        {
            if (file.Tables == null || !file.Tables.TryGetValue(layout.Table, out var rows))
            { unavailable.Add(layout.Table); continue; }
            if (layout.Table == "tactic")
            {
                if (second.Tactic is null) { unavailable.Add(layout.Table); continue; }
                foreach (var r in second.Tactic.Records)
                {
                    var row = rows[r.Id];
                    if (!r.MirrorsAgree) throw new InvalidOperationException(L.Rules.Stale);
                    if (row.NameBytes != Convert.ToHexString(r.Data, 0x10, layout.NameSize) || row.Kind != r.Data[0x40])
                    { missing.Add($"tactic/{r.Id}"); continue; }
                    for (int i = 0; i < TacticRules.Fields.Length; i++)
                    {
                        var f = TacticRules.Fields[i];
                        Observe(TacticRules.Key(r.Id, f), int.Parse(row.Values[i]), r.Value(f));
                    }
                }
            }
            else
            {
                if (second.Policy is null) { unavailable.Add(layout.Table); continue; }
                foreach (var r in second.Policy.Records.Where(r => r.Table == layout.Table))
                {
                    var row = rows[r.Id];
                    if (!r.MirrorsAgree) throw new InvalidOperationException(L.Rules.Stale);
                    if (row.NameBytes != Convert.ToHexString(r.Data, 0x10, layout.NameSize))
                    { missing.Add($"{layout.Table}/{r.Id}"); continue; }
                    foreach (var f in PolicyRules.Fields(layout.Table))
                        Observe(PolicyRules.Key(r.Id, f), int.Parse(row.Values[f.Slot]), r.Value(f));
                }
            }
        }
        Guard();
        return new(clock, file.Sha256, compared, differences.ToArray(), missing.ToArray(), unavailable.ToArray());

        void Guard()
        {
            token.ThrowIfCancellationRequested();
            if (pausedClock() != clock) throw new InvalidOperationException(L.Rules.Stale);
        }
        void Observe(RuleKey key, int disk, int game)
        {
            compared++;
            if (disk != game) differences.Add(new(key, disk, game));
        }
    }
}

internal sealed partial class EditorWindow
{
    private bool CanReadLoadedRules => !developerChanging && !busy && !refreshing && !rulePolling && !runtimeRulesChecking
        && !environmentChecking && !seasonStarting && !seasonStopping && game is { } identity && SupportsRuleTables(identity) && GameMemory.SupportsCalendar(identity)
        && layout != null && snapshot is { Pace: GamePace.Paused, Session.IsPlaying: true };

    private async Task ReadLoadedRules()
    {
        if (!CanReadLoadedRules || game is not { } identity || layout is not { } expected || developerMenu is not { } window) return;
        developerChanging = true;
        using var cancellation = new CancellationTokenSource();
        compatibilityCancellation = cancellation;
        SetBusy(true, false);
        UpdateDeveloperControls();
        try
        {
            string? exe = GameMemory.ExecutablePath(identity);
            if (exe is null) throw new IOException(L.Errors.ProcessChanged);
            string file = Path.Combine(Path.GetDirectoryName(exe)!, FormationFileData.RelativePath);
            GameClock Clock()
            {
                if (!GameMemory.IsSameGame(identity) || GameMemory.ReadPace(identity, expected) != GamePace.Paused)
                    throw new InvalidOperationException(L.Rules.Stale);
                return GameMemory.ReadCalendar(identity, expected);
            }
            RuleReadState Read()
            {
                var formation = GameMemory.ReadFormations(identity);
                var tactic = GameMemory.ReadTactics(identity);
                var policy = GameMemory.ReadPolicies(identity);
                if (formation.State != expected.State || formation.Root != tactic.Root || formation.Root != policy.Root
                    || formation.State != tactic.State || formation.State != policy.State) throw new InvalidOperationException(L.Rules.Stale);
                return new(formation, tactic, policy);
            }
            var result = await Task.Run(() => LoadedRuleComparison.Capture(() => FormationFileData.Read(file), Read, Clock, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (game != identity || layout is null || !layout.SameStorage(expected)) throw new InvalidOperationException(L.Rules.Stale);
            string directory = Path.Combine(seasonStorageRoot, "compatibility", "diagnostics");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"loaded-rules-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, identity.ProcessId, identity.ExeHash,
                file, result, scope = "two paused reads of editable rule fields", gameWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            window.AppendCompatibility(L.DeveloperUi.RuleLoadResult(result.Compared, result.Differences.Length,
                result.UnidentifiedRows.Length, result.UnavailableTables.Length, path));
        }
        catch (Exception ex) { window.AppendCompatibility(ex is OperationCanceledException ? L.DeveloperUi.CompatibilityCancelled : ex.Message); }
        finally
        {
            compatibilityCancellation = null;
            developerChanging = false;
            SetBusy(false, false);
            UpdateDeveloperControls();
        }
    }
}
#endif
