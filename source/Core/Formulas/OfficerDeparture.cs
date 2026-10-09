namespace San14Tenkazu.Formulas;

// Departure eligibility and seeded chance only; 0x264DD0 officer removal is a separate body.
internal static class OfficerDeparture
{
    [Rva(0x1A4968C)] internal const int LoyaltyLimit = 85;
    [Rva(0x1A49698)] internal const int CompatibilityDivisor = 5;
    [Rva(0x1A496A4)] internal const int ChanceCap = 50;

    // 0x2665F0 applies the first clamp before subtracting the signed service value at +0x16C.
    internal static int Chance(World w, Rec person)
    {
        Rec force = Objects.ForceOf(w, person), ruler = w.OfficerById(force.U16(0x10))!;
        return ChanceForRuler(w, person, ruler, person.U8(0xE8));
    }

    private static int ChanceForRuler(World w, Rec person, Rec ruler, int loyalty)
    {
        int basis = Math.Clamp(LoyaltyLimit + Link.CompatibilityDiff(person, ruler) / CompatibilityDivisor + 1 -
            (OfficerRelations.Likes(w, person, ruler) ? 10 : 0) - loyalty, 0, 50);
        return Math.Clamp(basis - person.I16(0x16C), 0, ChanceCap);
    }

    // 0x275F40 tests relationship and date gates before the base/gate location and seeded roll.
    internal static bool Test(World w, Rec person)
    {
        if (person.U8(0xE6) != 4) return false;
        Rec force = Objects.ForceOf(w, person), ruler = w.OfficerById(force.U16(0x10))!;
        return TestForRuler(w, person, ruler, person.U8(0xE6), person.U8(0xE8));
    }

    // Read-only preview for the caller before installing the future ruler and recalculated loyalty.
    internal static bool TestForRuler(World w, Rec person, Rec ruler, int status, int loyalty)
    {
        if (status != 4) return false;
        if ((person.U16(0x15E) & 0x4000) != 0 || loyalty > LoyaltyLimit ||
            OfficerRelations.Spouse(w, person, ruler) || OfficerRelations.Sworn(w, person, ruler) ||
            (RulerSuccession.HasParent(w, person, ruler) || RulerSuccession.HasParent(w, ruler, person)) &&
            person.I16(0xB2) == ruler.I16(0xB2)) return false;
        int years = w.WorldState.I16(0x34) - person.U16(0x166);
        bool longstanding = years > 2 || years > 1 && w.WorldState.I8(0x36) > 6;
        if (loyalty >= 60 && !longstanding || Objects.Place(person).Kind is not (5 or 6)) return false;
        int chance = ChanceForRuler(w, person, ruler, loyalty);
        int seed = w.WorldState.I16(0x34) + w.WorldState.I8(0x36) + w.WorldState.I8(0x37);
        return Wounded.Seeded(w, 100, 1, seed, person.U16(0x10), 0, 0) < chance;
    }
}
