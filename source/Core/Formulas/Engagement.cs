namespace San14Tenkazu.Formulas;

// One engagement step as the game runs it (0x169C50 over each record): whether the sides can fight (0x174BC0), the
// siege path for a base or building, else both sides' powers (0x16A030), attack values (0x16AB50), defense values
// (0x16A990) and the damage (0x16A780), then the damage applied to the defender (0x1749A0 → 0x178E30). The tactic
// and link effects of 0x16FDD0 are handled separately by EngagementTactic.
internal static class Engagement
{
    [Rva(0x1A48920)] internal const int ProvokedAttackPercent = 150;
    [Rva(0x1A4892C)] internal const int ProvokedDefensePercent = 80;
    [Rva(0x1A4A47C)] internal const int WoundedRemovedPercent = 15;
    [Rva(0x1A49000)] internal const int MoraleKind13Factor = 1;

    // A side of a record: the object's index, kind (0x1B unit, 5 city, 6 gate, 0xB building), hex, force and how
    // many engagements it stands in this round (+0x14, 1 at least).
    internal readonly record struct Side(int Index, int Kind, int Hex, int Force, int Split);

    internal readonly record struct Powers(float PowerA, float AttackA, float DefenseA, float PowerB, float AttackB, float DefenseB, int Damage);
    internal readonly record struct UnitAfter(int Troops, int Wounded, int Morale, int Gold, int Food, int Lit, bool Destroyed, bool Routed = false);
    internal readonly record struct BaseAfter(int Troops, int Wounded, int Morale, bool Fell);
    internal readonly record struct StartResult(int NextStep, Rec? Building, Bombard.Result? Siege);

    // The unit, city or gate a side names (the game's own lookups: a unit or World_GetBase).
    private static (Rec? Unit, Rec? Base) Objects_(World w, Side s) => s.Kind switch
    {
        0x1B => (w.Entry(Tables.Units, s.Index, 0x1F4), null),
        5 => (null, Map.Base(w, s.Index <= 0x33 ? s.Index : 0)),
        6 => (null, Map.Base(w, (uint)(s.Index - 1) < 10 ? s.Index + 0x33 : 0)),
        _ => (null, null),
    };

    // Step 5's own lookup: the record by kind and index (cities and gates straight from their tables).
    private static Rec? Record(World w, Side s) => s.Kind switch
    {
        5 => w.Entry(Tables.Cities, s.Index, 0x33),
        6 => w.Entry(Tables.Gates, s.Index, 0xA),
        0x1B => w.Entry(Tables.Units, s.Index, 0x1F4),
        _ => null,
    };

    // 0x169C50 validates objects, pending requests and force hostility before entering step 1.
    internal static bool CanResolve(World w, Side a, Side b)
    {
        bool SideAllowed(Side side, out int force)
        {
            force = 0;
            if (side.Kind == 27)
            {
                Rec unit = w.Entry(Tables.Units, side.Index, 500);
                if (!Objects.Valid(w, unit) || Map.Engaged(w, unit)) return false;
                force = Objects.UnitForce(w, unit);
                return true;
            }
            if (side.Kind is not (5 or 6 or 11)) return true;
            int id = side.Index;
            if (side.Kind is 5 or 6)
            {
                Rec baseRec = w.Entry(side.Kind == 5 ? Tables.Cities : Tables.Gates, side.Index, side.Kind == 5 ? 51 : 10);
                if (Objects.Valid(w, baseRec) && w.List(Lists.Engagements).Any(row => Objects.Valid(w, row[1]) && ReferenceEquals(row[1], baseRec)))
                    return false;
                id = side.Kind == 5 ? side.Index <= 51 ? side.Index : 0 : (uint)(side.Index - 1) < 10 ? side.Index + 51 : 0;
            }
            Rec building = w.Entry(Tables.Buildings, id, 3000);
            if (!Objects.Valid(w, building) || w.List(Lists.SiegeExempt).Any(row => ReferenceEquals(row[0], building))) return false;
            force = building.U8(0x13);
            return true;
        }
        bool allowedA = SideAllowed(a, out int forceA), allowedB = SideAllowed(b, out int forceB);
        if (!allowedA || !allowedB || !Reach.Hostile(w, forceA, forceB)) return false;
        // 0x496600 uses the stored source force, whereas hostility above uses current object ownership.
        Rec storedForce = w.Entry(Tables.Forces, a.Force, 51);
        if (!w.Flag("challenge_active") || !Objects.Valid(w, storedForce) || !Objects.IsPlayerForce(w, storedForce)) return true;
        return b.Kind == 27 || b.Kind == 5 && (b.Index == 7 || b.Index == 5 && w.Value("challenge_kind") == 3);
    }

    // 0x174BC0 uses reach only for siege. Unit/base records still advance to step 2 when reach fails.
    internal static StartResult Start(World w, Side? source, Side target)
    {
        if (source is not { } a) return new(8, null, null);
        if (!Reach.CanAttack(w, a.Hex, target.Hex)) return new(target.Kind == 11 ? 8 : 2, null, null);
        Rec? building = null;
        if (target.Kind is 5 or 6)
        {
            Rec baseRec = w.Entry(target.Kind == 5 ? Tables.Cities : Tables.Gates, target.Index, target.Kind == 5 ? 51 : 10);
            if (Objects.Valid(w, baseRec)) building = w.Entry(Tables.Buildings, baseRec.U16(0x4E), 3000);
        }
        else if (target.Kind == 11) building = w.Entry(Tables.Buildings, target.Index, 3000);
        Rec unit = w.Entry(Tables.Units, a.Index, 500);
        if (!Objects.Valid(w, building) || !Objects.Valid(w, unit)) return new(2, building, null);
        if (target.Kind == 5 && Damage.TribeCityWithGovernor(w, w.Entry(Tables.Cities, target.Index, 51)))
        {
            Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
            if (!Objects.Valid(w, player) || Objects.UnitForce(w, unit) != player.Index) return new(2, building, null);
        }
        Bombard.Result siege = Siege(w, a, target);
        return new(target.Kind == 11 || siege.Fell && target.Kind is 5 or 6 ? 8 : 2, building, siege);
    }

    // Steps 2..5 for a unit defender (b.Kind 0x1B) or a base shooting / being shot at by a unit.
    internal static Powers Compute(World w, Side a, Side b)
    {
        float[] power = new float[2], attack = new float[2], defense = new float[2];
        Side[] sides = [a, b];
        for (int k = 0; k < 2; k++)
        {
            var (unit, baseRec) = Objects_(w, sides[k]);
            int split = sides[k].Split == 0 ? 1 : sides[k].Split;
            if (unit is not null && Objects.Valid(w, unit))
            {
                Rec cmd = w.OfficerById(unit.U16(0x12)) ?? w.Table(Tables.Officers)[0];
                if (Personality.HasLit(w, cmd, 0x37)) split = 1;
            }
            power[k] = BattlePower.Get(w, unit, baseRec, split, true);
        }
        for (int k = 0; k < 2; k++)
        {
            Side x = sides[k], y = sides[1 - k];
            var (unit, baseRec) = Objects_(w, x);
            bool unitOk = unit is not null && Objects.Valid(w, unit);
            Rec? formation = unitOk ? Objects.FormationOf(w, unit!) : null;
            float atk = 0f;
            if (y.Kind == 0x1B)
            {
                atk = Attack.VsUnit(w, power[k], unit, baseRec, formation, true);
                if (x.Kind == 0x1B)
                {
                    Rec ux = w.Entry(Tables.Units, x.Index, 0x1F4);
                    if (ux.U8(0x25) != 0 && ux.I16(0x3E) == y.Index) atk = (float)ProvokedAttackPercent * atk / 100f;
                }
            }
            else if (y.Kind is 5 or 6) atk = Attack.VsBase(w, power[k], unit, baseRec, formation, true);
            attack[k] = atk;
            float def = Attack.Defense(w, power[k], unit, baseRec, formation, true);
            if (x.Kind == 0x1B)
            {
                Rec ux = w.Entry(Tables.Units, x.Index, 0x1F4);
                if (ux.U8(0x25) != 0 && ux.U16(0x3E) != y.Index) def = (float)ProvokedDefensePercent * def / 100f;
            }
            defense[k] = def;
        }
        int damage = 0;
        Rec? attacker = Record(w, a), defender = Record(w, b);
        if (attacker is not null && defender is not null) damage = Damage.Get(w, attacker, attack[0], defense[0], defender, attack[1], defense[1]);
        return new Powers(power[0], attack[0], defense[0], power[1], attack[1], defense[1], damage);
    }

    // The wounded are kept under presets unless option +0xCC is set, under sliders while slider 7 is above 0.
    private static bool WoundedOn(World w) =>
        w.Settings.I32(0xE58) == 0 ? BattlePower.Slider(w, 7) > 0 : w.Settings.I32(0xCC) == 0;

    // Step 7 (0x1749A0 → 0x178E30) on a unit defender: `damage` is the step's (at least 1). The attacker's
    // experience, the transport's gold and food handed to the attacker's base, the rout when morale reaches 0 and
    // the destruction that follows are left to the engine; `Lit` is the commander's +0x184 after the 개성 of
    // condition 0x1E light up.
    internal static UnitAfter ApplyUnit(World w, Rec unit, int damage, Side attacker, bool minimumDamage = true, bool routeDefersDestruction = false)
    {
        int troops = unit.U16(0x16), wounded = unit.U16(0x18), morale = unit.U8(0x1A), gold = unit.I32(0x5C), food = unit.I32(0x60);
        Rec cmd = w.OfficerById(unit.U16(0x12)) ?? w.Table(Tables.Officers)[0];
        int lit = cmd.U16(0x184);
        var same = new UnitAfter(troops, wounded, morale, gold, food, lit, false);
        if (minimumDamage && damage < 1) damage = 1;
        if (!Objects.Valid(w, unit) || Map.Engaged(w, unit)) return same;
        var (aUnit, aBase) = Objects_(w, attacker);
        bool aUnitOk = aUnit is not null && Objects.Valid(w, aUnit), aBaseOk = aBase is not null && Objects.Valid(w, aBase);
        if (troops < damage) damage = troops;
        if (unit.U8(0x10) == 2)
        {
            float ratio = (float)damage / (float)troops;
            int foodLoss = (int)((float)food * ratio), goldLoss = (int)((float)gold * ratio);
            gold = Math.Clamp(gold - goldLoss, 0, 100000);
            food = Math.Clamp(food - foodLoss, 0, 5000000);
        }
        if (troops != 0) troops = Math.Clamp(troops - damage, 0, 0xFFFF);
        Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 0x33);
        int cap = BattlePower.MaxMorale(w, force);
        if (WoundedOn(w))
        {
            Rec tile = w.Entry(Tables.Tiles, unit.U16(0x2A), 0xBD0F);
            Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 0x1E);
            bool flag = false;
            if (aUnitOk) flag = Map.HexDistance(unit.U16(0x2A), aUnit!.U16(0x2A)) == 1;
            else if (aBaseOk) flag = Map.HexDistance(unit.U16(0x2A), Map.BaseHex(w, aBase!)) == (aBase!.U16(0x4E) < 0x34 ? 2 : 1);
            Rec? aCmd = aUnitOk ? w.OfficerById(aUnit!.U16(0x12)) ?? w.Table(Tables.Officers)[0] : null;
            int m = Math.Min(cap, (int)unit.U8(0x1A));
            float r = Wounded.Ratio(w, cmd, m, flag, terrain, aCmd, aUnitOk);
            int add = (int)(r * (float)damage);
            if (w.Settings.I32(0xE58) == 0) add = Math.Min(damage, BattlePower.Slider(w, 7) * add / 100);
            wounded = Math.Clamp(wounded + add, 0, 20000);
            wounded = Math.Clamp(wounded - (int)((double)WoundedRemovedPercent / 100.0 * (float)damage), 0, 20000);
        }
        bool routed = false;
        if (unit.U8(0x10) != 2)
        {
            int m = Math.Min(cap, (int)unit.U8(0x1A));
            if (m > 0)
            {
                int lead = Objects.PersonValid(cmd) ? OfficerAbility.Get(w, cmd, 0, OfficerAbility.All) : 0;
                int loss = Wounded.MoraleLoss(w, wounded, troops, lead, damage);
                if (loss > 0)
                {
                    // Unit_AddMorale (0x270550): 개성 kind 13 softens a loss; then the force's cap
                    int delta = -loss;
                    int s13 = Personality.UnitSums(w, unit, false)[13];
                    if (s13 != 0)
                    {
                        float f = (float)(loss * s13 * MoraleKind13Factor) / 100f;
                        if ((int)f == 0) f = f >= 0f ? 1f : -1f;
                        delta += (int)f;
                    }
                    int next = m + delta;
                    morale = next < 0 ? 0 : Math.Min(next, cap);
                    routed = morale == 0;
                }
            }
        }
        bool destroyed = !Map.Engaged(w, unit) && troops == 0 && !(routeDefersDestruction && routed);
        if (!destroyed && Objects.PersonValid(cmd))
            for (int slot = 0; slot < 9; slot++)
            {
                Rec p = w.Entry(Tables.Personalities, Personality.Slot(w, cmd, slot), 0x163);
                if (Personality.Valid(p) && p.U8(0xA4) == 0x1E && cmd.I16(0x186 + 2 * slot) == 0 && (lit & (1 << slot)) == 0 && Personality.Flag(p, 2))
                    lit |= 1 << slot;
            }
        return new UnitAfter(troops, wounded, morale, gold, food, lit, destroyed, routed);
    }

    // Step 7 on a city or gate: troops (+0x3C), wounded (+0x40) and morale (+0x44). The assembly passes the
    // wounded deltas in EDX at 0x17974B and 0x1797BE; the locked decompiler signature hides both arguments.
    internal static BaseAfter ApplyBase(World w, Rec baseRec, int damage, Side attacker, bool minimumDamage = true)
    {
        int troops = baseRec.I32(0x3C), wounded = baseRec.I32(0x40), morale = baseRec.U8(0x44);
        // 0x1749A0 returns before the minimum-damage rule when a tribe city has a governor and the attacker
        // is not the valid player force. A zero computed damage must therefore leave that city unchanged.
        if (baseRec.Table == Tables.Cities && Damage.TribeCityWithGovernor(w, baseRec))
        {
            Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 0x33);
            if (!Objects.ForceValid(player) || attacker.Force != player.Index)
                return new BaseAfter(troops, wounded, morale, false);
        }
        if (minimumDamage && damage < 1) damage = 1;
        if (!Objects.Valid(w, baseRec)) return new BaseAfter(troops, wounded, morale, false);
        var (aUnit, _) = Objects_(w, attacker);
        bool aUnitOk = aUnit is not null && Objects.Valid(w, aUnit);
        if (troops < damage) damage = troops;
        // 0x178E30 calls 0x515390 before subtraction and wounded/morale calculations, only with positive troops.
        if (troops > 0 && w.Flag("challenge_active") && (w.Value("challenge_rules") & 64) != 0 && baseRec.U16(0x4E) == 5)
            damage = Math.Min(damage, troops - 1);
        int capacity = MaxTroops(w, baseRec);
        if (troops > 0) troops = Math.Clamp(troops - damage, 0, capacity);
        Rec cmd = w.OfficerById(baseRec.U16(0x2E)) ?? w.Table(Tables.Officers)[0];
        if (WoundedOn(w))
        {
            Rec tile = Map.BuildingTile(w, baseRec);
            Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 0x1E);
            bool flag = aUnitOk && Map.HexDistance(Map.BaseHex(w, baseRec), aUnit!.U16(0x2A)) == (baseRec.U16(0x4E) < 0x34 ? 2 : 1);
            float ratio = Wounded.Ratio(w, cmd, morale, flag, terrain, null, false);
            int add = (int)(ratio * (float)damage);
            if (w.Settings.I32(0xE58) == 0) add = Math.Min(damage, BattlePower.Slider(w, 7) * add / 100);
            wounded = Math.Clamp(wounded + add, 0, capacity);
            // Unlike the unit branch, both the division and multiplication here use single precision.
            int removed = (int)((float)WoundedRemovedPercent / 100f * (float)damage);
            wounded = Math.Clamp(wounded - removed, 0, capacity);
        }
        if (baseRec.U8(0x44) != 0)
        {
            int lead = Objects.PersonValid(cmd) ? OfficerAbility.Get(w, cmd, 0, OfficerAbility.All) : 0;
            int loss = Wounded.MoraleLoss(w, wounded, troops, lead, damage);
            if (loss > 0)
            {
                // Base_AddMorale (0x2238F0): within the force's cap; a tribe's base keeps its morale
                int forceN = Map.BaseForce(w, baseRec);
                Rec force = w.Entry(Tables.Forces, forceN, 0x33);
                int cap = BattlePower.MaxMorale(w, force);
                int next = Math.Clamp(morale - loss, 0, cap);
                if ((uint)(force.Index - 0x2E) > 4) morale = Math.Clamp(next, 0, cap);
            }
        }
        bool fell = troops <= 0 && (Map.BaseForce(w, baseRec) != 0 || attacker.Kind != 0xFF || attacker.Index != -1);
        return new BaseAfter(troops, wounded, morale, fell);
    }

    // 0x267DB0's third value (what 0x22E530 answers): the troops a base can hold. A city: by its size +0xB8
    // (20000 / 50000 / 100000 for sizes 2..4, exe + 0x27D2690 for 1 and others) + 50000; a tribe's home city
    // 300000; a gate: 50000 + exe + 0x27D2690 + the force's 주의 panel 0x38.
    [Rva(0x1A4A204)] internal const int CapacityBase = 50000;
    [Rva(0x1A4A218)] internal const int CapacitySize2 = 20000;
    [Rva(0x1A4A224)] internal const int CapacitySize3 = 50000;
    [Rva(0x1A4A230)] internal const int CapacitySize4 = 100000;
    [Rva(0x1A49CA0)] internal const int TribeHomeCapacity = 300000;
    [Rva(0x1A4A1EC)] internal const int GoldPerSize = 50000;
    [Rva(0x1A4A1F8)] internal const int FoodPerSize = 100000;
    [Rva(0x1A49C88)] internal const int TribeHomeGold = 250000;
    [Rva(0x1A49C94)] internal const int TribeHomeFood = 500000;
    internal const string CapacityExtra = "capacity_extra";   // exe + 0x27D2690 (0 as the game runs)
    // Gate gold/food getters and setters delegate to the city containing the gate (0x22A7B0).
    internal static void AddBaseResources(World w, Rec b, int gold, int food)
    {
        Rec city = b.Table == Tables.Cities ? b : Exchange.CityOf(w, b);
        if (!Objects.Valid(w, city)) return;
        int maximumGold = TribeHome(w, city) ? TribeHomeGold : unchecked(GoldPerSize * city.U8(0xB8));
        int maximumFood = TribeHome(w, city) ? TribeHomeFood : unchecked(FoodPerSize * city.U8(0xB8));
        int nextGold = unchecked(city.I32(0x34) + gold), nextFood = unchecked(city.I32(0x38) + food);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(city.Bytes.AsSpan(0x34), nextGold < 0 ? 0 : Math.Min(nextGold, maximumGold));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(city.Bytes.AsSpan(0x38), nextFood < 0 ? 0 : Math.Min(nextFood, maximumFood));
    }
    internal static int MaxTroops(World w, Rec b)
    {
        if (!Objects.Valid(w, b)) return 0;
        if (b.U16(0x4E) < 0x34)
        {
            if (TribeHome(w, b)) return TribeHomeCapacity;
            int size = b.U8(0xB8);
            int bySize = size == 2 ? CapacitySize2 : size == 3 ? CapacitySize3 : size == 4 ? CapacitySize4 : w.Value(CapacityExtra);
            return bySize + CapacityBase;
        }
        int v = CapacityBase + w.Value(CapacityExtra);
        Rec force = w.Entry(Tables.Forces, Map.BaseForce(w, b), 0x33);
        if (Objects.ForceValid(force)) v += Objects.Doctrine(w, force, 0x38);
        return v;
    }

    // 0x232FB0: a tribe's home city — the city's force is a tribe and the city is the one after the force's number.
    internal static bool TribeHome(World w, Rec b)
    {
        if (b.U16(0x4E) >= 0x34) return false;
        int forceN = Map.BaseForce(w, b);
        Rec force = w.Entry(Tables.Forces, forceN, 0x33);
        if ((uint)(force.Index - 0x2E) > 4) return false;
        int city = Exchange.CityOf(w, b).Index;
        return (uint)(city - 0x2F) < 5 && force.Index == city - 1;
    }

    [Rva(0x1A4A238)] internal const int DurabilityBase = 2000;
    [Rva(0x1A4A244)] internal const int DurabilityPerSize = 1000;
    [Rva(0x1A4A250)] internal const int DurabilityPolicyFactor = 1;
    [Rva(0x1A4A634)] internal const int DamageLevel0Threshold = 75;
    [Rva(0x1A4A640)] internal const int DamageLevel1Threshold = 50;
    [Rva(0x1A4A64C)] internal const int DamageLevel2Threshold = 25;

    // 0x267DB0's fourth value, returned by the base's virtual getter 0x22C730.
    internal static int MaxDurability(World w, Rec b)
    {
        if (!Objects.Valid(w, b)) return 0;
        int maximum;
        if (b.U16(0x4E) < 52)
        {
            if (TribeHome(w, b)) return Bombard.TribeCityMax;
            Rec force = w.Entry(Tables.Forces, Map.BaseForce(w, b), 51);
            maximum = unchecked(Policy.Effect(w, force, 32) * DurabilityPolicyFactor + DurabilityBase + (b.U8(0xB8) - 1) * DurabilityPerSize);
        }
        else maximum = b.U16(0x46);
        if (w.Settings.I32(0xE58) == 0) maximum = unchecked(BattlePower.Slider(w, 39) * maximum) / 100;
        return Math.Min(maximum, 9999);
    }

    // 0x2AA570 uses ceilf before integer conversion; zero/negative maxima retain the native float behavior.
    internal static int DamageLevel(World w, Rec b)
    {
        if (!Objects.Valid(w, b)) return 0;
        Rec building = w.Entry(Tables.Buildings, b.U16(0x4E), 3000);
        int current = Map.BuildingValid(building) ? building.U16(0x14) : 6000;
        int percent = Objects.Trunc(MathF.Ceiling((float)current / MaxDurability(w, b) * 100f));
        if (percent > DamageLevel0Threshold && percent <= 100) return 0;
        if (percent > DamageLevel1Threshold && percent <= DamageLevel0Threshold) return 1;
        if (percent > DamageLevel2Threshold && percent <= DamageLevel1Threshold) return 2;
        return percent >= 0 && percent <= DamageLevel2Threshold ? 3 : 0;
    }

    // Step 1's siege path: the attacker unit's siege damage on the base's or building's durability.
    internal static Bombard.Result Siege(World w, Side a, Side b)
    {
        Rec building = b.Kind switch
        {
            5 => w.Entry(Tables.Buildings, w.Entry(Tables.Cities, b.Index, 0x33).U16(0x4E), 0xBB8),
            6 => w.Entry(Tables.Buildings, w.Entry(Tables.Gates, b.Index, 0xA).U16(0x4E), 0xBB8),
            _ => w.Entry(Tables.Buildings, b.Index, 0xBB8),
        };
        Rec unit = w.Entry(Tables.Units, a.Index, 0x1F4);
        var none = new Bombard.Result(building.U16(0x14), false);
        if (!Map.BuildingValid(building) || !Objects.Valid(w, unit)) return none;
        if (b.Kind == 5 && Damage.TribeCityWithGovernor(w, w.Entry(Tables.Cities, b.Index, 0x33)))
        {
            Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 0x33);
            if (!Objects.ForceValid(player) || Objects.UnitForce(w, unit) != player.Index) return none;
        }
        int dmg = (int)Attack.SiegeDamage(w, unit, Objects.FormationOf(w, unit), true);
        return Bombard.Apply(w, building, dmg, unit);
    }
}
