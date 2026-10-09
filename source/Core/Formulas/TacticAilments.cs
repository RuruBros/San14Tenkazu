namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    [Rva(0x1A48B20)] internal const int AilmentBaseSuccess = 90;
    [Rva(0x1A48B2C)] internal const int AilmentAbilityRootFactor = 4;
    [Rva(0x1A48B38)] internal const int AilmentAbilitySquareDivisor = 100;
    [Rva(0x1A48BDC)] internal const int AilmentFailureKindFactor = 1;
    [Rva(0x1A48BB0)] internal const int AilmentResistanceFactor = 1;
    [Rva(0x1A48BBC)] internal const int ConfusionResistanceFactor = 1;
    [Rva(0x1A48BC8)] internal const int StopResistanceFactor = 1;
    [Rva(0x1A48BD0)] internal const int ProvocationResistanceFactor = 1;
    [Rva(0x1A4A4DC)] internal const int SourceAilmentKindFactor = 1;
    [Rva(0x1A48C08)] internal const int AilmentForceDurationPercent = 120;

    private static Rec? SideTile(World w, Tactic.Side side) => Objects.Valid(w, side.Unit)
        ? w.Entry(Tables.Tiles, side.Unit!.U16(0x2A), 0xBD0F) : Objects.Valid(w, side.Base) ? Map.BuildingTile(w, side.Base!) : null;

    private static Rec? TargetBase(World w, int kind, int number) => Map.Base(w,
        kind == 5 ? (uint)number < 52 ? number : 0 : (uint)(number - 1) < 10 ? number + 51 : 0);

    // 0x16F910: a seeded failure roll is inverted. Kind 98 on the source guarantees success before validity checks.
    internal static bool AilmentSucceeds(World w, Rec tactic, Tactic.Side source, Tactic.Side target)
    {
        if (Leader(w, source) is { } leader && Personality.Has(w, leader, 98)) return true;
        int a = SourceAbility(w, tactic, source), b = TargetAbility(w, tactic, target);
        Rec? from = SideTile(w, source), to = SideTile(w, target);
        if (from is null || to is null) return false;
        float difference = b < a ? (float)Math.Sqrt(unchecked((a - b) * AilmentAbilityRootFactor))
            : (float)unchecked(b * b - a * a) * -1f / AilmentAbilitySquareDivisor;
        float failure = Math.Clamp(100f - (difference + AilmentBaseSuccess), 0f, 100f);
        if (Objects.Valid(w, source.Unit))
            failure += (float)unchecked(Personality.UnitSums(w, source.Unit!, false)[99] * AilmentFailureKindFactor) * (100f - failure) / 100f;
        failure = Math.Min(100f, failure);
        return failure <= 0f || Wounded.Seeded(w, 10000, 1, unchecked(from.Index * (tactic.Index + 5)), unchecked(to.Index * 2), 0, 0) >= Objects.Trunc(failure * 10000f / 100f);
    }

    private static int ResistantTime(int time, int resistance)
    {
        float delta = (float)unchecked(resistance * time) / 100f;
        if (Math.Abs(delta) < 1.1920929e-5f) return time;
        int change = Objects.Trunc(delta);
        if (change == 0) change = delta >= 0f ? 1 : -1;
        return unchecked(time - change);
    }

    // 0x2EEA60: immunity only reads the commander's lit kinds; received kinds still change the duration.
    internal static void AddUnitAilment(World w, Rec unit, Rec? source, int ailment, int time, Rec? sourceTile)
    {
        if (!Objects.UnitValid(unit)) return;
        Rec? leader = w.OfficerById(unit.U16(0x12));
        if (leader is not null && (Personality.HasLit(w, leader, 21) || Personality.HasLit(w, leader, ailment + 22))) return;
        int[] sums = Personality.UnitSums(w, unit, false);
        int specific = ailment switch { 0 => sums[10] * ConfusionResistanceFactor, 1 => sums[11] * StopResistanceFactor, 2 => sums[12] * ProvocationResistanceFactor, _ => 0 };
        int attack = source is null ? 0 : Personality.UnitSums(w, source, false)[98] * SourceAilmentKindFactor;
        time = ResistantTime(time, unchecked(sums[9] * AilmentResistanceFactor + specific + attack));
        if ((uint)ailment < 3) unit.Bytes[0x23 + ailment] = unchecked((byte)Math.Clamp(unchecked(unit.U8(0x23 + ailment) + time), 0, AilmentMaximum));
        if (time > 0 && ailment == 2 && sourceTile is not null)
        {
            var at = Reach.ObjectAt(w, sourceTile);
            if (at.Kind != 255)
            {
                unit.Bytes[0x3C] = unchecked((byte)at.Kind);
                BitConverter.GetBytes(unchecked((ushort)at.Number)).CopyTo(unit.Bytes, 0x3E);
            }
        }
    }

    // 0x2EEF10: a base ignores stop immunity and stop-specific resistance. Its governor's always-on sums count.
    internal static void AddBaseAilment(World w, Rec b, Rec? source, int ailment, int time, Rec? sourceTile)
    {
        if (!Objects.Valid(w, b)) return;
        Rec? leader = w.OfficerById(b.U16(0x2E));
        if (leader is not null && Objects.PersonValid(leader))
        {
            if (Personality.HasLit(w, leader, 21) || ailment is 0 or 2 && Personality.HasLit(w, leader, ailment + 22)) return;
            int[] sums = Personality.OfficerSums(w, leader, true);
            int specific = ailment == 0 ? sums[10] * ConfusionResistanceFactor : ailment == 2 ? sums[12] * ProvocationResistanceFactor : 0;
            int attack = source is null ? 0 : Personality.UnitSums(w, source, false)[98] * SourceAilmentKindFactor;
            time = ResistantTime(time, unchecked(sums[9] * AilmentResistanceFactor + specific + attack));
        }
        if ((uint)ailment < 3) b.Bytes[0x49 + ailment] = unchecked((byte)Math.Clamp(unchecked(b.U8(0x49 + ailment) + time), 0, AilmentMaximum));
        if (time > 0 && ailment == 2 && sourceTile is not null)
        {
            var at = Reach.ObjectAt(w, sourceTile);
            if (at.Kind != 255)
            {
                b.Bytes[0x62] = (byte)Math.Clamp(at.Kind, 0, 47);
                BitConverter.GetBytes((ushort)Math.Clamp(at.Number, 0, 65535)).CopyTo(b.Bytes, 0x64);
            }
        }
    }

    // 0x178470 / 0x175F60: single ailments 2..4 and all ailments 23; the latter skips stop for bases.
    internal static List<Applied> ApplyAilment(World w, Rec tactic, Tactic.Side source, int slot, int links, int kind, IReadOnlyList<Rec> targets)
    {
        if (kind is not (2 or 3 or 4 or 23)) throw new ArgumentOutOfRangeException(nameof(kind));
        int sourceForce = Objects.Valid(w, source.Unit) ? Objects.UnitForce(w, source.Unit!) : Objects.Valid(w, source.Base) ? Map.BaseForce(w, source.Base!) : 0;
        Rec force = w.Entry(Tables.Forces, sourceForce, 51);
        Rec? sourceTile = SideTile(w, source);
        var results = new List<Applied>();
        foreach (Rec tile in targets)
        {
            var at = Reach.ObjectAt(w, tile);
            Rec? unit = at.Kind == 27 ? w.Entry(Tables.Units, at.Number, 500) : null;
            Rec? b = at.Kind is 5 or 6 ? TargetBase(w, at.Kind, at.Number) : null;
            var target = new Tactic.Side(unit, b);
            if (!Objects.Valid(w, unit) && !Objects.Valid(w, b) || !AilmentSucceeds(w, tactic, source, target)) continue;
            float value = (float)Amount(tactic, slot) * AttackFactor(w, tactic, source, target) * ((float)unchecked(links * LinkPercent) / 100f + 1f) * SupplyFactor(w, source);
            if (unit is not null || kind == 23)
                value = value * ((force.U8(0x193) & 4) != 0 ? AilmentForceDurationPercent : 100f) / 100f;
            int duration = Objects.Trunc(value);
            for (int k = kind == 23 ? 0 : kind - 2; k <= (kind == 23 ? 2 : kind - 2); k++)
            {
                if (unit is not null) AddUnitAilment(w, unit, source.Unit, k, duration, sourceTile);
                else if (kind != 23 || k != 1) AddBaseAilment(w, b!, source.Unit, k, duration, sourceTile);
            }
            results.Add(new Applied(at.Kind, at.Number, tile.Index, 0));
        }
        return results;
    }
}
