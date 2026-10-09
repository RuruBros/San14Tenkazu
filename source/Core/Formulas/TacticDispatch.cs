using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    [Rva(0x1A49D34)] internal const int SupportMerit = 60;
    internal readonly record struct DispatchApplication(int ResultIndex, List<Applied>[] Slots,
        Dictionary<Rec, int> UnitDamage, Dictionary<Rec, int> BaseDamage, List<PendingDestruction> Pending, bool Destroyed);

    // 0x178900: resolve the source, execute both slots in order, and award non-damage unit tactics once.
    // The caller owns result storage, persistent damage maps, and subsequent engine destruction processing.
    internal static DispatchApplication Dispatch(World w, Rec? tactic, int sourceKind, int sourceNumber,
        Rec? mainUnit, IReadOnlyList<Rec?> previousLinkedTactics, IReadOnlyList<Rec>[] targets,
        int links, int center, bool forceImmediate)
    {
        List<Applied>[] slots = [[], []];
        var units = new Dictionary<Rec, int>();
        var bases = new Dictionary<Rec, int>();
        var pending = new List<PendingDestruction>();
        bool destroyed = false;
        DispatchApplication Empty() => new(-1, slots, units, bases, pending, destroyed);
        if (!Objects.Valid(w, tactic)) return Empty();
        Rec? unit = sourceKind == 27 ? w.Entry(Tables.Units, sourceNumber, 500) : null;
        Rec? b = sourceKind is 5 or 6 ? TargetBase(w, sourceKind, sourceNumber) : null;
        if (!Objects.Valid(w, unit) && !Objects.Valid(w, b)) return Empty();
        int resultIndex = 0;
        if (unit is not null && (!Objects.Valid(w, mainUnit) || unit.Index != mainUnit!.Index))
        {
            int count = 0;
            while (count < 10 && count < previousLinkedTactics.Count && Objects.Valid(w, previousLinkedTactics[count])) count++;
            // 0x16D750 returns zero when all ten linked entries are valid.
            resultIndex = 1 + (count == 10 ? 0 : count);
        }
        var source = new Tactic.Side(unit, b);
        var queue = w.List(Lists.Engagements);
        int initialCount = queue.Count;
        bool damageKind = false;
        void Enqueue(PendingDestruction request)
        {
            if (queue.Any(e => Objects.Valid(w, request.Unit) && Objects.Valid(w, e[0]) && request.Unit!.Index == e[0]!.Index
                || Objects.Valid(w, request.Base) && Objects.Valid(w, e[1]) && request.Base!.U16(0x4E) == e[1]!.U16(0x4E))) return;
            queue.Add([request.Unit, request.Base, request.AttackerUnit]);
            pending.Add(request);
        }
        try
        {
            for (int slot = 0; slot < 2; slot++)
            {
                int kind = Kind(tactic!, slot);
                damageKind |= kind is 1 or 18;
                if (kind == 1)
                {
                    var applied = ApplyDamage(w, tactic!, source, slot, links, targets[slot], center, forceImmediate);
                    slots[slot] = applied.Results;
                    foreach (var entry in applied.UnitDamage) units[entry.Key] = unchecked(units.GetValueOrDefault(entry.Key) + entry.Value);
                    foreach (var entry in applied.BaseDamage) bases[entry.Key] = unchecked(bases.GetValueOrDefault(entry.Key) + entry.Value);
                    foreach (var request in applied.Pending) Enqueue(request);
                    destroyed |= applied.Destroyed;
                }
                else if (kind is 2 or 3 or 4 or 23) slots[slot] = ApplyAilment(w, tactic!, source, slot, links, kind, targets[slot]);
                else if (kind is >= 5 and <= 14 or 21 or 22) slots[slot] = ApplyBoost(w, tactic!, source, slot, links, kind, targets[slot]);
                else if (kind is 15 or 19 or 20) slots[slot] = ApplySupport(w, tactic!, source, slot, links, kind, targets[slot]);
                else if (kind == 16)
                {
                    var applied = ApplyMorale(w, tactic!, source, slot, links, targets[slot]);
                    slots[slot] = applied.Results;
                    int sourceHex = unit is not null ? unit.U16(0x2A) : Map.BaseHex(w, b!);
                    Rec tile = w.Entry(Tables.Tiles, sourceHex, 48399), building = Reach.Building(w, tile);
                    Rec? attackerBase = Map.BuildingValid(building) && (Reach.IsCity(building) || Reach.IsGate(building)) ? Map.Base(w, building.Index) : null;
                    Rec? attackerUnit = attackerBase is null && Reach.UnitOnHex(tile) ? w.Entry(Tables.Units, tile.U16(0x1C), 500) : null;
                    foreach (Rec routed in applied.Routed) Enqueue(new(routed, null, attackerUnit, attackerBase, 1, 0));
                    destroyed |= applied.Routed.Count != 0;
                }
                else if (kind == 17) slots[slot] = ApplyFire(w, tactic!, source, slot, links, center, targets[slot]);
                else if (kind == 18)
                {
                    var applied = ApplySiege(w, tactic!, source, slot, links, targets[slot]);
                    slots[slot] = applied.Results;
                    foreach (Rec fallen in applied.PendingBases) Enqueue(new(null, fallen, unit, null, 0, 4));
                }
            }
            if (sourceKind == 27 && !damageKind && w.OfficerById(unit!.U16(0x12)) is { } person && Objects.Valid(w, person))
                BinaryPrimitives.WriteUInt16LittleEndian(person.Bytes.AsSpan(0x1C0), (ushort)Experience.Merit(w, person, unchecked(SupportMerit * 10)));
        }
        finally { queue.RemoveRange(initialCount, queue.Count - initialCount); }
        return new(resultIndex, slots, units, bases, pending, destroyed);
    }
}
