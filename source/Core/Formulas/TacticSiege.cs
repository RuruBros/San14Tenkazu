using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    [Rva(0x1A48BFC)] internal const int SiegeMildReductionPercent = 30;
    // The caller owns capture/destruction and battle-event processing after these formula writes.
    internal readonly record struct SiegeApplication(List<Applied> Results, List<Rec> PendingBases, List<Rec> DestroyedBuildings);

    // 0x177580: kind 18 requires a source unit, even when its context also contains a base.
    internal static SiegeApplication ApplySiege(World w, Rec tactic, Tactic.Side source, int slot, int links, IReadOnlyList<Rec> targets)
    {
        var results = new List<Applied>();
        var pending = new List<Rec>();
        var destroyed = new List<Rec>();
        if (!Objects.Valid(w, source.Unit)) return new(results, pending, destroyed);
        int damage = Objects.Trunc((float)Amount(tactic, slot) * SupportFactor(w, tactic, source)
            * ((float)unchecked(links * LinkPercent) / 100f + 1f) * SupplyFactor(w, source));
        if (w.Settings.I32(0xE58) != 0)
        {
            if (damage > 0 && w.Settings.I32(0xB8) == 0)
                damage = unchecked(damage - unchecked(SiegeMildReductionPercent * damage) / 100);
        }
        else damage = unchecked(BattlePower.Slider(w, 2) * damage) / 100;
        List<Rec?[]> queue = w.List(Lists.Engagements);
        int initialCount = queue.Count;
        try
        {
            foreach (Rec tile in targets)
            {
                Rec building = Reach.Building(w, tile);
                if (!Map.BuildingValid(building)) continue;
                Rec? b = Reach.IsCity(building) ? w.Entry(Tables.Cities, building.Index, 51)
                    : Reach.IsGate(building) ? w.Entry(Tables.Gates, building.Index - 51, 10) : null;
                if (Reach.IsCity(building) && Damage.TribeCityWithGovernor(w, b!))
                {
                    Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
                    if (!Objects.ForceValid(player) || Objects.UnitForce(w, source.Unit!) != player.Index) continue;
                }
                if (Bombard.Allowed(w, building) && !w.List(Lists.SiegeExempt).Any(node => ReferenceEquals(node[0], building)))
                {
                    int adjusted = Bombard.AdjustedDamage(w, building, damage, source.Unit);
                    if (adjusted > 0 && w.OfficerById(source.Unit!.U16(0x12)) is { } commander)
                    {
                        var xp = Experience.Gain(w, commander, 1, adjusted);
                        BinaryPrimitives.WriteUInt16LittleEndian(commander.Bytes.AsSpan(0x1C0), (ushort)xp.Merit);
                        BinaryPrimitives.WriteInt32LittleEndian(commander.Bytes.AsSpan(0x104), xp.Exp);
                        commander.Bytes[0xEC] = (byte)xp.Level;
                    }
                    var hit = Bombard.Apply(w, building, damage, source.Unit);
                    BinaryPrimitives.WriteUInt16LittleEndian(building.Bytes.AsSpan(0x14), (ushort)hit.Durability);
                    if (b is not null && b.U8(0x66) != 3 && Objects.Valid(w, b)) b.Bytes[0x66] = (byte)Engagement.DamageLevel(w, b);
                    if (hit.Fell)
                    {
                        if (b is not null && !queue.Any(node => Objects.Valid(w, node[1]) && node[1]!.U16(0x4E) == b.U16(0x4E)))
                        {
                            pending.Add(b);
                            // Native 0x31EAB0 stores the source unit in field 2 of this base-fall request.
                            // 0x233440 consequently treats the source as engaged before the next experience award.
                            queue.Add([null, b, source.Unit]);
                        }
                        else if (b is null) destroyed.Add(building);
                    }
                }
                var at = Reach.ObjectAt(w, tile);
                if (at.Kind != 255) results.Add(new Applied(at.Kind, at.Number, tile.Index, damage & 0xFFFF));
            }
        }
        finally { queue.RemoveRange(initialCount, queue.Count - initialCount); }
        return new(results, pending, destroyed);
    }
}
