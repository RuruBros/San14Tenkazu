namespace San14Tenkazu;

// Which game build the editor writes to: the verified one always; another (a patch) only once the user has allowed
// writing to that build after seeing its self-checks, and then only the groups of values that passed (GameChecks).
internal sealed partial class EditorWindow
{
    // What is kept about unverified builds: whether writing was allowed, and where their tables are (KnownBuilds).
    private static readonly KnownBuilds builds = new(Program.DataFolder);
    private List<GameChecks.Check> checks = [];
    private bool writesAllowed;

    // After connecting: runs the checks and reports them; for another build, says how it was found and asks once
    // whether to write to it.
    private void TrustBuild(GameIdentity identity, GameLayout found, GameSnapshot data)
    {
        checks = GameChecks.Run(data, catalog);
        foreach (var capability in GameCapabilities.Inspect(identity))
            AddLog($"Compatibility {capability.Feature}: {capability.State}; {string.Join("; ", capability.Evidence.Select(e => e.Stage+":"+e.Detail))}");
        List<GameChecks.Check> failed = checks.Where(c => !c.Passed).ToList();
        AddLog(failed.Count == 0 ? L.Builds.ChecksPassed(checks.Count) : L.Builds.ChecksFailed(checks.Count, failed.Count));
        foreach (GameChecks.Check check in failed) AddLog("  " + L.Builds.CheckFailed(check.Group, check));
        if (identity.Verified)
        {
            writesAllowed = true;
            if (failed.Count > 0) AddLog("  " + L.Builds.VerifiedStillWrites);
            return;
        }

        GameTables t = found.Tables;
        string Offset(ulong table) => table == 0 ? L.Terms.None : $"{(table >= t.City ? "+" : "-")}0x{(table >= t.City ? table - t.City : t.City - table):X}";
        AddLog(L.Builds.Unverified(identity.ExeHash[..16]));
        AddLog("  " + L.Builds.TableOffsets(Offset(t.Force), Offset(t.Corps), Offset(t.Tile), Offset(t.County), Offset(t.Building), Offset(t.Officer), Offset(t.Unit),
            Offset(t.State), Offset(t.Country), Offset(t.Item), Offset(t.Personality), Offset(t.Tactic), Offset(t.Policy), Offset(t.Ideology), Offset(t.Office), Offset(t.Rank)));

        writesAllowed = builds.IsAllowed(identity.ExeHash);
        if (!writesAllowed)
        {
            string[] passed = checks.Select(c => c.Group).Distinct().Where(g => checks.All(c => c.Group != g || c.Passed)).ToArray();
            string[] blocked = checks.Select(c => c.Group).Distinct().Except(passed).ToArray();
            writesAllowed = Confirm(
                L.Builds.AllowWrites(passed.Length > 0 ? string.Join(", ", passed) : L.Terms.None, blocked.Length > 0 ? string.Join(", ", blocked) : L.Terms.None),
                System.Windows.MessageBoxImage.Warning, defaultNo: true);
            if (writesAllowed) Keep(() => builds.Allow(identity.ExeHash), L.Builds.AllowNotSaved);
        }
        AddLog(writesAllowed ? ("  " + L.Builds.WritesAllowed) : ("  " + L.Builds.ReadOnly));
    }

    // Why values of these groups may not be written to the game now, or null when they may.
    private string? WriteBlock(IEnumerable<string> groups) =>
        layoutRecovering || snapshot is null ? L.Connection.SessionWaiting :
        game is { } identity ? GameChecks.WriteBlock(identity.Verified, writesAllowed, checks, groups) : null;

    // Saves what is kept about a build; a save that fails is only logged (the next connection asks or searches again).
    private void Keep(Action save, string failed)
    {
        try { save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog($"{failed}: {ex.Message}"); }
    }
}
