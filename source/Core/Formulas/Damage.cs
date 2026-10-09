namespace San14Tenkazu.Formulas;

// An engagement's damage (rva 0x16A1D0, Battle step 5, the game's debug line "攻撃先攻撃被害"): from the two sides'
// troops and the attack and defense values:
//   d = max(1, 1000 + attack − defense); q = max(1, d² / 2000)
//   damage = mult × ((troopsA + troopsB) / 300 + 300) × q / 1500, at most the defender's troops
// then, for a city attacking, its 정책 effect 0x15, the best 개성 kind 0x44 of its officers and the 지역 이점 kind 3,
// a halving for a defending commander with an always-on 개성 kind 0x38, and 9999 at most.
internal static class Damage
{
    [Rva(0x1A48974)] internal const int TransportMultiplier = 10;
    [Rva(0x1A48938)] internal const int Offset = 1000;
    [Rva(0x1A48944)] internal const int SquareDivisor = 2000;
    [Rva(0x1A48950)] internal const int TroopsDivisor = 300;
    [Rva(0x1A4895C)] internal const int TroopsAdd = 300;
    [Rva(0x1A48968)] internal const int Divisor = 1500;
    [Rva(0x1A48D48)] internal const int BuildingTroopsPercent = 1000;
    [Rva(0x1A489BC)] internal const int PolicyFactor = 1;
    [Rva(0x1A489C8)] internal const int OfficerFactor = 1;
    [Rva(0x1A48BB8)] internal const int AdvantageFactor = 1;
    [Rva(0x1A4A3A8)] internal const int HalvingDivisor = 2;
    [Rva(0x1A4A504)] internal const int Max = 9999;

    // The side's object: a unit, a city or gate, or a building; its troops and its force for the player check.
    private static bool Side(World w, Rec r, out int troops, out int force, out Rec? unit, out Rec? baseRec, out Rec? building)
    {
        troops = 0; force = 0; unit = null; baseRec = null; building = null;
        switch (r.Table)
        {
            case Tables.Units:
                unit = r;
                troops = r.U16(0x16);
                force = Objects.UnitForce(w, r);
                return true;
            case Tables.Cities:
            case Tables.Gates:
                baseRec = Map.Base(w, r.U16(0x4E));
                if (baseRec is null || !Objects.Valid(w, baseRec)) return false;
                troops = baseRec.I32(0x3C);
                force = Map.BaseForce(w, baseRec);
                return true;
            case Tables.Buildings:
                building = r;
                troops = Objects.Trunc((float)(r.U16(0x14) * BuildingTroopsPercent / 100));
                force = r.U8(0x13);
                return true;
            default:
                throw new NotSupportedException($"a side of table {r.Table:X}");
        }
    }

    // 0x16A1D0. attacker/defender: a unit, a city, a gate or a building; the attack (atkA) and defense (defB)
    // values the engagement computed for them.
    internal static int Get(World w, Rec attacker, float atkA, float defA, Rec defender, float atkB, float defB)
    {
        if (!Objects.Valid(w, attacker) || !Objects.Valid(w, defender)) return 0;
        Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 0x33);
        bool playerOk = Objects.ForceValid(player);

        if (!Side(w, attacker, out int troopsA, out int forceA, out Rec? unitA, out Rec? baseA, out _)) return 0;
        bool notPlayer = !playerOk || forceA != player.Index;

        // the game sorts the defender by kind: a unit, a city, a gate; a building is none of them, so no damage
        if (defender.Table == Tables.Buildings) return 0;
        if (!Side(w, defender, out int troopsB, out _, out Rec? unitB, out Rec? baseB, out _)) return 0;
        // a tribe's city (forces 46..50) with a governor takes no damage but from the player
        bool tribeCity = defender.Table == Tables.Cities && baseB is not null && TribeCityWithGovernor(w, baseB);
        if (notPlayer && tribeCity) return 0;

        int mult = unitB is not null && unitB.U8(0x10) == 2 ? TransportMultiplier : 1;
        float d = (float)Offset + atkA;
        d -= defB;
        if (d <= 1f) d = 1f;
        float q = d * d;
        q /= SquareDivisor;
        if (q <= 1f) q = 1f;
        float v = (float)(troopsA + troopsB) / TroopsDivisor;
        v += TroopsAdd;
        v *= q;
        v /= Divisor;
        int dmg = Objects.Trunc((float)mult * v);
        int result = troopsB <= dmg ? troopsB : dmg;

        if (baseA is not null && baseA.U16(0x4E) < 0x34)
        {
            Rec force = w.Entry(Tables.Forces, Map.BaseForce(w, baseA), 0x33);
            if (Objects.ForceValid(force))
            {
                int policy = result * PolicyFactor * Policy.Effect(w, force, 0x15) / 100;
                int neg = -1, pos = 1;
                foreach (Rec o in Map.OfficersOf(w, baseA, 0))
                {
                    if (Objects.Away(w, o)) continue;
                    int kind = Personality.EffectSum(w, o, 0x44);
                    int t = OfficerFactor * kind * result;
                    if (kind > 0) pos = Math.Max(pos, t / 100);
                    else neg = Math.Max(neg, t / 100);
                }
                int chosen = neg + pos > 0 ? pos : neg;
                int adv = Advantage.Effect(w, force, 3, 0);
                int total = result * adv * AdvantageFactor / 100 + result + chosen + policy;
                result = total > 0 ? total : 0;
            }
        }

        if (result > 0 && unitB is not null)
        {
            Rec? cmd = w.OfficerById(unitB.U16(0x12));
            if (cmd is not null && Personality.FindKind(w, cmd, 0x38) is { } p && p.I16(0xB4) == -1)
                result -= result / HalvingDivisor;
        }
        return Math.Min(result, Max);
    }

    // 0x232820: the city's governor (+0x2E) is based in 1..61 and the city's force is one of 46..50.
    internal static bool TribeCityWithGovernor(World w, Rec city)
    {
        Rec? g = w.OfficerById(city.U16(0x2E));
        if (g is null || (uint)(g.U16(0xE2) - 1) > 0x3C) return false;
        Rec force = w.Entry(Tables.Forces, Map.BaseForce(w, city), 0x33);
        return (uint)(force.Index - 0x2E) < 5;
    }
}

// 지역 이점 (CAdvantageData, raw 0x85990): a 주's bonus for the force holding enough of its cities.
internal static class Advantage
{
    // 0x2AD780: the sum of effect `kind` (1..13) over the 주 whose 이점 has it and whose cities the force holds enough
    // of (0x2C6450); `none` when no 주 has the kind at all. Nothing in modes 2 and 4 (CWorldData +0x40).
    internal static int Effect(World w, Rec force, int kind, int none)
        => EffectWithActivation(w, force, kind, none).Value;

    // The native output pointer distinguishes an active zero/100 effect from the fallback value.
    internal static (int Value, bool Active) EffectWithActivation(World w, Rec force, int kind, int none)
    {
        if (!Objects.ForceValid(force) || (uint)(kind - 1) >= 0xD) return (none, false);
        int mode = w.WorldState.I32(0x40);
        if (mode == 2 || mode == 4) return (none, false);
        bool any = false;
        int sum = 0;
        foreach (Rec state in w.Table(Tables.States))
        {
            if (!StateValid(state)) continue;
            Rec adv = w.Entry(Tables.Advantages, state.U8(0x30), 0x11);
            if (!AdvantageValid(adv)) continue;
            if (EffectKind(adv, 0) != kind && EffectKind(adv, 1) != kind) continue;
            if (!Holds(w, force, state)) continue;
            any = true;
            sum += EffectValue(w, state, kind);
        }
        return (any ? sum : none, any);
    }

    // 0x2AD6C0: the value of `kind` in a 주's 이점 (its first matching effect), 0 without one.
    internal static int EffectValue(World w, Rec state, int kind)
    {
        if (!StateValid(state)) return 0;
        Rec adv = w.Entry(Tables.Advantages, state.U8(0x30), 0x11);
        if (!AdvantageValid(adv)) return 0;
        for (int j = 0; j < 2; j++)
            if (EffectKind(adv, j) == kind) return EffectVal(adv, j);
        return 0;
    }

    // 0x2C6450: the force holds at least the 이점's required number of the 주's cities.
    internal static bool Holds(World w, Rec force, Rec state)
    {
        Rec adv = w.Entry(Tables.Advantages, state.U8(0x30), 0x11);
        if (!AdvantageValid(adv)) return false;
        return Requirement(adv) <= CitiesIn(w, force, state);
    }

    // 0x2AAAA0: the force's cities (+0xCE the 주) on the city list.
    internal static int CitiesIn(World w, Rec force, Rec state)
    {
        int n = 0;
        foreach (Rec?[] node in w.List(Lists.CityList))
            if (node[0] is { } c && Objects.Valid(w, c) && c.U8(0xCE) == state.Index && Map.BaseForce(w, c) == force.Index) n++;
        return n;
    }

    // CStateData / CAdvantageData validity: every entry but 0 (slot +0x18 of each: index ≠ 0).
    internal static bool StateValid(Rec s) => s.Index != 0;
    internal static bool AdvantageValid(Rec a) => a.Index != 0;

    // 0x92440, 0x2AB010, 0x2AB0B0: the requirement (+0x95), the effect kinds (+0x96, +0x97) and values (+0x98, +0x99),
    // kept encoded in the five-dword vector at +0xA0 (bytes 0, 4, 8, 12, 16; rotate 3, 5, 7).
    internal static int Requirement(Rec a) => Field(a, 0x95, 0, 3);
    internal static int EffectKind(Rec a, int j) => Field(a, 0x96 + Math.Min(j, 2), (j + 1) * 4, 5);
    internal static int EffectVal(Rec a, int j) => Field(a, 0x98 + Math.Min(j, 2), (j + 3) * 4, 7);
    private static int Field(Rec a, int plain, int at, int rotate)
    {
        byte[] v = a.VectorA0 ?? [];
        if ((v.Length & ~3) != 0x14) return a.U8(plain);
        return Objects.Decode(v[at], rotate, a.Index);
    }
}
