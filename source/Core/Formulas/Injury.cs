namespace San14Tenkazu.Formulas;

// An officer's injury after damage lands (Injury_Calc 0x27D4B0, the game's "怪我計算"): the state +0x161 (0 none,
// 1 light, 2 heavy, 3 critical) rises by one on a roll, and a heavily injured officer may die first. The death
// itself (0x295AA0, with its camera and message work) and the messages are not modelled; `Died` only says the
// roll went that way.
internal static class Injury
{
    [Rva(0x1A4A510)] internal const int DeathBase = 10;
    [Rva(0x1A4A51C)] internal const int DeathPreset2Percent = 200;
    [Rva(0x1A4A528)] internal const int DeathPreset0Percent = 50;
    [Rva(0x1A4A574)] internal const int Base0 = 60;
    [Rva(0x1A4A580)] internal const int Base1 = 80;
    [Rva(0x1A4A58C)] internal const int Base2 = 100;
    [Rva(0x1A4A598)] internal const int Divisor0 = 10;
    [Rva(0x1A4A5A4)] internal const int Divisor1 = 15;
    [Rva(0x1A4A5B0)] internal const int Divisor2 = 20;
    [Rva(0x1A4A534)] internal const int TribeBase0 = 60;
    [Rva(0x1A4A540)] internal const int TribeBase1 = 40;
    [Rva(0x1A4A548)] internal const int TribeBase2 = 100;
    [Rva(0x1A4A550)] internal const int TribeDivisor0 = 10;
    [Rva(0x1A4A55C)] internal const int TribeDivisor1 = 15;
    [Rva(0x1A4A568)] internal const int TribeDivisor2 = 20;
    [Rva(0x1A4A5C8)] internal const int HardPlayerPercent = 130;

    internal readonly record struct Result(int State, bool Died);

    // `mode` is what hit the officer (1 a battle, 2 and 3 other events); `attacker` the attacking commander or
    // null. Nothing while the option (sliders: +0x2B4, presets: +0xD0) is on, for mode 2 with bit 14 of +0x15E,
    // for an officer whose own 개성 kind 0x35 has no range, or whose unit received kind 0x35.
    internal static Result Apply(World w, Rec person, int mode, Rec? attacker)
    {
        int state = person.U8(0x161);
        var same = new Result(state, false);
        bool sliders = w.Settings.I32(0xE58) == 0;
        if ((sliders ? w.Settings.I32(0x2B4) : w.Settings.I32(0xD0)) != 0) return same;
        if ((person.U16(0x15E) & 0x4000) != 0 && mode == 2) return same;
        for (int slot = 0; slot < 9; slot++)
        {
            Rec p = w.Entry(Tables.Personalities, Personality.Slot(w, person, slot), 0x163);
            if (Personality.Valid(p) && Personality.EffectValue(p, 0x35, 0) > 0 && Personality.Range(p) == 0) return same;
        }
        Rec unit = Objects.UnitOf(w, person);
        if (Objects.UnitValid(unit) && Personality.ReceivedHas(w, unit, 0x35)) return same;
        bool tribe = Wounded.InTribeForce(w, person) || Objects.SpecialOfficer(w, person, false);

        Rec? attackerUnit = null;
        if (attacker is not null && Objects.PersonValid(attacker))
        {
            Rec au = Objects.UnitOf(w, attacker);
            if (Objects.UnitValid(au)) attackerUnit = au;
        }

        // the death roll: presets other than 3, or the death slider (1) above 0; not for bit 10 of +0x15E, a tribe
        // or special officer, or anything but a battle
        bool deathCheck = sliders ? BattlePower.Slider(w, 1) > 0 : w.Settings.I32(0x30) != 3;
        if (deathCheck && (person.U16(0x15E) & 0x400) == 0 && !tribe && mode == 1)
        {
            if (state == 3) return new Result(state, true);
            if (state == 2)
            {
                int roll = Wounded.Seeded(w, 100, 1, person.U16(0x10), w.WorldState.I8(0x38), 0, 0);
                int preset = w.Settings.I32(0x30);
                int factor = preset == 2 ? DeathPreset2Percent : preset == 0 ? DeathPreset0Percent : 100;
                if (sliders) factor = 100;
                int divisor = person.U8(0xE6) == 1 ? 2 : 1;
                int thr = factor * DeathBase / 100 / divisor;
                int c = thr >= 0 ? Math.Min(thr, 100) : 0;
                float f = 0f;
                if (attackerUnit is not null)
                {
                    int s = Personality.UnitSums(w, attackerUnit, false)[0x52];
                    if (s > 0) f = s >= 1 ? s : 1f;
                }
                c += (int)f;
                int threshold = sliders ? BattlePower.Slider(w, 1) * c / 100 : c;
                if (roll < threshold) return new Result(state, true);
            }
        }

        if (state == 3) return same;
        {
            int roll = Wounded.Seeded(w, 100, 1, person.U16(0x10), 0, 0, 0);
            (int b, int d) = tribe
                ? state == 1 ? (TribeBase1, TribeDivisor1) : state == 2 ? (TribeBase2, TribeDivisor2) : (TribeBase0, TribeDivisor0)
                : state == 1 ? (Base1, Divisor1) : state == 2 ? (Base2, Divisor2) : (Base0, Divisor0);
            int ability = Math.Max(OfficerAbility.Get(w, person, 2, OfficerAbility.All), OfficerAbility.Get(w, person, 1, OfficerAbility.All));
            int thr = b - ability / d;
            int preset = sliders ? w.Settings.I32(0x134) : w.Settings.I32(0x28);
            if (preset > 2)
            {
                Rec force = Objects.ForceOf(w, person);
                if (Objects.ForceValid(force) && Objects.IsPlayerForce(w, force)) thr = thr * HardPlayerPercent / 100;
            }
            int atk = attackerUnit is not null ? Personality.UnitSums(w, attackerUnit, false)[0x52] * 3 : 0;
            int own = Objects.UnitValid(unit) ? -Personality.UnitSums(w, unit, false)[0x2F] : 0;
            thr = thr + own + atk;
            if (roll < thr)
            {
                int next = state == 0 ? 1 : state == 1 ? 2 : state == 2 ? 3 : state;
                state = Math.Min(next, 3);
            }
        }
        return new Result(state, false);
    }
}
