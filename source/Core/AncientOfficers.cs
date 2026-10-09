namespace San14Tenkazu;

// 고대무장: officers 2001~2088, of the eras before and after the Three Kingdoms, whom a new game may bring in (신무장
// 등장). Whether one may appear is the game's question 0x2CA010 (read 2026-10-03): 36 wait for their bit in the play
// record, which a tutorial, a scenario or 전기제패 sets once cleared; five come with a DLC and appear when the game
// has it; the other 47 always may. The bits are the player's, not a save's: the game keeps them in prdataN.s14 and
// writes them out at its next save or clear, and every new game offers whom they let in.
internal static class AncientOfficers
{
    internal const int First = 2001, Last = 2088;
    internal const int FirstWithDlc = 2078, LastWithDlc = 2082;

    internal static bool IsAncient(int id) => id is >= First and <= Last;

    // The officer's bit in the play record, null for one who does not wait for one (0x3EB430): of 2003~2018 and
    // 2028~2088 those the game's two masks pick, at ID − 2001, but 2068~2077 at ID − 2023 and 2083~2088 at ID − 2028.
    internal static int? Bit(int id)
    {
        bool waits = id is >= 2003 and <= 2018 && (0xFBFF >> (id - 2003) & 1) != 0
            || id is >= 2028 and <= 2088 && (0x1F83_FF00_0002_2019UL >> (id - 2028) & 1) != 0;
        if (!waits) return null;
        return id switch
        {
            >= 2068 and <= 2077 => id - 2023,
            >= 2083 and <= 2088 => id - 2028,
            _ => id - 2001
        };
    }

    // 사카모토 료마 and the four after him come with a DLC (0x36EC90); no bit lets them in.
    internal static bool ComesWithDlc(int id) => id is >= FirstWithDlc and <= LastWithDlc;

    internal enum Access { Always, Unlocked, Locked, DlcHeld, DlcMissing, Dlc }

    // Whether the officer may appear now: by his bit, by his DLC (Dlc when the DLC was not read), or always.
    internal static Access AccessOf(int id, ulong unlocks, IReadOnlySet<int>? dlcOfficers) =>
        Bit(id) is int bit ? ((unlocks >> bit & 1) != 0 ? Access.Unlocked : Access.Locked)
        : !ComesWithDlc(id) ? Access.Always
        : dlcOfficers is null ? Access.Dlc
        : dlcOfficers.Contains(id) ? Access.DlcHeld : Access.DlcMissing;

    // What sets an officer's bit, as the game does it. The tutorials give two or three each (0x5148B0 with the table
    // read by 0x4C8B70), the scenarios 1~8 one each when cleared (0x44E400, 0x3F3520), an S rank in any 전기 이세민
    // (0x1D2DB0). The other 전기 give theirs from the 전기's own data, not the exe; those are as the game's wiki lists
    // them (wikiwiki.jp/sangokushi14, 戦記制覇モード).
    internal abstract record Source;
    internal sealed record Tutorial(int Number) : Source;
    internal sealed record Scenario(int Number) : Source;
    internal sealed record Chronicle(int Officer) : Source;
    internal sealed record ChronicleS : Source;

    private static readonly int[][] TutorialRewards =
    [
        [2004, 2006, 2018], [2031, 2032, 2041], [2014, 2015, 2028], [2003, 2011, 2045],
        [2073, 2069], [2076, 2075], [2083, 2085, 2086], [2084, 2087, 2088]
    ];
    private static readonly int[] ScenarioRewards = [2007, 2005, 2008, 2009, 2012, 2017, 2010, 2016];
    private static readonly int[] ChronicleRewards = [2070, 2071, 2072, 2074, 2077];
    internal const int ChronicleSReward = 2068;

    internal static Source? SourceOf(int id)
    {
        for (int t = 0; t < TutorialRewards.Length; t++)
            if (TutorialRewards[t].Contains(id)) return new Tutorial(t + 1);
        int scenario = Array.IndexOf(ScenarioRewards, id);
        if (scenario >= 0) return new Scenario(scenario + 1);
        if (ChronicleRewards.Contains(id)) return new Chronicle(id);
        return id == ChronicleSReward ? new ChronicleS() : null;
    }

    // How the source reads in the list: "튜토리얼 3 클리어", "관도대전 클리어", "전기제패 적벽대전 클리어".
    internal const string ScenarioTable = "Scenarios", ChronicleTable = "Chronicles";

    internal static string Describe(Source? source) => source switch
    {
        Tutorial t => L.Ancient.Tutorial(t.Number),
        Scenario s => L.Ancient.Scenario(L.Table(ScenarioTable, s.Number)),
        Chronicle c => L.Ancient.Chronicle(L.Table(ChronicleTable, c.Officer)),
        ChronicleS => L.Ancient.ChronicleS,
        _ => ""
    };

    internal static string Condition(int id) => ComesWithDlc(id) ? L.Ancient.WithDlc : Describe(SourceOf(id));

    internal static string AccessName(Access access) => access switch
    {
        Access.Unlocked => L.Ancient.Unlocked,
        Access.Locked => L.Ancient.Locked,
        Access.DlcHeld => L.Ancient.DlcHeld,
        Access.DlcMissing => L.Ancient.DlcMissing,
        Access.Dlc => L.Ancient.Dlc,
        _ => L.Ancient.Always
    };

    // The 고대무장 in the game's officer table, by ID.
    internal static IEnumerable<OfficerRecord> In(GameSnapshot data) => data.Officers.Where(o => IsAncient(o.Id)).OrderBy(o => o.Id);

    // Lets the officers who wait for a bit appear: their bits set in the play record (with its change mark,
    // GameMemory.Apply), the bits already set and the rest of the record left as they are.
    internal static EditPlan Unlock(GameSnapshot data, IEnumerable<int> ids)
    {
        if (data.Play is not { } play) return EditPlan.Refuse(L.Ancient.NoPlayRecord);
        ulong unlocks = play.Unlocks;
        var names = new List<string>();
        foreach (int id in ids.Distinct().Order())
        {
            if (Bit(id) is not int bit || (unlocks >> bit & 1) != 0) continue;
            unlocks |= 1UL << bit;
            names.Add(data.OfficerById.TryGetValue(id, out OfficerRecord? officer) && officer.Name.Length > 0 ? officer.Name : L.Terms.OfficerNumber(id));
        }
        if (names.Count == 0) return new EditPlan([], L.Ancient.NothingLocked);
        var changes = new List<FieldChange>();
        foreach ((FieldDef field, int shift) in new[] { (PlayRecordFields.Unlocks1, 0), (PlayRecordFields.Unlocks2, 32) })
        {
            long word = unchecked((int)(uint)(unlocks >> shift));
            if (word != play[field]) changes.Add(new FieldChange(field, word));
        }
        return new EditPlan([new RecordEdit(RecordKind.PlayRecord, play.Address, play.Data, L.Ancient.Label, changes)],
            L.Ancient.UnlockSummary(names.Count, string.Join(", ", names)));
    }
}
