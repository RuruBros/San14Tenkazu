namespace San14Tenkazu.Formulas;

// A force's 정책 effects (rva 0x41C200): the officers whose 정책 counts are visited (CForce 0x26AA20), each adds his
// 정책 level to every effect kind his 정책 has (0x258D90), and the sum, at most 10, picks the effect's value at that
// level from CPolicyEffectData (0x22C590).
internal static class Policy
{
    internal const int LevelMax = 10;

    // 0x41C200: the value of effect `kind` (1..100) for a force; 0 for a force that is not a real one.
    internal static int Effect(World w, Rec force, int kind)
    {
        if (!Objects.ForceValid(force)) return 0;
        int level = Math.Min(Levels(w, force, kind), LevelMax);
        if (level <= 0) return 0;
        Rec rec = w.Entry(Tables.PolicyEffects, kind, 0x64);
        return rec.Index != 0 ? LevelValue(rec, level) : 0;
    }

    // 0x41C440: return the summed policy level, rather than its effect value.
    internal static int Level(World w, Rec force, int kind) =>
        Objects.ForceValid(force) ? Math.Min(Levels(w, force, kind), LevelMax) : 0;

    // 0x26AA20 with only enacted slots (its third argument 1): the ruler (+0x10), the strategist (+0x52) unless
    // +0x16C is set, then the five fields' seven slots (+0x54, +0x72, +0x90, +0xAE, +0xCC) whose days left
    // (+0x62, +0x80, +0x9E, +0xBC, +0xDA) are 0.
    private static int Levels(World w, Rec force, int kind)
    {
        int level = 0;
        Visit(w, w.OfficerById(force.U16(0x10)), kind, ref level);
        if (force.I16(0x16C) == 0) Visit(w, w.OfficerById(force.U16(0x52)), kind, ref level);
        for (int field = 0x2A; field <= 0x74; field += 0xF)
            for (int slot = 0; slot < 7; slot++)
            {
                int at = (field + slot) * 2;
                if (force.I16(0xE + at) != 0) continue;
                Visit(w, w.OfficerById(force.U16(at)), kind, ref level);
            }
        return level;
    }

    // 0x258D90: an officer's 정책 (+0x14D) adds his level (+0x182, with his 공훈 bonus, at most 10) to each of its
    // eight effect kinds (CPolicyData +0x98..+0x9F, 1..100) that is `kind`.
    private static void Visit(World w, Rec? person, int kind, ref int level)
    {
        if (person is null || !Objects.PersonValid(person)) return;
        Rec policy = w.Entry(Tables.Policies, person.U8(0x14D), 0x78);
        if (policy.Index == 0) return;
        int own = Math.Min(person.U8(0x182) + LevelBonus(w, person, 3), LevelMax);
        for (int j = 0; j < 8; j++)
        {
            int k = policy.U8(0x98 + j);
            if (k is >= 1 and <= 100 && k == kind) level = Math.Max(0, own + Math.Min(level, LevelMax));
        }
    }

    // 0x22C590: the effect's value at a level (1..10), kept encoded in its ten-entry vector (rotate 11, keyed by
    // the record's index); a record without the vector reads +0x82.
    internal static int LevelValue(Rec rec, int level)
    {
        int i = Math.Clamp(level - 1, 0, 9);
        byte[] vec = rec.Vector98 ?? [];
        if ((vec.Length & ~3) == 0x28) return Objects.DecodeWord(BitConverter.ToUInt16(vec, i * 4), 11, rec.Index);
        return rec.U16(0x82 + i * 2);
    }

    // 0x2A9D30: the extra 정책 level an officer's 공훈 gives (kind 3); none in the challenge screens.
    internal static int LevelBonus(World w, Rec person, int kind)
    {
        if (!Objects.PersonValid(person)) return 0;
        if (w.Flag("mgr_2143268") && w.WorldState.I8(0xBC) != -1) return 0;
        if (w.WorldState.I32(0x44) == 3 && w.Flag("cc_person_select")) return 0;
        return CreditBonus(w, person, kind);
    }

    // 0x2A9BB0: the sum of `kind` bonuses of the 공훈 steps an officer has reached (+0x1C2 of steps 1..6, +0x1C3 of
    // steps 4..6); rulers get 2 of kind 2 only.
    internal static int CreditBonus(World w, Rec p, int kind)
    {
        if (Objects.InForces46To50(w, p) || Objects.SpecialOfficer(w, p, false) || (p.U16(0x15E) & 0x4000) != 0) return 0;
        if (p.U8(0xE6) == 1) return kind == 2 ? 2 : 0;
        if (p.U8(0x1BC) == 0) return 0;
        Rec[] credits = w.Table(Tables.Credits);
        int sum = 0;
        for (int i = 1; i <= p.U8(0x1C2); i++)
        {
            Rec c = credits[i <= 6 ? i : 0];
            if (c.U8(0x10) == kind) sum += c.U16(0x12);
        }
        for (int j = 0; j < p.U8(0x1C3); j++)
        {
            Rec c = credits[j + 4 < 7 ? j + 4 : 0];
            if (c.U8(0x10) == kind) sum += c.U16(0x12);
        }
        return sum;
    }
}
