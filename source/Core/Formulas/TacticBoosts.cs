namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    [Rva(0x1A48AE0)] internal const int AttackMartialDivisor = 50;
    [Rva(0x1A48AEC)] internal const int AttackIntelligenceDivisor = 50;
    [Rva(0x1A48AF8)] internal const int AttackFactorMinimumPercent = 10;

    private static Rec? Leader(World w, Tactic.Side side) => Objects.Valid(w, side.Unit)
        ? w.OfficerById(side.Unit!.U16(0x12)) : Objects.Valid(w, side.Base) ? w.OfficerById(side.Base!.U16(0x2E)) : null;

    // 0x16D7B0: source ability is zero without a valid commander or governor.
    internal static int SourceAbility(World w, Rec tactic, Tactic.Side side) => Leader(w, side) is { } person && Objects.PersonValid(person)
        ? OfficerAbility.Get(w, person, AbilityKind(tactic) == 1 ? 1 : 2, OfficerAbility.All) : 0;

    // 0x16E790: a valid target base without a valid governor uses 50; an absent target uses zero.
    internal static int TargetAbility(World w, Rec tactic, Tactic.Side side)
    {
        if (Objects.Valid(w, side.Unit)) return SourceAbility(w, tactic, side);
        if (!Objects.Valid(w, side.Base)) return 0;
        Rec? person = w.OfficerById(side.Base!.U16(0x2E));
        return Objects.Valid(w, person) ? OfficerAbility.Get(w, person!, AbilityKind(tactic) == 1 ? 1 : 2, OfficerAbility.All) : 50;
    }

    // 0x16B790: attacking ability relative to the target; maxss clamps the multiplier at 0.1.
    internal static float AttackFactor(World w, Rec tactic, Tactic.Side source, Tactic.Side target) =>
        Math.Max((float)unchecked(SourceAbility(w, tactic, source) - TargetAbility(w, tactic, target))
            / (AbilityKind(tactic) == 1 ? AttackMartialDivisor : AttackIntelligenceDivisor) + 1f, (float)AttackFactorMinimumPercent / 100f);

    // 0x176580 / 0x1768F0: boosts 5..9 / 10..14 change one statistic; 21 / 22 change all five.
    internal static List<Applied> ApplyBoost(World w, Rec tactic, Tactic.Side source, int slot, int links, int kind, IReadOnlyList<Rec> targets)
    {
        bool positive = kind is >= 5 and <= 9 or 21;
        int stat = kind is >= 5 and <= 9 ? kind - 5 : kind is >= 10 and <= 14 ? kind - 10 : -1;
        if (stat < 0 && kind is not (21 or 22)) throw new ArgumentOutOfRangeException(nameof(kind));
        int sourceForce = Objects.Valid(w, source.Unit) ? Objects.UnitForce(w, source.Unit!) : Objects.Valid(w, source.Base) ? Map.BaseForce(w, source.Base!) : 0;
        Rec force = w.Entry(Tables.Forces, sourceForce, 51);
        float doctrine = 1f;
        if (stat >= 0 && Objects.ForceValid(force))
        {
            int panel = positive && tactic.U8(0x40) == 3 ? 34 : !positive && tactic.U8(0x40) == 4 ? 35 : -1;
            if (panel >= 0) doctrine = (float)Objects.Doctrine(w, force, panel) / 100f + 1f;
        }
        var results = new List<Applied>();
        foreach (Rec tile in targets)
        {
            var at = Reach.ObjectAt(w, tile);
            if (at.Kind != 27) continue;
            Rec unit = w.Entry(Tables.Units, at.Number, 500);
            if (!Objects.UnitValid(unit)) continue;
            float factor = positive ? SupportFactor(w, tactic, source) : AttackFactor(w, tactic, source, new Tactic.Side(unit, null));
            float duration = (float)Amount(tactic, slot) * factor * ((float)unchecked(links * LinkPercent) / 100f + 1f) * SupplyFactor(w, source);
            if (stat >= 0) duration *= doctrine;
            int time = Objects.Trunc(duration), level = Level(tactic, slot) * (positive ? 1 : -1);
            for (int k = stat < 0 ? 0 : stat; k <= (stat < 0 ? 4 : stat); k++) UnitBoost.Set(unit, k, level, time);
            results.Add(new Applied(at.Kind, at.Number, tile.Index, 0));
        }
        return results;
    }
}
