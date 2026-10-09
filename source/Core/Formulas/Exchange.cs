namespace San14Tenkazu.Formulas;

// A force's answer to an exchange (Exchange_Judge 0x324300, kind 0x13, the game's "交換成否判定"): force A's envoy
// offers gold, food or an item for gold, food or an item of force B's. B refuses when the ask would leave its
// ruler's base short of gold or food, or when losing the item would lower any of its ruler's abilities; otherwise a
// chance from the relation, the envoy's ability and the values' ratio is rolled. Diplomacy.Judge handles kinds
// 6 and 7; exchange offers of officers or land are not modelled here.
internal static class Exchange
{
    [Rva(0x1A483B4)] internal const int GoldFactor = 100;      // value = gold × 100 / 100
    [Rva(0x1A483C0)] internal const int FoodDivisor = 50;      // value = food × 10 / 50
    [Rva(0x1A483CC)] internal const int ItemGradeFactor = 400; // value = min(50, item +0x49) × 400
    [Rva(0x1A48814)] internal const int RelationDivisor = 6;
    [Rva(0x1A48820)] internal const int AbilityDivisor = 10;
    [Rva(0x1A48838)] internal const int AiBonus = 20;
    [Rva(0x1A487FC)] internal const int FoodReserve = 20000;
    [Rva(0x1A487F0)] internal const int GoldReserve = 2000;

    internal readonly record struct Offer(int Gold, int Food, int Item);

    // 0x3222B0 for gold, food and an item (the first that is set); officers and land count 0 here.
    internal static int Value(World w, Offer o)
    {
        if (o.Gold > 0) return (int)((long)GoldFactor * o.Gold / 100);
        if (o.Food > 0) return (int)((long)o.Food * 10 / FoodDivisor);
        if ((uint)(o.Item - 1) < 0xC9) return Math.Min(50, (int)w.Entry(Tables.Items, o.Item, 0xC9).U8(0x49)) * ItemGradeFactor;
        return 0;
    }

    // 0x22A7B0: the city a base counts to (its building's hex → county → the county's city, +0x35).
    internal static Rec CityOf(World w, Rec b)
    {
        Rec tile = Map.BuildingTile(w, b);
        Rec county = w.Entry(Tables.Counties, tile.U16(0x12), 0x1F4);
        return w.Entry(Tables.Cities, Objects.CountyValid(county) ? county.U8(0x35) : 0, 0x33);
    }

    // CCityData +0x34 / +0x38 (slots +0x90 / +0xA0); a gate answers with its city's.
    internal static int BaseGold(World w, Rec b) => (b.Table == Tables.Cities ? b : CityOf(w, b)).I32(0x34);
    internal static int BaseFood(World w, Rec b) => (b.Table == Tables.Cities ? b : CityOf(w, b)).I32(0x38);

    internal static bool Judge(World w, Rec a, Rec envoy, Rec b, Offer give, Offer ask)
    {
        Rec[] officers = w.Table(Tables.Officers);
        Rec rulerB = w.OfficerById(b.U16(0x10)) ?? officers[0];
        int giveValue = Math.Max(1, Value(w, give));
        int askValue = Value(w, ask);
        Rec? home = Map.Base(w, rulerB.U16(0xE2));
        if (home is null) return false;
        int rel = b.Index < 0x34 ? a.U8(0x13 + b.Index) : 0;
        int troops = home.I32(0x3C);
        int ability = OfficerAbility.Get(w, envoy, 3, OfficerAbility.All);

        if (ask.Gold > 0)
        {
            if (BaseGold(w, home) - ask.Gold <= GoldReserve) return false;
        }
        else if (ask.Food > 0)
        {
            if (BaseFood(w, home) - ask.Food <= troops + FoodReserve) return false;
        }
        else if ((uint)(ask.Item - 1) < 0xC9)
        {
            // B's ruler without the asked item and with the offered one: no ability may drop
            Rec itemB = w.Entry(Tables.Items, ask.Item, 0xC9);
            Rec? itemA = (uint)(give.Item - 1) < 0xC9 ? w.Entry(Tables.Items, give.Item, 0xC9) : null;
            int[] before = new int[5];
            for (int k = 0; k < 5; k++) before[k] = OfficerAbility.Get(w, rulerB, k, OfficerAbility.Items);
            ushort ownerB = itemB.U16(0x4A), ownerA = itemA?.U16(0x4A) ?? 0;
            Set16(itemB, 0x4A, 0);
            if (itemA is not null) Set16(itemA, 0x4A, Math.Min(rulerB.U16(0x10), (ushort)6000));
            bool drops = false;
            for (int k = 0; k < 5; k++) if (OfficerAbility.Get(w, rulerB, k, OfficerAbility.Items) < before[k]) drops = true;
            Set16(itemB, 0x4A, Math.Min(ownerB, (ushort)6000));
            if (itemA is not null) Set16(itemA, 0x4A, Math.Min(ownerA, (ushort)6000));
            if (drops) return false;
        }

        int rulerA = a.Index != 0 ? a.U16(0x10) : 0x1396;
        int roll = Wounded.Seeded(w, 100, 1, envoy.U16(0x10), rulerA, rulerB.U16(0x10), 0);
        int chance = rel / RelationDivisor + 100 + (ability / AbilityDivisor - askValue * 100 / giveValue);
        if (!Objects.IsPlayerForce(w, a) && !Objects.IsPlayerForce(w, b)) chance += AiBonus;
        return roll < chance;
    }

    private static void Set16(Rec r, int at, int value)
    {
        r.Bytes[at] = (byte)value;
        r.Bytes[at + 1] = (byte)(value >> 8);
    }
}
