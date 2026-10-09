namespace San14Tenkazu.Formulas;

// The hex map and the bases as the rules see them: rings of hexes, the units standing in them, the bases by number,
// and whether a unit is engaged.
internal static class Map
{
    internal const int Size = 220;          // 220 × 220 hexes; a hex's number is row × 220 + column

    // The hex steps by column parity (rdata 0x13ACD20, 1-based directions): {column, row}.
    private static readonly (int Col, int Row)[] Even = [(0, 0), (-1, 0), (0, -1), (1, 0), (-1, 1), (0, 1), (1, 1)];
    private static readonly (int Col, int Row)[] Odd = [(0, 0), (-1, -1), (0, -1), (1, -1), (-1, 0), (0, 1), (1, 0)];
    private static readonly int[] Sides = [2, 3, 6, 5, 4, 1];   // rdata 0x13ACD80

    private static (int Col, int Row) Step(int col, int dir)
    {
        // the parity of |col|, as the code takes it
        int parity = Math.Abs(col) & 1;
        return parity == 0 ? Even[dir] : Odd[dir];
    }

    // 0x2A7720: one of the six 1-based directions; off-map coordinates use the 0xBD10 sentinel.
    internal static int Neighbor(int center, int direction)
    {
        int col = center % Size, row = center / Size;
        bool odd = (col & 1) != 0;
        // The native bottom-row diagonal branches fall back to their horizontal neighbor.
        return direction switch
        {
            0 => center,
            1 when col > 0 && (!odd || row > 0) => center - (odd ? Size + 1 : 1),
            2 when row > 0 => center - Size,
            3 when col < Size - 1 && (!odd || row > 0) => center + (odd ? 1 - Size : 1),
            4 when col > 0 => center + (!odd && row < Size - 1 ? Size - 1 : -1),
            5 when row < Size - 1 => center + Size,
            6 when col < Size - 1 => center + (!odd && row < Size - 1 ? Size + 1 : 1),
            _ => Size * Size,
        };
    }

    // 0x2A7890: the hexes around a hex out to `radius`, ring by ring (each from four steps out in direction 4, then
    // six sides), only those on the map.
    internal static List<int> Ring(int center, int radius)
    {
        var hexes = new List<int>();
        for (int r = 1; r <= radius; r++)
        {
            int row = center / Size, col = center % Size;
            for (int k = 0; k < r; k++)
            {
                var s = Step(col, 4);
                row += s.Row; col += s.Col;
            }
            foreach (int dir in Sides)
                for (int k = 0; k < r; k++)
                {
                    var s = Step(col, dir);
                    row += s.Row; col += s.Col;
                    if ((uint)col < Size && (uint)row < Size) hexes.Add(row * Size + col);
                }
        }
        return hexes;
    }

    // 0x2B48A0: the hexes at exactly `radius` from a hex (one ring of Ring).
    internal static List<int> RingAt(int center, int radius)
    {
        var hexes = new List<int>();
        int row = center / Size, col = center % Size;
        for (int k = 0; k < radius; k++)
        {
            var s = Step(col, 4);
            row += s.Row; col += s.Col;
        }
        foreach (int dir in Sides)
            for (int k = 0; k < radius; k++)
            {
                var s = Step(col, dir);
                row += s.Row; col += s.Col;
                if ((uint)col < Size && (uint)row < Size) hexes.Add(row * Size + col);
            }
        return hexes;
    }

    // 0x2AA850: the distance between two hexes in steps, on the offset-column grid (columns of odd number sit half
    // a row lower).
    internal static int HexDistance(int a, int b)
    {
        int dc = Math.Abs(b % Size - a % Size);
        int pa = (a % Size) & 1, pb = (b % Size) & 1;
        int v = (pb == 0 ? 1 : 0) + (b / Size - a / Size) * 2 - (pa == 0 ? 1 : 0);
        int dr = Math.Abs(v);
        return dc <= dr ? (dr - dc) / 2 + dc : dc;
    }

    // 0x2A7490: the units standing within `radius` of a unit (the hex's +0x1C names its unit, 1..500).
    internal static List<Rec> UnitsNear(World w, Rec unit, int radius)
    {
        int center = w.Entry(Tables.Tiles, unit.U16(0x2A), 0xBD0F).Index;
        var units = new List<Rec>();
        foreach (int hex in Ring(center, radius))
        {
            int id = w.Table(Tables.Tiles)[hex].I16(0x1C);
            if ((uint)(id - 1) >= 500) continue;
            Rec u = w.Entry(Tables.Units, id, 0x1F4);
            if (Objects.UnitValid(u)) units.Add(u);
        }
        return units;
    }

    // World_GetBase (0x32C130): base 0 is city 0; 1..61 the base on the bases list with that +0x4E (else the list's
    // first); above that the list's first.
    internal static Rec? Base(World w, int id)
    {
        if (id == 0) return w.Table(Tables.Cities)[0];
        List<Rec?[]> bases = w.List(Lists.Bases);
        if (id < 0x3E)
            foreach (Rec?[] node in bases)
                if (node[0] is { } b && b.U16(0x4E) == id) return b;
        return bases.Count > 0 ? bases[0][0] : null;
    }

    // CCityData / CGateData slot +0x18 (0x233C40, 0x234010): a numbered base whose building stands and +0x69 is 0.
    internal static bool BaseValid(World w, Rec b) =>
        b.Index != 0 && BuildingValid(w.Entry(Tables.Buildings, b.U16(0x4E), 0xBB8)) && b.U8(0x69) == 0;

    // CObjectData slot +0x18 (0x2341C0).
    internal static bool BuildingValid(Rec o) => o.Index != 0 && o.U16(0x10) < 0xBD10 && o.U8(0x12) != 0;

    // 0x22D740: the hex a base stands on (its building's +0x10), 0xBD10 for none.
    internal static int BaseHex(World w, Rec b)
    {
        Rec o = w.Entry(Tables.Buildings, b.U16(0x4E), 0xBB8);
        return BuildingValid(o) ? o.U16(0x10) : 0xBD10;
    }

    // Slot +0xB0 of CCityData (0x22CD10: its corps' force, +0x30) and CGateData (0x22CD80: the owner of its 군's
    // centre hex): the force a base belongs to, 0 for none.
    internal static int BaseForce(World w, Rec b)
    {
        if (b.Table == Tables.Cities)
        {
            Rec corps = w.Entry(Tables.Corps, b.U8(0x30), 0x33);
            return corps.Index != 0 && corps.U8(0x10) != 0 && corps.U8(0x11) != 0 ? corps.U8(0x10) : 0;
        }
        Rec tile = BuildingTile(w, b);
        Rec county = w.Entry(Tables.Counties, tile.U16(0x12), 0x1F4);
        if (!Objects.CountyValid(county)) return 0;
        return w.Entry(Tables.Tiles, county.U16(0x3A), 0xBD0F).U8(0x14);
    }

    // 0x22D500: the hex record a base's building stands on (hex 0 for none).
    internal static Rec BuildingTile(World w, Rec b)
    {
        Rec o = w.Entry(Tables.Buildings, b.U16(0x4E), 0xBB8);
        return w.Entry(Tables.Tiles, BuildingValid(o) ? o.U16(0x10) : 0, 0xBD0F);
    }

    // 0x2C9900: the unit stands on the ring around one of its force's bases (2 hexes out from a city, 1 from a gate).
    internal static bool NearOwnBase(World w, Rec unit)
    {
        if (!Objects.UnitValid(unit)) return false;
        Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 0x33);
        if (!Objects.ForceValid(force)) return false;
        int fi = force.Index;
        if ((uint)(fi - 1) > 0x32) return false;
        foreach (Rec?[] node in w.List(Lists.Bases))
        {
            if (node[0] is not { } b || BaseForce(w, b) != fi || !Objects.Valid(w, b)) continue;
            int radius = (uint)(b.U16(0x4E) - 0x34) > 9 ? 2 : 1;
            if (RingAt(BaseHex(w, b), radius).Contains(unit.U16(0x2A))) return true;
        }
        return false;
    }

    // CFootholdPersonIterator (0x234810, filter 0x333160): the officers on the officer list based at the base
    // (+0xE2 == its number +0x4E, 1..61) in the status group `mode` (0x232DE0: 0 in service 1..4, 1 status 6,
    // 2 status 5, 3 status 8, 4 any).
    internal static IEnumerable<Rec> OfficersOf(World w, Rec baseRec, int mode)
    {
        int id = baseRec.U16(0x4E);
        if ((uint)(id - 1) >= 0x3D) yield break;
        foreach (Rec?[] node in w.List(Lists.OfficerList))
        {
            if (node[0] is not { } p || p.U16(0xE2) != id) continue;
            int s = p.U8(0xE6);
            bool ok = mode switch { 0 => s == 1 || (uint)(s - 2) <= 2, 1 => s == 6, 2 => s == 5, 3 => s == 8, 4 => true, _ => false };
            if (ok) yield return p;
        }
    }

    // 0x233440: the unit is pending final removal, or participates in a deferred destruction/capture request.
    internal static bool Engaged(World w, Rec unit)
    {
        foreach (Rec?[] node in w.List(Lists.EngagedUnits))
            if (ReferenceEquals(node[0], unit)) return true;
        foreach (Rec?[] e in w.List(Lists.Engagements))
        {
            Rec? attacker = e[0], target = e[2];
            if (Objects.Valid(w, e[1]) && Objects.Valid(w, target))
            {
                int f = Objects.UnitForce(w, target!);
                if (f != 0x33 && (uint)(f - 0x2E) > 4 && ReferenceEquals(target, unit)) return true;
            }
            if (Objects.Valid(w, attacker) && ReferenceEquals(attacker, unit)) return true;
        }
        return false;
    }

    // 0x2C9310: the unit stands in its commander's base (and is not engaged; forces 46..50 only while based in 1..61;
    // never force 51).
    internal static bool InOwnBase(World w, Rec unit)
    {
        if (Engaged(w, unit)) return false;
        int f = Objects.UnitForce(w, unit);
        if (f == 0x33) return false;
        Rec? cmd = w.OfficerById(unit.U16(0x12));
        if ((uint)(f - 0x2E) < 5 && !(cmd is not null && (uint)(cmd.U16(0xE2) - 1) <= 0x3C)) return false;
        if (!Objects.UnitValid(unit) || cmd is null || !Objects.PersonValid(cmd)) return false;
        Rec? b = Base(w, cmd.U16(0xE2));
        if (b is null || !Objects.Valid(w, b)) return false;
        return BaseHex(w, b) == unit.U16(0x2A);
    }
}
