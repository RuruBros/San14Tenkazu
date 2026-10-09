namespace San14Tenkazu.Formulas;

// 0x1843E0 -> 0x183A30 with the exact 0x2AE980 return-call contract: one goal, null army/force,
// options 8 and workspace 2. Admission is 0x2C6850 with force 0, except the source bypasses it.
internal static class ReturnPath
{
    internal sealed record Result(IReadOnlyList<int> Hexes, int Cost)
    {
        internal bool Success => Hexes.Count != 0;
    }

    internal static Result Find(World w, int source, int target)
    {
        const int count = Map.Size * Map.Size;
        if ((uint)source >= count || (uint)target >= count)
            throw new ArgumentOutOfRangeException(nameof(source), "Return routing requires bounded tile indices.");
        int[] parent = new int[count];
        Array.Fill(parent, -1);
        var queue = new Queue<int>();
        parent[target] = target;
        queue.Enqueue(target);
        // options & 5 is zero, so each step costs 10000 and the heuristic is disabled. The native binary
        // queue inserts equal priorities to the right, making minimum removal FIFO for this uniform-cost
        // branch. First discovery cannot be improved: at most 48399 steps cannot overflow signed cost.
        while (queue.TryDequeue(out int current))
        {
            if (current == source)
            {
                var path = new List<int> { source };
                for (int at = source; at != target;) { at = parent[at]; path.Add(at); }
                return new(path, (path.Count - 1) * 10000);
            }
            for (int direction = 1; direction <= 6; direction++)
            {
                int next = Map.Neighbor(current, direction);
                if (next >= count || parent[next] != -1) continue;
                Rec tile = w.Table(Tables.Tiles)[next];
                Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 30);
                if (next != source && terrain.I16(0x2A) == 0) continue;
                parent[next] = current;
                queue.Enqueue(next);
            }
        }
        return new([], 0);
    }
}
