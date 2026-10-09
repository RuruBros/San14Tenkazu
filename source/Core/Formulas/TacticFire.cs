namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    [Rva(0x1A48BE8)] internal const int TacticFirePolicyFactor = 1;
    internal const int FireMaximum = 720;

    // 0x16E570: policy effect on the source force, excluding tribe forces and force 51.
    internal static float SourcePolicy(World w, Tactic.Side source, int kind)
    {
        int id = Objects.Valid(w, source.Unit) ? Objects.UnitForce(w, source.Unit!) : Objects.Valid(w, source.Base) ? Map.BaseForce(w, source.Base!) : 0;
        Rec force = w.Entry(Tables.Forces, id, 51);
        return Objects.ForceValid(force) && (uint)(id - 46) > 4 && id != 51 ? (float)unchecked(Policy.Effect(w, force, kind) * TacticFirePolicyFactor) / 100f : 0f;
    }

    // 0x2E39E0: unit immunity precedes base immunity; an always-on governor fire immunity only works at home.
    // 0x2EDD40 writes only the timer for gameplay; the fire renderer and speech records are outside this method.
    internal static void Ignite(World w, Rec tile, int duration)
    {
        if (!Objects.Valid(w, tile)) return;
        Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 30);
        if (!Objects.Valid(w, terrain)) return;
        int index = tile.I16(0x1C);
        Rec unit = w.Entry(Tables.Units, index < 0 ? 0 : index, 500);
        if (Objects.Valid(w, unit))
        {
            if (Map.Engaged(w, unit)) return;
            Rec? leader = w.OfficerById(unit.U16(0x12));
            if (leader is not null && Personality.FindKind(w, leader, 25) is { } immune && immune.I16(0xB4) == -1) return;
        }
        else
        {
            Rec? b = tile.U16(0x1A) < 52 ? w.Table(Tables.Cities)[tile.U16(0x1A)]
                : (uint)(tile.U16(0x1A) - 52) < 10 ? w.Table(Tables.Gates)[tile.U16(0x1A) - 51] : null;
            if (Objects.Valid(w, b))
            {
                if (w.List(Lists.Engagements).Any(e => Objects.Valid(w, e[1]) && ReferenceEquals(e[1], b))) return;
                Rec? governor = w.OfficerById(b!.U16(0x2E));
                if (governor is not null && Objects.PersonValid(governor))
                {
                    int location = governor.U16(0xE4) <= 61 ? governor.U16(0xE4) : 0;
                    if ((uint)(location - 1) < 61 && location == governor.U16(0xE2)
                        && Personality.FindKind(w, governor, 25) is { } immune && immune.I16(0xB4) == -1) return;
                }
            }
        }
        if (terrain.I16(0x2A) == 0 || tile.I16(0x16) != 0) return;
        if (terrain.U8(0x41) == 0 && ((terrain.U8(0x2C) & 2) == 0 || !Objects.Valid(w, unit))) return;
        BitConverter.GetBytes((ushort)Math.Clamp(duration, 0, FireMaximum)).CopyTo(tile.Bytes, 0x16);
    }

    // 0x177BD0: skip friendly units and friendly standing city/gate buildings, and report only the center tile.
    internal static List<Applied> ApplyFire(World w, Rec tactic, Tactic.Side source, int slot, int links, int center, IReadOnlyList<Rec> targets)
    {
        int duration = Objects.Trunc((float)Amount(tactic, slot) * SupportFactor(w, tactic, source) * ((float)unchecked(links * LinkPercent) / 100f + 1f) * SupplyFactor(w, source));
        duration = unchecked(duration + Objects.Trunc(SourcePolicy(w, source, 19) * duration));
        int force = Objects.Valid(w, source.Unit) ? Objects.UnitForce(w, source.Unit!) : Objects.Valid(w, source.Base) ? Map.BaseForce(w, source.Base!) : 0;
        var results = new List<Applied>();
        foreach (Rec tile in targets)
        {
            Rec building = Reach.Building(w, tile);
            if (Map.BuildingValid(building) && (Reach.IsCity(building) || Reach.IsGate(building)) && !Reach.Hostile(w, force, building.U8(0x13))) continue;
            if (Reach.UnitOnHex(tile))
            {
                Rec unit = w.Entry(Tables.Units, tile.I16(0x1C), 500);
                if (Objects.Valid(w, unit) && !Reach.Hostile(w, force, Objects.UnitForce(w, unit))) continue;
            }
            Ignite(w, tile, duration);
            if (tile.Index == center)
            {
                var at = Reach.ObjectAt(w, tile);
                // 0x17A130 drops result entries for an empty/engaged target (kind 255).
                if (at.Kind != 255) results.Add(new Applied(at.Kind, at.Number, tile.Index, 0));
            }
        }
        return results;
    }
}
