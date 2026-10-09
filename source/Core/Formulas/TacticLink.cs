namespace San14Tenkazu.Formulas;

internal static class TacticLink
{
    // 0x16DE70/0x16DC80: expand only the original neighboring list, deduplicate, then sort (0x167350).
    internal static List<Rec> CandidateUnits(World w, Rec source)
    {
        var result = new List<Rec>();
        if (!Objects.Valid(w, source)) return result;
        void Append(Rec unit)
        {
            foreach (Rec ally in BattlePower.AlliedUnits(w, unit))
                if (!result.Contains(ally)) result.Add(ally);
        }
        Append(source);
        foreach (Rec unit in result.ToArray()) Append(unit);
        int center = w.Entry(Tables.Tiles, source.U16(0x2A), 48399).Index;
        int Ability(Rec unit) => w.OfficerById(unit.U16(0x12)) is { } person && Objects.Valid(w, person)
            ? OfficerAbility.Get(w, person, 0, OfficerAbility.All) : 0;
        return result.OrderBy(u => Map.HexDistance(center, u.U16(0x2A))).ThenByDescending(Ability).ThenBy(u => u.Index).ToList();
    }

    [Rva(0x1A4A030)] internal const int Player0Grade3 = 40;
    [Rva(0x1A4A03C)] internal const int Player0Grade4 = 50;
    [Rva(0x1A4A048)] internal const int Player0Grade5 = 80;
    [Rva(0x1A4A064)] internal const int Player1Grade3 = 30;
    [Rva(0x1A4A070)] internal const int Player1Grade4 = 40;
    [Rva(0x1A4A07C)] internal const int Player1Grade5 = 70;
    [Rva(0x1A4A098)] internal const int Player2Grade3 = 20;
    [Rva(0x1A4A0A4)] internal const int Player2Grade4 = 30;
    [Rva(0x1A4A0B0)] internal const int Player2Grade5 = 60;
    [Rva(0x1A4A0C8)] internal const int OtherGrade3 = 20;
    [Rva(0x1A4A0D4)] internal const int OtherGrade4 = 30;
    [Rva(0x1A4A0E0)] internal const int OtherGrade5 = 60;
    [Rva(0x1A48FAC)] internal const int RecordedDelayDays = 3;

    internal readonly record struct Candidate(Rec Unit, Rec Tactic, List<Rec>[] Targets);

    // 0x4982A0: challenge kind 3 excludes tactics with a fire slot.
    internal static bool Allowed(World w, Rec tactic) => !w.Flag("challenge_active") || w.Value("challenge_kind") != 3
        || TacticEffects.Kind(tactic, 0) != 17 && TacticEffects.Kind(tactic, 1) != 17;

    // 0x2C4F90: compare a recorded commander entry's date with the current date.
    internal static bool RecordedDelayReached(World w, Rec unit)
    {
        if (!Objects.Valid(w, unit)) return false;
        for (int i = 0; i < w.WorldState.U8(0x165D); i++)
            if (unit.U16(0x12) == w.WorldState.U16(0x165E + i * 2))
                return unchecked(w.WorldState.I32(0x454) - w.WorldState.I32(0x1668 + i * 4)) >= RecordedDelayDays;
        return false;
    }

    // 0x175350: iterate the caller's ordered units, use linked-count seeded rolls, and retain at most ten.
    internal static List<Candidate> Select(World w, Rec? sourceOfficer, Rec? target, int mode, IReadOnlyList<Rec> units)
    {
        var result = new List<Candidate>();
        if (!Objects.Valid(w, sourceOfficer)) return result;
        Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
        bool playerSource = Objects.Valid(w, player) && Objects.ForceOf(w, sourceOfficer!).Index == player.Index;
        int preset = Math.Min(w.Settings.I32(w.Settings.I32(0xE58) == 0 ? 0x134 : 0x28), 2);
        int[] chances = playerSource ? preset switch
        {
            0 => [w.Value("tactic_link_p0_1"), w.Value("tactic_link_p0_2"), Player0Grade3, Player0Grade4, Player0Grade5],
            1 => [w.Value("tactic_link_p1_1"), w.Value("tactic_link_p1_2"), Player1Grade3, Player1Grade4, Player1Grade5],
            _ => [w.Value("tactic_link_p2_1"), w.Value("tactic_link_p2_2"), Player2Grade3, Player2Grade4, Player2Grade5],
        } : [w.Value("tactic_link_ai_1"), w.Value("tactic_link_ai_2"), OtherGrade3, OtherGrade4, OtherGrade5];
        foreach (Rec unit in units)
        {
            if (unit.U16(0x12) == sourceOfficer!.U16(0x10)) continue;
            List<Rec> tactics = UnitTactics.Candidates(w, unit);
            if (tactics.Count == 0) continue;
            Rec? commander = w.OfficerById(unit.U16(0x12));
            if (mode == 0 && RecordedDelayReached(w, unit) && (commander is null || (commander.U16(0x15E) & 0x20) == 0)) continue;
            if (!Objects.Valid(w, commander)) continue;
            int grade = Link.Grade(w, sourceOfficer, commander!, true);
            if (grade is < 1 or > 5) continue;
            float chance = chances[grade - 1];
            if (w.Settings.I32(0xE58) == 0) chance = (float)BattlePower.Slider(w, 0x19) * chance / 100f;
            chance = Math.Min(chance, 100f);
            bool fires = chance > 0f && Wounded.Seeded(w, 10000, 1, unit.Index, result.Count, 0, 0) < Objects.Trunc(chance * 10000f / 100f);
            if (w.Flag("challenge_active") && (w.Value("challenge_flags") & 2) != 0) fires = true;
            if (!fires) continue;
            var candidates = new List<Candidate>();
            Rec source = w.Entry(Tables.Tiles, unit.U16(0x2A), 48399);
            foreach (Rec tactic in tactics)
            {
                var targets = TacticEffects.Targets(w, tactic, source, target, Objects.UnitForce(w, unit));
                if ((targets[0].Count != 0 || targets[1].Count != 0) && Allowed(w, tactic)) candidates.Add(new(unit, tactic, targets));
            }
            if (candidates.Count == 0) continue;
            int selected = Wounded.Seeded(w, candidates.Count, 1, unit.Index, result.Count, 0, 0);
            result.Add(candidates[selected]);
            if (result.Count == 10) break;
        }
        return result;
    }
}
