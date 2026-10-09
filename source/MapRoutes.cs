namespace San14Tenkazu;

// The way a unit will take to its target, estimated. The game keeps only a unit's next hex (it finds the way
// one step at a time), so the rest is the cheapest way over the hexes, with costs fitted to units' real steps
// (2026-10-01: 82% of 202 steps watched in the running game lay on such a way): roads and passes 0.6, great
// rivers 0.8, other land and water 1; sea, mountains and cliffs closed.
internal static class MapRoutes
{
    private static float Cost(int terrain) => terrain switch
    {
        19 or 20 or 22 or 23 => float.PositiveInfinity,
        2 or 11 or 12 => 0.6f,
        14 => 0.8f,
        _ => 1f
    };

    // The hexes from start to goal, both included; null when there is no way (or the goal is too far to look).
    internal static List<int>? Find(int start, int goal)
    {
        if (start == goal) return [start];
        byte[] terrain = MapTerrain.Terrain;
        var cost = new Dictionary<int, float> { [start] = 0 };
        var from = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, Distance(start, goal) * 0.6f);
        while (open.TryDequeue(out int tile, out _))
        {
            if (tile == goal)
            {
                var path = new List<int> { goal };
                while (from.TryGetValue(path[^1], out int previous)) path.Add(previous);
                path.Reverse();
                return path;
            }
            if (cost.Count > 20_000) return null;
            float here = cost[tile];
            foreach (int next in MapTerrain.Neighbours(tile))
            {
                float step = next == goal ? 1 : Cost(terrain[next]);
                if (float.IsInfinity(step)) continue;
                float total = here + step;
                if (cost.TryGetValue(next, out float known) && known <= total) continue;
                cost[next] = total;
                from[next] = tile;
                open.Enqueue(next, total + Distance(next, goal) * 0.6f);
            }
        }
        return null;
    }

    // Hexes between two hexes (even columns half a hex lower).
    internal static int Distance(int a, int b)
    {
        static (int X, int Y, int Z) Cube(int tile)
        {
            int column = tile % MapTerrain.Columns, row = tile / MapTerrain.Columns;
            int z = row - (column + (column & 1)) / 2;
            return (column, -column - z, z);
        }
        var (ax, ay, az) = Cube(a);
        var (bx, by, bz) = Cube(b);
        return Math.Max(Math.Abs(ax - bx), Math.Max(Math.Abs(ay - by), Math.Abs(az - bz)));
    }
}
