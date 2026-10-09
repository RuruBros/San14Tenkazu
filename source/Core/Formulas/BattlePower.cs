namespace San14Tenkazu.Formulas;

// A unit's or a base's 전투력 (rva 0x2A8D40), the first number of every engagement (Battle 0x169C50, step 2: the
// game's own debug line names it "戦闘力"):
//   (f / 10) × (√(min(troops, ⌊4L/3⌋²) / split) + 2L + 200) × 10 / (9 + split) × (a + morale) / b
//   × facility × terrain × allies
// L the commander's 통솔 with every bonus, f 10 (8 for a unit with +0x23 set when effects count), a/b 200/300 moved by
// the difficulty, facility the 군 facilities' bonus, terrain the siege bonus, allies the units fighting alongside.
// Single-precision products in the game's order.
internal static class BattlePower
{
    [Rva(0x1A4A664)] internal const int RootDivisor = 1;
    [Rva(0x1A4A670)] internal const int LeadershipFactor = 2;
    [Rva(0x1A4A67C)] internal const int BaseAdd = 200;
    [Rva(0x1A4A688)] internal const int SplitNumerator = 10;
    [Rva(0x1A4A694)] internal const int SplitAdd = 9;
    [Rva(0x1A4A6A0)] internal const int MoraleAdd = 200;
    [Rva(0x1A4A6AC)] internal const int MoraleDivisor = 300;
    [Rva(0x1A48040)] internal const int MoraleAddEasy = 75;
    [Rva(0x1A4804C)] internal const int MoraleDivisorEasy = 175;
    [Rva(0x1A4A658)] internal const int SiegeTerrain6 = 150;
    [Rva(0x1A48064)] internal const int SiegeTerrain14 = 150;
    [Rva(0x1A4A6C4)] internal const int AlliesPercent = 10;
    [Rva(0x1A48058)] internal const int AlliesPercentLow = 5;
    [Rva(0x1A4978C)] internal const int MoraleMaxBase = 100;
    [Rva(0x1A49798)] internal const int MoraleMaxPolicy = 1;

    // The options object: +0xE58 0 = sliders (custom difficulty), else presets; +0xEC the preset; +0xD8 another
    // preset; sliders at options + 8 (Options_Slider 0x3EA400).
    private static int Option(World w, int at) => w.Settings.I32(at);
    internal static int Slider(World w, int index) => w.Settings.I32(8 + SliderOffset(index));
    private static int SliderOffset(int index) => index switch
    {
        1 => 0x1A4, 2 => 0x204, 4 => 0x24C, 5 => 0x264, 7 => 0x294, 8 => 0x2DC, 0xC => 0x354, 0xF => 0x3CC, 0x10 => 0x3E4, 0x15 => 0x4A4, 0x18 => 0x51C, 0x19 => 0x534, 0x27 => 0x684,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    // 0x2A8D40. Either a unit (base null) or a base (unit null).
    internal static float Get(World w, Rec? unit, Rec? baseRec, int split, bool withEffects)
    {
        bool unitOk = unit is not null && Objects.UnitValid(unit);
        bool baseOk = baseRec is not null && BaseValid(w, baseRec);
        if (!unitOk && !baseOk) return 0f;

        Rec? person = null;
        int force = 0, troops = 0, morale = 0, f8flag = 0;
        Rec? hex = null;
        if (!unitOk)
        {
            person = w.OfficerById(baseRec!.U16(0x2E));
            force = BaseForce(w, baseRec);
            troops = baseRec.I32(0x3C);
            morale = baseRec.U8(0x44);
            f8flag = baseRec.U8(0x49);
            hex = BaseHex(w, baseRec);
        }
        else
        {
            person = w.OfficerById(unit!.U16(0x12));
            force = Objects.UnitForce(w, unit);
            troops = unit.U16(0x16);
            int max = MaxMorale(w, w.Entry(Tables.Forces, force, 0x33));
            morale = Math.Min(unit.U8(0x1A), max);
            hex = w.Entry(Tables.Tiles, unit.U16(0x152), 0xBD0F);
            f8flag = unit.U8(0x23);
        }

        int lead = person is not null && Objects.PersonValid(person) ? OfficerAbility.Get(w, person, 0, OfficerAbility.All) : 0;
        int cap = (int)Math.Pow(lead * 4 / 3, 2.0);
        int t = cap >= troops ? troops : cap;
        double root = Math.Sqrt(t / split);

        float baseTerm = (float)(root / RootDivisor);
        baseTerm += LeadershipFactor * lead;
        baseTerm += BaseAdd;
        float f = withEffects && f8flag != 0 ? 8f : 10f;
        f /= 10f;
        float splitTerm = (float)SplitNumerator / (SplitAdd + split);

        int a = MoraleAdd, b = MoraleDivisor;
        if (Option(w, 0xE58) != 0)
        {
            int preset = Option(w, 0xEC);
            if (preset == 1) { a = MoraleAddEasy; b = MoraleDivisorEasy; }
        }
        else
        {
            int s = Slider(w, 0xC);
            if (s < 0) s = 0;
            if (s > MoraleAdd) s = MoraleAdd;
            float shift = (s - 100) / 100f * MoraleAdd;
            a = (int)(MoraleAdd - shift);
            b = (int)(MoraleDivisor - shift);
        }
        float moraleTerm = (float)(a + morale) / b;

        float facilityTerm = 1f;
        if (withEffects && !(unitOk && person is not null && Objects.PersonValid(person)
                             && (Objects.ForceOf(w, person).Index == 0x33 || Objects.InForces46To50(w, person) || Objects.SpecialOfficer(w, person, false)))
            && hex is not null)
            facilityTerm = Facility(w, hex, force, out _);

        float terrainTerm = 1f;
        if (unitOk)
        {
            Rec formation = Objects.FormationOf(w, unit!);
            int fi = formation.Index;
            if (Objects.FormationValid(formation) && ((fi - 6) & ~8) == 0)
            {
                int kind = w.Entry(Tables.Tiles, unit!.U16(0x2A), 0xBD0F).U8(0x10);
                if (kind > 4 && (kind < 7 || (uint)(kind - 8) < 4))
                    terrainTerm = fi == 6 ? SiegeTerrain6 / 100f : fi == 0xE ? SiegeTerrain14 / 100f : 1f;
            }
        }

        float alliesTerm = 1f;
        if (unitOk && withEffects)
        {
            int pct = Option(w, 0xD8) == 0 ? AlliesPercentLow : AlliesPercent;
            if (Option(w, 0xE58) == 0) pct = Slider(w, 8) * AlliesPercent / 100;
            Rec forceRec = w.Entry(Tables.Forces, force, 0x33);
            int doctrine = Objects.ForceValid(forceRec) ? Objects.Doctrine(w, forceRec, 0x26) : 0;
            int n = Allies(w, unit!);
            alliesTerm = (float)((pct + doctrine) * n) / 100f + 1f;
        }

        float r = f * baseTerm;
        r *= splitTerm;
        r *= moraleTerm;
        r *= facilityTerm;
        r *= terrainTerm;
        r *= alliesTerm;
        return r;
    }

    [Rva(0x1A4A6B8)] internal const int FacilityPercent = 4;
    [Rva(0x1A47D00)] internal const int FacilityPercentHard = 16;
    [Rva(0x1A47D24)] internal const int FacilityPercentHarder = 24;
    [Rva(0x1A47D0C)] internal const int FacilityScale1 = 120;
    [Rva(0x1A47D18)] internal const int FacilityScale2 = 140;

    // 0x2B07C0: the 군 facilities' bonus for a force fighting on a hex: the durability ratios (×100) of the hex's
    // 군 and of its marked neighbours (+0x56.., mask +0x66) that the force holds (or an ally holds, with 주의 0x27),
    // × (4 + 주의 0x46)% (16 or 24 for computer forces at the harder presets), then the preset's scale; 1 for a
    // force that is not 1..45. `sum` is the ratio total (the game's out argument).
    internal static float Facility(World w, Rec hex, int fi, out int sum)
    {
        sum = 0;
        if ((uint)(fi - 1) > 0x32 || (uint)(fi - 0x2E) <= 4) return 1f;
        Rec force = w.Entry(Tables.Forces, fi, 0x33);
        if (!Objects.ForceValid(force)) return 1f;
        Rec county = w.Entry(Tables.Counties, hex.U16(0x12), 0x1F4);
        if (Objects.CountyValid(county))
        {
            if (Holds(w, force, fi, county)) sum += Ratio(county);
            for (int s = 0; s < 8; s++)
            {
                Rec c = w.Entry(Tables.Counties, county.U16(0x56 + 2 * s), 0x1F4);
                if (Objects.CountyValid(c) && (county.U8(0x66) & (1 << s)) != 0 && Holds(w, force, fi, c)) sum += Ratio(c);
            }
        }
        float pct = FacilityPercent;
        if (Option(w, 0xE58) != 0)
        {
            int preset = Option(w, 0x28);
            if (Objects.ForceValid(force) && !Objects.IsPlayerForce(w, force))
            {
                if (preset == 3) pct = FacilityPercentHard;
                else if (preset >= 4) pct = FacilityPercentHarder;
            }
        }
        if (Objects.ForceValid(force)) pct += Objects.Doctrine(w, force, 0x46);
        float r = sum;
        pct /= 100f;
        r *= pct;
        r /= 100f;
        r += 1f;
        if (Option(w, 0xE58) != 0)
        {
            if (r > 1f)
            {
                int v = Option(w, 0xC4);
                if (v != 0)
                {
                    float scale = v == 1 ? FacilityScale1 : v == 2 ? FacilityScale2 : 0f;
                    r = scale * r / 100f;
                }
            }
        }
        else if (Objects.ForceValid(force))
        {
            int slider = Slider(w, Objects.IsPlayerForce(w, force) ? 4 : 5);
            r = sum;
            r *= pct;
            r /= 100f;
            r *= slider;
            r /= 100f;
            r += 1f;
        }
        return r;
    }

    // The 군 counts for the force: it holds it (+0x78), or with 주의 0x27 an ally does (agreement months +0xEA + force).
    private static bool Holds(World w, Rec force, int fi, Rec county) =>
        county.U8(0x78) == fi || (Objects.Doctrine(w, force, 0x27) > 0 && (force.U8(0xEA + county.U8(0x78)) & 0x7F) != 0);

    // The 군's facility durability over its maximum (+0x68 / +0x76), as a percentage.
    private static int Ratio(Rec county) => Objects.Trunc((float)county.U16(0x68) / county.U16(0x76) * 100f);

    [Rva(0x1A4A220)] internal const int AlliesRadius = 2;
    [Rva(0x1A4A22C)] internal const int AlliesGrade = 3;

    // 0x2AF7E0: the units fighting alongside: within 2 hexes, of the same corps, not engaged nor in their base, none
    // of them (nor this one) with +0x23..+0x25 set, whose commanders' link grade (with 개성) is 3 or more.
    internal static int Allies(World w, Rec unit)
        => AlliedUnits(w, unit).Count;

    // The same native function also returns its ordered list to the tactic link caller.
    internal static List<Rec> AlliedUnits(World w, Rec unit)
    {
        var result = new List<Rec>();
        if (!Objects.UnitValid(unit)) return result;
        Rec? cmd = w.OfficerById(unit.U16(0x12));
        if (cmd is null || !Objects.PersonValid(cmd) || (cmd.U16(0x15E) & 0x4000) != 0) return result;
        if (unit.U8(0x23) != 0 || unit.U8(0x24) != 0 || unit.U8(0x25) != 0) return result;
        foreach (Rec u in Map.UnitsNear(w, unit, AlliesRadius))
        {
            Rec? c = w.OfficerById(u.U16(0x12));
            if (c is null || !Objects.PersonValid(c) || (c.U16(0x15E) & 0x4000) != 0) continue;
            if (Map.InOwnBase(w, u) || Map.Engaged(w, u)) continue;
            if (CorpsOf(w, u) != CorpsOf(w, unit)) continue;
            if (u.U8(0x23) != 0 || u.U8(0x24) != 0 || u.U8(0x25) != 0) continue;
            if (Link.Grade(w, cmd, c, true) >= AlliesGrade) result.Add(u);
        }
        return result;
    }

    // 0x22BAE0: the corps of a unit's commander (+0xE0), 0 without one.
    private static int CorpsOf(World w, Rec unit) =>
        w.OfficerById(unit.U16(0x12)) is { } c && Objects.PersonValid(c) ? c.U8(0xE0) : 0;

    // 0x268C50 (the force's maximum 사기): 100 + its 정책 effect 10 + 주의 panel 0x47; forces 46..51 have no 정책.
    internal static int MaxMorale(World w, Rec force)
    {
        if (!Objects.ForceValid(force)) return MoraleMaxBase;
        int fi = force.Index;
        int v = (uint)(fi - 0x2E) > 4 && fi != 0x33 ? Policy.Effect(w, force, 10) : 0;
        return Objects.Doctrine(w, force, 0x47) + (v * MoraleMaxPolicy + 100) * MoraleMaxBase / 100;
    }

    // A base side: a city or gate (World_GetBase), its force and the hex its building stands on.
    private static bool BaseValid(World w, Rec b) => Objects.Valid(w, b);
    private static int BaseForce(World w, Rec b) => Map.BaseForce(w, b);
    private static Rec BaseHex(World w, Rec b) => Map.BuildingTile(w, b);
}
