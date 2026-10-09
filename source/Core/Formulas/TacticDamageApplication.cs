using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    internal readonly record struct PendingDestruction(Rec? Unit, Rec? Base, Rec? AttackerUnit, Rec? AttackerBase, int Mode, int Reason, int CaptureMode = 0);
    internal readonly record struct DamageApplication(List<Applied> Results, Dictionary<Rec, int> UnitDamage,
        Dictionary<Rec, int> BaseDamage, List<PendingDestruction> Pending, bool Destroyed);

    // 0x176B80 applies splash targets immediately; center is deferred while forceImmediate (+0x30) is false.
    // Queue requests are kept visible within the leaf, then returned for the caller's engine processing.
    internal static DamageApplication ApplyDamage(World w, Rec tactic, Tactic.Side source, int slot, int links,
        IReadOnlyList<Rec> targets, int center, bool forceImmediate)
    {
        var results = new List<Applied>();
        var units = new Dictionary<Rec, int>();
        var bases = new Dictionary<Rec, int>();
        var pending = new List<PendingDestruction>();
        List<Rec?[]> queue = w.List(Lists.Engagements);
        int count = queue.Count;
        bool destroyed = false, fromUnit = Objects.Valid(w, source.Unit), fromBase = Objects.Valid(w, source.Base);
        if (!fromUnit && !fromBase) return new(results, units, bases, pending, destroyed);
        int kind = fromUnit ? 27 : source.Base!.U16(0x4E) < 52 ? 5 : 6;
        int index = fromUnit ? source.Unit!.Index : kind == 5 ? source.Base!.U16(0x4E) : source.Base!.U16(0x4E) - 51;
        int hex = fromUnit ? source.Unit!.U16(0x2A) : Map.BaseHex(w, source.Base!);
        int force = fromUnit ? Objects.UnitForce(w, source.Unit!) : Map.BaseForce(w, source.Base!);
        int initialTroops = fromUnit ? source.Unit!.U16(0x16) : source.Base!.I32(0x3C);
        var attacker = new Engagement.Side(index, kind, hex, force, 1);
        void Enqueue(PendingDestruction request)
        {
            bool present = queue.Any(e => Objects.Valid(w, request.Unit) && Objects.Valid(w, e[0]) && request.Unit!.Index == e[0]!.Index
                || Objects.Valid(w, request.Base) && Objects.Valid(w, e[1]) && request.Base!.U16(0x4E) == e[1]!.U16(0x4E));
            if (!present)
            {
                queue.Add([request.Unit, request.Base, request.AttackerUnit]);
                pending.Add(request);
            }
        }
        try
        {
            foreach (Rec tile in targets)
            {
                var at = Reach.ObjectAt(w, tile);
                int? computed = DamageFor(w, tactic, source, slot, links, tile, initialTroops);
                if (computed is not { } damage) continue;
                Rec? unit = at.Kind == 27 ? w.Entry(Tables.Units, at.Number, 500) : null;
                Rec? b = at.Kind is 5 or 6 ? TargetBase(w, at.Kind, at.Number) : null;
                if (forceImmediate || tile.Index != center)
                {
                    if (fromUnit && damage > 0 && w.OfficerById(source.Unit!.U16(0x12)) is { } commander)
                    {
                        var xp = Experience.Gain(w, commander, 0, damage);
                        BinaryPrimitives.WriteUInt16LittleEndian(commander.Bytes.AsSpan(0x1C0), (ushort)xp.Merit);
                        BinaryPrimitives.WriteInt32LittleEndian(commander.Bytes.AsSpan(0x104), xp.Exp);
                        commander.Bytes[0xEC] = (byte)xp.Level;
                    }
                    if (unit is not null)
                    {
                        if (unit.U8(0x10) == 2)
                        {
                            int appliedDamage = Math.Min(damage, (int)unit.U16(0x16));
                            float share = (float)appliedDamage / unit.U16(0x16);
                            int gold = Objects.Trunc((float)unit.I32(0x5C) * share), food = Objects.Trunc((float)unit.I32(0x60) * share);
                            Rec? recipient = fromUnit ? null : source.Base;
                            if (fromUnit && Objects.ForceValid(w.Entry(Tables.Forces, force, 51)) && force != 51 && (uint)(force - 46) > 4)
                            {
                                Rec? leader = w.OfficerById(source.Unit!.U16(0x12));
                                recipient = Map.Base(w, Objects.Valid(w, leader) ? leader!.U16(0xE2) : 0);
                            }
                            if (Objects.Valid(w, recipient)) Engagement.AddBaseResources(w, recipient!, gold, food);
                        }
                        Rec person = w.OfficerById(unit.U16(0x12)) ?? w.Table(Tables.Officers)[0];
                        int oldLit = person.U16(0x184);
                        var after = Engagement.ApplyUnit(w, unit, damage, attacker, minimumDamage: false, routeDefersDestruction: true);
                        BinaryPrimitives.WriteUInt16LittleEndian(unit.Bytes.AsSpan(0x16), (ushort)after.Troops);
                        BinaryPrimitives.WriteUInt16LittleEndian(unit.Bytes.AsSpan(0x18), (ushort)after.Wounded);
                        unit.Bytes[0x1A] = (byte)after.Morale;
                        BinaryPrimitives.WriteInt32LittleEndian(unit.Bytes.AsSpan(0x5C), after.Gold);
                        BinaryPrimitives.WriteInt32LittleEndian(unit.Bytes.AsSpan(0x60), after.Food);
                        BinaryPrimitives.WriteUInt16LittleEndian(person.Bytes.AsSpan(0x184), (ushort)after.Lit);
                        for (int k = 0; k < 9; k++)
                            if ((after.Lit & (1 << k)) != 0 && (oldLit & (1 << k)) == 0)
                            {
                                Rec trait = w.Entry(Tables.Personalities, Personality.Slot(w, person, k), 355);
                                // 0x178E30 zero-extends the stored word before passing it to 0x17A090.
                                BinaryPrimitives.WriteUInt16LittleEndian(person.Bytes.AsSpan(0x186 + 2 * k), (ushort)Math.Min((int)trait.U16(0xB4), 720));
                            }
                        if (after.Routed)
                        {
                            Rec sourceTile = w.Entry(Tables.Tiles, hex, 48399);
                            Rec building = Reach.Building(w, sourceTile);
                            Rec? routeBase = Map.BuildingValid(building) && (Reach.IsCity(building) || Reach.IsGate(building))
                                ? Map.Base(w, building.Index) : null;
                            Rec? routeUnit = routeBase is null && Reach.UnitOnHex(sourceTile)
                                ? w.Entry(Tables.Units, sourceTile.U16(0x1C), 500) : null;
                            Enqueue(new(unit, null, routeUnit, routeBase, 1, 0));
                        }
                        if (after.Destroyed)
                        {
                            destroyed = true;
                            Enqueue(new(unit, null, fromUnit ? source.Unit : null, fromUnit ? null : source.Base, 1, 0));
                        }
                    }
                    else if (b is not null)
                    {
                        var after = Engagement.ApplyBase(w, b, damage, attacker, minimumDamage: false);
                        BinaryPrimitives.WriteInt32LittleEndian(b.Bytes.AsSpan(0x3C), after.Troops);
                        BinaryPrimitives.WriteInt32LittleEndian(b.Bytes.AsSpan(0x40), after.Wounded);
                        b.Bytes[0x44] = (byte)after.Morale;
                        if (after.Fell)
                        {
                            destroyed = true;
                            Enqueue(new(null, b, fromUnit ? source.Unit : null, fromUnit ? null : source.Base, 0, 4));
                        }
                    }
                }
                results.Add(new Applied(at.Kind, at.Number, tile.Index, damage & 0xFFFF));
                if (unit is not null) units[unit] = unchecked(units.GetValueOrDefault(unit) + damage);
                else if (b is not null) bases[b] = unchecked(bases.GetValueOrDefault(b) + damage);
            }
        }
        finally { queue.RemoveRange(count, queue.Count - count); }
        return new(results, units, bases, pending, destroyed);
    }
}
