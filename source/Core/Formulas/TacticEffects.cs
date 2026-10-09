using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// CTacticsData's two effects and the hex lists built before a tactic fires.
internal static partial class TacticEffects
{
    [Rva(0x1A48AA4)] internal const int LinkPercent = 25;
    [Rva(0x1A48B04)] internal const int SupportMartialDivisor = 150;
    [Rva(0x1A48B0C)] internal const int SupportIntelligenceDivisor = 150;
    [Rva(0x1A48B98)] internal const int BrokenSupplyPercent = 140;
    [Rva(0x1A48BA4)] internal const int NoSupplyPercent = 180;
    [Rva(0x1A48B44)] internal const int HealingMinimum = 500;
    [Rva(0x1A48070)] internal const int AilmentMaximum = 120;

    internal readonly record struct Applied(int Kind, int Number, int Hex, int Amount);

    private static byte[]? Encoded(Rec tactic)
    {
        if (tactic.Vector70 is { } v) return (v.Length & ~3) == 40 ? v : null;
        long begin = BinaryPrimitives.ReadInt64LittleEndian(tactic.Bytes.AsSpan(0x70));
        long end = BinaryPrimitives.ReadInt64LittleEndian(tactic.Bytes.AsSpan(0x78));
        if (((end - begin) & ~3L) == 40)
            throw new InvalidOperationException($"Tactic {tactic.Index} has an encoded vector missing from the snapshot.");
        return null;
    }

    private static int Value(Rec tactic, int slot, int word, int rotate, int plain) =>
        Encoded(tactic) is { } v ? Objects.Decode(v[(slot + word) * 4], rotate, tactic.Index)
            : tactic.U8(plain + Math.Clamp(slot, 0, 1));

    // 0x16D510, 0x16D5A0, 0x16D630, 0x16D6C0: encoded slots are not clamped; plain slots are 0..1.
    internal static int Kind(Rec tactic, int slot) => Value(tactic, slot, 1, 7, 0x62);
    internal static int Range(Rec tactic, int slot) => Value(tactic, slot, 3, 1, 0x64);
    internal static int Amount(Rec tactic, int slot) => Value(tactic, slot, 5, 3, 0x66);
    internal static int Level(Rec tactic, int slot) => Value(tactic, slot, 7, 5, 0x68);

    // 0x16CDA0: 1 selects martial ability, every other value selects intelligence.
    internal static int AbilityKind(Rec tactic) => Encoded(tactic) is { } v
        ? Objects.Decode(v[36], 7, tactic.Index) : tactic.U8(0x6A);

    // 0x16FC30: flags are the first encoded dword's low byte, or the plain byte at +0x44.
    internal static bool Flag(Rec tactic, int bit)
    {
        int flags = Encoded(tactic) is { } v ? Objects.Decode(v[0], 1, tactic.Index) : tactic.U8(0x44);
        return (uint)bit < 8 && (flags & (1 << bit)) != 0;
    }

    // 0x16BE90: each slot contains hexes in native order, with its center first. Target-centered effects must
    // retain the target itself or their entire list is discarded. Only fire (17) accepts an unresolved object.
    internal static List<Rec>[] Targets(World w, Rec? tactic, Rec? source, Rec? target, int sourceForce)
    {
        List<Rec>[] result = [[], []];
        if (!Objects.Valid(w, tactic) || !Objects.Valid(w, source)) return result;
        for (int slot = 0; slot < 2; slot++)
        {
            int kind = Kind(tactic!, slot);
            if (kind == 0 || (kind == 17 && Objects.Valid(w, target) && !Reach.Hostile(w, sourceForce, target!.U8(0x14)))) continue;
            bool centeredOnTarget = kind is 1 or 2 or 3 or 4 or 10 or 11 or 12 or 13 or 14 or 16 or 17 or 18 or 22 or 23;
            Rec? center = centeredOnTarget ? target : kind is 5 or 6 or 7 or 8 or 9 or 15 or 19 or 20 or 21 ? source : null;
            if (!Objects.Valid(w, center)) continue;
            List<int> hexes = Map.Ring(center!.Index, Range(tactic!, slot));
            hexes.Insert(0, center.Index);
            var buildings = new HashSet<Rec>();
            foreach (int hex in hexes)
            {
                Rec tile = w.Table(Tables.Tiles)[hex];
                var at = Reach.ObjectAt(w, tile);
                if (at.Kind == 0xFF && kind != 17) continue;
                int force = 0;
                Rec? unit = null, baseRec = null;
                if (at.Kind is 5 or 6)
                {
                    if (!Flag(tactic!, 0)) continue;
                    Rec building = Reach.Building(w, tile);
                    if (!Map.BuildingValid(building) || buildings.Contains(building)) continue;
                    baseRec = w.Entry(at.Kind == 5 ? Tables.Cities : Tables.Gates, at.Number, at.Kind == 5 ? 0x33 : 10);
                    if (!Objects.Valid(w, baseRec) || w.List(Lists.Engagements).Any(e => Objects.Valid(w, e[1]) && ReferenceEquals(e[1], baseRec))) continue;
                    buildings.Add(building);
                    force = building.U8(0x13);
                }
                else if (at.Kind == 0x1B)
                {
                    unit = w.Entry(Tables.Units, at.Number, 500);
                    if (!Objects.UnitValid(unit) || Map.Engaged(w, unit)) continue;
                    force = Objects.UnitForce(w, unit);
                }
                else if (kind != 17) continue;

                bool accepted = kind switch
                {
                    1 or 2 or 3 or 4 or 16 => Reach.Hostile(w, sourceForce, force),
                    5 or 6 or 7 or 8 or 9 or 21 => sourceForce == force && unit is not null,
                    10 or 11 or 12 or 13 or 14 or 22 => Reach.Hostile(w, sourceForce, force) && unit is not null,
                    15 => sourceForce == force,
                    17 => tile.Index != source!.Index,
                    18 => Reach.Hostile(w, sourceForce, force) && at.Kind is 5 or 6 or 0xB,
                    19 => sourceForce == force && (unit is not null ? HasAilment(unit, 0x23) : baseRec is not null && HasAilment(baseRec, 0x49)),
                    20 => sourceForce == force && unit is not null && CanHeal(w, unit),
                    23 => Reach.Hostile(w, sourceForce, force) && (unit is not null || Flag(tactic!, 0) && at.Kind is 5 or 6),
                    _ => false,
                };
                if (accepted) result[slot].Add(tile);
            }
            if (centeredOnTarget && !result[slot].Contains(target!)) result[slot].Clear();
        }
        return result;
    }

    private static bool HasAilment(Rec r, int at) => r.U8(at) != 0 || r.U8(at + 1) != 0 || r.U8(at + 2) != 0;
    private static bool CanHeal(World w, Rec unit) => w.OfficerById(unit.U16(0x12)) is { } p
        && Objects.PersonValid(p) && unit.U16(0x18) != 0 && unit.U16(0x16) < TroopCapacity.Get(w, p);

    // 0x16B640: the source's martial/intelligence support multiplier, including the base governor.
    internal static float SupportFactor(World w, Rec tactic, Tactic.Side source)
    {
        bool unit = Objects.Valid(w, source.Unit), baseOk = Objects.Valid(w, source.Base);
        if (!unit && !baseOk) return 0f;
        Rec? person = w.OfficerById(unit ? source.Unit!.U16(0x12) : source.Base!.U16(0x2E));
        int ability = Objects.Valid(w, person) ? OfficerAbility.Get(w, person!, AbilityKind(tactic) == 1 ? 1 : 2, OfficerAbility.All) : 0;
        return (float)ability / (AbilityKind(tactic) == 1 ? SupportMartialDivisor : SupportIntelligenceDivisor) + 1f;
    }

    // 0x16D870: supply flags on the source unit; bases and supplied units use 1.
    internal static float SupplyFactor(World w, Tactic.Side source) => Objects.Valid(w, source.Unit) && source.Unit!.U8(0x21) == 0
        ? (float)(source.Unit.U8(0x22) != 0 ? BrokenSupplyPercent : NoSupplyPercent) / 100f : 1f;

    // 0x176320 / 0x178260 / 0x177E40: apply morale recovery (15), ailment removal (19), or wounded recovery (20).
    // The returned entries are exactly the gameplay fields written to the native result vector (0x17A130).
    internal static List<Applied> ApplySupport(World w, Rec tactic, Tactic.Side source, int slot, int links, int kind, IReadOnlyList<Rec> targets)
    {
        if (kind is not (15 or 19 or 20)) throw new ArgumentOutOfRangeException(nameof(kind));
        var result = new List<Applied>();
        float factor = kind == 19 ? 0f : SupportFactor(w, tactic, source);
        float linked = (float)unchecked(links * LinkPercent) / 100f + 1f;
        float supply = kind == 19 ? 0f : SupplyFactor(w, source);
        int moraleAdd = kind == 15 ? Objects.Trunc((float)Amount(tactic, slot) * factor * linked * supply) : 0;
        foreach (Rec tile in targets)
        {
            var at = Reach.ObjectAt(w, tile);
            Rec? unit = at.Kind == 0x1B ? w.Entry(Tables.Units, at.Number, 500) : null;
            Rec? b = at.Kind is 5 or 6 ? Map.Base(w, at.Kind == 5 ? at.Number < 52 ? at.Number : 0 : (uint)(at.Number - 1) < 10 ? at.Number + 51 : 0) : null;
            Rec? target = Objects.Valid(w, unit) ? unit : Objects.Valid(w, b) ? b : null;
            if (target is null) continue;
            int amount = 0;
            if (kind == 19)
            {
                if (unit is not null && unit.U8(0x25) != 0)
                {
                    unit.Bytes[0x3C] = 0xFF;
                    BinaryPrimitives.WriteUInt16LittleEndian(unit.Bytes.AsSpan(0x3E), 0);
                }
                int off = unit is not null ? 0x23 : 0x49;
                for (int i = 0; i < 3; i++) target.Bytes[off + i] = AilmentMaximum < 0 ? unchecked((byte)AilmentMaximum) : (byte)0;
            }
            else if (kind == 15)
            {
                int forceIndex = unit is not null ? Objects.UnitForce(w, unit) : Map.BaseForce(w, b!);
                Rec force = w.Entry(Tables.Forces, forceIndex, 51);
                int cap = BattlePower.MaxMorale(w, force), off = unit is not null ? 0x1A : 0x44;
                if (unit is not null || (uint)(force.Index - 46) > 4)
                {
                    int before = unit is not null ? Math.Min((int)unit.U8(off), cap) : b!.U8(off);
                    int next = Math.Min(unchecked(before + moraleAdd), cap);
                    target.Bytes[off] = (byte)(next < 0 ? 0 : Math.Min(next, cap));
                }
            }
            else
            {
                Rec? cmd = unit is not null ? w.OfficerById(unit.U16(0x12)) : null;
                if (unit is not null && !Objects.Valid(w, cmd)) continue;
                int troops = unit is not null ? unit.U16(0x16) : b!.I32(0x3C);
                int wounded = unit is not null ? unit.U16(0x18) : b!.I32(0x40);
                int capacity = unit is not null ? TroopCapacity.Get(w, cmd!) : Engagement.MaxTroops(w, b!);
                float healed = (float)wounded * factor / Math.Max(1, 20 - Amount(tactic, slot));
                healed = (healed <= HealingMinimum ? HealingMinimum : healed) * linked * supply;
                healed = (float)wounded <= healed ? wounded : healed;
                amount = Math.Min(Objects.Trunc(healed), unchecked(capacity - troops));
                int troopMax = unit is not null ? 65535 : capacity, woundedMax = unit is not null ? 20000 : capacity;
                int nextTroops = unchecked(troops + amount), nextWounded = unchecked(wounded - amount);
                nextTroops = nextTroops < 0 ? 0 : nextTroops > troopMax ? troopMax : nextTroops;
                nextWounded = nextWounded < 0 ? 0 : nextWounded > woundedMax ? woundedMax : nextWounded;
                if (unit is not null)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(unit.Bytes.AsSpan(0x16), (ushort)nextTroops);
                    BinaryPrimitives.WriteUInt16LittleEndian(unit.Bytes.AsSpan(0x18), (ushort)nextWounded);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(b!.Bytes.AsSpan(0x3C), nextTroops);
                    BinaryPrimitives.WriteInt32LittleEndian(b.Bytes.AsSpan(0x40), nextWounded);
                }
            }
            result.Add(new Applied(at.Kind, at.Number, tile.Index, amount & 0xFFFF));
        }
        return result;
    }
}
