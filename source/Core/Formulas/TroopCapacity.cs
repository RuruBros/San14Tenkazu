namespace San14Tenkazu.Formulas;

internal static class TroopCapacity
{
    [Rva(0x1A47D3C)] internal const int PersonalityUnit = 1;
    [Rva(0x1A49CE8)] internal const int TribeCapacity = 20000;

    // 0x2B2920: office/rank capacity, capped before the ordinary personality and credit bonuses, then doctrine 55.
    internal static int Get(World w, Rec person)
    {
        if (!Objects.PersonValid(person)) return 0;
        Rec force = Objects.ForceOf(w, person);
        bool ruler = person.U8(0xE6) == 1;
        Rec office = w.Table(Tables.Offices)[person.U8(0xE7)];
        byte[] v = office.Vector40 ?? [];
        int capacity = ruler ? w.Entry(Tables.Ranks, force.U8(0x47), 12).U16(0xAA)
            : (v.Length & ~3) == 20 ? Objects.DecodeWord(BitConverter.ToUInt16(v, 0), 13, office.Index) : office.U16(0x34);
        bool tribe = Wounded.InTribeForce(w, person) || Objects.SpecialOfficer(w, person, false);
        if (tribe) capacity = TribeCapacity;
        else
        {
            int ordinary = 0, custom = 0;
            for (int slot = 0; slot < 9; slot++)
            {
                Rec p = w.Entry(Tables.Personalities, Personality.Slot(w, person, slot), 0x163);
                if (!Personality.Valid(p)) continue;
                int amount = Personality.EffectValue(p, 0x4D, 0);
                if ((uint)(p.Index - 0x100) < 100) custom += amount;
                else ordinary += amount;
            }
            capacity = Math.Min(capacity + custom * PersonalityUnit, 20000) + ordinary * PersonalityUnit;
            if (!ruler) capacity += Policy.LevelBonus(w, person, 1);
        }
        return capacity + (Objects.ForceValid(force) ? Objects.Doctrine(w, force, 0x37) : 0);
    }
}
