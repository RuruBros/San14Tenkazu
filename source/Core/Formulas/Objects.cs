namespace San14Tenkazu.Formulas;

// The small functions of the game's objects that its formulas call: which force an officer serves, whether a record
// is a real one, the hidden-value decoding, the items an officer holds.
internal static class Objects
{
    // 0xDB00 (slot +0x48 of the data classes): a value kept hidden in a byte, rotated left by `rotate` bits and
    // xored with the record's index. CPersonData keeps its ability caps so (+0x1EC, +0x1ED: rotate 3), CPostData its
    // ability bonuses (rotate 5).
    internal static int Decode(int value, int rotate, int key) =>
        (((value & 0xFF) >> (8 - rotate)) | ((value & 0xFF) << rotate)) & 0xFF ^ key & 0xFF;

    // 0xDB30 (slot +0x58): a word kept hidden, rotated left by `rotate` bits and xored with the record's index
    // (CPolicyEffectData's per-level values: rotate 11).
    internal static int DecodeWord(int value, int rotate, int key) =>
        ((((value & 0xFFFF) >> (16 - rotate)) | ((value & 0xFFFF) << rotate)) ^ (key & 0xFFFF)) & 0xFFFF;

    // cvttss2si / cvttsd2si: truncation toward zero, and int.MinValue for NaN or a value out of range, as the CPU does.
    internal static int Trunc(float v) => float.IsNaN(v) || v >= 2147483648f || v < -2147483648f ? int.MinValue : (int)v;
    internal static int Trunc(double v) => double.IsNaN(v) || v >= 2147483648.0 || v <= -2147483649.0 ? int.MinValue : (int)v;

    // 0x2337B0: the force is the player's (CWorldData +0x3A).
    internal static bool IsPlayerForce(World w, Rec force)
    {
        int player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 0x33).Index;
        return player != 0 && player == force.Index;
    }

    // CAreaData slot +0x18 (0x233AD0): a 군 that belongs to one of forces 1..51.
    internal static bool CountyValid(Rec c) => c.Index != 0 && (uint)(c.U8(0x34) - 1) <= 0x32;

    // 0x22CA00: the officer's force, through his corps (+0xE0); the force table's entry 0 when the corps is not a
    // real one or names no force.
    internal static Rec ForceOf(World w, Rec person)
    {
        Rec corps = w.Entry(Tables.Corps, person.U8(0xE0), 0x33);
        int force = CorpsValid(corps) ? corps.U8(0x10) : 0;
        return w.Entry(Tables.Forces, force, 0x33);
    }

    // 0x22CC40: the number of the force a unit fights for, through its commander (+0x12) and his corps; 0 when
    // there is none.
    internal static int UnitForce(World w, Rec unit)
    {
        Rec? p = w.OfficerById(unit.U16(0x12));
        int c = p is not null && PersonValid(p) ? p.U8(0xE0) : 0;
        Rec corps = w.Entry(Tables.Corps, c, 0x33);
        return CorpsValid(corps) ? corps.U8(0x10) : 0;
    }

    // CArmyUnitData slot +0x18 (0x233B20): a unit slot in use.
    internal static bool UnitValid(Rec u) => u.Index != 0 && u.U16(0x12) != 0 && u.U8(0x10) != 0;

    // CFormationData slot +0x18 (0x233FD0).
    internal static bool FormationValid(Rec f) => f.Index != 0;

    // CItemData slot +0x18 (0x234130): only a nonzero item in ownership state 1 is valid.
    internal static bool ItemValid(Rec item) => item.Index != 0 && item.U8(0x50) == 1;

    // 0x22B090: the formation a unit fights in: +0x1B on land, +0x1C on terrain with bit 2 of +0x2C (water).
    internal static Rec FormationOf(World w, Rec unit)
    {
        Rec tile = w.Entry(Tables.Tiles, unit.U16(0x2A), 0xBD0F);
        Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 0x1E);
        int f = (terrain.U8(0x2C) & 2) != 0 ? unit.U8(0x1C) : unit.U8(0x1B);
        return w.Entry(Tables.Formations, f, 0x14);
    }

    // 0x22C0F0: a 주의 panel's value for a force: panel k when the force has it open (+0x1CB bits) and not shut
    // (+0x1D5 bits), else 0.
    internal static int Doctrine(World w, Rec force, int k)
    {
        int n = (k & 0xFF) - 1;
        int at = (n / 8) & 0xFF;
        int mask = (1 << ((n % 8) & 0xFF & 31)) & 0xFF;
        if ((force.U8(0x1CB + at) & mask) == 0 || (force.U8(0x1D5 + at) & mask) != 0) return 0;
        return w.Entry(Tables.Doctrine, k, 0x4F).I16(0x44);
    }

    // 0x232B80: the officer's force is one of 46..50.
    internal static bool InForces46To50(World w, Rec person) => (uint)(ForceOf(w, person).Index - 0x2E) < 5;

    // 0x233110: an officer of the special ID blocks; with `any` 0 only while based in 1..61 and outside forces 46..50.
    internal static bool SpecialOfficer(World w, Rec person, bool any)
    {
        if (!IsSpecialId(person.U16(0x10))) return false;
        if (any) return true;
        int b = person.U16(0xE2);
        return (uint)(b - 1) <= 0x3C && (uint)(ForceOf(w, person).Index - 0x2E) > 4;
    }

    // Obj_IsValid (0x32F500): a record that is there and passes its class's own check (vtable slot +0x18).
    internal static bool Valid(World w, Rec? r) => r is not null && r.Table switch
    {
        Tables.Officers => PersonValid(r),
        Tables.Forces => ForceValid(r),
        Tables.Corps => CorpsValid(r),
        Tables.Units => UnitValid(r),
        Tables.Cities or Tables.Gates => Map.BaseValid(w, r),
        Tables.Buildings => Map.BuildingValid(r),
        Tables.Counties => CountyValid(r),
        Tables.Tiles or Tables.Terrains => true,
        Tables.Formations or Tables.Policies or Tables.PolicyEffects or Tables.Credits or Tables.Ranks or Tables.Tactics => r.Index != 0,
        Tables.Items => ItemValid(r),
        Tables.Offices => OfficeValid(r),
        Tables.Personalities => Personality.Valid(r),
        _ => throw new NotSupportedException($"validity of table {r.Table:X}"),
    };

    // 0x231C60: where an officer is (+0xE4) as a kind and number: 0x232.. a county (10), 0x3E.. a unit (0x1B),
    // 0x34..0x3D a gate (6), 1..0x33 a city (5), else (0xFF, 0).
    internal static (int Kind, int Number) Place(Rec p)
    {
        int at = p.U16(0xE4);
        if ((uint)(at - 0x232) < 500) return (10, at - 0x231);
        if ((uint)(at - 0x3E) < 500) return (0x1B, at - 0x3D);
        if ((uint)(at - 0x34) < 10) return (6, at - 0x33);
        if (at is >= 1 and <= 0x33) return (5, at);
        return (0xFF, 0);
    }

    // 0x22A070 (second argument 1): the unit an officer leads: unit N when he is placed in unit N (0x1B) and it
    // names him as commander (+0x12), else unit 0.
    internal static Rec UnitOf(World w, Rec p)
    {
        var (kind, n) = Place(p);
        Rec u = w.Entry(Tables.Units, kind == 0x1B ? n : 0, 0x1F4);
        return u.U16(0x12) == p.U16(0x10) ? u : w.Table(Tables.Units)[0];
    }

    // 0x2345C0: the officer is in a city or gate.
    internal static bool InBase(Rec p) => (uint)(Place(p).Kind - 5) < 2;

    // 0x232400: an officer of a tribe (forces 46..50) whose base (+0xE2) is not one of 1..61.
    internal static bool TribeOutside(World w, Rec p) =>
        ((uint)(p.U16(0xE2) - 1) > 0x3C || p.U16(0xE2) == 0) && (uint)(ForceOf(w, p).Index - 0x2E) < 5;

    // 0x232240: the officer is away: in service (status 1..4) and not where his base is, or on a command of kind 0x10.
    internal static bool Away(World w, Rec p)
    {
        int status = p.U8(0xE6);
        if (status == 1 || (uint)(status - 2) <= 2)
        {
            int b = p.U16(0xE2);
            int kind, num;
            if ((uint)((b <= 0x33 ? b : 0) - 1) < 0x33) { kind = 5; num = b; }
            else
            {
                int gate = (uint)(b - 0x34) < 10 ? b - 0x33 : 0;
                if ((uint)(gate - 1) > 9) { kind = 5; num = 0; } else { kind = 6; num = gate; }
            }
            (int pk, int pn) = Place(p);
            if (kind != pk || num != pn) return true;
        }
        int id = p.U16(0x10);
        foreach ((int k, List<int> ids) in w.CmdActions)
            if (ids.Contains(id)) return k == 0x10;
        return false;
    }

    // CDistrictData slot +0x18 (0x233D90).
    internal static bool CorpsValid(Rec c) => c.Index != 0 && c.U8(0x10) != 0 && c.U8(0x11) != 0 && c.U16(0x12) != 0;

    // CRankData slot +0x18 (0x2343C0): every 작위 but entry 0.
    internal static bool RankValid(Rec r) => r.Index != 0;

    // CPostData slot +0x18 (0x234340).
    internal static bool OfficeValid(Rec o) => o.Index != 0 && o.U16(0x34) != 0;

    // CForceData slot +0x18 (0x233F10): a numbered force with a ruler.
    internal static bool ForceValid(Rec f) => f.Index != 0 && f.U16(0x10) != 0;

    // CPersonData slot +0x18 (0x2341F0).
    internal static bool PersonValid(Rec p)
    {
        if (p.Index == 0) return false;
        if ((p.U16(0x15E) & 0x4000) != 0) return p.U8(0x1C5) != 0;
        return (ushort)(p.U16(0x10) - 5001) <= 99 || p.U8(0xE6) != 0;
    }

    // 0x2C6E80: officer IDs in five blocks of ten (0x100F.., 0x1023.., 0x1037.., 0x104B.., 0x105F..).
    internal static bool IsSpecialId(int id) =>
        (uint)(id - 0x100F) <= 9 || (uint)(id - 0x1023) <= 9 || (uint)(id - 0x1037) <= 9 || (uint)(id - 0x104B) <= 9 ||
        (uint)(id - 0x105F) <= 9;

    // 0x3E9BA0 on the options object: one option, read from one of two copies by the selector at +0xE58.
    internal static int OptionAt4C4(World w) => w.Settings.I32(0xE58) == 0 ? w.Settings.I32(0x4C4) : w.Settings.I32(0x128);

    // CPersonItemIterator (0x92610, filter 0x333320): the items of the owned-items list whose owner (+0x4A) is the
    // officer's ID, for IDs 1..6000 only.
    internal static IEnumerable<Rec> ItemsOf(World w, Rec person)
    {
        int id = person.U16(0x10);
        if ((uint)(id - 1) > 0x176F) yield break;
        foreach (Rec?[] node in w.List(Lists.OwnedItems))
            if (node[0] is { } item && item.U16(0x4A) == id) yield return item;
    }
}
