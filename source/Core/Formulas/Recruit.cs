namespace San14Tenkazu.Formulas;

// Whether an officer accepts recruitment (Recruit_Chance 0x2784B0): first the ties that settle it outright (sworn
// brothers, spouse, parents, hatred), then a chance from the recruiter's charm, the 상성 to both rulers, the
// target's +0xE8 and +0x16C, family and liking of the own ruler, a free officer's bonus, the player's preset and
// the 지역 이점 kind 10, rolled with the seeded random (or a roll handed in).
internal static class Recruit
{
    [Rva(0x1A48F88)] internal const int CharmKind3FFactor = 1;
    [Rva(0x1A49B94)] internal const int PlayerPreset0 = 10;
    [Rva(0x1A49BA0)] internal const int PlayerPreset1 = 5;
    [Rva(0x1A48C34)] internal const int AdvantageFactor = 1;
    // exe + 0x27D2624: the player's bonus at presets 2 and up (0 as the game runs).
    internal const string PlayerPreset2 = "recruit_preset2";

    internal readonly record struct Result(bool Accepts, int Roll);

    // `target` is recruited by `recruiter`; `roll` is -1 for the game's own seeded roll, else the roll to use.
    // Returns whether the target accepts and the roll (unchanged when a tie decided).
    internal static Result Chance(World w, Rec target, Rec recruiter, int roll)
    {
        Rec[] officers = w.Table(Tables.Officers);
        Rec force = Objects.ForceOf(w, recruiter);
        Rec ruler = w.OfficerById(force.U16(0x10)) ?? officers[0];
        Rec? sworn = Rel(w, target, 0xBC), spouse = Rel(w, target, 0xBA), father = Rel(w, target, 0xB4), mother = Rel(w, target, 0xB6);
        Rec tforce = Objects.ForceOf(w, target);
        int cities = Objects.ForceValid(tforce) ? Capture.CityCount(w, tforce) : 0;
        int tId = target.U16(0x10), rulerId = ruler.U16(0x10), recruiterId = recruiter.U16(0x10);
        int rForce = force.Index;
        bool tforceOk = Objects.ForceValid(tforce);

        if (target.U8(0xE8) > 99 || target.I16(0x162) == ruler.I16(0x10)) return new Result(false, roll);
        if (target.U8(0xE6) == 1 && cities > 0) return new Result(false, roll);
        if (tforceOk && cities > 0 && sworn is not null && tId != sworn.U16(0x10) && CorpsForce(w, sworn) == tforce.Index) return new Result(false, roll);
        if (sworn is not null && (sworn.I16(0x10) == ruler.I16(0x10) || sworn.I16(0x10) == recruiter.I16(0x10))) return new Result(true, roll);
        if (tforceOk && cities > 0)
            foreach (Rec?[] node in w.List(Lists.OfficerList))
                if (node[0] is { } o && target.U16(0xBC) is var g && (uint)(g - 1) < 5999 && o.I16(0x10) != target.I16(0x10) && o.U16(0xBC) == g
                    && Objects.PersonValid(o) && CorpsForce(w, o) == CorpsForce(w, target)) return new Result(false, roll);
        if (tforceOk && cities > 0 && spouse is not null && CorpsForce(w, spouse) == CorpsForce(w, target)) return new Result(false, roll);
        if (sworn is not null && Objects.ForceValid(Objects.ForceOf(w, sworn)) && tId != sworn.U16(0x10) && CorpsForce(w, sworn) != rForce) return new Result(false, roll);
        if (spouse is not null && Objects.ForceValid(Objects.ForceOf(w, spouse)) && CorpsForce(w, spouse) != rForce) return new Result(false, roll);
        if (spouse is not null && (spouse.I16(0x10) == ruler.I16(0x10) || spouse.I16(0x10) == recruiter.I16(0x10))) return new Result(true, roll);
        if (sworn is not null && CorpsForce(w, sworn) == rForce) return new Result(true, roll);
        if (spouse is not null && CorpsForce(w, spouse) == rForce) return new Result(true, roll);
        if (Link.Hates(target, ruler) || Link.Hates(target, recruiter)) return new Result(false, roll);
        bool free = target.U8(0xE6) == 6;
        if (free && sworn is not null && CorpsForce(w, sworn) == rForce) return new Result(true, roll);
        if (free && spouse is not null && CorpsForce(w, spouse) == rForce) return new Result(true, roll);
        if (free && father is not null && CorpsForce(w, father) == rForce) return new Result(true, roll);
        if (free && mother is not null && CorpsForce(w, mother) == rForce) return new Result(true, roll);
        if (target.I16(0x16C) / 5 + target.U8(0xE8) > 0x5F) return new Result(false, roll);

        int preset = w.Settings.I32(0xE58) == 0 ? w.Settings.I32(0x134) : w.Settings.I32(0x28);
        int charmBase = OfficerAbility.Get(w, recruiter, 4, OfficerAbility.All);
        int charm = Personality.EffectSum(w, recruiter, 0x3F) * charmBase * CharmKind3FFactor / 100 + charmBase;
        int loyalty = target.U8(0xE8);
        bool prisoner = target.U8(0xE6) == 5;
        if (!prisoner)
        {
            if (free)
            {
                if (cities == 1)
                {
                    Rec? home = Map.Base(w, target.U16(0xE2));
                    if (!tforceOk || home is null || !Objects.Valid(w, home)) loyalty = target.I16(0x16C) + 0x46;
                    else
                        foreach (Rec?[] node in w.List(Lists.Bases))
                            if (node[0] is { } b && Map.BaseForce(w, b) == tforce.Index && (uint)(b.U16(0x4E) - 0x34) > 9 && b.U16(0x4E) == home.U16(0x4E))
                            {
                                if (loyalty > 0x45) loyalty = 0x46;
                                break;
                            }
                }
                else if (cities == 0) loyalty = target.I16(0x16C) + 0x46;
            }
        }
        else loyalty = preset == 0 ? 0x3C : preset == 1 ? 0x41 : 0x46;

        int trait = prisoner ? 0 : target.I16(0x16C) / 5;
        int compatOwn = 0x19;
        int family = 0, liked = 0, hated = 0;
        if (tforceOk)
        {
            Rec ownRuler = w.OfficerById(tforce.U16(0x10)) ?? officers[0];
            compatOwn = Link.CompatibilityDiff(target, ownRuler);
            if (Link.IsFather(target, ownRuler) || Link.IsMother(target, ownRuler) || Link.IsFather(ownRuler, target) || Link.IsMother(ownRuler, target)) family = 0xF;
            if (Likes(target, ownRuler)) liked = 0xF;
            if (Link.Hates(target, ownRuler)) hated = 0xF;
        }
        int compatNew = Link.CompatibilityDiff(target, ruler);
        int freeBonus = free ? 0xF : 0;
        int r = roll != -1 ? roll : Wounded.Seeded(w, 100, 1, recruiterId, tId, 0, 0);
        int playerBonus = 0;
        if (Objects.IsPlayerForce(w, force)) playerBonus = preset == 0 ? PlayerPreset0 : preset == 1 ? PlayerPreset1 : w.Value(PlayerPreset2);
        int charmTerm = charm > 0x1D ? charm : 0x1E;
        int value = charmTerm * 3 / 5 + ((compatOwn - compatNew) / 5 - ((trait + 4) / 2 + 0x12) * loyalty * 5 / 100) + (9 - r / 5) * 5
                    - liked + playerBonus + r + freeBonus + hated + family;
        int adv = Advantage.Effect(w, force, 10, 0);
        int extra = free || (prisoner && !tforceOk) ? 1 : 0;
        float f = trait == -4 ? 1.5f : trait == -3 ? 1.3f : trait == -2 ? 1.1f : 1f;
        int chance = (int)((float)(value + extra * AdvantageFactor * adv) * f);
        int capped = chance >= 0 ? Math.Min(chance, 100) : 0;
        return new Result(r < capped, r);
    }

    // The officer an ID field names, when real.
    private static Rec? Rel(World w, Rec p, int at)
    {
        Rec? o = w.OfficerById(p.U16(at)) ?? w.Table(Tables.Officers)[0];
        return Objects.PersonValid(o) ? o : null;
    }

    // 0x22CE20: the force number through the officer's corps, 0 for an invalid corps.
    private static int CorpsForce(World w, Rec p)
    {
        Rec corps = w.Entry(Tables.Corps, p.U8(0xE0), 0x33);
        return Objects.CorpsValid(corps) ? corps.U8(0x10) : 0;
    }

    // 0x232EE0: b's ID is in a's 친애 slots.
    private static bool Likes(Rec a, Rec b)
    {
        if (!Objects.PersonValid(b)) return false;
        for (int i = 0; i < 8; i++) if (a.I16(0xC0 + 2 * i) == b.I16(0x10)) return true;
        return false;
    }
}
