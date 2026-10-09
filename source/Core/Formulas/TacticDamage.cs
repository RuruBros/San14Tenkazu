namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    [Rva(0x1A48AB0)] internal const int DamageAbilityAdd = 5;
    [Rva(0x1A48ABC)] internal const int DamageRootAdd = 5;
    [Rva(0x1A48AC8)] internal const int DamageTroopsDivisor = 200;
    [Rva(0x1A48B68)] internal const int ProvokedDamagePercent = 130;
    [Rva(0x1A488F0)] internal const int TacticDefenseBoostPercent = 5;
    [Rva(0x1A488A8)] internal const int TacticBaseDurabilityPercent = 20;
    [Rva(0x1A48C14)] internal const int TacticForceDamagePercent = 120;
    [Rva(0x1A48BF0)] internal const int TacticMildDamageReductionPercent = 30;

    // 0x176B80: the damage recorded for a target before troop writes. The main target can be deferred by the
    // caller's center field, while splash targets are applied immediately; this method only computes the value.
    internal static int? DamageFor(World w, Rec tactic, Tactic.Side source, int slot, int links, Rec tile, int? initialTroops = null)
    {
        bool fromUnit = Objects.Valid(w, source.Unit), fromBase = Objects.Valid(w, source.Base);
        if (!fromUnit && !fromBase) return null;
        var at = Reach.ObjectAt(w, tile);
        if (at.Kind == 255) return null;
        Rec? unit = at.Kind == 27 ? w.Entry(Tables.Units, at.Number, 500) : null;
        Rec? b = at.Kind is 5 or 6 ? TargetBase(w, at.Kind, at.Number) : null;
        if (at.Kind == 27 && !Objects.Valid(w, unit) || at.Kind is 5 or 6 && !Objects.Valid(w, b)) return null;
        int forceId = fromUnit ? Objects.UnitForce(w, source.Unit!) : Map.BaseForce(w, source.Base!);
        Rec force = w.Entry(Tables.Forces, forceId, 51);
        if (b is not null && b.U16(0x4E) <= 51 && Damage.TribeCityWithGovernor(w, b))
        {
            Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
            if (!Objects.ForceValid(player) || forceId != player.Index) return null;
        }
        int sourceKind = fromUnit ? 27 : source.Base!.U16(0x4E) < 52 ? 5 : 6;
        int sourceNumber = fromUnit ? source.Unit!.Index : sourceKind == 5 ? source.Base!.U16(0x4E) : source.Base!.U16(0x4E) - 51;
        int troops = initialTroops ?? (fromUnit ? source.Unit!.U16(0x16) : source.Base!.I32(0x3C));
        int ability = Math.Max(70, unchecked(SourceAbility(w, tactic, source) + DamageAbilityAdd));
        float root = unchecked((int)(Math.Sqrt(troops / DamageTroopsDivisor) + DamageRootAdd)) / 10f;
        float provoked = unit is not null && unit.U8(0x25) != 0 && (unit.U8(0x3C) != sourceKind || unit.U16(0x3E) != sourceNumber) ? ProvokedDamagePercent / 100f : 1f;
        float defense = fromUnit && source.Unit!.U8(0x53) != 0 ? 1f - (float)source.Unit.I8(0x4D) * TacticDefenseBoostPercent / 100f : 1f;
        float flat = 1f;
        if (!fromUnit)
        {
            Rec building = w.Entry(Tables.Buildings, source.Base!.U16(0x4E), 3000);
            int durability = Map.BuildingValid(building) ? building.U16(0x14) : 6000;
            flat = (float)TacticBaseDurabilityPercent * durability / 100f;
        }
        float doctrine = 1f;
        if (Objects.ForceValid(force) && tactic.U8(0x40) is 1 or 2)
            doctrine = (float)Objects.Doctrine(w, force, tactic.U8(0x40) == 1 ? 32 : 33) / 100f + 1f;
        float forcePercent = (force.U8(0x193) & 16) != 0 ? TacticForceDamagePercent : 100f;
        float damage = (float)unchecked(Amount(tactic, slot) * ability) * ((float)unchecked(links * LinkPercent) / 100f + 1f)
            * root * AttackFactor(w, tactic, source, new Tactic.Side(unit, b)) * provoked * defense * SupplyFactor(w, source) * forcePercent;
        int result = Objects.Trunc(damage / 100f * doctrine + flat);
        int advantage = fromUnit ? 0 : unchecked(Advantage.Effect(w, force, 3, 0) * result * Damage.AdvantageFactor) / 100;
        if (unit is not null && w.OfficerById(unit.U16(0x12)) is { } leader && Personality.FindKind(w, leader, 56) is { } p && p.I16(0xB4) == -1)
            result = unchecked(result - result / Damage.HalvingDivisor);
        if (w.Settings.I32(0xE58) == 0) result = unchecked(BattlePower.Slider(w, 2) * unchecked(result + advantage)) / 100;
        else
        {
            if (result > 0 && w.Settings.I32(0xB8) == 0) result = unchecked(result - unchecked(result * TacticMildDamageReductionPercent) / 100);
            result = unchecked(result + advantage);
        }
        return Math.Min(result, Damage.Max);
    }
}
