namespace San14Tenkazu.Formulas;

// Who may strike whom: whether a force may attack another (0x2CA280), what stands on a hex (0x16EA90) and whether
// an attack reaches from one hex to another (0x2CA3E0), which the capture chance and the AI ask.
internal static class Reach
{
    // 0x2CA280: force `a` (by number) may attack force `b`: not force 0, not itself; against force 0 not force 51
    // nor a tribe (46..50); not two forces with bit 1 of +0x12 while CWorldData +0x165C is set; between two real
    // forces neither the other's suzerain (+0x194) and `a`'s relation byte to `b` (+0xEA + b) clear in its low 7
    // bits; anything else goes.
    internal static bool Hostile(World w, int a, int b)
    {
        Rec fa = w.Entry(Tables.Forces, a, 0x33), fb = w.Entry(Tables.Forces, b, 0x33);
        int na = fa.Index, nb = fb.Index;
        if (na == 0) return false;
        if (fa.Index == fb.Index) return false;
        if (nb == 0)
        {
            if (na == 0x33) return false;
            if ((uint)(na - 0x2E) < 5) return false;
        }
        if (w.WorldState.U8(0x165C) != 0 && (fa.U8(0x12) & 2) != 0 && (fb.U8(0x12) & 2) != 0) return false;
        if (na != 0x33 && nb != 0x33 && nb != 0)
        {
            if (fa.U8(0x194) != nb && fb.U8(0x194) != na) return (fa.U8(0xEA + nb) & 0x7F) == 0;
            return false;
        }
        return true;
    }

    // 0x22FB10: the building on a hex (its +0x1A, 3000 at most; building 0 for none).
    internal static Rec Building(World w, Rec tile) => w.Entry(Tables.Buildings, tile.U16(0x1A), 0xBB8);

    // 0x232650 / 0x232BD0: buildings 1..51 are the cities, 52..61 the gates.
    internal static bool IsCity(Rec building) => (uint)(building.Index - 1) < 0x33;
    internal static bool IsGate(Rec building) => (uint)(building.Index - 0x34) < 10;

    // 0x2C6950: a unit (1..500 by the hex's +0x1C) stands on the hex.
    internal static bool UnitOnHex(Rec tile) => (uint)(tile.I16(0x1C) - 1) < 500;

    // 0x16EA90: what the hex holds, as a kind and number: a standing city (5) or gate (6) by its building, else a
    // unit (0x1B) unless it is engaged (then nothing), else any other standing building (0xB), else nothing (0xFF).
    internal static (int Kind, int Number) ObjectAt(World w, Rec tile)
    {
        Rec o = Building(w, tile);
        bool standing = Map.BuildingValid(o);
        if (standing)
        {
            if (IsCity(o))
            {
                Rec c = w.Entry(Tables.Cities, o.Index, 0x33);
                if (Map.BaseValid(w, c)) return (5, c.Index);
            }
            else if (IsGate(o))
            {
                Rec g = w.Entry(Tables.Gates, o.Index - 0x33, 0xA);
                if (Map.BaseValid(w, g)) return (6, g.Index);
            }
        }
        int i = tile.I16(0x1C);
        if (i != 0)
        {
            Rec u = w.Entry(Tables.Units, i < 0 ? 0 : i, 0x1F4);
            if (Objects.UnitValid(u)) return Map.Engaged(w, u) ? (0xFF, 0) : (0x1B, u.Index);
        }
        return standing ? (0xB, o.Index) : (0xFF, 0);
    }

    // 0x2C6CD0: how far a unit of `force` in `formation` strikes: 2 with bit 0 of the formation's encoded +0xA8
    // vector byte 0x1C (plain +0xA6 without the vector), or for formation 10 under a force with bit 7 of +0x193;
    // else 1.
    internal static int Range(Rec force, Rec formation)
    {
        byte[] v = formation.VectorA8 ?? [];
        int bit = (v.Length & ~3) == 0x20 ? Objects.Decode(v[0x1C], 1, formation.Index) & 1 : formation.U8(0xA6) & 1;
        if (force.U8(0x193) >= 0x80 && formation.Index == 10) bit = 1;
        return bit != 0 ? 2 : 1;
    }

    // 0x2CA3E0: an attack from hex `from` reaches hex `to`. The attacker is the city (reach 3) or gate (2) whose
    // building stands on `from`, else the unit there (its formation's reach): none while shut (+0x49), and one
    // provoked (+0x4B / unit +0x25) only against what it was provoked by (+0x62/+0x64, unit +0x3C/+0x3E); a
    // confused unit (+0x23) never. The target is the city (reach +1), gate or buildings of class 2..3 standing on
    // `to`, measured to the building's hex, else the unit there. Then the hex distance within reach, the attacker's
    // troops above 0 and the forces hostile.
    internal static bool CanAttack(World w, int from, int to)
    {
        Rec tileF = w.Entry(Tables.Tiles, from, 0xBD0F), tileT = w.Entry(Tables.Tiles, to, 0xBD0F);
        int hexA = from, hexB = to, range = 0, troops = 0, forceA = 0;
        bool unitPath = true;
        int bld = tileF.U16(0x1A);
        if (bld != 0)
        {
            Rec o = Building(w, tileF);
            forceA = o.U8(0x13);
            if (IsCity(o))
            {
                Rec c = w.Entry(Tables.Cities, bld, 0x33);
                if (c.U8(0x49) != 0) return false;
                if (c.U8(0x4B) != 0 && !Provoker(w, tileT, c.U8(0x62), c.U16(0x64))) return false;
                hexA = o.U16(0x10); range = 3; troops = c.I32(0x3C); unitPath = false;
            }
            else if (IsGate(o))
            {
                Rec g = w.Entry(Tables.Gates, (uint)(bld - 0x34) < 10 ? bld - 0x33 : 0, 0xA);
                if (g.U8(0x49) != 0) return false;
                if (g.U8(0x4B) != 0 && !Provoker(w, tileT, g.U8(0x62), g.U16(0x64))) return false;
                hexA = o.U16(0x10); range = 2; troops = g.I32(0x3C); unitPath = false;
            }
        }
        if (unitPath)
        {
            if (!UnitOnHex(tileF)) return false;
            Rec u = w.Entry(Tables.Units, tileF.I16(0x1C), 0x1F4);
            if (u.U8(0x23) != 0) return false;
            if (u.U8(0x25) != 0 && !Provoker(w, tileT, u.U8(0x3C), u.U16(0x3E))) return false;
            Rec formation = w.Entry(Tables.Formations, Attack.RawFormation(w, u), 0x14);
            forceA = Objects.UnitForce(w, u);
            range = Range(w.Entry(Tables.Forces, forceA, 0x33), formation);
            troops = u.U16(0x16);
        }

        int forceB = 0;
        bool found = false;
        if (tileT.U16(0x1A) != 0)
        {
            Rec o = Building(w, tileT);
            forceB = o.U8(0x13);
            if (IsCity(o)) { hexB = o.U16(0x10); range++; found = true; }
            else if (IsGate(o)) { hexB = o.U16(0x10); found = true; }
            else
            {
                Rec kind = w.Entry(Tables.ObjectKinds, o.U8(0x12), 0x1E);
                if (kind.Index != 0 && kind.U8(0x2C) is 2 or 3) { hexB = o.U16(0x10); found = true; }
            }
        }
        if (!found)
        {
            if (!UnitOnHex(tileT)) return false;
            forceB = Objects.UnitForce(w, w.Entry(Tables.Units, tileT.I16(0x1C), 0x1F4));
        }
        return Map.HexDistance(hexA, hexB) <= range && troops > 0 && Hostile(w, forceA, forceB);
    }

    // The provoked side's remembered target (kind, number) is what stands on the hex.
    private static bool Provoker(World w, Rec tileT, int kind, int number)
    {
        var at = ObjectAt(w, tileT);
        return at.Kind == kind && at.Number == number;
    }
}
