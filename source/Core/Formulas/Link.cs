namespace San14Tenkazu.Formulas;

// How well two officers work together (rva 0x2A6B60, the game's debug line "連携値"): 친애 and 혐오 both ways,
// 의형제, 부부, 부모자식, 형제, 혈연, 상성, the 개성 effect 0x6C and the same-force bonus, summed. Units fight
// alongside each other by its grade (0x2A6AA0).
internal static class Link
{
    [Rva(0x1A4A268)] internal const int Like = 15;
    [Rva(0x1A4A274)] internal const int Dislike = 20;
    [Rva(0x1A4A27C)] internal const int Sworn = 50;
    [Rva(0x1A4A288)] internal const int Spouse = 40;
    [Rva(0x1A4A294)] internal const int ParentChild = 15;
    [Rva(0x1A4A2A0)] internal const int Siblings = 15;
    [Rva(0x1A4A2AC)] internal const int Clan = 10;
    [Rva(0x1A4A2B8)] internal const int Compatibility = 10;
    [Rva(0x1A4A2C4)] internal const int FamilyFactor = 2;
    [Rva(0x1A4A2D0)] internal const int SameForce = 10;
    [Rva(0x1A4A240)] internal const int Grade3 = 11;
    [Rva(0x1A4A24C)] internal const int Grade4 = 30;
    [Rva(0x1A4A254)] internal const int Grade5 = 47;
    // The grade's floor is a global the game never sets (exe + 0x27D2694, 0): any value from it up is grade 2+.
    internal const string GradeFloor = "link_floor";

    // 0x2A6B60.
    internal static int Value(World w, Rec a, Rec b, bool personality)
    {
        if (!Objects.PersonValid(a) || !Objects.PersonValid(b)) return 0;
        int factor = Factor(a, b);
        bool plain = Plain(w, a, b);

        int like = (Contains(Likes(w, a), b) ? Like : 0) + (Contains(Likes(w, b), a) ? Like : 0);
        bool hates = false;
        int dislike = 0;
        if (Contains(Dislikes(w, a), b)) { dislike = Dislike; hates = true; }
        if (Contains(Dislikes(w, b), a)) { dislike += Dislike; hates = true; }
        int likeTerm = like * factor;
        int dislikeTerm = -(dislike * factor);

        int sworn = IsSworn(a, b) ? Sworn : 0;
        int spouse = plain && IsSpouse(a, b) ? Spouse : 0;
        bool family = IsFather(a, b) || IsMother(a, b) || IsFather(b, a) || IsMother(b, a);
        int parent = family && plain ? ParentChild : 0;
        bool siblings = AreSiblings(a, b);
        int sibling = siblings && plain ? Siblings : 0;
        int clan = plain && !family && !siblings && a.I16(0xB2) != 0 && a.I16(0xB2) == b.I16(0xB2) ? Clan : 0;

        int diff = CompatibilityDiff(a, b);
        int compat = Objects.Trunc(MathF.Round((75f - diff) * Compatibility / 75f, MidpointRounding.AwayFromZero));
        if (compat > Compatibility) compat = Compatibility;

        int trait = personality && !hates && Personality.Has(w, b, 0x6C) ? Personality.EffectSum(w, b, 0x6C) : 0;

        int force = 0;
        Rec fa = Objects.ForceOf(w, a);
        if (ReferenceEquals(fa, Objects.ForceOf(w, b)) && (fa.U8(0x178) & 1) != 0 && !Hates(a, b))
            force = Hates(b, a) ? 0 : SameForce;

        return trait + force + compat + clan + sibling + parent + spouse + sworn + dislikeTerm + likeTerm;
    }

    // 0x2A6AA0: the grade 1..5 of a link (1 for no link).
    internal static int Grade(World w, Rec? a, Rec? b, bool personality)
    {
        if (a is null || b is null || !Objects.PersonValid(a) || !Objects.PersonValid(b)) return 1;
        int v = Value(w, a, b, personality);
        if (v < w.Value(GradeFloor)) return 1;
        if (v < Grade3) return 2;
        if (v < Grade4) return 3;
        return v >= Grade5 ? 5 : 4;
    }

    // 0x2AFAC0: family (spouse, parent or child, siblings, same clan +0xB2) count double.
    private static int Factor(Rec a, Rec b) =>
        IsSpouse(a, b) || IsFather(a, b) || IsMother(a, b) || IsFather(b, a) || IsMother(b, a) || AreSiblings(a, b)
        || (a.I16(0xB2) != 0 && a.I16(0xB2) == b.I16(0xB2)) ? FamilyFactor : 1;

    // 0x2C8180: neither likes nor hates the other, and they are not sworn brothers.
    private static bool Plain(World w, Rec a, Rec b) =>
        !Contains(Likes(w, a), b) && !Contains(Likes(w, b), a) && !Contains(Dislikes(w, a), b) && !Contains(Dislikes(w, b), a)
        && !IsSworn(a, b);

    // 0x22E310 / 0x22B840: the real officers in the eight 친애 (+0xC0) or 혐오 (+0xD0) slots.
    internal static List<Rec> Likes(World w, Rec p) => Slots(w, p, 0xC0);
    internal static List<Rec> Dislikes(World w, Rec p) => Slots(w, p, 0xD0);
    private static List<Rec> Slots(World w, Rec p, int at)
    {
        var list = new List<Rec>();
        for (int i = 0; i < 8; i++)
            if (w.OfficerById(p.U16(at + 2 * i)) is { } o && Objects.PersonValid(o)) list.Add(o);
        return list;
    }

    private static bool Contains(List<Rec> list, Rec p) => list.Any(x => ReferenceEquals(x, p));

    // 0x232A00: b's ID is in a's 혐오 slots.
    internal static bool Hates(Rec a, Rec b)
    {
        if (!Objects.PersonValid(b)) return false;
        for (int i = 0; i < 8; i++) if (a.I16(0xD0 + 2 * i) == b.I16(0x10)) return true;
        return false;
    }

    private static bool Same(Rec a, Rec b) => a.I16(0x10) == b.I16(0x10);
    // 0x2C9BD0 의형제 (+0xBC), 0x2C8AC0 부부 (+0xBA), 0x2C5780 b is a's father (+0xB4), 0x2C6EF0 mother (+0xB6),
    // 0x2C3B50 same father or mother.
    private static bool IsSworn(Rec a, Rec b) => !Same(a, b) && a.I16(0xBC) != 0 && a.I16(0xBC) == b.I16(0xBC);
    private static bool IsSpouse(Rec a, Rec b) => !Same(a, b) && a.I16(0xBA) != 0 && a.I16(0xBA) == b.I16(0x10);
    internal static bool IsFather(Rec a, Rec b) => !Same(a, b) && a.I16(0xB4) != 0 && a.I16(0xB4) == b.I16(0x10);
    internal static bool IsMother(Rec a, Rec b) => !Same(a, b) && a.I16(0xB6) != 0 && a.I16(0xB6) == b.I16(0x10);
    private static bool AreSiblings(Rec a, Rec b) =>
        !Same(a, b) && ((a.I16(0xB4) != 0 && a.I16(0xB4) == b.I16(0xB4)) || (a.I16(0xB6) != 0 && a.I16(0xB6) == b.I16(0xB6)));

    // 0x2A5E20: the distance of the two 상성 (+0xBE) on the circle of 150, 75 at most.
    internal static int CompatibilityDiff(Rec a, Rec b)
    {
        int d = Math.Abs(a.U8(0xBE) - b.U8(0xBE));
        if (150 - d < d) d = 150 - d;
        return Math.Min(d, 75);
    }
}
