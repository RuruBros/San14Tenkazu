namespace San14Tenkazu;

// What the game does with a 개성's condition and effect kinds, read from its code on 2026-10-03 (the reading, and the
// commands that repeat it: source/Reverse/notes/personality.md, personality.py). In battle the game runs, for each
// unit's commander and each of his 개성, the handler its condition picks (0x27DF10); every unit the handler names
// that also meets the second condition (+0xAC) gets the effects: his own unit has the 개성 lit, other units hold it
// among those they received. How an effect kind is read decides which of those it reaches, and some kinds are read
// from the officer's 개성 whatever their condition. The duration (+0xB4) comes from the game's files and the save does
// not keep it, so it is not changed: the conditions and kinds offered are the ones that work with it.
internal static class PersonalityRules
{
    // How the game reads an effect kind.
    internal enum Reach
    {
        Unknown,        // nothing found that reads it: not offered
        Unit,           // summed for a unit (0x26A2C0): its commander's own 개성 lit or always on, and those received
        Received,       // only among those received from other units (0x2E1800): reaches others only
        Own,            // the commander's own 개성, lit or always on (0x2C37F0): his own unit only
        OwnLit,         // the commander's own 개성 while lit (0x2B5840): needs a condition naming his own unit
        Officer,        // the officer's 개성 whatever their condition (0x2C5460, 0x2B2DA0, ...): always, for him
        OfficerAlways,  // only the officer's 개성 that are always on (25 화신, 27 함정 피해, 56 등갑)
        Guard,          // 53, 54: the officer's own when always on, others through condition 9 (0x258EE0, 0x27E420)
        Event,          // 79: the mark of the 괴멸·격파 events, which add the values to morale; nothing reads the kind
    }

    internal const int LastKind = 0x7E;   // the sums take kinds below 0x7F
    internal const int EventMorale = 79;

    private static readonly HashSet<int> UnitKinds = [4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 19, 20, 26, 28, 31, 47, 62, 80, 82, 94, 95, 97, 98, 99, 103, 116, 118];
    private static readonly HashSet<int> OwnKinds = [21, 22, 24, 55];
    private static readonly HashSet<int> OfficerKinds =
    [
        1, 2, 3, 14, 15, 17, 18, 29, 30, 32, 33, 34, 35, 36, 37, 38, 39, 43, 44, 45, 46, 48, 49, 50, 57, 63, 64, 65, 66, 67, 68, 70, 71,
        72, 74, 75, 76, 77, 81, 83, 84, 85, 86, 87, 96, 100, 101, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 119, 120, 122, 123,
        124, 125, 126
    ];

    internal static Reach ReachOf(int kind) => kind switch
    {
        _ when UnitKinds.Contains(kind) => Reach.Unit,
        117 => Reach.Received,
        _ when OwnKinds.Contains(kind) => Reach.Own,
        51 => Reach.OwnLit,
        _ when OfficerKinds.Contains(kind) => Reach.Officer,
        25 or 27 or 56 => Reach.OfficerAlways,
        53 or 54 => Reach.Guard,
        EventMorale => Reach.Event,
        _ => Reach.Unknown
    };

    // Whom a condition names: the battle's handlers name the unit itself or others; 30 is the 공격 that lights a
    // 개성 for some days (0x178E30); 39 and 40 are the 괴멸·격파 events (0x258EE0); the rest have no handler.
    internal enum Target { None, Own, Others, Attacked, Event, Unused }

    internal const int RangeAllies = 9, RangeEnemies = 10, Troops = 20, Attacked = 30, AllyDestroyed = 39, EnemyDestroyed = 40;

    internal static Target TargetOf(int condition) => condition switch
    {
        PersonalityFields.NoCondition => Target.None,
        1 or (>= 4 and <= 8) or (>= 11 and <= 20) or 25 or 26 or 28 or 31 or 32 or 34 or 36 or 37 or 41 => Target.Own,
        2 or 3 or RangeAllies or RangeEnemies or (>= 21 and <= 24) or 38 => Target.Others,
        Attacked => Target.Attacked,
        AllyDestroyed or EnemyDestroyed => Target.Event,
        _ => Target.Unused
    };

    // The conditions offered, each meaning once (36 is 11 again, 41 is 25 with a test of the base), own unit first.
    internal static readonly int[] Offered = [0, 11, 1, 37, 13, 4, 5, 6, 7, 8, 12, 14, 15, 16, 17, 18, 19, Troops, 25, 26, 28, 31, 32, 34,
        RangeAllies, RangeEnemies, 2, 3, 21, 22, 23, 24, 38, AllyDestroyed, EnemyDestroyed];

    // The second condition every named unit must meet (+0xAC): with none (0) no unit gets the effects.
    internal const int FilterArmy = 1, FilterAny = 11, FilterNoShipsOrSiege = 37;
    internal static readonly int[] Filters = [FilterArmy, FilterNoShipsOrSiege, FilterAny];

    // 40's spread to the units around with filter 11 is the game's for these two only (시상, 대담).
    private static readonly int[] SpreadOnEnemyDestroyed = [27, 170];

    // How long a 개성 lasts (+0xB4): always on (-1), while its condition holds (0), or for days once attacked.
    internal enum Timing { Always, WhileMet, AfterAttack }

    internal static Timing TimingOf(int duration) => duration switch { -1 => Timing.Always, 0 => Timing.WhileMet, _ => Timing.AfterAttack };

    // What a condition's number (+0xA8) is (0x22C3B0): troops for 20, the range of 9 and 10 for 개성 256..355
    // (천기, 무염, ...; the others have the range 3).
    internal enum Number { None, Troops, Range }

    internal const int MaxTroops = 60_000, MaxRange = 10;

    internal static Number NumberOf(int personality, int condition) => condition switch
    {
        Troops => Number.Troops,
        RangeAllies or RangeEnemies when personality is >= 0x100 and <= 0x163 => Number.Range,
        _ => Number.None
    };

    // Why a 개성 cannot take a condition, "" when it can.
    internal static string ConditionProblem(Timing timing, int condition) => (timing, TargetOf(condition)) switch
    {
        (_, Target.Unused) => L.PersonalityRules.UnusedCondition,
        (Timing.AfterAttack, _) => L.PersonalityRules.AfterAttackKeepsCondition,
        (_, Target.Attacked) => L.PersonalityRules.AttackedNeedsDays,
        (Timing.Always, Target.Own or Target.Others) => L.PersonalityRules.AlwaysTakesNoCondition,
        _ => ""
    };

    // Why an effect kind does nothing under a condition, "" when it works.
    internal static string KindProblem(Timing timing, int condition, int kind)
    {
        if (kind == 0) return "";
        Reach reach = ReachOf(kind);
        // A 개성 lit by an attack lights its commander's own slot.
        Target target = timing == Timing.AfterAttack ? Target.Own : TargetOf(condition);
        if (target == Target.Event) return kind == EventMorale ? "" : L.PersonalityRules.EventTakesNoKind;
        return reach switch
        {
            Reach.Unknown => L.PersonalityRules.NoReader,
            Reach.Event => L.PersonalityRules.EventOnly,
            Reach.Officer => "",
            _ when timing == Timing.Always => reach switch
            {
                Reach.OwnLit => L.PersonalityRules.NeedsLit,
                Reach.Received => L.PersonalityRules.OthersOnly,
                _ => ""
            },
            Reach.OfficerAlways => L.PersonalityRules.AlwaysOnly,
            _ when target == Target.None => reach == Reach.Guard ? L.PersonalityRules.GuardNeedsRange : L.PersonalityRules.NeedsCondition,
            Reach.Guard => condition == RangeAllies ? "" : L.PersonalityRules.GuardNeedsRange,
            Reach.Received when target == Target.Own => L.PersonalityRules.OthersOnly,
            Reach.Own or Reach.OwnLit when target == Target.Others => L.PersonalityRules.OwnOnly,
            _ => ""
        };
    }

    // The second condition a 개성 needs: one of Filters while the battle checks its condition; for 40, 1 (its own unit
    // gets the morale) unless it is one of the two that spread with 11; otherwise what it has.
    internal static int FilterFor(int personality, Timing timing, int condition, int filter)
    {
        Target target = TargetOf(condition);
        if (timing == Timing.WhileMet && target is Target.Own or Target.Others)
            return Filters.Contains(filter) ? filter : FilterArmy;
        if (target == Target.Event)
            return filter == FilterAny && SpreadOnEnemyDestroyed.Contains(personality) ? filter : FilterArmy;
        return filter;
    }
}
