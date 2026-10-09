namespace San14Tenkazu.Formulas;

// CPersonData::GetAbility (rva 0x22FCE0, 610 callers): an officer's ability (0 통솔, 1 무력, 2 지력, 3 정치, 4 매력)
// as the game's rules use it. flags picks the bonuses: 1 injury, 2 items, 4 the ruler's 작위, 8 the officer's 관직;
// the 주의 panel bonus always applies. Checked against the game's code on every officer and on fuzzed objects
// (Tests/Formulas/OfficerAbilityTests).
internal static class OfficerAbility
{
    internal const int Injury = 1, Items = 2, Rank = 4, Office = 8, All = 0xFFF;

    [Rva(0x1A49D44)] internal const int PercentHealthy = 100;
    [Rva(0x1A49D50)] internal const int PercentInjured1 = 80;
    [Rva(0x1A49D5C)] internal const int PercentInjured2 = 50;
    [Rva(0x1A49D68)] internal const int PercentInjured3 = 20;
    [Rva(0x1A49D74)] internal const int Scale = 100;
    // Officers whose +0x1BC is 1 work at 90% (not rulers, not with +0x15E bit 14, and only while option 0x4C4 is 0).
    [Rva(0x1A49D80)] internal const int ScaleService1 = 90;

    // The item bonus counts the best item of the ability's kind, at most this.
    internal const int ItemBonusMax = 10;
    // 옥새 (item 123) makes its holder's 매력 100 before the cap.
    internal const int SealItem = 0x7B, SealCharm = 100;

    internal static int Get(World w, Rec p, int ability, int flags)
    {
        int value = p.U8(0xEC + ability);

        int injury = (flags & Injury) != 0 ? p.U8(0x161) : 0;
        int percent = injury switch
        {
            0 => PercentHealthy,
            1 => PercentInjured1,
            2 => PercentInjured2,
            3 => PercentInjured3,
            _ => 0,
        };

        int item = 0;
        if ((flags & Items) != 0)
        {
            int kind = ability + 1;                     // 0x2AF740: {1, 2, 3, 4, 5}[ability]
            foreach (Rec it in Objects.ItemsOf(w, p))
            {
                if (it.U16(0x4A) != p.U16(0x10) || it.U8(0x45) != kind) continue;
                int bonus = Math.Min((int)it.U8(0x46), ItemBonusMax);
                if (bonus > item) item = bonus;
            }
        }

        int rank = 0, office = 0;
        if (p.U8(0xE6) == 1)
        {
            if ((flags & Rank) != 0)
            {
                Rec r = w.Entry(Tables.Ranks, Objects.ForceOf(w, p).U8(0x47), 12);
                if (Objects.RankValid(r)) rank = r.U8(0xAD + ability);
            }
        }
        else if ((flags & Office) != 0)
        {
            Rec o = w.Table(Tables.Offices)[p.U8(0xE7)];
            if (Objects.OfficeValid(o)) office = OfficeBonus(o, ability);
        }

        int scale = Scale;
        if (Objects.OptionAt4C4(w) == 0 && p.U8(0xE6) != 1)
        {
            bool check = true;
            if (Objects.IsSpecialId(p.U16(0x10)))
            {
                int b = p.U16(0xE2);
                if ((uint)(b - 1) <= 0x3C && (uint)(Objects.ForceOf(w, p).Index - 0x2E) > 4) check = false;
            }
            if (check && (p.U16(0x15E) & 0x4000) == 0 && p.U8(0x1BC) == 1) scale = ScaleService1;
        }

        int doctrine = 0;
        int ideology = p.U8(0x14C);
        if (ideology != 0)
        {
            int k = ability + ideology * 4 + (ideology - 3);
            Rec f = Objects.ForceOf(w, p);
            int n = (k & 0xFF) - 1;
            int at = (n / 8) & 0xFF;
            int mask = (1 << ((n % 8) & 0xFF & 31)) & 0xFF;
            if ((f.U8(0x1CB + at) & mask) != 0 && (f.U8(0x1D5 + at) & mask) == 0)
                doctrine = w.Entry(Tables.Doctrine, k, 0x4F).I16(0x44);
        }

        int v = value * percent / 100 * scale / 100 + doctrine + office + rank + item;

        int cap;
        if (ability == 4)
        {
            if (Objects.ItemsOf(w, p).Any(it => it.Index == SealItem)) v = SealCharm;
            cap = Objects.Decode(p.U8(0x1ED), 3, p.Index);
        }
        else cap = Objects.Decode(p.U8(0x1EC), 3, p.Index);

        return v < 1 ? 1 : Math.Min(v, cap);
    }

    // 0x1EE270: a 관직's bonus to 통솔·무력·지력·정치 (none to 매력), kept encoded in its vector; a record without the
    // five-entry vector reads +0x38 instead.
    internal static int OfficeBonus(Rec office, int ability)
    {
        byte[] vec = office.Vector40 ?? [];
        if ((vec.Length & ~3) == 0x14)
            return ability >= 4 ? 0 : Objects.Decode(vec[(ability + 1) * 4], 5, office.Index);
        return office.U8(0x38 + ability);
    }
}
