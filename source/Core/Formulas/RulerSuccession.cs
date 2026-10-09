namespace San14Tenkazu.Formulas;

internal static class RulerSuccession
{
    [Rva(0x1A49A34)] internal const int SeniorMerit = 10;
    [Rva(0x1A49A28)] internal const int SeniorOfficeGrade = 2;

    internal enum SelectionKind { DissolveForce, Single, PlayerChoice, Historical, Predicate, Ability }
    internal readonly record struct Selection(SelectionKind Kind, Rec? Officer, int Predicate = -1);

    // 0x28C180 through 0x28CB07: decide the successor before history, notifications or record mutation.
    // Dissolution and player choice are requests for the caller; this method executes neither body.
    internal static Selection SelectBeforeSuccession(World w, Rec dying)
    {
        Rec force = Objects.ForceOf(w, dying);
        if (force.Index is < 1 or > 51) return new(SelectionKind.DissolveForce, null);
        var serving = w.List(Lists.OfficerList).Select(n => n[0] ??
                throw new NotSupportedException("The native force iterator dereferences a null officer node."))
            .Where(p => OfficerRelations.RawForce(w, p) == force.Index &&
                p.U8(0xE6) is >= 1 and <= 4).ToList();
        if (serving.Count(p => OfficerCapture.CountedOfficer(w, p)) == 1 || Capture.CityCount(w, force) == 0)
            return new(SelectionKind.DissolveForce, null);
        var candidates = serving.Where(p => p.U16(0x10) != dying.U16(0x10) &&
            !Objects.SpecialOfficer(w, p, false) && (p.U16(0x15E) & 0x4000) == 0).ToList();
        if (candidates.Count == 1) return new(SelectionKind.Single, candidates[0]);
        if (Objects.IsPlayerForce(w, force)) return new(SelectionKind.PlayerChoice, null);

        Rec? selected = null;
        if (w.WorldState.I32(0x50) != 0)
        {
            foreach (var (ruler, successor) in new (int, int)[]
                { (557, 553), (521, 526), (952, 945), (35, 34), (953, 946), (638, 625) })
            {
                if (dying.U16(0x10) != ruler) continue;
                Rec person = w.OfficerById(successor) ??
                    throw new NotSupportedException("The native historical successor lookup dereferences a missing officer.");
                if (person.U8(0xE6) is >= 1 and <= 4 && OfficerRelations.RawForce(w, person) == force.Index)
                {
                    selected = person;
                    break;
                }
            }
            if (Objects.Valid(w, selected)) return new(SelectionKind.Historical, selected);
        }

        List<Rec> eligible = [];
        int predicate = 0;
        for (; predicate < 8; predicate++)
        {
            eligible = candidates.Where(p => Matches(w, p, dying, predicate)).ToList();
            if (eligible.Any(p => p.U8(0xA9) == 0)) break;
        }
        if (eligible.Count != 0)
        {
            SortCandidates(w, dying, eligible);
            return new(SelectionKind.Predicate, eligible[0], Math.Min(predicate, 7));
        }
        int best = 0;
        foreach (Rec person in candidates)
        {
            int total = 0;
            for (int ability = 0; ability < 5; ability++)
                total = unchecked(total + OfficerAbility.Get(w, person, ability, OfficerAbility.All));
            if (total > best) { best = total; selected = person; }
        }
        return new(SelectionKind.Ability, selected);
    }

    // The caller passes candidate first and dying officer second, in this exact predicate order.
    internal static bool Matches(World w, Rec? candidate, Rec? dying, int predicate) => predicate switch
    {
        0 => HasParent(w, candidate, dying) && candidate!.U16(0xB2) == dying!.U16(0xB2), // 0x276900
        1 => OfficerRelations.Siblings(w, candidate, dying), // 0x2768E0
        2 => OfficerRelations.Related(w, candidate, dying), // 0x2767E0
        3 => OfficerRelations.Sworn(w, candidate, dying), // 0x282110
        4 => OfficerRelations.Spouse(w, candidate, dying), // 0x27BF80
        5 => PriorityIds.Contains((int)(candidate ?? throw new ArgumentNullException(nameof(candidate))).U16(0x10)), // 0x280D90
        6 => IsSenior(w, candidate ?? throw new ArgumentNullException(nameof(candidate))), // 0x2750F0
        7 => OfficerRelations.Likes(w, dying ?? throw new ArgumentNullException(nameof(dying)), candidate), // 0x27A640
        _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };

    // 0x2750F0 checks no officer validity and ignores the grade of an invalid office record.
    private static bool IsSenior(World w, Rec candidate)
    {
        if (candidate.I16(0x17C) < SeniorMerit) return false;
        Rec office = w.Table(Tables.Offices)[candidate.U8(0xE7)];
        if (Objects.OfficeValid(office) && office.U8(0x37) >= SeniorOfficeGrade) return false;
        return w.WorldState.I16(0x34) - candidate.U16(0x166) > 4;
    }

    // 0x13A7C60: the complete 22-entry priority table used by 0x2561A0.
    internal static ReadOnlySpan<int> PriorityIds =>
        [132, 607, 736, 521, 35, 32, 953, 551, 266, 557, 771, 987, 952, 553, 526, 262, 166, 925, 666, 769, 893, 97];

    // 0x2C5780/0x2C6EF0 -> 0x2C8A70: the first officer names the second as a parent.
    internal static bool HasParent(World w, Rec? person, Rec? parent) =>
        Objects.Valid(w, person) && Objects.Valid(w, parent) && person!.U16(0x10) != parent!.U16(0x10) &&
        (person.U16(0xB4) != 0 && person.U16(0xB4) == parent.U16(0x10) ||
         person.U16(0xB6) != 0 && person.U16(0xB6) == parent.U16(0x10));

    // 0x2C5C40: resolve each parent through the native ID map, then check that parent's two parent IDs.
    internal static bool HasGrandparent(World w, Rec? person, Rec? grandparent)
    {
        if (!Objects.Valid(w, person) || !Objects.Valid(w, grandparent) || person!.U16(0x10) == grandparent!.U16(0x10))
            return false;
        foreach (int at in new[] { 0xB4, 0xB6 })
            if (HasParent(w, w.OfficerById(person.U16(at)), grandparent)) return true;
        return false;
    }

    private static int Priority(Rec person)
    {
        int index = PriorityIds.IndexOf((int)person.U16(0x10));
        return index < 0 ? int.MaxValue : index;
    }

    // 0x2561A0: compare the two candidate pointers; the context points to the dying officer.
    internal static bool Before(World w, Rec dying, Rec a, Rec b)
    {
        int ap = Priority(a), bp = Priority(b);
        if (ap != bp) return ap < bp;
        bool am = a.U8(0xA9) == 0, bm = b.U8(0xA9) == 0;
        if (am != bm) return am;
        int aa = Attack.Age(w, a), ba = Attack.Age(w, b);
        if (aa != ba) return aa > ba;
        bool af = HasParent(w, dying, a), bf = HasParent(w, dying, b);
        if (af != bf) return af;
        bool ag = HasGrandparent(w, a, dying), bg = HasGrandparent(w, b, dying);
        if (ag != bg) return ag;
        int ao = w.Table(Tables.Offices)[a.U8(0xE7)].U8(0x37), bo = w.Table(Tables.Offices)[b.U8(0xE7)].U8(0x37);
        return ao != bo ? ao < bo : a.U16(0x10) < b.U16(0x10);
    }

    // 0x24F400: move each native node backwards only while the boolean comparator returns true.
    // This helper orders a supplied candidate list; it does not select predicates or execute succession.
    internal static void SortCandidates(World w, Rec dying, List<Rec> candidates)
    {
        for (int i = 1; i < candidates.Count; i++)
        {
            Rec current = candidates[i];
            int j = i;
            while (j > 0 && Before(w, dying, current, candidates[j - 1]))
            {
                candidates[j] = candidates[j - 1];
                j--;
            }
            candidates[j] = current;
        }
    }
}
