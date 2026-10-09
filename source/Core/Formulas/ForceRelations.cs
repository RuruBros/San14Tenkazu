namespace San14Tenkazu.Formulas;

// 0x2700A0 relationship change; 0x2586E0 updates both directions, while the return observes a -> b only.
internal static class ForceRelations
{
    // 0x229730: the first matching destination wins; only then may an empty appointment be selected.
    internal static int DiplomaticSlot(Rec force, uint destination, bool allowEmpty)
    {
        if (destination == 0) return -1;
        for (int i = 0; i < 3; i++) if (force.U8(0x19C + i) == destination) return i;
        if (allowEmpty)
            for (int i = 0; i < 3; i++)
                if (force.U16(0x196 + i * 2) == 0 || force.U8(0x19C + i) == 0) return i;
        return -1;
    }

    // 0x58440: unsigned slot bounds, followed by the bounded officer-ID lookup.
    internal static Rec? DiplomaticOfficer(World w, Rec force, uint slot) =>
        w.OfficerById(slot < 3 ? force.U16(0x196 + (int)slot * 2) : 0);

    // The record mutation in 0x277C40. The caller emits player history/message requests after a successful clear.
    internal static Rec? ClearDiplomaticAppointment(World w, Rec? force, Rec? destination)
    {
        if (!Objects.Valid(w, force) || !Objects.Valid(w, destination) || ReferenceEquals(force, destination)) return null;
        int slot = DiplomaticSlot(force!, (uint)destination!.Index, false);
        if (slot < 0) return null;
        Rec? person = DiplomaticOfficer(w, force!, (uint)slot);
        if (!Objects.Valid(w, person)) return null;
        force!.Bytes[0x19C + slot] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(force.Bytes.AsSpan(0x196 + slot * 2), 0);
        return person;
    }

    [Rva(0x1A481F8)] internal const int Band1Min = 1;
    [Rva(0x1A481D4)] internal const int Band2Min = 30;
    [Rva(0x1A481DC)] internal const int Band3Min = 50;
    [Rva(0x1A481E4)] internal const int Band4Min = 70;
    [Rva(0x1A481F0)] internal const int Band5Min = 100;

    internal static int Band(int value) => value < Band1Min ? 0 : value < Band2Min ? 1 :
        value < Band3Min ? 2 : value < Band4Min ? 3 : value < Band5Min ? 4 : 5;

    // 0x2EBCC0: retain bit 7 and replace the bilateral low bits with a signed-clamped value.
    // Unlike ChangeNpc, this setter includes invalid, zero and identical force records.
    internal static void SetAgreementNpc(World w, Rec a, Rec b, int value)
    {
        if (Objects.IsPlayerForce(w, a) || Objects.IsPlayerForce(w, b))
            throw new NotSupportedException("Player agreement changes require their refresh and flag policy.");
        SetAgreement(w, a, b, value);
    }

    // 0x2EBCC0: city refreshes are emitted to the host when its presentation manager is available.
    // The final flag policy uses the original signed value, not the clamped low bits.
    internal static void SetAgreement(World w, Rec a, Rec b, int value, Action<Rec>? refreshCity = null)
    {
        byte low = (byte)Math.Clamp(value, 0, 48);
        if ((uint)b.Index < 52) a.Bytes[0xEA + b.Index] = (byte)((a.U8(0xEA + b.Index) & 0x80) | low);
        if ((uint)a.Index < 52) b.Bytes[0xEA + a.Index] = (byte)((b.U8(0xEA + a.Index) & 0x80) | low);
        if ((Objects.IsPlayerForce(w, a) || Objects.IsPlayerForce(w, b)) && refreshCity is not null)
            foreach (Rec force in new[] { a, b })
                foreach (Rec city in w.Table(Tables.Cities))
                    if (force.Index is >= 1 and <= 51 && Map.BaseForce(w, city) == force.Index) refreshCity(city);
        if (value is < 1 or > 3)
        {
            if (Objects.IsPlayerForce(w, a)) a.Bytes[0xEA + b.Index] &= 0x7F;
            else if (Objects.IsPlayerForce(w, b)) b.Bytes[0xEA + a.Index] &= 0x7F;
        }
    }

    internal static int ChangeNpc(World w, Rec? a, Rec? b, int delta)
    {
        if (!Objects.Valid(w, a) || !Objects.Valid(w, b) || a!.Index == b!.Index) return 0;
        if ((uint)(a.Index - 46) < 5 || (uint)(b.Index - 46) < 5)
            throw new NotSupportedException("Tribe band changes require the separate tribe-response body.");
        if (Objects.IsPlayerForce(w, a) || Objects.IsPlayerForce(w, b))
            throw new NotSupportedException("Player relationship changes require their notification policy.");
        return ChangeSilent(w, a, b, delta);
    }

    // 0x2700A0 with notification0: player membership alone does not emit presentation requests.
    // A tribal grade transition still needs 0x27ACA0, which is a gameplay body.
    internal static int ChangeSilent(World w, Rec? a, Rec? b, int delta)
    {
        if (!Objects.Valid(w, a) || !Objects.Valid(w, b) || a!.Index == b!.Index) return 0;
        int before = Band(a.U8(0x13 + b.Index));
        int next = Math.Clamp(unchecked(a.U8(0x13 + b.Index) + delta), 0, 200);
        if (before != Band(next) && ((uint)(a.Index - 46) < 5 || (uint)(b.Index - 46) < 5))
            throw new NotSupportedException("A tribal grade transition requires the tribe-response gameplay body.");
        a.Bytes[0x13 + b.Index] = (byte)next;
        b.Bytes[0x13 + a.Index] = (byte)Math.Clamp(unchecked(b.U8(0x13 + a.Index) + delta), 0, 200);
        int after = Band(a.U8(0x13 + b.Index));
        return before == after ? 0xFF : after;
    }
}
