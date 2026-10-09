namespace San14Tenkazu.Formulas;

// A base or building under attack (0x29E840, the 攻城 step): how much durability it loses from the siege damage
// the engagement computed, and whether it falls. The capture, destruction, event and message calls that follow a
// fall are not modelled; neither is the attacker's experience (Experience.Gain kind 1 with the damage).
internal static class Bombard
{
    [Rva(0x1A4A4E8)] internal const int Kind67Factor = 1;
    [Rva(0x1A49CA8)] internal const int TribeCityMax = 9999;
    internal const int DurabilityMax = 9999;
    // exe + 0x27D26A8 / 0x27D26AC: runtime extras on a positive / negative 개성 kind 0x67 sum (0 as the game runs).
    internal const string ExtraPositive = "bombard_fx_pos", ExtraNegative = "bombard_fx_neg";

    internal readonly record struct Result(int Durability, bool Fell);

    // 0x496700: challenge siege admits city 7, city 5 only in mode 3, and ordinary buildings; gates are excluded.
    internal static bool Allowed(World w, Rec building) => !w.Flag("challenge_active") ||
        (Reach.IsCity(building) ? building.Index == 7 || building.Index == 5 && w.Value("challenge_kind") == 3 : !Reach.IsGate(building));

    // `building` is the CObjectData (a city's or gate's building, or any other); `unit` the attacking unit or null.
    internal static Result Apply(World w, Rec building, int damage, Rec? unit)
    {
        int durability = building.U16(0x14);
        if (!Map.BuildingValid(building) || !Allowed(w, building)) return new Result(durability, false);
        foreach (Rec?[] node in w.List(Lists.SiegeExempt))
            if (ReferenceEquals(node[0], building)) return new Result(durability, false);

        damage = AdjustedDamage(w, building, damage, unit);
        bool attacker = unit is not null && Objects.Valid(w, unit);
        int cap = durability;
        if (attacker)
        {
            int expected = Reach.IsCity(building) ? 2 : 1;
            if (Map.HexDistance(unit!.U16(0x2A), building.U16(0x10)) != expected) cap = durability - 1;
        }
        int loss = damage < cap ? damage : cap;
        // 0x515330: challenge byte +8 bit 6 protects building 5's last durability point.
        if (w.Flag("challenge_active") && (w.Value("challenge_rules") & 64) != 0 && building.Index == 5)
            loss = Math.Min(loss, durability - 1);
        int next = durability - loss;
        // Building_SetDurability (0x1F42B0): 0..9999 (a tribe's city has its own cap, also 9999)
        int after = next < 0 ? 0 : Math.Min(next, DurabilityMax);
        bool neutralBase = (Reach.IsCity(building) || Reach.IsGate(building)) && building.U8(0x13) == 0;
        return new Result(after, next < 1 && !neutralBase);
    }

    // 0x29E840 applies these adjustments before awarding experience and capping the durability loss.
    internal static int AdjustedDamage(World w, Rec building, int damage, Rec? unit)
    {
        bool attacker = unit is not null && Objects.Valid(w, unit);
        if (attacker)
        {
            Rec kind = w.Entry(Tables.ObjectKinds, building.U8(0x12), 0x1E);
            int cls = kind.U8(0x2C);
            int extra = 0;
            if ((uint)(cls - 2) < 3)
            {
                Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit!), 0x33);
                int panel = Objects.Doctrine(w, force, 0x45);
                if (panel > 0) extra = Math.Max(1, damage * panel / 100);
            }
            int sum = cls == 3 ? Personality.UnitSums(w, unit!, false)[0x67] : 0;
            if (cls == 3 && sum != 0)
            {
                int t = sum * damage * Kind67Factor / 100;
                if (sum < 1) damage = Math.Max(1, Math.Min(-1, t) - w.Value(ExtraNegative) + damage + extra);
                else damage = damage + Math.Max(1, t) + w.Value(ExtraPositive) + extra;
            }
            else damage += extra;
        }

        return damage;
    }
}
