namespace San14Tenkazu.Formulas;

// What an officer gains from fighting: 공적 (Officer_AddMerit 0x169530), ability experience and levels
// (Officer_AddAbilityExp 0x2B9D50) and the amounts a battle event hands them (Officer_GainExperience 0x2B8EC0).
internal static class Experience
{
    // 0x169530
    [Rva(0x1A49FD8)] internal const int MeritKind7APercent = 1;
    [Rva(0x1A487CC)] internal const int MeritClanPercent = 10;
    [Rva(0x1A49FFC)] internal const int MeritFlag1BCPercent = 10;
    [Rva(0x1A49FF0)] internal const int MeritPreset1Percent = 150;
    [Rva(0x1A49FE4)] internal const int MeritPreset0Percent = 100;
    // 0x2B9D50
    [Rva(0x1A4A1C8)] internal const int ExpKind4BPercent = 1;
    [Rva(0x1A48BE4)] internal const int ExpAdvantagePercent = 1;
    [Rva(0x1A487C0)] internal const int ExpClanPercent = 5;
    [Rva(0x1A4A1BC)] internal const int ExpPolicyFactor = 1;
    // 0x2B8EC0: by event kind 0..3, the merit and exp factors
    [Rva(0x1A49D40)] internal const int Merit0 = 4;
    [Rva(0x1A49DA4)] internal const int Exp0 = 50;
    [Rva(0x1A49D4C)] internal const int Merit1 = 18;
    [Rva(0x1A49DB0)] internal const int Exp1 = 640;
    [Rva(0x1A49D58)] internal const int Merit2 = 100;
    [Rva(0x1A49DBC)] internal const int Exp2 = 750;
    [Rva(0x1A49D98)] internal const int Exp3 = 2000;

    // 0x22BE10: the clan (+0x14C, 0..6 into the clan table) of a force's ruler (+0x10), 0 when that clan is not a
    // standing one. The clan's own check (0x233DE0) also asks that its name string resolves; every real clan's does.
    internal static int RulerClan(World w, Rec force)
    {
        Rec ruler = w.OfficerById(force.U16(0x10)) ?? w.Table(Tables.Officers)[0];
        Rec clan = w.Entry(Tables.Clans, ruler.U8(0x14C), 6);
        return clan.Index != 0 ? clan.Index : 0;
    }

    // The officer shares the ruler's clan, in a force with bit 3 of +0x12.
    private static bool ClanBonus(World w, Rec p, Rec force) =>
        Objects.ForceValid(force) && (force.U8(0x12) & 8) != 0 && p.U8(0x14C) == RulerClan(w, force);

    // 0x169530: the 공적 (+0x1C0) an officer ends with after `amount` is awarded: nothing for a ruler (status 1),
    // status 5, a special officer, an officer with bit 14 of +0x15E, or amounts under 1. The amount grows by 개성 kind
    // 0x7A (rounded away from zero below 1), the 주의 panel 0x32, the ruler's clan (10%) and +0x1BC == 1 (10%), then
    // by the 공적 slider (0x15) or the preset at options +0x124 (1: 150%, 0: 100%).
    internal static int Merit(World w, Rec p, int amount)
    {
        int merit = p.U16(0x1C0);
        if (p.U8(0xE6) == 1 || p.U8(0xE6) == 5) return merit;
        if (Objects.SpecialOfficer(w, p, false)) return merit;
        if ((p.U16(0x15E) & 0x4000) != 0) return merit;
        if (amount < 1) return merit;
        int kind = Objects.PersonValid(p) ? Personality.EffectSum(w, p, 0x7A) : 0;
        float f = (float)(MeritKind7APercent * kind) * (float)amount / 100f;
        int byKind;
        if (f >= 0f) byKind = f <= 0f ? 0 : f < 1f ? 1 : (int)f;
        else byKind = f > -1f ? -1 : (int)f;
        Rec force = Objects.ForceOf(w, p);
        int byPanel = Objects.ForceValid(force) ? Objects.Doctrine(w, force, 0x32) * amount / 100 : 0;
        int byClan = ClanBonus(w, p, force) ? MeritClanPercent * amount / 100 : 0;
        int byFlag = p.U8(0x1BC) == 1 ? MeritFlag1BCPercent * amount / 100 : 0;
        int sum = byFlag + byClan + byPanel + byKind + amount;
        if (sum < 0) sum = 0;
        int gain = sum;
        if (w.Settings.I32(0xE58) == 0) gain = BattlePower.Slider(w, 0x15) * sum / 100;
        else if (w.Settings.I32(0x124) == 1) gain = MeritPreset1Percent * sum / 100;
        else if (w.Settings.I32(0x124) == 0) gain = MeritPreset0Percent * sum / 100;
        return Math.Clamp(merit + gain, 0, 0xFFFF);
    }

    // 0x2B9D50: the exp (+0x104 + 4k) and level (+0xEC + k) of ability k (0..4) after `amount` is awarded: nothing
    // for a force 51 or tribe officer or at level 100. The amount gains 개성 kind 0x4B (at least 1 with any), the 지역
    // 이점 kind 7, the ruler's clan (5%) and the 정책 0x28 effect, then the exp slider (0x18) under sliders. Levels
    // rise while the exp reaches the next level's need (level table +0x10 + 4k), to 100.
    internal static (int Exp, int Level) AbilityExp(World w, Rec p, int k, int amount)
    {
        if ((uint)k > 4) return (0, 0);   // the game changes nothing and answers 0
        int exp = p.I32(0x104 + 4 * k), level = p.U8(0xEC + k);
        if (!Objects.PersonValid(p)) return (exp, level);
        Rec force = Objects.ForceOf(w, p);
        if (force.Index == 0x33 || (uint)(force.Index - 0x2E) < 5) return (exp, level);
        if (w.RuntimeRules.Abilities == AbilityRuleMode.Original && level == 100
            || w.RuntimeRules.Abilities == AbilityRuleMode.Cap200 && level >= 100
            || w.RuntimeRules.Growth && level >= 200) return (exp, level);
        // the game holds the amount unsigned: a negative 개성 sum wraps the product and the division hands out
        // tens of millions of exp (a bug no shipped 개성 triggers; kept as the code has it)
        uint amt = (uint)amount;
        int kind = Personality.EffectSum(w, p, 0x4B);
        uint byKind = unchecked((uint)(ExpKind4BPercent * kind) * amt) / 100u;
        int adv = Advantage.Effect(w, force, 7, 0);
        uint bonus = Math.Max(kind != 0 ? 1u : 0u, byKind) + unchecked((uint)(ExpAdvantagePercent * adv) * amt) / 100u;
        if (ClanBonus(w, p, force)) bonus += unchecked((uint)ExpClanPercent * amt) / 100u;
        if (Objects.ForceValid(force)) amt = unchecked((uint)(ExpPolicyFactor * Policy.Effect(w, force, 0x28) + 100) * amt) / 100u;
        if (w.Settings.I32(0xE58) == 0) exp += unchecked((int)(BattlePower.Slider(w, 0x18) * (bonus + amt))) / 100;
        else exp = unchecked((int)((uint)exp + bonus + amt));
        // levels
        if (w.RuntimeRules.Growth)
        {
            int threshold100 = w.Entry(Tables.Levels, 100, 100).I32(0x10 + 4 * k);
            while (level < 200)
            {
                long threshold = level < 100 ? w.Entry(Tables.Levels, level + 1, 100).I32(0x10 + 4 * k)
                    : (long)threshold100 * (level + 1) * (level + 1) / 10000;
                if ((uint)exp < threshold) break;
                level++;
            }
            return (exp, level);
        }
        Rec next = w.Entry(Tables.Levels, level + 1, 100);
        if (next.Index != 0 && (uint)next.I32(0x10 + 4 * k) <= (uint)exp)
        {
            while (true)
            {
                int up = level + 1;
                level = Math.Min(up, 100);
                if (up == 100) break;
                next = w.Entry(Tables.Levels, up + 1, 100);
                if (next.Index == 0 || (uint)exp < (uint)next.I32(0x10 + 4 * k)) break;
            }
        }
        return (exp, level);
    }

    // 0x2B8EC0: what a battle event of `kind` worth `amount` gives: nothing to an invalid, tribe or special officer
    // or one not leading a unit, or whose unit is engaged. Kind 0: merit 4%, exp 50% of the amount (×10 for merit);
    // 1: 18% / 640%; 2: a flat 1000 merit and 750 exp; 3: a flat 2000 exp. Exp goes to ability 0.
    internal static (int Merit, int Exp, int Level) Gain(World w, Rec p, int kind, int amount)
    {
        var none = (p.U16(0x1C0), p.I32(0x104), p.U8(0xEC));
        if (!Objects.PersonValid(p) || Wounded.InTribeForce(w, p) || Objects.SpecialOfficer(w, p, false)) return none;
        Rec unit = Objects.UnitOf(w, p);
        if (!Objects.UnitValid(unit) || Map.Engaged(w, unit)) return none;
        int merit, exp;
        switch (kind)
        {
            case 0: merit = Merit0 * amount * 10 / 100; exp = Exp0 * amount / 100; break;
            case 1: merit = Merit1 * amount * 10 / 100; exp = Exp1 * amount / 100; break;
            case 2: merit = Merit2 * 10; exp = Exp2; break;
            case 3: merit = 0; exp = Exp3; break;
            default: return none;
        }
        var (e, l) = AbilityExp(w, p, 0, exp);
        return (Merit(w, p, merit), e, l);
    }
}
