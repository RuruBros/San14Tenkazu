namespace San14Tenkazu.Formulas;

// What a side loses beyond the troops when damage lands (Battle_ApplyTroopDamage 0x178E30): the share of the dead
// that are wounded instead (0x17D1D0) and the morale lost (0x17D4F0), the latter by the game's seeded random
// (0x3FB600), which draws from its inputs and the world's counters, not the global state.
internal static class Wounded
{
    [Rva(0x1A4A3CC)] internal const int CharmFactor = 30;
    [Rva(0x1A4A3D8)] internal const int CharmDivisor = 100;
    [Rva(0x1A4A3E4)] internal const int MoraleFactor = 10;
    [Rva(0x1A4A3F0)] internal const int MoraleDivisor = 100;
    [Rva(0x1A4A3FC)] internal const int FlagBonus = 15;
    [Rva(0x1A4A488)] internal const int HardPlayerPenalty = -20;
    [Rva(0x1A4A494)] internal const int TribePenalty = -20;
    // By the terrain's +0x42 (0..10 in the data), an 11-entry table on the game's stack; entry 1 is a global the
    // game never sets (exe + 0x27D26A4, 0). The caller (0x178E30) passes the terrain of the hex the side stands on.
    // An index past the table (the vectors fuzz one) reads the game's own stack; modelled as 0.
    [Rva(0x1A4A410)] internal const int Force2 = 5;
    [Rva(0x1A4A41C)] internal const int Force3 = 5;
    [Rva(0x1A4A428)] internal const int Force4 = -50;
    [Rva(0x1A4A434)] internal const int Force5 = -20;
    [Rva(0x1A4A440)] internal const int Force6 = -50;
    [Rva(0x1A4A44C)] internal const int Force7 = 5;
    [Rva(0x1A4A458)] internal const int Force8 = 5;
    [Rva(0x1A4A464)] internal const int Force9 = 5;
    [Rva(0x1A4A470)] internal const int Force10 = 5;
    internal const string ForceEntry1 = "wounded_force1";
    internal const int OutOfTable = 0;

    // 0x17D1D0: the wounded share (0..) of the commander `person` with the unit's `morale`; `flag` (the attacker
    // stands at the expected distance: 1 of a unit, 2 of a city) +15; the terrain's +0x42 entry; -20 for the player
    // at presets 3+; 개성 kind 0x6F of both commanders when there is an attacker; -20 for an officer of forces
    // 46..50 based in 1..61.
    internal static float Ratio(World w, Rec? person, int morale, bool flag, Rec? terrain, Rec? attacker, bool hasAttacker)
    {
        float charm = 0f, byForce = 0f, hard = 0f, kinds = 0f, tribe = 0f;
        if (person is not null && Objects.PersonValid(person))
        {
            charm = CharmFactor;
            charm = (float)OfficerAbility.Get(w, person, 4, OfficerAbility.All) * charm / CharmDivisor;
        }
        float factor = MoraleFactor, divisor = MoraleDivisor;
        float bonus = flag ? FlagBonus : 0f;
        if (terrain is not null && Objects.Valid(w, terrain))
        {
            int[] table = [0, w.Value(ForceEntry1), Force2, Force3, Force4, Force5, Force6, Force7, Force8, Force9, Force10];
            int k = terrain.U8(0x42);
            byForce = k < table.Length ? table[k] : OutOfTable;
        }
        if (w.Settings.I32(0xE58) != 0 && person is not null)
        {
            int preset = w.Settings.I32(0xE58) == 0 ? w.Settings.I32(0x134) : w.Settings.I32(0x28);
            Rec f = Objects.ForceOf(w, person);
            if (Objects.ForceValid(f) && Objects.IsPlayerForce(w, f) && preset > 2) hard = HardPlayerPenalty;
        }
        if (hasAttacker && attacker is not null && Objects.PersonValid(attacker))
        {
            float a = Personality.Has(w, attacker, 0x6F) ? Personality.EffectSum(w, attacker, 0x6F) : 0f;
            float b = person is not null && Personality.Has(w, person, 0x6F) ? Personality.EffectSum(w, person, 0x6F) : 0f;
            kinds = b + a;
        }
        if (person is not null && InTribeForce(w, person)) tribe = TribePenalty;
        float r = factor * morale / divisor + charm + bonus + byForce + hard + kinds + tribe;
        if (r <= 0f) r = 0f;
        return r / 100f;
    }

    // 0x232920: based in 1..61 and in one of forces 46..50.
    internal static bool InTribeForce(World w, Rec p) =>
        (uint)(p.U16(0xE2) - 1) <= 0x3C && (uint)(Objects.ForceOf(w, p).Index - 0x2E) < 5;

    [Rva(0x1A4A39C)] internal const int LossChancePercent = 125;
    [Rva(0x1A4A390)] internal const int LossRandom = 3;

    // 0x17D4F0: the morale lost when `damage` lands on a side with `wounded` and `troops` left and the commander's
    // `lead`: with chance (wounded share × 125%) a loss of 1 + random(3), + 1 with chance (100 − lead)%.
    internal static int MoraleLoss(World w, int wounded, int troops, int lead, int damage)
    {
        float p = 1f - (float)troops / ((float)wounded + (float)troops);
        p *= LossChancePercent;
        p /= 100f;
        p *= 100f;
        if (p > 100f) p = 100f;
        if (p <= 0f) return 0;
        if (Seeded(w, 10000, 1, troops, damage, 0, 0) >= Objects.Trunc(p * 10000f / 100f)) return 0;
        int loss = Seeded(w, LossRandom, 1, wounded, troops, 0, 0);
        int m = Math.Max(0, 100 - lead);
        float q = m > 100 ? 100f : m;
        int extra = 0;
        if (q > 0f && Seeded(w, 10000, 1, wounded, lead, 0, 0) < Objects.Trunc(q * 10000f / 100f)) extra = 1;
        return loss + 1 + extra;
    }

    // 0x3FB600: a number in 0..n-1 from the inputs, the world's +0x45C salt and +0x450 / div, hashed as the global
    // random does; 0 for n < 2.
    internal static int Seeded(World w, int n, int div, int a, int b, int c, int d)
    {
        if (n < 2) return 0;
        uint x = (uint)(a + b + c + d);
        if (w.HasWorldState)
            x += (uint)w.WorldState.I32(0x45C) + (uint)(w.WorldState.I32(0x450) / Math.Max(div, 1));
        for (uint k = (x & 0xF) + 1; k > 0; k--)
            x = Rotate16(x * 0x693D4B5 + 0xD6FAAF49);
        x = x * 0x41C64E6D + 0x3039;
        uint r = (x << 16) & 0x7FFFFFFF | x >> 16;
        return (int)((int)r % n);
    }

    private static uint Rotate16(uint v) => v << 16 | v >> 16;
}
