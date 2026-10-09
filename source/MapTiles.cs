using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// Borders (force and county), grid and map symbols as a pyramid of tiles in world space, like a web map.
// Level k is drawn at 4 * 2^k device pixels per world unit and the level nearest the current zoom
// is shown. Tiles are drawn on worker threads and kept, so going back to an area or a zoom level
// is instant, and they are small enough to show while the map moves. Where a tile in view is not
// drawn yet, the finer tiles inside it or a part of a coarser tile stand in (StandIn). When the
// territory changes, only tiles near the changed hexes are drawn again; until then the old ones stay
// on screen.
internal sealed class MapTiles
{
    internal const int TileSize = 512;
    private const int LevelCount = 6;               // 4 to 128 device pixels per world unit
    private const int Workers = 2;

    // Draws one tile (level, x, y) for the given territory, counties and base map kind; null leaves it empty.
    internal delegate WMI.BitmapSource? Renderer(TileKey key, byte[]? owners, ushort[]? counties, bool fromGame);

    private readonly Renderer render;
    private readonly int firstLevel;               // coarser levels are not drawn at all
    private readonly int maxCached;                // tiles are about 1 MB each
    private readonly int standInLevels;            // how much coarser a tile may be to stand in

    internal static float PixelsPerUnit(int level) => 4f * (1 << level);

    internal readonly record struct TileKey(int Level, int X, int Y);

    private sealed class Tile(TileKey key)
    {
        internal readonly TileKey Key = key;
        internal WMI.BitmapSource? Image;
        internal int Version = -1;                  // content version the image was drawn for
        internal bool Rendering;
        internal WC.Image? Element;                 // its image, or the part of a coarser one standing in
        internal WMI.BitmapSource? StandIn, StandInFrom;    // that part, and the coarser image it is cut from
        internal long LastUsed;
    }

    private readonly WC.Canvas canvas = new() { IsHitTestVisible = false };
    private readonly Dictionary<TileKey, Tile> tiles = [];
    private readonly HashSet<Tile> shown = [];
    private readonly List<Tile> wanted = [];        // in drawing order
    private (int Level, int X0, int Y0, int X1, int Y1) range = (-1, 0, 0, -1, -1);
    private (int X0, int Y0, int X1, int Y1) inView;       // the tiles on screen: range without its margin
    private W.Point focus;
    private int version;
    private byte[]? owners;
    private ushort[]? counties;
    private bool fromGame = true;
    private bool enabled = true;
    private int running;
    private long frame;

    // A layer that starts at firstLevel shows nothing when zoomed out further, so what lies under it shows.
    // A layer of thin lines takes a small standInLevels: magnified, a coarse tile's lines are wide smears.
    internal MapTiles(Renderer render, int firstLevel = 0, int maxCached = 120, int standInLevels = LevelCount)
    {
        this.render = render;
        this.firstLevel = firstLevel;
        this.maxCached = maxCached;
        this.standInLevels = standInLevels;
        // Tiles meet edge to edge; anti-aliased quad edges would show thin seams between them.
        WM.RenderOptions.SetEdgeMode(canvas, WM.EdgeMode.Aliased);
        WM.RenderOptions.SetBitmapScalingMode(canvas, WM.BitmapScalingMode.Linear);
    }

    internal W.UIElement Element => canvas;

    // A tile was drawn or the tiles shown changed (the GPU renderer draws the next frame).
    internal event Action? Changed;

    // Tiles still to be drawn for the current view (for diagnostics).
    internal int Pending => wanted.Count(t => t.Version != version);

    // Off while the GPU's shaders draw what the tiles show (MapShaders): no tile is drawn, shown or kept.
    internal bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            if (!enabled) tiles.Clear();
            Refresh();
        }
    }

    // Called for every frame the view changes; cheap unless the set of tiles changes.
    internal void Update(W.Rect visible, double pixelsPerUnit)
    {
        if (!enabled) return;
        int level = Math.Clamp((int)Math.Round(Math.Log2(pixelsPerUnit / 4)), 0, LevelCount - 1);
        if (level < firstLevel)
        {
            if (range.Level == -1 && shown.Count == 0) return;
            range = (-1, 0, 0, -1, -1);
            Refresh();
            return;
        }
        float ppu = PixelsPerUnit(level);
        // One tile of margin, so short moves find their tiles ready.
        double units = TileSize / ppu;
        (int columns, int rows) = Counts(level);
        (int X0, int Y0, int X1, int Y1) view = ((int)Math.Floor(visible.Left / units), (int)Math.Floor(visible.Top / units),
            (int)Math.Floor(visible.Right / units), (int)Math.Floor(visible.Bottom / units));
        int x0 = Math.Max(0, view.X0 - 1), x1 = Math.Min(columns - 1, view.X1 + 1);
        int y0 = Math.Max(0, view.Y0 - 1), y1 = Math.Min(rows - 1, view.Y1 + 1);
        focus = new W.Point((visible.Left + visible.Right) / 2, (visible.Top + visible.Bottom) / 2);
        if (range == (level, x0, y0, x1, y1) && inView == view) return;
        range = (level, x0, y0, x1, y1);
        inView = view;
        Refresh();
    }

    // New territory (null before the game is connected) or a different base map.
    internal void SetContent(byte[]? newOwners, bool game)
    {
        bool all = game != fromGame || owners is null || newOwners is null;
        List<System.Drawing.PointF>? changed = null;
        if (!all)
        {
            changed = [];
            for (int tile = 0; tile < newOwners!.Length && changed.Count <= 3000; tile++)
                if (newOwners[tile] != owners![tile]) changed.Add(MapTerrain.TileCenter(tile));
            if (changed.Count == 0) return;
            all = changed.Count > 3000;
        }
        if (owners is null && newOwners is null && game == fromGame && version > 0) return;
        owners = newOwners;
        fromGame = game;
        version++;
        // Tiles away from every changed hex are still correct.
        if (!all)
            foreach (Tile tile in tiles.Values)
                if (tile.Version == version - 1 && !Touches(tile.Key, changed!)) tile.Version = version;
        Refresh();
    }

    // County of each hex (null before the game is connected); counties only change with the save, so
    // a change redraws everything.
    internal void SetCounties(ushort[]? newCounties)
    {
        if (counties is null ? newCounties is null : newCounties is not null && counties.AsSpan().SequenceEqual(newCounties)) return;
        counties = newCounties;
        version++;
        Refresh();
    }

    // Something every tile shows changed (the force colours): draw them all again.
    internal void Invalidate()
    {
        version++;
        Refresh();
    }

    private static (int Columns, int Rows) Counts(int level)
    {
        float ppu = PixelsPerUnit(level);
        return ((int)Math.Ceiling(MapTerrain.WorldWidth * ppu / TileSize), (int)Math.Ceiling(MapTerrain.WorldHeight * ppu / TileSize));
    }

    private static W.Rect Bounds(TileKey key)
    {
        double units = TileSize / PixelsPerUnit(key.Level);
        return new W.Rect(key.X * units, key.Y * units, units, units);
    }

    private static bool Touches(TileKey key, List<System.Drawing.PointF> points)
    {
        W.Rect area = Bounds(key);
        area.Inflate(2.5, 2.5); // borders and symbols reach a little past their hex
        foreach (System.Drawing.PointF p in points)
            if (area.Contains(p.X, p.Y)) return true;
        return false;
    }

    private Tile Get(TileKey key)
    {
        if (!tiles.TryGetValue(key, out Tile? tile)) tiles[key] = tile = new Tile(key);
        tile.LastUsed = frame;
        return tile;
    }

    // Recomputes what to show and what to draw for the current range.
    private void Refresh()
    {
        frame++;
        wanted.Clear();
        var display = new Dictionary<Tile, WMI.BitmapSource>();
        if (!enabled)
        {
            Show(display);
            Changed?.Invoke();
            return;
        }
        // The coarsest level covers the whole world and is always there to stand in.
        if (firstLevel == 0)
        {
            (int baseColumns, int baseRows) = Counts(0);
            for (int y = 0; y < baseRows; y++)
                for (int x = 0; x < baseColumns; x++)
                    wanted.Add(Get(new TileKey(0, x, y)));
        }
        if (range.Level >= 0)
        {
            var inRange = new List<Tile>();
            for (int y = range.Y0; y <= range.Y1; y++)
                for (int x = range.X0; x <= range.X1; x++)
                    inRange.Add(Get(new TileKey(range.Level, x, y)));
            // Nearest the centre of the view first.
            inRange.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
            foreach (Tile tile in inRange)
            {
                if (tile.Image is not null) display[tile] = tile.Image;
                else if (tile.Version < 0 && InView(tile.Key)) StandIn(tile, display);
                if (tile.Key.Level != 0 || firstLevel > 0) wanted.Add(tile);
            }
        }
        Show(display);
        Pump();
        Evict();
        Changed?.Invoke();
    }

    private double Distance(Tile tile)
    {
        W.Rect area = Bounds(tile.Key);
        return (new W.Point(area.X + area.Width / 2, area.Y + area.Height / 2) - focus).LengthSquared;
    }

    private bool InView(TileKey key) => key.X >= inView.X0 && key.X <= inView.X1 && key.Y >= inView.Y0 && key.Y <= inView.Y1;

    // A tile on screen that is not drawn yet. The finer tiles inside it stand in when they are all drawn (the
    // level just left, zooming out), else the part of a coarser tile over it. Only that part: the borders'
    // tiles are transparent, so a whole coarser tile under the finer ones would show its wide lines through
    // them all, and flash on and off as tiles at the edge of the view are drawn.
    private void StandIn(Tile tile, Dictionary<Tile, WMI.BitmapSource> display)
    {
        TileKey key = tile.Key;
        if (key.Level + 1 < LevelCount)
        {
            (int columns, int rows) = Counts(key.Level + 1);
            var children = new List<Tile>(4);
            bool all = true;
            for (int y = key.Y * 2; y < Math.Min(rows, key.Y * 2 + 2) && all; y++)
                for (int x = key.X * 2; x < Math.Min(columns, key.X * 2 + 2) && all; x++)
                {
                    if (!tiles.TryGetValue(new TileKey(key.Level + 1, x, y), out Tile? child) || child.Version < 0) all = false;
                    else if (child.Image is not null) children.Add(child);
                }
            if (all)
            {
                foreach (Tile child in children) display[child] = child.Image!;
                return;
            }
        }
        if (Ancestor(key) is not { } ancestor) return;
        ancestor.LastUsed = frame;  // kept while it stands in
        display[tile] = Part(tile, ancestor);
    }

    private Tile? Ancestor(TileKey key)
    {
        for (int level = key.Level - 1; level >= Math.Max(firstLevel, key.Level - standInLevels); level--)
        {
            int shift = key.Level - level;
            if (tiles.TryGetValue(new TileKey(level, key.X >> shift, key.Y >> shift), out Tile? tile) && tile.Image is not null) return tile;
        }
        return null;
    }

    // The part of a coarser tile's image over a tile.
    private static WMI.BitmapSource Part(Tile tile, Tile ancestor)
    {
        WMI.BitmapSource image = ancestor.Image!;
        if (tile.StandIn is { } known && ReferenceEquals(tile.StandInFrom, image)) return known;
        int shift = tile.Key.Level - ancestor.Key.Level, mask = (1 << shift) - 1;
        int width = image.PixelWidth >> shift, height = image.PixelHeight >> shift;
        var part = new WMI.CroppedBitmap(image, new W.Int32Rect((tile.Key.X & mask) * width, (tile.Key.Y & mask) * height, width, height));
        part.Freeze();
        tile.StandInFrom = image;
        return tile.StandIn = part;
    }

    // Each tile shown covers only its own area, with its image or the part standing in for it.
    private void Show(Dictionary<Tile, WMI.BitmapSource> display)
    {
        foreach (Tile tile in shown.Where(t => !display.ContainsKey(t)).ToList())
        {
            canvas.Children.Remove(tile.Element);
            tile.Element = null;
            shown.Remove(tile);
        }
        foreach ((Tile tile, WMI.BitmapSource image) in display)
        {
            if (shown.Add(tile))
            {
                W.Rect area = Bounds(tile.Key);
                tile.Element = new WC.Image { Stretch = WM.Stretch.Fill, Width = area.Width, Height = area.Height };
                WC.Canvas.SetLeft(tile.Element, area.X);
                WC.Canvas.SetTop(tile.Element, area.Y);
                canvas.Children.Add(tile.Element);
            }
            tile.Element!.Source = image;
        }
    }

    private void Pump()
    {
        while (running < Workers && wanted.FirstOrDefault(t => !t.Rendering && t.Version != version) is { } tile)
        {
            tile.Rendering = true;
            running++;
            int drawnFor = version;
            byte[]? territory = owners;
            ushort[]? tileCounties = counties;
            bool game = fromGame;
            TileKey key = tile.Key;
            Task.Run(() => render(key, territory, tileCounties, game)).ContinueWith(task =>
                canvas.Dispatcher.BeginInvoke(() => Done(tile, drawnFor, task)), TaskScheduler.Default);
        }
    }

    private void Done(Tile tile, int drawnFor, Task<WMI.BitmapSource?> task)
    {
        running--;
        tile.Rendering = false;
        // An image for older territory is still better than none; it is drawn again afterwards.
        if (task.IsCompletedSuccessfully && (drawnFor == version || tile.Image is null || tile.Version < drawnFor))
        {
            tile.Image = task.Result;
            tile.Version = drawnFor;
            tile.StandIn = tile.StandInFrom = null;
        }
        else if (!task.IsCompletedSuccessfully) tile.Version = version; // do not retry a failing tile forever
        Refresh();
    }

    private void Evict()
    {
        if (tiles.Count <= maxCached) return;
        var keep = new HashSet<Tile>(wanted);
        foreach (Tile tile in tiles.Values.Where(t => !keep.Contains(t) && !shown.Contains(t) && !t.Rendering)
                     .OrderBy(t => t.LastUsed).Take(tiles.Count - maxCached).ToList())
            tiles.Remove(tile.Key);
    }
}
