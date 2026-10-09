namespace San14Tenkazu.Formulas;

// 0x2ACB50 -> 0x1843E0/0x183A30: one goal, no army, the source base's force, options 4 and workspace 0.
internal static class CorpsPath
{
    [Rva(0x1A4A1DC)] internal const int FireCost = 5000;

    // 0xBE120 and 0x2AE6F0 with the null-army context's initial fire-cost switch equal to one.
    internal static int StepCost(World w, Rec tile)
    {
        Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 30);
        int cost = terrain.U16(0x2A);
        if (w.Settings.I32(0xE58) == 0 && (terrain.Index is >= 3 and <= 14 or 24))
            cost = Math.Clamp(unchecked(cost * BattlePower.Slider(w, 26)) / 100, 225, 999);
        return unchecked(cost + tile.U16(0x16) * FireCost / 100);
    }

    internal static ReturnPath.Result Find(World w, Rec sourceBase, Rec targetBase)
    {
        if (!Objects.Valid(w, sourceBase) || !Objects.Valid(w, targetBase) ||
            Map.BaseForce(w, sourceBase) != Map.BaseForce(w, targetBase)) return new([], 0);
        int source = Map.BuildingTile(w, sourceBase).Index, target = Map.BuildingTile(w, targetBase).Index;
        const int count = Map.Size * Map.Size;
        int force = Map.BaseForce(w, sourceBase);
        int[] parent = new int[count], cost = new int[count], version = new int[count];
        Array.Fill(parent, -1);
        parent[target] = target;
        var queue = new PriorityQueue<(int Hex, int Version), (int Priority, long Order)>();
        long order = 0;
        queue.Enqueue((target, ++version[target]), (0, order++));
        float plain = w.Table(Tables.Terrains)[2].U16(0x2A);
        float factor = plain / plain;
        while (queue.TryDequeue(out var item, out _))
        {
            int current = item.Hex;
            if (item.Version != version[current]) continue;
            if (current == source)
            {
                var path = new List<int> { source };
                for (int at = source; at != target;) { at = parent[at]; path.Add(at); }
                return new(path, cost[source]);
            }
            for (int direction = 1; direction <= 6; direction++)
            {
                int next = Map.Neighbor(current, direction);
                if (next >= count) continue;
                Rec tile = w.Table(Tables.Tiles)[next];
                Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 30);
                if (next != source && (terrain.I16(0x2A) == 0 || force != 0 && Reach.Hostile(w, force, tile.U8(0x14)))) continue;
                int value = unchecked(cost[current] + StepCost(w, tile));
                if (parent[next] != -1 && cost[next] <= value) continue;
                parent[next] = current;
                cost[next] = value;
                int priority = unchecked(value + Objects.Trunc(Map.HexDistance(next, source) * factor));
                // Equal priorities go to the native queue's right side. A decrease removes and reinserts a node;
                // versioned entries preserve that ordering while discarding the removed queue entries.
                queue.Enqueue((next, ++version[next]), (priority, order++));
            }
        }
        return new([], 0);
    }
}
