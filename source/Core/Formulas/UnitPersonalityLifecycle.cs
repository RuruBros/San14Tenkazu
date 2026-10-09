using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Propagation lists retain native wrapper storage while their mutable payloads are cleared or erased.
internal sealed class UnitPersonalityLifecycle(World w, IReadOnlyDictionary<string, List<Rec?>> propagated,
    bool refreshAvailable, Action<Rec> refresh)
{
    private static void Word(Rec p, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p.Bytes.AsSpan(at), unchecked((ushort)value));
    private Rec Commander(Rec unit) => w.OfficerById(unit.U16(0x12)) ??
        throw new InvalidOperationException("The native personality helper dereferences a null commander.");
    private List<Rec?> Members(Rec unit, int slot) => propagated[$"unit:{unit.Index}:{0x160 + slot * 16:x}"];
    private int FirstSlot(Rec commander, int personality)
    {
        for (int slot = 0; slot < 9; slot++)
            if (Personality.Slot(w, commander, slot) == personality) return slot;
        return -1;
    }
    private static void ClearReceived(Rec unit, int slot)
    {
        Word(unit, 0x64 + slot * 2, 0);
        Word(unit, 0xAC + slot * 2, 0);
        Word(unit, 0xF4 + slot * 2, 0);
    }

    // 0x2A1F00 -> 0x234D80/0x234E60/0x2265C0. Duplicate personality IDs always use the first slot's list.
    internal void Deactivate(Rec unit, int slot)
    {
        Rec commander = Commander(unit);
        if (slot >= 0)
        {
            if (slot < 9) Word(commander, 0x184, commander.U16(0x184) & ~(1 << slot));
            if (slot < 10) Word(commander, 0x186 + slot * 2, 0);
        }
        int personality = Personality.Slot(w, commander, slot);
        int first = FirstSlot(commander, personality);
        foreach (Rec? recipient in Members(unit, first >= 0 ? first : 0))
        {
            if (!Objects.Valid(w, recipient)) continue;
            for (int received = 0; received < 36; received++)
                if (recipient!.U16(0xAC + received * 2) == unit.U16(0x12) &&
                    recipient.U16(0x64 + received * 2) == personality) ClearReceived(recipient, received);
        }
        if (first >= 0) Members(unit, first).Clear();
        if (refreshAvailable) refresh(unit);
    }

    // 0x2287C0 -> 0x335060: erase every recipient alias from the first matching personality slot.
    internal int RemoveRecipient(Rec source, int personality, Rec recipient)
    {
        int slot = FirstSlot(Commander(source), personality);
        if (slot < 0) return 0;
        Members(source, slot).RemoveAll(p => ReferenceEquals(p, recipient));
        return 1;
    }

    // 0x2A2100. The source is resolved before clearing the three parallel arrays; notify controls reverse removal.
    internal void DropReceived(Rec unit, int slot, bool notify)
    {
        Rec sourcePerson = w.OfficerById(unit.U16(0xAC + slot * 2)) ??
            throw new InvalidOperationException("The native received-personality helper dereferences a null source officer.");
        Rec source = Objects.UnitOf(w, sourcePerson);
        if (Objects.UnitValid(source) && notify)
        {
            RemoveRecipient(source, unit.U16(0x64 + slot * 2), unit);
            if (refreshAvailable) refresh(source);
        }
        if ((uint)slot < 36) ClearReceived(unit, slot);
    }
}
