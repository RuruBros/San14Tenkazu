namespace San14Tenkazu.Formulas;

// Whether officer `a` is captured by officer `b` when a's side is destroyed (Capture_Chance 0x27E420, the game's
// debug line "捕獲基準値"): a roll of the seeded random against a basis that grows with how few escape routes a has
// and how weak a is, cut by presets, options and a's standing, raised by b's 개성 kind 0x39 and scaled by the
// difficulty sliders.
internal static class Capture
{
    [Rva(0x1A4983C)] internal const int AbilityBase = 120;
    [Rva(0x1A49844)] internal const int BasisPercent = 103;
    [Rva(0x1A49850)] internal const int RoutePercent = 33;
    [Rva(0x1A4985C)] internal const int Formation4Percent = 60;
    [Rva(0x1A49868)] internal const int HardPlayerPercent = 70;
    [Rva(0x1A49874)] internal const int OptionPercent = 130;
    [Rva(0x1A49880)] internal const int RulerPercent = 90;
    [Rva(0x1A48FD8)] internal const int Radius = 3;
    // exe + 0x27D260C: a runtime global added to the route count (0 in the game as it runs).
    internal const string Extra = "capture_extra";

    // 0x22A9D0 (iterator filter 0x333240): the cities (1..51) whose force (+0xB0 slot) is this force's number.
    internal static int CityCount(World w, Rec force)
    {
        int n = force.Index;
        if ((uint)(n - 1) > 0x32) return 0;
        int count = 0;
        foreach (Rec c in w.Table(Tables.Cities))
            if (Map.BaseForce(w, c) == n) count++;
        return count;
    }

    // 0x22D580 without its command path: the hex an officer stands on: his base's building hex, his corps' county
    // hex (+0x3A), or his unit's hex; -1 for none (the game would read a null tile).
    internal static int OfficerHex(World w, Rec p)
    {
        var (kind, n) = Objects.Place(p);
        switch (kind)
        {
            case 5: return BuildingHexOrZero(w, w.Entry(Tables.Cities, n, 0x33));
            case 6: return BuildingHexOrZero(w, w.Entry(Tables.Gates, n, 0xA));
            case 10: { int h = w.Entry(Tables.Counties, n, 0x1F4).U16(0x3A); return h < 0xBD10 ? h : 0; }
            case 0x1B:
            {
                Rec u = Objects.UnitOf(w, p);
                if (!Objects.UnitValid(u)) return -1;
                int h = u.U16(0x2A);
                return h < 0xBD10 ? h : 0;
            }
            default: return -1;
        }
    }

    // 0x22D500: the hex of a base's building, 0 when the building does not stand.
    private static int BuildingHexOrZero(World w, Rec b)
    {
        Rec o = w.Entry(Tables.Buildings, b.U16(0x4E), 0xBB8);
        return Map.BuildingValid(o) && o.U16(0x10) < 0xBD10 ? o.U16(0x10) : 0;
    }

    // 0x27E420: a (the one caught) is captured by b. Outside the challenge modes and replays.
    internal static bool Chance(World w, Rec a, Rec b)
    {
        if (w.Settings.I32(0x108) == 2) return false;
        if (Objects.SpecialOfficer(w, a, false)) return false;
        if (Wounded.InTribeForce(w, a)) return false;
        if (Objects.TribeOutside(w, a)) return false;
        if (!Objects.PersonValid(b)) return false;
        Rec forceB = Objects.ForceOf(w, b);
        if (forceB.Index == 0x33 || (uint)(forceB.Index - 0x2E) < 5 || !Objects.ForceValid(forceB)) return false;
        if (!Objects.PersonValid(a)) return false;
        Rec forceA = Objects.ForceOf(w, a);
        if (forceA.Index == 0x33 || (uint)(forceA.Index - 0x2E) < 5 || !Objects.ForceValid(forceA)) return false;
        if (CityCount(w, forceA) == 0) return false;

        // b's unit in formation 3 takes no prisoners from the field
        Rec unitB = Objects.UnitOf(w, b);
        if (Objects.UnitValid(unitB) && Attack.RawFormation(w, unitB) == 3 && !Objects.InBase(a)) return false;
        // the last city falling: everyone in it is caught
        if (Objects.InBase(a) && CityCount(w, forceA) == 1) return true;
        // a's always-on 개성 kind 0x36
        if (Personality.FindKind(w, a, 0x36) is { } own && own.I16(0xB4) == -1) return false;
        // a friendly commander within 3 hexes whose 개성 kind 0x36 has condition 9 (a named one: within its range)
        Rec unitA = Objects.UnitOf(w, a);
        if (Objects.UnitValid(unitA))
            foreach (Rec u in Map.UnitsNear(w, unitA, Radius))
            {
                if (u.Index == unitA.Index) continue;
                if (w.Entry(Tables.Forces, Objects.UnitForce(w, u), 0x33).Index != forceA.Index) continue;
                Rec cmd = w.OfficerById(u.U16(0x12)) ?? w.Table(Tables.Officers)[0];
                if (Personality.FindKind(w, cmd, 0x36) is not { } k) continue;
                if ((uint)(k.Index - 0x100) > 99)
                {
                    if (k.U8(0xA4) == 9) return false;
                }
                else
                {
                    int d = Map.HexDistance(OfficerHex(w, cmd), OfficerHex(w, a));
                    if (k.U8(0xA4) == 9 && d <= Personality.Range(k)) return false;
                }
            }
        // an item of a's with +0x47 == 1
        foreach (Rec item in Objects.ItemsOf(w, a))
            if (item.U8(0x47) == 1) return false;

        // a's hex and the ring the escape routes are counted on: a base's building hex (a city 2 out, a gate 1),
        // else the unit's hex, 1 out
        int hexA = 0xBD10, radius = 1;
        if (!Objects.UnitValid(unitA))
        {
            if (Objects.InBase(a))
            {
                var (kind, n) = Objects.Place(a);
                if (kind == 5)
                {
                    Rec city = w.Entry(Tables.Cities, n, 0x33);
                    if (Map.BaseValid(w, city)) { hexA = Map.BaseHex(w, city); radius = 2; }
                }
                else if (kind == 6)
                {
                    Rec g = w.Entry(Tables.Gates, n, 0xA);
                    if (Map.BaseValid(w, g)) { hexA = Map.BaseHex(w, g); radius = 1; }
                }
            }
        }
        else hexA = unitA.U16(0x2A);
        int routes = 0;
        if (hexA < 0xBD10)
            foreach (int h in Map.RingAt(hexA, radius))
                if (Reach.UnitOnHex(w.Table(Tables.Tiles)[h]) && Reach.CanAttack(w, h, hexA)) routes++;

        int ability = Math.Max(OfficerAbility.Get(w, a, 2, OfficerAbility.All), OfficerAbility.Get(w, a, 1, OfficerAbility.All));
        float basis = (float)(((AbilityBase - ability) + 5) * BasisPercent) / 100f;
        if (routes < 1) routes = 1;
        if (basis < 5f) basis = 5f;
        int chance = (int)(((float)(routes + w.Value(Extra)) * basis * (float)RoutePercent) / 100f);
        if (chance > 100) chance = 100;
        if (Objects.UnitValid(unitA) && Attack.RawFormation(w, unitA) == 4) chance = chance * Formation4Percent / 100;
        int c = chance >= 0 ? Math.Min(chance, 100) : 0;
        if (w.Settings.I32(0xE58) != 0)
        {
            int preset = w.Settings.I32(0x28);
            if (preset > 2 && Objects.UnitValid(unitB))
            {
                Rec fb = w.Entry(Tables.Forces, Objects.UnitForce(w, unitB), 0x33);
                if (Objects.ForceValid(fb) && Objects.IsPlayerForce(w, fb)) c = c * HardPlayerPercent / 100;
            }
            int c3 = c >= 0 ? Math.Min(c, 100) : 0;
            if (w.Settings.I32(0x108) == 1) c3 = c3 * OptionPercent / 100;
            if (a.U8(0xE6) == 1 || a.U8(0xE6) == 2) c3 = c3 * RulerPercent / 100;
            c = c3 >= 0 ? Math.Min(c3, 100) : 0;
        }
        int roll = Wounded.Seeded(w, 100, 1, a.U16(0x10), forceB.Index, w.WorldState.I8(0x38), 0);
        int total = Personality.EffectSum(w, b, 0x39) + c;
        int threshold = total;
        if (w.Settings.I32(0xE58) == 0)
            threshold = BattlePower.Slider(w, Objects.IsPlayerForce(w, forceB) ? 0xF : 0x10) * total / 100;
        return roll < threshold;
    }
}
