namespace San14Tenkazu.Formulas;

internal static class RulerRebellion
{
    [Rva(0x1A497DC)] internal const int LoyaltyGate = 90;
    [Rva(0x1A497E8)] internal const int ServiceDivisor = 4;
    [Rva(0x1A497F4)] internal const int MeritGate = 10;
    [Rva(0x1A49800)] internal const int CompatibilityGate = 5;
    [Rva(0x1A4980C)] internal const int ChanceBase = 75;
    [Rva(0x1A49818)] internal const int DyingBonus = 15;
    [Rva(0x1A49824)] internal const int MeritDivisor = 4;
    [Rva(0x1A49830)] internal const int ChanceServiceDivisor = 4;
    [Rva(0x1A4984C)] internal const int ImperialPenalty = 30;
    internal readonly record struct Plan(Rec[] Candidates, int Chance, int Draw, bool Execute);

    // 0x255DA0: disliked ruler first, then lower loyalty, higher nonzero office number and lower officer ID.
    internal static bool Before(World w, Rec ruler, Rec a, Rec b)
    {
        bool ad = OfficerRelations.Dislikes(w, a, ruler), bd = OfficerRelations.Dislikes(w, b, ruler);
        if (ad != bd) return ad;
        if (a.U8(0xE8) != b.U8(0xE8)) return a.U8(0xE8) < b.U8(0xE8);
        if (a.U8(0xE7) != 0 && b.U8(0xE7) != 0 && a.U8(0xE7) != b.U8(0xE7)) return a.U8(0xE7) > b.U8(0xE7);
        return a.U16(0x10) < b.U16(0x10);
    }

    private static Rec CityOf(World w, Rec home)
    {
        Rec county = w.Entry(Tables.Counties, Map.BuildingTile(w, home).U16(0x12), 500);
        return w.Entry(Tables.Cities, Objects.CountyValid(county) ? county.U8(0x35) : 0, 51);
    }

    // 0x32CB30: the marked officer must currently be in a city, independently of the officer's home base.
    private static Rec MarkedCity(World w)
    {
        Rec? marked = w.OfficerById(w.WorldState.U16(0x1654));
        var place = marked is not null ? Objects.Place(marked) : default;
        return w.Entry(Tables.Cities, Objects.Valid(w, marked) && place.Kind == 5 ? place.Number : 0, 51);
    }

    private static bool Eligible(World w, Rec person, Rec ruler, Rec? dying, Rec markedCity)
    {
        if (Objects.Valid(w, dying) && person.U16(0x10) == dying!.U16(0x10) || person.U8(0xE6) is not (2 or 3)) return false;
        Rec home = Map.Base(w, person.U16(0xE2)) ?? throw new InvalidDataException("Missing rebellion candidate home.");
        if ((uint)(home.U16(0x4E) - 52) <= 9) return false;
        Rec city = CityOf(w, home);
        Rec rulerHome = Map.Base(w, ruler.U16(0xE2)) ?? throw new InvalidDataException("Missing ruler home.");
        if (CityOf(w, rulerHome).U16(0x4E) == city.U16(0x4E) ||
            Objects.Valid(w, markedCity) && markedCity.U16(0x4E) == city.U16(0x4E) || (home.U8(0x67) & 1) != 0 ||
            OfficerRelations.Spouse(w, person, ruler) || OfficerRelations.Sworn(w, person, ruler) ||
            RulerSuccession.HasParent(w, person, ruler) || RulerSuccession.HasParent(w, ruler, person) ||
            OfficerRelations.Related(w, person, ruler) && !OfficerRelations.Dislikes(w, person, ruler) ||
            OfficerRelations.Likes(w, person, ruler) || Link.CompatibilityDiff(person, ruler) <= 5 ||
            Objects.Place(person).Kind != 5 || w.CmdActions.Any(c => c.Officers.Contains(person.U16(0x10)))) return false;
        return person.I16(0x16C) / ServiceDivisor + person.U8(0xE8) < LoyaltyGate &&
            (OfficerRelations.Dislikes(w, person, ruler) || person.I16(0x17C) >= MeritGate &&
                Link.CompatibilityDiff(person, ruler) > CompatibilityGate);
    }

    internal static int Chance(World w, Rec force, Rec person, Rec ruler, Rec? dying)
    {
        // 0x234160/0x22DE00 derive title validity/kind from its contiguous table index; index6 is imperial.
        int penalty = force.U8(0x48) == 6 && ruler.U16(0x10) == 607 ? ImperialPenalty : 0;
        int value = ChanceBase - person.U8(0xE8) + (Objects.Valid(w, dying) ? DyingBonus : 0) +
            person.I16(0x17C) / MeritDivisor - person.I16(0x16C) / ChanceServiceDivisor - penalty;
        return Math.Clamp(value, 0, 70);
    }

    // 0x2E54E0 through the final seeded check. Execution of 0x2E5D40 belongs to the caller.
    internal static Plan Select(World w, Rec force, Rec? dying)
    {
        if (Capture.CityCount(w, force) < 3) return new([], 0, 0, false);
        Rec ruler = w.OfficerById(force.U16(0x10)) ?? throw new InvalidDataException("Missing rebellion ruler.");
        Rec markedCity = MarkedCity(w);
        Rec[] candidates = w.List(Lists.OfficerList).Select(n => n[0] ??
                throw new InvalidDataException("Null native force iterator officer."))
            .Where(p => OfficerRelations.RawForce(w, p) == force.Index && p.U8(0xE6) is >= 1 and <= 4 &&
                Eligible(w, p, ruler, dying, markedCity)).ToArray();
        for (int i = 1; i < candidates.Length; i++)
        {
            Rec candidate = candidates[i]; int j = i;
            while (j > 0 && Before(w, ruler, candidate, candidates[j - 1]))
            { candidates[j] = candidates[j - 1]; j--; }
            candidates[j] = candidate;
        }
        if (candidates.Length == 0) return new([], 0, 0, false);
        Rec first = candidates[0];
        int chance = Chance(w, force, first, ruler, dying);
        int draw = Wounded.Seeded(w, 100, 1, w.WorldState.I16(0x34) + w.WorldState.I8(0x36),
            w.WorldState.I8(0x37), first.U16(0x10), 0);
        return new(candidates, chance, draw, draw < chance);
    }
}
