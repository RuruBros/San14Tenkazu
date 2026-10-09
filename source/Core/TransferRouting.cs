using System.Buffers.Binary;
using San14Tenkazu.Formulas;

namespace San14Tenkazu;

// Reachability for Unit_SendHome -> 301AB0 -> 183A30, options 1, one goal.
// Costs choose a route; admission decides whether any route exists. Search backwards, including
// the native source-tile exception. No ownership, terrain or water admission rule is bypassed.
internal static class TransferRouting
{
    internal static IReadOnlyList<int> Find(World w, UnitRecord unit, int force, int home)
    {
        Rec? destination = Map.Base(w, home);
        if (!Objects.Valid(w, destination)) return [];
        int source = unit.Tile, goal = Map.BaseHex(w, destination!);
        if (source >= GameMemory.TileCount || goal >= GameMemory.TileCount) return [];
        bool transport = unit.Kind == UnitFields.TransportKind;
        Rec targetTile = w.Table(Tables.Tiles)[goal];
        if (transport && (targetTile.U8(0x14) != force || Reach.Hostile(w, force, targetTile.U8(0x14)))) return [];
        var parent = new int[GameMemory.TileCount];
        Array.Fill(parent, -1);
        parent[goal] = goal;
        var queue = new Queue<int>();
        queue.Enqueue(goal);
        while (queue.TryDequeue(out int current))
        {
            if (current == source)
            {
                var path = new List<int> { source };
                for (int at = source; at != goal;) { at = parent[at]; path.Add(at); }
                return path;
            }
            for (int direction = 1; direction <= 6; direction++)
            {
                int next = Map.Neighbor(current, direction);
                if (next >= parent.Length || parent[next] >= 0) continue;
                if (next != source && !Admitted(w, unit, force, home, goal, next)) continue;
                parent[next] = current;
                queue.Enqueue(next);
            }
        }
        return [];
    }

    // 18A080, 2C6640, 2C6700 and the transport/facility filters in 183A30.
    internal static bool Admitted(World w, UnitRecord unit, int force, int home, int goal, int next)
    {
        Rec tile = w.Table(Tables.Tiles)[next];
        Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 30);
        int kind = terrain.Index;
        Rec building = Reach.Building(w, tile);
        Rec definition = w.Entry(Tables.ObjectKinds, building.U8(0x12), 30);
        bool gate = Reach.IsGate(building);
        bool primary = terrain.I16(0x2A) != 0 && (kind != 24 || WaterAllowed(unit.Data[0x1C])) &&
            (gate ? building.U16(0x10) == next && !Reach.Hostile(w, force, tile.U8(0x14))
                : definition.Index == 0 || definition.U8(0x2C) == 4);
        bool admitted = primary;
        if (!primary && kind == 17)
        {
            Rec b = w.Entry(Tables.Gates, building.Index - 51 is >= 1 and <= 10 ? building.Index - 51 : 0, 10);
            admitted = Objects.Valid(w, b) && Map.BaseHex(w, b) == next;
        }
        else if (!primary && kind == 16)
        {
            Rec city = w.Entry(Tables.Cities, building.Index <= 51 ? building.Index : 0, 51);
            if (Objects.Valid(w, city) && city.U16(0x4E) == home)
                admitted = Map.HexDistance(Map.BaseHex(w, city), unit.Tile) >= 3 ||
                    Map.BaseForce(w, city) == force && Map.RingAt(Map.BaseHex(w, city), 1).Contains(next);
        }
        if (!admitted || unit.Kind == UnitFields.TransportKind && Reach.Hostile(w, force, tile.U8(0x14))) return false;
        return !Map.BuildingValid(building) || Reach.IsCity(building) || gate || definition.U8(0x2C) is not (2 or 3);
    }

    internal static bool WaterAllowed(int code) => code == 12 || ((code - 13) & ~5) == 0 && code != 14;

    // Only the tables read by admission. Native pointer lists are read rather than reconstructed from map labels.
    internal static World Read(IRecordMemory memory, ulong raw)
    {
        var tables = new Dictionary<int, Rec[]>();
        foreach (var (table, size, count) in new[] {
            (Tables.Tiles, 0x20, 48400), (Tables.Terrains, 0x48, 31), (Tables.Buildings, 0x20, 3001),
            (Tables.ObjectKinds, 0x68, 31), (Tables.Counties, 0x98, 501), (Tables.Cities, 0x168, 52),
            (Tables.Gates, 0xC0, 11), (Tables.Forces, 0x268, 52), (Tables.Corps, 0x30, 52) })
        {
            byte[] pointers = memory.Read(raw + (ulong)table, count * 8);
            var records = new Rec[count];
            ulong first = BinaryPrimitives.ReadUInt64LittleEndian(pointers);
            bool contiguous = Enumerable.Range(0, count).All(i => BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(i * 8)) == first + (ulong)(i * size));
            byte[]? all = contiguous ? memory.Read(first, count * size) : null;
            for (int i = 0; i < count; i++)
            {
                ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(i * 8));
                if (pointer == 0) throw new InvalidDataException(L.Transfers.RouteTableMissing);
                records[i] = new Rec(i, all is null ? memory.Read(pointer, size) : all.AsSpan(i * size, size).ToArray(), table, pointer);
            }
            tables.Add(table, records);
        }
        ulong state = BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(raw + 0x871E8, 8));
        var bases = tables[Tables.Cities].Skip(1).Concat(tables[Tables.Gates].Skip(1)).Select(b => new Rec?[] { b }).ToList();
        return new World(tables, new Rec(0, []), new Dictionary<int, List<Rec?[]>> { [Lists.Bases] = bases }, [],
            new Rec(0, memory.Read(state, 0x1660)), []);
    }
}
