namespace San14Tenkazu.Formulas;

// 개성 (CPersonalityData, raw 0x7DD18) as the game's rules read them: an officer's slots, and the value an effect kind
// has for him.
internal static class Personality
{
    internal const int Count = 0x164;

    // CPersonalityData slot +0x18 (0x234270): every 개성 but entry 0; entries 0x100..0x163 only with an effect.
    internal static bool Valid(Rec p) => p.Index != 0 && ((uint)(p.Index - 0x100) > 0x63 || p.U8(0xB6) != 0);

    // 0x16F7F0: a bit of the 개성's flags (+0xBF; kept encoded in the +0xC8 vector, rotate 1, for the entries below
    // 0x100 that have one). Bit 4: it works in slots.
    internal static bool Flag(Rec p, int bit)
    {
        byte[] v = p.VectorC8 ?? [];
        int flags = (uint)(p.Index - 0x100) < 100 || (v.Length & ~3) != 4
            ? p.U8(0xBF)
            : Objects.DecodeWord(BitConverter.ToUInt16(v, 0), 1, p.Index) & 0xFF;
        return (uint)bit < 8 && (flags & (1 << bit)) != 0;
    }

    // 0x2305D0: the 개성 in an officer's slot (0..8): 0 for slots past the fifth, and for one without flag 4 (the
    // 새 무장 IDs 5101..5200 skip that test).
    internal static int Slot(World w, Rec person, int slot)
    {
        if ((uint)(person.U16(0x10) - 0x13ED) > 0x63)
        {
            Rec rec = w.Entry(Tables.Personalities, person.U16(0x118 + slot * 2), 0x163);
            if (Valid(rec) && !Flag(rec, 4)) return 0;
        }
        return slot > 4 ? 0 : person.U16(0x118 + slot * 2);
    }

    // 0x22C630: the value effect `kind` has in a 개성 (its first effect +0xB6/+0xB8, else its second +0xBA/+0xBC),
    // -100..2000; `none` when it has no such effect.
    internal static int EffectValue(Rec p, int kind, int none)
    {
        int v;
        if (p.U8(0xB6) == kind) v = p.I16(0xB8);
        else if (p.U8(0xBA) == kind) v = p.I16(0xBC);
        else return none;
        return v < -100 ? -100 : v > 2000 ? 2000 : v;
    }

    // 0x2B2DA0: an officer's total of effect `kind` over his nine slots.
    internal static int EffectSum(World w, Rec person, int kind)
    {
        if (!Objects.PersonValid(person)) return 0;
        int sum = 0;
        for (int slot = 0; slot < 9; slot++)
        {
            Rec rec = w.Entry(Tables.Personalities, Slot(w, person, slot), 0x163);
            if (Valid(rec)) sum += EffectValue(rec, kind, 0);
        }
        return sum;
    }

    // 0x232D00: the officer has 개성 `id` (1..355) in a slot.
    internal static bool HasPersonality(World w, Rec person, int id)
    {
        if ((uint)(id - 1) >= 0x163) return false;
        for (int slot = 0; slot < 9; slot++)
            if (Slot(w, person, slot) == id) return true;
        return false;
    }

    // Unit_EffectSums (0x26A2C0) as battle reads it (not judging conditions afresh): per effect kind (0..126), the
    // 개성 the unit received from others (+0x64, 36 of them) unless only its own count, and its commander's that are
    // lit for it (+0x184 bit per slot) or always on (+0xB4 == -1).
    internal static int[] UnitSums(World w, Rec unit, bool ownAlwaysOnly)
    {
        var sums = new int[0x7F];
        if (!Objects.UnitValid(unit)) return sums;
        Rec? cmd = w.OfficerById(unit.U16(0x12));
        if (!ownAlwaysOnly)
            for (int k = 0; k < 36; k++)
            {
                Rec p = w.Entry(Tables.Personalities, unit.U16(0x64 + 2 * k), 0x163);
                if (Valid(p)) AddEffects(p, sums);
            }
        for (int slot = 0; slot < 9; slot++)
        {
            Rec p = w.Entry(Tables.Personalities, cmd is null ? 0 : Slot(w, cmd, slot), 0x163);
            if (!Valid(p)) continue;
            bool on = p.I16(0xB4) == -1 || (!ownAlwaysOnly && (cmd!.U16(0x184) & (1 << slot)) != 0);
            if (on) AddEffects(p, sums);
        }
        return sums;
    }

    private static void AddEffects(Rec p, int[] sums)
    {
        int k1 = p.U8(0xB6), k2 = p.U8(0xBA);
        if (k1 < 0x7F) sums[k1] += Math.Clamp((int)p.I16(0xB8), -100, 2000);
        if (k2 < 0x7F) sums[k2] += Math.Clamp((int)p.I16(0xBC), -100, 2000);
    }

    // 0x26A750, kind 0: officer slot sums; repeated effect kinds within one personality count once.
    internal static int[] OfficerSums(World w, Rec person, bool alwaysOnly)
    {
        var sums = new int[0x7F];
        if (!Objects.PersonValid(person)) return sums;
        for (int slot = 0; slot < 9; slot++)
        {
            Rec p = w.Entry(Tables.Personalities, Slot(w, person, slot), 0x163);
            if (!Valid(p) || alwaysOnly && p.I16(0xB4) != -1) continue;
            int last = 0;
            for (int effect = 0; effect < 2; effect++)
            {
                int k = p.U8(effect == 0 ? 0xB6 : 0xBA);
                if (k == last) continue;
                if (k < sums.Length) sums[k] += Math.Clamp((int)p.I16(effect == 0 ? 0xB8 : 0xBC), -100, 2000);
                last = k;
            }
        }
        return sums;
    }

    // 0x22C3B0: how far a 개성's condition reaches: 1 for conditions 2, 3, 0x17, 0x18; for 9, 10, 0x15, 0x16, 0x1F,
    // 0x20, 0x27, 0x28 the 개성's own +0xA8 when it is one of 0x100..0x163 with a positive value, else 3 (exe
    // 0x1A48FD8); 0 for the rest.
    [Rva(0x1A48FD8)] internal const int DefaultRange = 3;
    internal static int Range(Rec p)
    {
        switch (p.U8(0xA4))
        {
            case 2 or 3 or 0x17 or 0x18: return 1;
            case 9 or 10 or 0x15 or 0x16 or 0x1F or 0x20 or 0x27 or 0x28:
                if ((uint)(p.Index - 0x100) > 99) return DefaultRange;
                int r = p.I32(0xA8);
                return r < 1 ? DefaultRange : r;
            default: return 0;
        }
    }

    // 0x2C37F0: one of the officer's 개성 with effect `kind` is on: always (+0xB4 == -1) or lit for him (+0x184 bit).
    internal static bool HasLit(World w, Rec person, int kind)
    {
        if (!Objects.PersonValid(person)) return false;
        for (int slot = 0; slot < 9; slot++)
        {
            Rec p = w.Entry(Tables.Personalities, Slot(w, person, slot), 0x163);
            if (Valid(p) && (p.U8(0xB6) == kind || p.U8(0xBA) == kind) && (p.I16(0xB4) == -1 || (person.U16(0x184) & (1 << slot)) != 0)) return true;
        }
        return false;
    }

    // 0x2C89D0: one of the 36 개성 a unit received (+0x64) has effect `kind`.
    internal static bool ReceivedHas(World w, Rec unit, int kind)
    {
        for (int k = 0; k < 36; k++)
        {
            int id = unit.U16(0x64 + 2 * k);
            if ((uint)(id - 1) >= 0x163) continue;
            Rec p = w.Entry(Tables.Personalities, id, 0x163);
            if (Valid(p) && (p.U8(0xB6) == kind || p.U8(0xBA) == kind)) return true;
        }
        return false;
    }

    // 0x2AB2E0: the first of the officer's 개성 with effect `kind`, null without one.
    internal static Rec? FindKind(World w, Rec person, int kind)
    {
        if (!Objects.PersonValid(person)) return null;
        for (int slot = 0; slot < 9; slot++)
        {
            Rec rec = w.Entry(Tables.Personalities, Slot(w, person, slot), 0x163);
            if (Valid(rec) && (rec.U8(0xB6) == kind || rec.U8(0xBA) == kind)) return rec;
        }
        return null;
    }

    // 0x2C5460: one of the officer's 개성 has effect `kind`.
    internal static bool Has(World w, Rec person, int kind)
    {
        if (!Objects.PersonValid(person)) return false;
        for (int slot = 0; slot < 9; slot++)
        {
            Rec rec = w.Entry(Tables.Personalities, Slot(w, person, slot), 0x163);
            if (Valid(rec) && (rec.U8(0xB6) == kind || rec.U8(0xBA) == kind)) return true;
        }
        return false;
    }
}
