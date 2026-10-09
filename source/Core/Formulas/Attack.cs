namespace San14Tenkazu.Formulas;

// A side's attack in an engagement (Battle step 3, the game's debug line "攻軍"): its battle power scaled by the
// formation's attack (or siege) value and the force's aptitude for it, the 개성 effects, the unit's attack buff and
// 주의 0x28 near its own bases. 0x2B21A0 against units, 0x2B2440 against bases.
internal static class Attack
{
    [Rva(0x1A48890)] internal const int Percent = 5;
    [Rva(0x1A488B4)] internal const int CityValue = 34;
    [Rva(0x1A488C0)] internal const int GateValue = 30;
    [Rva(0x1A488F0)] internal const int EffectPercent = 5;
    [Rva(0x1A47C68)] internal const int EffectCap = 10;
    [Rva(0x1A493C8)] internal const int OldAge = 60;
    [Rva(0x1A4A764)] internal const int Age60 = 1;
    [Rva(0x1A4A770)] internal const int Age65 = 2;
    [Rva(0x1A4A778)] internal const int Age70 = 3;
    [Rva(0x1A4A784)] internal const int Age80 = 4;
    [Rva(0x1A4A790)] internal const int Age90 = 5;

    // 0x2B21A0. power: the side's battle power; formation: the unit's (Objects.FormationOf). a5: the engagement's
    // own (1), a6: judge conditions afresh (battle passes 0; not written here).
    internal static float VsUnit(World w, float power, Rec? unit, Rec? baseRec, Rec? formation, bool a5)
    {
        bool unitOk = Objects.Valid(w, unit), baseOk = Objects.Valid(w, baseRec);
        if (unitOk == baseOk) return 0f;
        float value = 0f;
        float pct = Percent / 100f;
        float effects = 1f, buff = 1f;
        if (unitOk)
        {
            if (Objects.Valid(w, formation)) value = (float)(Aptitude(w, unit!) * FormationAttack(formation!)) / 100f;
            effects = EffectTerm(w, unit!, a5, 5, EffectCap, EffectPercent);
        }
        else
        {
            int id = baseRec!.U16(0x4E);
            if (id <= 0x33) value = CityValue;
            else if ((uint)(id - 0x34) <= 9) value = GateValue;
        }
        if (unitOk && a5) buff = (float)unit!.I8(0x4B) * EffectPercent / 100f + 1f;
        float r = value * power;
        r *= pct;
        r *= effects;
        r *= buff;
        if (unitOk)
        {
            Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit!), 0x33);
            if (Objects.ForceValid(force))
            {
                int d = Objects.Doctrine(w, force, 0x28);
                if (d > 0 && Map.NearOwnBase(w, unit!)) r = (float)(d + 100) * r / 100f;
            }
        }
        return r;
    }

    [Rva(0x1A48B94)] internal const int SiegeFormation5Bonus = 3;
    [Rva(0x1A47C88)] internal const int SiegeEffectCap = 10;
    [Rva(0x1A488FC)] internal const int SiegeEffectPercent = 5;
    [Rva(0x1A4889C)] internal const int DefensePercent = 5;
    [Rva(0x1A488CC)] internal const int CityDefense = 20;
    [Rva(0x1A488D8)] internal const int GateDefense = 16;
    [Rva(0x1A48BAC)] internal const int DefenseFormation7Bonus = 20;
    [Rva(0x1A47CF4)] internal const int DefenseEffectCap = 10;
    [Rva(0x1A48914)] internal const int DefenseEffectPercent = 5;
    [Rva(0x1A488A8)] internal const int BaseDurabilityPercent = 20;

    // 0x2B2440 (against bases, "攻城"): the formation's siege value (+3 for formation 5 when the force has bit 2 of
    // +0x193) and the force's aptitude; 개성 kinds 6 and 19 (±10, × 5%); the unit's siege buff (+0x4C); a base attacking
    // counts 1.
    internal static float VsBase(World w, float power, Rec? unit, Rec? baseRec, Rec? formation, bool a5)
    {
        bool unitOk = Objects.Valid(w, unit), baseOk = Objects.Valid(w, baseRec);
        if (unitOk == baseOk) return 0f;
        float value = 0f, effects = 1f, buff = 1f;
        float pct = Percent / 100f;
        if (unitOk)
        {
            if (Objects.Valid(w, formation))
            {
                Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit!), 0x33);
                int siege = FormationSiege(formation!);
                if ((force.U8(0x193) & 2) != 0 && formation!.Index == 5) siege += SiegeFormation5Bonus;
                value = (float)(siege * Aptitude(w, unit!)) / 100f;
            }
            effects = EffectTerm(w, unit!, a5, 6, SiegeEffectCap, SiegeEffectPercent);
            if (a5) buff = (float)unit!.I8(0x4C) * SiegeEffectPercent / 100f + 1f;
        }
        else value = 1f;
        float r = value * power;
        r *= pct;
        r *= effects;
        r *= buff;
        return r;
    }

    // 0x2A9E50 ("防御"): the formation's defense value (+20 for formation 7 when the force has bit 5 of +0x193) and
    // the aptitude; 개성 kinds 7 and 19; the defense buff (+0x4D); a base's own defense is 20 (city) or 16 (gate), and
    // 20% of its building's durability is added at the end (1 is added for a unit).
    internal static float Defense(World w, float power, Rec? unit, Rec? baseRec, Rec? formation, bool a5)
    {
        bool unitOk = Objects.Valid(w, unit), baseOk = Objects.Valid(w, baseRec);
        if (unitOk == baseOk) return 0f;
        float value = 0f, effects = 1f, buff = 1f, baseTerm = 1f;
        float pct = DefensePercent / 100f;
        if (unitOk)
        {
            if (Objects.Valid(w, formation))
            {
                Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit!), 0x33);
                int def = FormationDefense(formation!);
                if ((force.U8(0x193) & 0x20) != 0 && formation!.Index == 7) def += DefenseFormation7Bonus;
                int f = RawFormation(w, unit!);
                value = f is 0x13 or 0x14 ? (float)(def * 100) / 100f : (float)(def * Aptitude(w, unit!)) / 100f;
            }
            effects = EffectTerm(w, unit!, a5, 7, DefenseEffectCap, DefenseEffectPercent);
            if (a5) buff = (float)unit!.I8(0x4D) * DefenseEffectPercent / 100f + 1f;
        }
        else
        {
            int id = baseRec!.U16(0x4E);
            if (id <= 0x33) value = CityDefense;
            else if ((uint)(id - 0x34) <= 9) value = GateDefense;
        }
        if (baseOk)
        {
            Rec o = w.Entry(Tables.Buildings, baseRec!.U16(0x4E), 0xBB8);
            int durability = Map.BuildingValid(o) ? o.U16(0x14) : 6000;
            baseTerm = (float)BaseDurabilityPercent * durability;
            baseTerm /= 100f;
        }
        float r = value * power;
        r *= pct;
        r *= effects;
        r *= buff;
        r += baseTerm;
        return r;
    }

    // The 개성 term: the kind (5 attack, 6 siege, 7 defense) and 19, with the age bonus, for combat units only;
    // capped, × the percent.
    private static float EffectTerm(World w, Rec unit, bool a5, int kind, int cap, int percent)
    {
        int[] sums = Personality.UnitSums(w, unit, !a5);
        int e = 0;
        if (unit.U8(0x10) == 1) e = AgeBonus(w, unit, !a5) + sums[kind] + sums[19];
        int c = e < -cap ? -cap : e > cap ? cap : e;
        return (float)(percent * c) / 100f + 1f;
    }

    // 0x41C140 / 0x41BFC0: the force's aptitude (%) for the unit's formation: 100 + 정책 effect × 1 by the formation's
    // group; 100 for formations 19, 20 (the caller's) and for forces 46..51.
    internal static int Aptitude(World w, Rec unit)
    {
        int f = RawFormation(w, unit);
        if (f is 0x13 or 0x14) return 100;
        if (!Objects.UnitValid(unit)) return 100;
        Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 0x33);
        if (!Objects.ForceValid(force)) return 100;
        int fi = force.Index;
        if ((uint)(fi - 0x2E) <= 4 || fi == 0x33) return 100;
        if ((uint)(f - 1) >= 0x14) return 100;
        (int kind, int k) = f switch
        {
            1 => (1, 1), 2 => (2, 1), 3 => (3, 1), 4 => (4, 1), 5 => (5, 1), 6 => (6, 1), 7 => (7, 1),
            8 or 9 or 10 => (8, 1), 11 or 12 or 13 => (9, 1), 17 or 18 or 19 or 20 => (12, 1),
            _ => (0, 0),
        };
        return kind == 0 ? 100 : Policy.Effect(w, force, kind) * k + 100;
    }

    // 0x22B140: the formation number the unit has on its terrain (+0x1C on water, else +0x1B), unchecked.
    internal static int RawFormation(World w, Rec unit)
    {
        Rec tile = w.Entry(Tables.Tiles, unit.U16(0x2A), 0xBD0F);
        Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 0x1E);
        return (terrain.U8(0x2C) & 2) != 0 ? unit.U8(0x1C) : unit.U8(0x1B);
    }

    // 0x2B2720 / 0x2B2690 / 0x2A9DC0: a formation's attack (+0x83, at most 45), siege (+0x84, 70) and defense
    // (+0x86, 40) values, kept encoded in its +0xA8 vector (bytes 0xC, 0x10, 0x18; rotate 1, 3, 7); 0 reads as 1.
    internal static int FormationAttack(Rec f) => FormationValue(f, 0x83, 0xC, 1, 0x2D);
    internal static int FormationSiege(Rec f) => FormationValue(f, 0x84, 0x10, 3, 0x46);
    internal static int FormationDefense(Rec f) => FormationValue(f, 0x86, 0x18, 7, 0x28);
    // 0x2B27B0: the wall-breaking value (+0x85, at most 60; vector byte 0x14, rotate 5).
    internal static int FormationBreak(Rec f) => FormationValue(f, 0x85, 0x14, 5, 0x3C);

    [Rva(0x1A48BA0)] internal const int Formation5BreakBonus = 5;
    [Rva(0x1A47CDC)] internal const int BreakBonusCap = 10;
    [Rva(0x1A48908)] internal const int BreakBonusPercent = 5;
    [Rva(0x1A47CA0)] internal const int BreakDivisor = 100;
    [Rva(0x1A47CAC)] internal const int BreakTroopsOffset = 20000;
    [Rva(0x1A47CB8)] internal const int BreakTroopsDivisor = 10000;

    // 0x2AA2E0: the siege damage a unit deals a base or building (the 攻城 step's input):
    //   lead × (break value × aptitude / 100) / 100 × (troops + 20000) / 10000
    //   × (1 + 5% × clamp(age bonus + 개성 sums 8 and 19, ±10)) × (1 + 5% × 파성 단계 +0x4E, with `effects`)
    // Lead is the commander's ability 0 with every bonus; the break value is the formation's (+5 for formation 5
    // under a force with bit 1 of +0x193), aptitude 100 for formations 19 and 20. 0 without a commander; at least 1
    // otherwise.
    internal static float SiegeDamage(World w, Rec unit, Rec? formation, bool effects)
    {
        if (!Objects.Valid(w, unit)) return 0f;
        Rec? cmd = w.OfficerById(unit.U16(0x12)) ?? w.Table(Tables.Officers)[0];
        if (!Objects.PersonValid(cmd)) return 0f;
        int troops = unit.U16(0x16);
        int lead = OfficerAbility.Get(w, cmd, 0, OfficerAbility.All);
        float rate = 0f;
        if (formation is not null && Objects.FormationValid(formation))
        {
            Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 0x33);
            int value = FormationBreak(formation);
            if ((force.U8(0x193) & 2) != 0 && formation.Index == 5) value += Formation5BreakBonus;
            int raw = RawFormation(w, unit);
            int aptitude = raw is 0x13 or 0x14 ? 100 : Aptitude(w, unit);
            rate = (float)(value * aptitude) / 100f;
        }
        int[] sums = Personality.UnitSums(w, unit, !effects);
        int bonus = 0;
        if (unit.U8(0x10) == 1) bonus = AgeBonus(w, unit, !effects) + sums[8] + sums[19];
        int clamped = Math.Clamp(bonus, -BreakBonusCap, BreakBonusCap);
        float f5 = (float)(BreakBonusPercent * clamped) / 100f + 1f;
        float f4 = effects ? (float)(unit.I8(0x4E) * BreakBonusPercent) / 100f + 1f : 1f;
        float v = (float)lead * rate;
        v /= (float)BreakDivisor;
        v *= (float)(troops + BreakTroopsOffset);
        v /= (float)BreakTroopsDivisor;
        v *= f5;
        v *= f4;
        return Math.Max(v, 1f);
    }
    private static int FormationValue(Rec f, int plain, int at, int rotate, int max)
    {
        byte[] v = f.VectorA8 ?? [];
        if ((v.Length & ~3) != 0x20) return f.U8(plain);
        int b = Objects.Decode(v[at], rotate, f.Index);
        return b == 0 ? 1 : Math.Min(b, max);
    }

    // 0x2A5F70: the bonus of an old commander (60 and over: 1..5 by 60/65/70/80/90) with 개성 kind 97 (or 31, which
    // counts as 97), for a unit that is not engaged.
    internal static int AgeBonus(World w, Rec unit, bool ownAlwaysOnly)
    {
        if (!Objects.UnitValid(unit) || Map.Engaged(w, unit)) return 0;
        Rec? cmd = w.OfficerById(unit.U16(0x12));
        if (cmd is null || !Objects.PersonValid(cmd)) return 0;
        int age = Age(w, cmd);
        if (age < OldAge) return 0;
        int[] sums = Personality.UnitSums(w, unit, ownAlwaysOnly);
        int v = sums[31] != 0 ? sums[31] : sums[97];
        if (v == 0) return 0;
        int tier = age < OldAge + 5 ? Age60 : age < OldAge + 10 ? Age65 : age < OldAge + 20 ? Age70 : age < OldAge + 30 ? Age80 : Age90;
        return tier + v;
    }

    // 0x2A5EA0: an officer's age (this year - birth + 1, at least 0); in modes 2 and 3 (CWorldData +0x44) 80 for five
    // officers and holders of 개성 0x90, else 20.
    internal static readonly int[] AgeEighty = [231, 247, 271, 631, 5121];
    internal static int Age(World w, Rec p)
    {
        int mode = w.WorldState.I32(0x44);
        bool special = mode == 2 || (mode == 3 && w.WorldState.I32(0x50) == 0);
        if (special)
            return AgeEighty.Contains(p.U16(0x10)) || Personality.HasPersonality(w, p, 0x90) ? 0x50 : 0x14;
        int age = w.WorldState.I16(0x34) - p.U16(0xAC) + 1;
        return age < 0 ? 0 : age;
    }
}
