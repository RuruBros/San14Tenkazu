using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static class UnitLifecycle
{
    // 0xA2000: erase the exact native unit-pointer key; the unit record and other mapped values are preserved.
    internal static void RemoveUnitCache<T>(IDictionary<ulong, T> cache, ulong unitPointer) => cache.Remove(unitPointer);

    // 0xA22A0: a mapped UI object takes precedence over CTroopsData+0x30.
    // Property cleanup and owner-driven destruction remain explicit UI-engine callbacks, in native order.
    internal static void RemoveTroopCache(Rec troop, IDictionary<ulong, ulong> cache,
        Func<ulong, ulong> ownerOf, Action<ulong, int> cleanupProperties, Action<ulong, ulong> destroyObject)
    {
        bool found = cache.TryGetValue(troop.NativeAddress, out ulong uiObject);
        if (!found) uiObject = BinaryPrimitives.ReadUInt64LittleEndian(troop.Bytes.AsSpan(0x30));
        if (found || uiObject != 0)
        {
            ulong owner = ownerOf(uiObject);
            cleanupProperties(uiObject, 2);
            destroyObject(owner, uiObject);
            BinaryPrimitives.WriteUInt64LittleEndian(troop.Bytes.AsSpan(0x30), 0);
        }
        if (found) cache.Remove(troop.NativeAddress);
    }

    // 0x227D80: preserve the record header, padding and UI association unless the available manager removes it.
    internal static void ResetTroop(Rec troop, bool cacheManagerAvailable, Action<Rec> removeCache)
    {
        byte[] b = troop.Bytes;
        b.AsSpan(0x10, 3).Clear();
        b.AsSpan(0x14, 4).Clear();
        for (int at = 0x18; at <= 0x28; at += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), 0xBD10BD10);
        b.AsSpan(0x2C, 4).Clear();
        b[0x38] = 0;
        if (BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(0x30)) != 0 && cacheManagerAvailable) removeCache(troop);
    }

    // 0x241C90: assign the signed-clamped ID before testing the old group's remaining-member validity.
    internal static void SetTroop(World w, Rec unit, int id, Action<Rec> removeCache)
    {
        int oldId = unit.U16(0x58);
        Rec oldTroop = w.Entry(Tables.Troops, oldId, 500);
        int assigned = Math.Clamp(id, 0, 500);
        BinaryPrimitives.WriteUInt16LittleEndian(unit.Bytes.AsSpan(0x58), (ushort)assigned);
        if ((assigned == 0 || oldId != assigned) && oldTroop.Index != 0 && !TroopsValid(w, oldTroop))
            DissolveTroop(w, oldTroop, removeCache);
    }

    // 0x282540: only null/entry zero is rejected; invalid and empty groups are still reset.
    // Enumerate live member fields so recursive dissolution does not revisit detached members on unwind.
    internal static bool DissolveTroop(World w, Rec? troop, Action<Rec> removeCache)
    {
        if (troop is null || troop.Index == 0) return false;
        foreach (Rec unit in TroopMembers(w, troop)) SetTroop(w, unit, 0, removeCache);
        removeCache(troop);
        ResetTroop(troop, true, removeCache);
        return true;
    }

    // 0x282130 -> 0x32AE20/0x2269F0 and 0x335060: scan every caution record, including entry zero.
    // Callers supply accessible external lists; an absent/out-of-range wrapper has no accessible list.
    // A matching army caution is reset even if it is invalid or absent from World+0x130.
    internal static void CleanupCautions(World w, Rec unit, List<Rec?>? registered,
        IReadOnlyDictionary<int, List<Rec?>> troopLists)
    {
        foreach (Rec caution in w.Table(Tables.CautionUnits))
        {
            if (caution.U16(0x10) != unit.Index) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(caution.Bytes.AsSpan(0x10), 0);
            registered?.RemoveAll(entry => ReferenceEquals(entry, caution));
        }
        foreach (Rec caution in w.Table(Tables.CautionTroops))
            if (troopLists.TryGetValue(caution.Index, out List<Rec?>? list))
                list.RemoveAll(entry => ReferenceEquals(entry, unit));
    }

    // 0x222F20/0x333350: World+0x60 order, group IDs 1..500, excluding 0x233440 pending units.
    // The iterator does not call the unit's validity method.
    internal static IEnumerable<Rec> TroopMembers(World w, Rec troop)
    {
        if ((uint)(troop.Index - 1) > 499) yield break;
        foreach (Rec?[] node in w.List(Lists.UnitList))
        {
            Rec unit = node[0]!;
            if (unit.U16(0x58) == troop.Index && !Map.Engaged(w, unit)) yield return unit;
        }
    }

    // CTroopsData slot +0x18, 0x234540 -> 0x22BCB0: the first remaining member's valid officer has corps 1..51.
    internal static bool TroopsValid(World w, Rec troop)
    {
        if (troop.Index == 0) return false;
        Rec? first = TroopMembers(w, troop).FirstOrDefault();
        Rec? person = first is null ? null : w.OfficerById(first.U16(0x12));
        int corps = person is not null && Objects.PersonValid(person) ? person.U8(0xE0) : 0;
        return (uint)(corps - 1) <= 50;
    }

    // 0x2822C9..0x28241F: compare resolved officer pointers, then visit the materialized native member order.
    // The first different pointer ends the search even when its ID is 6000 or greater and cannot be assigned.
    // Temporary-list availability is supplied by the external wrapper allocator boundary.
    internal static void ReplaceCommander(World w, Rec unit, bool memberListAvailable)
        => ReplaceCommander(w, unit, () => memberListAvailable);

    internal static void ReplaceCommander(World w, Rec unit, Func<bool> memberListAvailable)
    {
        Rec troop = w.Entry(Tables.Troops, unit.U16(0x58), 500);
        if (!TroopsValid(w, troop) || !ReferenceEquals(w.OfficerById(troop.U16(0x2C)), w.OfficerById(unit.U16(0x12)))) return;
        if (!memberListAvailable()) return;
        foreach (Rec candidate in TroopMembers(w, troop).ToArray())
        {
            int id = candidate.U16(0x12);
            if (ReferenceEquals(w.OfficerById(id), w.OfficerById(troop.U16(0x2C)))) continue;
            if (id < 6000) BinaryPrimitives.WriteUInt16LittleEndian(troop.Bytes.AsSpan(0x2C), (ushort)id);
            break;
        }
    }

    // 0x2288B0: invoke the UI manager only for an attached object, then clear the association.
    internal static void DetachUi(Rec unit, Action<Rec> detachObject)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(unit.Bytes.AsSpan(0x148)) == 0) return;
        detachObject(unit);
        BinaryPrimitives.WriteUInt64LittleEndian(unit.Bytes.AsSpan(0x148), 0);
    }

    // 0x32ADA0 -> 0x335060: report whether the pointer occurs, then erase all its aliases.
    internal static bool RemoveActiveUnit(World w, Rec? unit)
    {
        List<Rec?[]> active = w.List(Lists.UnitList);
        if (!active.Any(node => ReferenceEquals(node[0], unit))) return false;
        active.RemoveAll(node => ReferenceEquals(node[0], unit));
        return true;
    }

    // 0x22A240: count the live filtered iterator without allocating a temporary member list.
    internal static int TroopMemberCount(World w, Rec troop) => TroopMembers(w, troop).Count();

    // 0x28E290: visit the initialized World+0x70 list, then return its nodes to the pool.
    // 0x282130 remains an engine callback; the caller performs no validity or null filtering.
    internal static void ConsumePending(LinkedList<Rec?> pending, Action<Rec?> finalize)
    {
        var node = pending.First;
        while (node is not null)
        {
            finalize(node.Value);
            node = node.Next;
        }
        pending.Clear();
    }

    // 0x226830: reset the complete unit record without replacing its header, padding or list wrappers.
    // Each initialized external list at +0x160 + 16*k is cleared through the explicit pool boundary.
    internal static void Reset(Rec unit, bool nodePoolEnabled, Action<int> clearList)
    {
        byte[] b = unit.Bytes;
        void Zero(int at, int length) => b.AsSpan(at, length).Clear();
        void Word(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), value);
        void Dword(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
        Zero(0x12, 8);
        Zero(0x1B, 2);
        Zero(0x1D, 9);
        b[0x26] = 0;
        Dword(0x28, 0xBD1001F5);
        Zero(0x2C, 12);
        b[0x38] = 0xFF;
        Word(0x3A, 0);
        b[0x3C] = 0xFF;
        Word(0x3E, 0);
        Dword(0x40, 0xBD100000);
        Word(0x44, 0xBD10);
        b[0x46] = 0;
        b[0x1A] = 0;
        Word(0x48, 0xBD10);
        Zero(0x4A, 12);
        b[0x56] = 0;
        Word(0x58, 0);
        b[0x10] = 0;
        Zero(0x5C, 8);
        Zero(0x64, 0xD8);
        b[0x13C] = 0;
        Zero(0x148, 8);
        b[0x150] = 0;
        Word(0x152, 0xBD10);
        for (int at = 0x160; at < 0x1F0; at += 16)
            if (nodePoolEnabled && BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at)) != 0) clearList(at);
        Zero(0x1E8, 6);
        Zero(0x1F0, 8);
        Word(0x1F8, 0xBD10);
        Word(0x13E, 0xBD10);
        Dword(0x140, 0xFF00);
    }
}
