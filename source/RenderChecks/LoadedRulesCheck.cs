#if LOCAL_DEVELOPER
using San14Tenkazu;
using San14Tenkazu.Tests;

internal static partial class RenderChecks
{
    private static void CheckLoadedRuleComparison()
    {
        var fixture = new PolicyFixture();
        var formation = fixture.Tactics.Formations.Snapshot();
        var tactics = fixture.Tactics.Snapshot();
        var policies = fixture.Snapshot();
        var state = new RuleReadState(formation, tactics, policies);
        var rows = formation.Records.Select(r => new FormationFileRow(r.Id, Convert.ToHexString(r.Data, 0x10, 10),
            r.Data[0x7e], FormationRules.Fields.ToDictionary(f => f.Id, f => (byte)r.Value(f)))).ToArray();
        var tables = new Dictionary<string, FileRuleRow[]>();
        foreach (var layout in FileRuleLayout.All)
        {
            var records = new FileRuleRow[layout.Count];
            if (layout.Table == "tactic")
                foreach (var r in tactics.Records) records[r.Id] = new(Convert.ToHexString(r.Data, 0x10, layout.NameSize),
                    r.Data[0x40], TacticRules.Fields.Select(f => r.Value(f).ToString()).ToArray());
            else
                foreach (var r in policies.Records.Where(r => r.Table == layout.Table))
                    records[r.Id] = new(Convert.ToHexString(r.Data, 0x10, layout.NameSize), 0,
                        PolicyRules.Fields(layout.Table).Select(f => r.Value(f).ToString()).ToArray());
            tables.Add(layout.Table, records);
        }
        tables["policyeffect"][1].Values[0] = "245";
        var file = new FormationFileData("fixture", "", 1, false, rows, tables);
        var time = new GameClock(new(203, 3, 2), 0);
        int files = 0, games = 0;
        var result = LoadedRuleComparison.Capture(() => { files++; return file; }, () => { games++; return state; },
            () => time, CancellationToken.None);
        Require(files == 2 && games == 2 && result.Compared == state.Values.Count && result.Differences is [{ File: 245 }]
            && result.Differences[0].Key == new RuleKey("policyeffect", 1, "level1")
            && result.UnidentifiedRows.Length == 0 && result.UnavailableTables.Length == 0, "Four loaded rule tables compared");
        files = 0;
        bool changedFile = false, changedClock = false, cancelled = false;
        try { LoadedRuleComparison.Capture(() => ++files == 1 ? file : file with { Sha256 = "changed" },
            () => state, () => time, CancellationToken.None); }
        catch (InvalidOperationException) { changedFile = true; }
        int clocks = 0;
        try { LoadedRuleComparison.Capture(() => file, () => state, () => ++clocks == 1 ? time : time with { Hour = 1 }, CancellationToken.None); }
        catch (InvalidOperationException) { changedClock = true; }
        try { LoadedRuleComparison.Capture(() => file, () => state, () => time, new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(changedFile && changedClock && cancelled && fixture.Writes + fixture.Tactics.Writes + fixture.Tactics.Formations.Writes == 0,
            "Changing disk, clock or cancellation aborts without writes");
        Console.WriteLine("PASS loaded rule comparison, stable reads, cancellation and file/clock changes; gameAccess=false");
    }
}
#endif
