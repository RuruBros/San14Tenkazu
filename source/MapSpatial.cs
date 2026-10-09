using System.Collections;
using W = System.Windows;

namespace San14Tenkazu;

// Screen-space obstacles. Exact rectangle tests still decide overlaps; the grid only narrows the search.
// Candidates stay in insertion order, including ties, as the original linear scan did.
internal sealed class MapAreas : IReadOnlyCollection<W.Rect>
{
    private const double CellSize = 64;
    private readonly List<W.Rect> areas = [];
    private readonly Dictionary<(int X, int Y), List<int>> cells = [];
    private readonly List<List<int>> spare = [];
    private readonly HashSet<int> hits = [];
    private readonly List<int> ordered = [];
    internal int CandidateTests { get; private set; }
    public int Count => areas.Count;

    internal MapAreas() { }
    internal MapAreas(IEnumerable<W.Rect> initial) { foreach (W.Rect area in initial) Add(area); }

    internal void Clear()
    {
        areas.Clear();
        foreach (List<int> bucket in cells.Values) { bucket.Clear(); spare.Add(bucket); }
        cells.Clear();
        CandidateTests = 0;
    }

    internal void Add(W.Rect area)
    {
        int index = areas.Count;
        areas.Add(area);
        if (area.IsEmpty) return;
        for (int y = Cell(area.Top); y <= Cell(area.Bottom); y++)
            for (int x = Cell(area.Left); x <= Cell(area.Right); x++)
            {
                if (!cells.TryGetValue((x, y), out List<int>? bucket))
                {
                    if (spare.Count == 0) bucket = [];
                    else { bucket = spare[^1]; spare.RemoveAt(spare.Count - 1); }
                    cells[(x, y)] = bucket;
                }
                bucket.Add(index);
            }
    }

    internal Nearby Near(W.Rect area)
    {
        hits.Clear();
        ordered.Clear();
        if (!area.IsEmpty)
            for (int y = Cell(area.Top); y <= Cell(area.Bottom); y++)
                for (int x = Cell(area.Left); x <= Cell(area.Right); x++)
                    if (cells.TryGetValue((x, y), out List<int>? bucket))
                        foreach (int index in bucket)
                            if (hits.Add(index)) ordered.Add(index);
        ordered.Sort();
        return new Nearby(this);
    }

    // Concrete foreach uses a value-type enumerator; LINQ/test callers retain IEnumerable support.
    internal readonly struct Nearby(MapAreas owner) : IEnumerable<W.Rect>
    {
        public Enumerator GetEnumerator() => new(owner);
        IEnumerator<W.Rect> IEnumerable<W.Rect>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        internal struct Enumerator(MapAreas owner) : IEnumerator<W.Rect>
        {
            private int index = -1;
            public W.Rect Current => owner.areas[owner.ordered[index]];
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (++index >= owner.ordered.Count) return false;
                owner.CandidateTests++;
                return true;
            }
            public void Reset() => index = -1;
            public void Dispose() { }
        }
    }

    private static int Cell(double v) => (int)Math.Floor(v / CellSize);
    public IEnumerator<W.Rect> GetEnumerator() => areas.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// Equal-radius unit tokens, indexed by their centres. FirstNear preserves the first token in drawing order.
internal sealed class MapPoints
{
    private readonly Dictionary<(int X, int Y), List<(W.Point Point, int Index)>> cells = [];
    private readonly List<List<(W.Point Point, int Index)>> spare = [];
    private double cellSize;
    internal void Clear(double distance)
    {
        foreach (var bucket in cells.Values) { bucket.Clear(); spare.Add(bucket); }
        cells.Clear(); cellSize = distance;
    }
    private int Cell(double v) => (int)Math.Floor(v / cellSize);
    internal void Add(W.Point point, int index)
    {
        var cell = (Cell(point.X), Cell(point.Y));
        if (!cells.TryGetValue(cell, out var bucket))
        {
            if (spare.Count == 0) bucket = [];
            else { bucket = spare[^1]; spare.RemoveAt(spare.Count - 1); }
            cells[cell] = bucket;
        }
        bucket.Add((point, index));
    }
    internal int FirstNear(W.Point point)
    {
        int found = int.MaxValue, x = Cell(point.X), y = Cell(point.Y);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (cells.TryGetValue((x + dx, y + dy), out var bucket))
                    foreach (var token in bucket)
                        if (token.Index < found && (token.Point - point).Length < cellSize) found = token.Index;
        return found == int.MaxValue ? -1 : found;
    }
}
