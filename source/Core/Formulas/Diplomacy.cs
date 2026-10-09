namespace San14Tenkazu.Formulas;

// The kind-7 branch of 0x324300, including the native geographic and military prerequisites.
internal static class Diplomacy
{
    [Rva(0x1A485E8)] internal const int MinimumElapsed = 360;
    [Rva(0x1A485F4)] internal const int CityRatio = 4;
    [Rva(0x1A48600)] internal const int TroopRatio = 3;
    [Rva(0x1A4860C)] internal const int CityLimit = 5;
    [Rva(0x1A48618)] internal const int WorldOfficerBonus = 10;
    [Rva(0x1A48624)] internal const int FamilyBonus = 20;
    [Rva(0x1A48630)] internal const int LikeBonus = 10;
    [Rva(0x1A4863C)] internal const int CityDifferenceDivisor = 5;
    [Rva(0x1A48648)] internal const int CityRatioDivisor = 2;
    [Rva(0x1A48654)] internal const int CompatibilityDivisor = 7;
    [Rva(0x1A48660)] internal const int ScoreDivisor = 2;
    [Rva(0x1A4866C)] internal const int RelationDivisor = 4;
    [Rva(0x1A48678)] internal const int CharmDivisor = 5;
    [Rva(0x1A48684)] internal const int PressureFactor = 10;
    [Rva(0x1A48690)] internal const int PressureMaximum = 30;
    [Rva(0x1A487E4)] internal const int EnvoyKind40Factor = 1;

    internal static bool Judge(World w, Rec a, Rec envoy, Rec b, int kind, Exchange.Offer give = default, Exchange.Offer ask = default) => kind switch
    {
        6 => true,
        7 => Kind7(w, a, envoy, b),
        19 => Exchange.Judge(w, a, envoy, b, give, ask),
        _ => false,
    };

    // 0x22F2D0: neighboring cities, with at most two intervening gates.
    internal static List<Rec> NeighborCities(World w, Rec city)
    {
        var result = new List<Rec>();
        void Add(Rec b) { if (!result.Contains(b)) result.Add(b); }
        for (int i = 0; i < 10; i++)
        {
            int id = city.U8(0x58 + i);
            Rec? first = id != 0 ? Map.Base(w, id) : null;
            if (!Objects.Valid(w, first) || id == city.U16(0x4E)) continue;
            if (first!.U16(0x4E) < 52) { Add(first); continue; }
            for (int j = 0; j < 10; j++)
            {
                int secondId = first.U8(0x58 + j);
                Rec? second = secondId != 0 ? Map.Base(w, secondId) : null;
                if (!Objects.Valid(w, second) || secondId == city.U16(0x4E)) continue;
                if (second!.U16(0x4E) < 52) { Add(second); continue; }
                for (int k = 0; k < 10; k++)
                {
                    int thirdId = second.U8(0x58 + k);
                    Rec? third = thirdId != 0 ? Map.Base(w, thirdId) : null;
                    if (Objects.Valid(w, third) && thirdId != first.U16(0x4E) && thirdId != city.U16(0x4E) && third!.U16(0x4E) < 52) Add(third);
                }
            }
        }
        return result;
    }

    // 0x2C74E0: A owns a city whose city-neighbor list contains a city belonging to B.
    internal static bool Adjacent(World w, Rec a, Rec b) => a.Index != 0 && b.Index != 0
        && w.Table(Tables.Cities).Where(c => (uint)(a.Index - 1) < 51 && Map.BaseForce(w, c) == a.Index)
            .Any(c => NeighborCities(w, c).Any(n => Map.BaseForce(w, n) == b.Index));

    // 0x2AD2B0(force, 1, 0): standing troops in the force's bases and active units, with signed I32 accumulation.
    internal static int Troops(World w, Rec force)
    {
        if (!Objects.Valid(w, force)) return 0;
        int total = 0;
        if ((uint)(force.Index - 1) < 51)
        {
            foreach (Rec?[] e in w.List(Lists.Bases))
                if (e[0] is { } b && Map.BaseForce(w, b) == force.Index) total = unchecked(total + b.I32(0x3C));
            foreach (Rec?[] e in w.List(Lists.UnitList))
                if (e[0] is { } u && Objects.UnitForce(w, u) == force.Index && !Map.Engaged(w, u)) total = unchecked(total + u.U16(0x16));
        }
        return total;
    }

    internal static bool Kind7(World w, Rec a, Rec envoy, Rec b)
    {
        int citiesA = Capture.CityCount(w, a), citiesB = Capture.CityCount(w, b);
        if (w.WorldState.I32(0x450) <= MinimumElapsed || !Adjacent(w, a, b)) return false;
        if (!w.List(Lists.ForceList).Any(e => e[0] is { } f && f.Index != 51 && (uint)(f.Index - 46) > 4 && f.Index != a.Index && f.Index != b.Index)) return false;
        if (citiesA < unchecked(CityRatio * citiesB) || Troops(w, a) < unchecked(Troops(w, b) * TroopRatio) || citiesB >= CityLimit) return false;
        Rec rulerA = w.OfficerById(a.U16(0x10)) ?? w.Table(Tables.Officers)[0];
        Rec rulerB = w.OfficerById(b.U16(0x10)) ?? w.Table(Tables.Officers)[0];
        if (Link.Hates(rulerB, rulerA) || Link.Hates(rulerB, envoy)) return false;
        int ability = Objects.Valid(w, envoy) ? OfficerAbility.Get(w, envoy, 3, OfficerAbility.All) : 0;
        if (Objects.Valid(w, envoy)) ability = unchecked(Personality.EffectSum(w, envoy, 0x40) * ability * EnvoyKind40Factor) / 100 + ability;
        int relation = b.Index < 52 ? a.U8(0x13 + b.Index) : 0;
        int compatibility = Link.CompatibilityDiff(rulerA, rulerB);
        int charm = OfficerAbility.Get(w, rulerB, 4, OfficerAbility.All);
        int worldOfficerBonus = 0;
        Rec? worldOfficer = w.OfficerById(w.WorldState.U16(0x1654));
        if (Objects.Valid(w, worldOfficer) && Objects.Place(worldOfficer!) is (5, int city))
        {
            Rec officerCity = w.Entry(Tables.Cities, city, 51);
            if (Objects.Valid(w, officerCity))
            {
                Rec officerForce = w.Entry(Tables.Forces, Map.BaseForce(w, officerCity), 51);
                if (Objects.Valid(w, officerForce) && officerForce.Index == b.Index) worldOfficerBonus = WorldOfficerBonus;
            }
        }
        bool validPair = Objects.Valid(w, envoy) && Objects.Valid(w, rulerB) && envoy.I16(0x10) != rulerB.I16(0x10);
        int ties = validPair && (envoy.I16(0xBA) != 0 && envoy.I16(0xBA) == rulerB.I16(0x10)
            || envoy.I16(0xBC) != 0 && envoy.I16(0xBC) == rulerB.I16(0xBC)) ? FamilyBonus
            : Objects.PersonValid(envoy) && Enumerable.Range(0, 8).Any(i => rulerB.I16(0xC0 + 2 * i) == envoy.I16(0x10)) ? LikeBonus : 0;
        int baseTroops = 0, ownUnits = 0, attackingUnits = 0;
        foreach (Rec?[] e in w.List(Lists.Bases))
            if ((uint)(b.Index - 1) < 51 && e[0] is { } bas && Map.BaseForce(w, bas) == b.Index) baseTroops = unchecked(baseTroops + bas.I32(0x3C));
        Rec home = Exchange.CityOf(w, Map.Base(w, rulerB.U16(0xE2)) ?? w.Table(Tables.Cities)[0]);
        foreach (Rec?[] e in w.List(Lists.UnitList))
        {
            if (e[0] is not { } u || !Objects.Valid(w, u) || Map.Engaged(w, u)) continue;
            Rec tile = w.Entry(Tables.Tiles, u.U16(0x2A), 48399);
            Rec county = w.Entry(Tables.Counties, tile.U16(0x12), 500);
            if (!Objects.Valid(w, county) || county.U8(0x35) != home.Index) continue;
            int force = Objects.UnitForce(w, u);
            if (force == b.Index) ownUnits = unchecked(ownUnits + u.U16(0x16));
            else if (force == a.Index) attackingUnits = unchecked(attackingUnits + u.U16(0x16));
        }
        int ratio = Math.Max(0, attackingUnits / Math.Max(1, unchecked(baseTroops + ownUnits)));
        int pressure = unchecked(ratio * ratio * PressureFactor) / 10 / citiesB;
        float term = (float)worldOfficerBonus + ability + ties + ((float)citiesA - citiesB) / CityDifferenceDivisor
            + ((float)citiesA / citiesB) / CityRatioDivisor - (float)compatibility / CompatibilityDivisor;
        int score = Objects.Trunc((float)Objects.Trunc(term) / ScoreDivisor);
        int rulerAId = a.Index != 0 ? a.U16(0x10) : 0x1396;
        int roll = Wounded.Seeded(w, score, 1, envoy.U16(0x10), rulerAId, rulerB.U16(0x10), 0);
        return unchecked(Math.Min(PressureMaximum, pressure) + roll + score) >= Objects.Trunc(100f - (float)relation / RelationDivisor + (float)charm / CharmDivisor);
    }
}
