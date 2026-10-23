using System.Globalization;
using System.Diagnostics;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// The units on the map: portrait tokens with their shields and troops, their glide from one read
// to the next, and the way of the unit under the pointer.
internal sealed partial class MapSurface
{
    // Which units the map shows: all, those of the selected city's force, or none.
    private enum UnitMode { All, SelectedForce, Hidden }
    private static readonly string[] UnitModeNames = [L.Map.UnitsAll, L.Map.UnitsOfForce, L.Map.UnitsHidden];
    private static readonly WM.Brush PortraitBack = Solid(58, 44, 32), TroopBack = Solid(176, 26, 21, 15), TroopInk = Solid(255, 246, 224);
    private static readonly WM.Pen StackEdge = Line(Solid(Theme.Gold), 1);

    private sealed record Glide(W.Point From, W.Point To, double Start, double Duration);
    private double lastUnitRead = double.NaN;
    private double lastGlideFrame = -1;

    private sealed class UnitLayer(MapSurface map) : LabelLayer(map)
    {
        protected override void Draw(MapSurface map, IMapCanvas dc) => map.DrawUnits(dc);
    }

    // The mode buttons in the top-left corner of the map.
    private WC.Border BuildUnitModes()
    {
        var row = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        row.Children.Add(unitCountText);
        for (int i = 0; i < modeButtons.Length; i++)
        {
            WC.TextBlock label = ChipText(UiFont, 12, W.FontWeights.Normal, default);
            label.Text = UnitModeNames[i];
            modeButtons[i] = new WC.Border
            {
                Tag = (UnitMode)i, CornerRadius = new W.CornerRadius(4), Padding = new W.Thickness(7, 0, 7, 0), Margin = new W.Thickness(1, 0, 0, 0),
                Height = ChipHeight - 6, VerticalAlignment = W.VerticalAlignment.Center,
                Background = WM.Brushes.Transparent, Cursor = WI.Cursors.Hand, Child = label
            };
            row.Children.Add(modeButtons[i]);
        }
        return new WC.Border
        {
            Background = HudBack, BorderBrush = HudEdge, BorderThickness = new W.Thickness(1),
            CornerRadius = new W.CornerRadius(5), Padding = new W.Thickness(8, 0, 2, 0), Height = ChipHeight,
            ToolTip = L.Map.UnitModesTip, Child = row
        };
    }

    private static UnitMode? ModeButtonAt(object source)
    {
        for (var node = source as W.DependencyObject; node is not null;
             node = node is WM.Visual ? WM.VisualTreeHelper.GetParent(node) : W.LogicalTreeHelper.GetParent(node))
            if (node is WC.Border { Tag: UnitMode mode }) return mode;
        return null;
    }

    private void SetUnitMode(UnitMode mode)
    {
        unitMode = mode;
        ShowUnitModes();
        units.Redraw();
    }

    private void ShowUnitModes()
    {
        foreach (WC.Border button in modeButtons)
        {
            bool on = (UnitMode)button.Tag == unitMode;
            button.Background = on ? HudOn : WM.Brushes.Transparent;
            ((WC.TextBlock)button.Child).Foreground = on ? HudOnInk : HudInk;
        }
        unitCountText.Text = unitMode == UnitMode.Hidden ? L.Map.Units : L.Map.UnitCount(ShownUnits().Count());
        unitModes.Visibility = snapshot is null ? W.Visibility.Collapsed : W.Visibility.Visible;
    }

    private IEnumerable<UnitRecord> ShownUnits()
    {
        if (snapshot is not { } data || unitMode == UnitMode.Hidden) return [];
        IEnumerable<UnitRecord> onMap = data.Units.Where(u => glides.ContainsKey((u.Slot, u.Commander)));
        if (highlightForce > 0) onMap = onMap.Where(u => data.ForceOfUnit(u) == highlightForce);
        if (unitMode == UnitMode.All) return onMap;
        int force = selectedCity != 0 ? data.ForceOfCity(selectedCity)
            : data.Gates.FirstOrDefault(g => g.Id == selectedGate) is { } gate ? data.ForceOfGate(gate) : 0;
        return force <= 0 ? [] : onMap.Where(u => data.ForceOfUnit(u) == force);
    }

    private (GameSnapshot? Data, object? Glides, UnitMode Mode, int City, int Gate, int Force, (int, int)? Selected) unitOrderKey;
    private UnitRecord[] unitOrder = [];
    private UnitRecord[] UnitOrder()
    {
        var key = (snapshot, (object?)glides, unitMode, selectedCity, selectedGate, highlightForce, selectedUnit);
        if (key != unitOrderKey)
        {
            unitOrderKey = key;
            unitOrder = ShownUnits().OrderBy(LayoutRank).ThenByDescending(u => u[UnitFields.Troops]).ToArray();
        }
        return unitOrder;
    }

    // Spread each observed move across the preceding read interval. Constant-speed interpolation
    // avoids braking at every tile and the idle gap of a short, fixed animation. Retarget from the
    // position already on screen; never extrapolate beyond the last observed tile. A long jump
    // (a unit entering a city and marching out elsewhere) is not animated.
    private void UpdateGlides(double now)
    {
        double duration = double.IsNaN(lastUnitRead) ? 0 : Math.Max(0, now - lastUnitRead);
        lastUnitRead = snapshot is null ? double.NaN : now;
        var next = new Dictionary<(int Slot, int Commander), Glide>();
        foreach (UnitRecord unit in snapshot?.Units ?? [])
        {
            if (unit.Tile >= GameMemory.TileCount) continue;
            W.Point to = TileWorld(unit.Tile);
            if (!glides.TryGetValue((unit.Slot, unit.Commander), out Glide? glide)) glide = new Glide(to, to, now, 0);
            else if (glide.To != to)
            {
                W.Point from = Position(glide, now);
                glide = (from - to).Length > 30 || duration <= 0
                    ? new Glide(to, to, now, 0) : new Glide(from, to, now, duration);
            }
            next[(unit.Slot, unit.Commander)] = glide;
        }
        glides = next;
        StartGlides(now);
    }

    // Reloading resumes the same trajectory and clock; it must not restart or extend the move.
    private void StartGlides(double now)
    {
        if (!gliding && AnyGliding(now))
        {
            gliding = true;
            lastGlideFrame = -1;
            WM.CompositionTarget.Rendering += OnGlide;
        }
    }

    private bool AnyGliding(double now) => glides.Values.Any(g => g.From != g.To && now - g.Start < g.Duration);

    // While the map moves the units' layer is shifted, not drawn again; they catch up when it stops.
    private void OnGlide(object? sender, EventArgs e)
    {
        double frameTime = ((WM.RenderingEventArgs)e).RenderingTime.TotalSeconds;
        if (frameTime == lastGlideFrame) return;
        lastGlideFrame = frameTime;
        if (!IsMoving) units.Redraw();
        if (AnyGliding(Now)) return;
        gliding = false;
        WM.CompositionTarget.Rendering -= OnGlide;
    }

    private static W.Point Position(Glide glide, double now)
    {
        if (glide.Duration <= 0) return glide.To;
        double t = Math.Clamp((now - glide.Start) / glide.Duration, 0, 1);
        return glide.From + (glide.To - glide.From) * t;
    }

    private W.Point UnitPoint(UnitRecord unit) =>
        glides.TryGetValue((unit.Slot, unit.Commander), out Glide? glide) ? ToScreen(Position(glide, Now)) : default;

    // As in the game, units show as portraits at every zoom: they grow with the map up to a little under a
    // hex (the user had them a little smaller) and never get smaller than this, so they read from afar.
    private const double UnitRadiusMax = 27;
    private double UnitRadius => Math.Clamp(Scale * 0.8, 10, UnitRadiusMax);

    internal Func<int, IconCache.Icon?>? FaceIcons { get; set; }
    private readonly Dictionary<int, WM.ImageBrush> faceBrushes = [];

    // Called when more portraits have been decoded.
    internal void RefreshFaces()
    {
        units.Redraw();
    }

    // A portrait as a brush that fills a unit's disc (cheaper to draw than an image clipped to a circle), or a
    // table's portrait cell.
    internal WM.ImageBrush? FaceBrush(int face)
    {
        if (faceBrushes.TryGetValue(face, out WM.ImageBrush? brush)) return brush;
        if (FaceIcons?.Invoke(face) is not { Pixels: { } pixels } icon) return null;
        var image = WMI.BitmapSource.Create(icon.Size, icon.Size, 96, 96, WM.PixelFormats.Bgra32, null, pixels, icon.Size * 4);
        image.Freeze();
        brush = new WM.ImageBrush(image);
        WM.RenderOptions.SetBitmapScalingMode(brush, WM.BitmapScalingMode.HighQuality);
        brush.Freeze();
        return faceBrushes[face] = brush;
    }

    // Tokens that would mostly cover one another are shown as one: the most important on top (the selected
    // unit, then the biggest armies) with "+N" for the others, which its hover card names. Zooming in
    // separates them. As drawn last, for the hit test.
    private readonly List<(UnitRecord Unit, W.Point Point, double Radius, List<UnitRecord> Others)> unitTokens = [];
    private readonly MapPoints tokenPoints = new();
    private readonly MapAreas shieldObstacles = new(), placedShields = new();
    internal bool ProfileUnits { get; set; }
    internal MapUnitProfile UnitProfile { get; private set; }

    private void DrawUnits(IMapCanvas dc)
    {
        bool profile = ProfileUnits || renderLog is not null;
        long began = profile ? Stopwatch.GetTimestamp() : 0;
        long bytes = profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
        if (snapshot is not { } data)
        {
            foreach (var token in unitTokens) { token.Others.Clear(); spareStacks.Add(token.Others); }
            unitTokens.Clear(); layoutSteady = false; UnitProfile = default;
            return;
        }
        double pixelsPerDip = WM.VisualTreeHelper.GetDpi(units).PixelsPerDip;
        CheckTextDpi(pixelsPerDip);
        labelBudget = looping ? 6 : 24;
        labelsPending = false;
        double radius = UnitRadius;
        bool reused = PrepareUnitLayout(radius);
        double layoutMs = profile ? Stopwatch.GetElapsedTime(began).TotalMilliseconds : 0;
        long tokensAt = profile ? Stopwatch.GetTimestamp() : 0;
        // The way the unit under the pointer is going, under the tokens (only while the pointer is on it).
        if (hoverUnit is { } k && data.Unit(k.Slot, k.Commander) is { } marching && data.Destination(marching) is { } destination)
            DrawRoute(dc, marching, destination);
        // Top to bottom so that nearer tokens overlap farther ones; the hovered and selected units last, on top.
        foreach (int tokenIndex in unitDrawOrder)
        {
            var token = unitTokens[tokenIndex];
            DrawPortraitUnit(dc, data, token.Unit, token.Point, token.Radius, pixelsPerDip);
            if (token.Others.Count > 0) DrawStackCount(dc, token.Point, token.Radius, token.Others.Count, pixelsPerDip);
        }
        double tokensMs = profile ? Stopwatch.GetElapsedTime(tokensAt).TotalMilliseconds : 0;
        long shieldsAt = profile ? Stopwatch.GetTimestamp() : 0;
        PlaceShields(dc, data, pixelsPerDip, reused);
        double shieldsMs = profile ? Stopwatch.GetElapsedTime(shieldsAt).TotalMilliseconds : 0;
        // From afar the ring alone tells the troops (the hover card has the figures): the selected unit's are
        // written out under it.
        if (zoom < TroopFiguresZoom)
            foreach (var token in unitTokens)
                if (UnitRank(token.Unit) == 0) DrawTroopsBelow(dc, token.Unit, token.Point, token.Radius, pixelsPerDip);
        if (labelsPending) Dispatcher.BeginInvoke(units.Redraw, WT.DispatcherPriority.Background);
        if (profile) UnitProfile = new(layoutMs, tokensMs, shieldsMs,
            GC.GetAllocatedBytesForCurrentThread() - bytes, unitTokens.Count, reused);
    }

    // The selected unit comes first, then the one under the pointer, then the others: the order they are drawn
    // on top in.
    private int UnitRank(UnitRecord unit) =>
        (unit.Slot, unit.Commander) == selectedUnit ? 0 : (unit.Slot, unit.Commander) == hoverUnit ? 1 : 2;

    // The order units take their places in (their stack, their shield's side): the selected one first. The one
    // under the pointer takes no other place than it had, so the map holds still under the hover card.
    private int LayoutRank(UnitRecord unit) => (unit.Slot, unit.Commander) == selectedUnit ? 0 : 1;

    // The force's shield with its mark (국호) beside each token, as on the city banners, so the unit's side is
    // plain. Over all the tokens, each where it covers least of the other tokens, shields and city banners
    // (TokenPlaces); the selected unit chooses first, then the bigger armies. In a crowd, a shield
    // with no free place is left out when a shield of the same force is already close by: one of them says
    // it for all.
    private void PlaceShields(IMapCanvas dc, GameSnapshot data, double pixelsPerDip, bool layoutReused)
    {
        if (layoutReused && ReuseShields(dc)) return;
        shieldDraws.Clear();
        shieldForces.Clear();
        MapAreas obstacles = shieldObstacles;
        obstacles.Clear();
        foreach (W.Rect area in bannerRects.Values) obstacles.Add(area);
        foreach (var token in unitTokens) obstacles.Add(TokenPlaces.Area(new(token.Point, token.Radius)));
        MapAreas placed = placedShields;
        placed.Clear();
        var shields = shieldForces;
        // Tokens were already created in this order.
        for (int tokenIndex = 0; tokenIndex < unitTokens.Count; tokenIndex++)
        {
            var token = unitTokens[tokenIndex];
            int force = data.ForceOfUnit(token.Unit);
            string mark = UnitForceMark(data, force);
            if (mark.Length == 0) continue;
            (W.Point p, double r) = (token.Point, token.Radius);
            System.Drawing.Color color = MapView.ForceColor(force);
            WM.FormattedText markText = Mark(mark, Math.Max(8, r * 0.55), InkOn(color), pixelsPerDip, LabelFace);
            double width = Math.Max(r * 0.8, markText.WidthIncludingTrailingWhitespace + 6), height = width * 1.25;
            (W.Rect at, double cost) = TokenPlaces.Shield(new(p, r), width, height, token.Unit.IsArmy, obstacles, placed, out int choice);
            if (cost > 0.3 && ShieldAlreadyNear(force, p, r * 3.2)) continue;
            placed.Add(at);
            shields.Add((force, p));
            shieldDraws.Add((tokenIndex, choice, width, height, color, force, markText));
            DrawShield(dc, at, color, OutlineOf(force));
            DrawCentred(dc, markText, new W.Point(at.X + width / 2, at.Y + height * 0.42));
        }
        SaveShieldLayout();
    }

    // A unit's troops are shown on its own token, where they cannot be taken for another unit's and do not
    // move about in a crowd: by the ring at every zoom (DrawTroopRing) and, from this zoom (four notches of
    // the wheel: the user wanted them that early), in figures in the portrait's foot (DrawTroopFigures).
    private const double TroopFiguresZoom = 2.4;

    // The ring is once round for this many troops.
    private const double TroopsPerLap = 10_000;
    private static readonly WM.Pen SecondLap = Line(TroopInk, UnitRing), ThirdLap = Line(Solid(255, 214, 72), UnitRing);

    // The force-coloured ring round a portrait, filled clockwise from the top as far as the unit has troops:
    // in the force's colour over a dim ring of it up to 10,000, then in white over the force's colour, then
    // in gold over the white. No number is taken for the most a unit can have (the editor writes up to
    // 60,000): each 10,000 is one more lap, and from 30,000 the ring is all gold.
    private void DrawTroopRing(IMapCanvas dc, System.Drawing.Color color, long troops, W.Point p, double r)
    {
        double laps = troops / TroopsPerLap;
        int full = (int)Math.Min(laps, 3);
        dc.DrawEllipse(null, TroopLap(color, full), p, r, r);
        if (full == 3 || laps <= full) return;
        // The part of the next lap, as a polyline close enough to the circle.
        long part = troops % (long)TroopsPerLap;
        if (!troopArcs.TryGet(part, out WM.Geometry arc)) arc = troopArcs.Add(part, MakeTroopArc(laps - full));
        dc.DrawGeometryAt(null, TroopLap(color, full + 1), arc, p, r);
    }

    private WM.Pen TroopLap(System.Drawing.Color color, int lap) => lap switch
    {
        0 => ForcePen(System.Drawing.Color.FromArgb((color.R + 2 * 26) / 3, (color.G + 2 * 21) / 3, (color.B + 2 * 15) / 3), UnitRing),
        1 => ForcePen(color, UnitRing), 2 => SecondLap, _ => ThirdLap
    };

    private static WM.Geometry MakeTroopArc(double fraction)
    {
        double turn = fraction * Math.PI * 2;
        int steps = Math.Max(1, (int)Math.Ceiling(turn / (Math.PI / 24)));
        var points = new W.Point[steps];
        for (int i = 1; i <= steps; i++) points[i - 1] = new W.Point(Math.Sin(turn * i / steps), -Math.Cos(turn * i / steps));
        var shape = new WM.StreamGeometry();
        using WM.StreamGeometryContext g = shape.Open();
        g.BeginFigure(new W.Point(0, -1), false, false);
        g.PolyLineTo(points, true, true);
        return shape;
    }

    // The troops in figures at the portrait's foot: white with a dark outline and nothing behind them (the
    // user's choice). On a large token they are inside the ring, as low as the disc is wide enough for them;
    // on a small one they stand on the ring's outer edge at the very foot, clear of the face, and reach over
    // the ring at the sides and below (the user would rather have them early, and as big as they were before
    // the tokens were made smaller, than inside it).
    private void DrawTroopFigures(IMapCanvas dc, UnitRecord unit, W.Point p, double r, double pixelsPerDip)
    {
        Label troops = OutlinedText(TroopText(unit[UnitFields.Troops]), Math.Clamp(r * 0.5 + 1, 11, 14), pixelsPerDip, NumberFace);
        double inside = r - UnitRing / 2 - LabelHalo.Thickness / 2, half = Math.Min(troops.Width / 2, inside * 0.9);
        double foot = p.Y + Math.Max(Math.Sqrt(inside * inside - half * half), Math.Min(r + UnitRing / 2, 13.5));    // where the figures stand
        DrawOutlined(dc, troops, p.X - troops.Width / 2, foot - troops.Bounds.Bottom * troops.Scale);
    }

    // The same figures under a token: the selected unit's from afar, clear of its ring.
    private void DrawTroopsBelow(IMapCanvas dc, UnitRecord unit, W.Point p, double r, double pixelsPerDip)
    {
        Label troops = OutlinedText(TroopText(unit[UnitFields.Troops]), 13, pixelsPerDip, NumberFace);
        DrawOutlined(dc, troops, p.X - troops.Width / 2, p.Y + r + 8 - troops.Bounds.Top * troops.Scale);
    }

    // A unit's way, as the game draws it: a smooth yellow line from the token through the hex it steps onto
    // next and on over the hexes to where it is going, gold rings there and the order's icon (move, attack,
    // go back) on a dark badge in them (above them it covered the target city's banner). The game keeps only
    // the next step; past it the way is estimated (MapRoutes), or straight when none is found. The mark grows and
    // shrinks with the tokens: as below when they are their largest, down to RouteSmallest of that from afar (the
    // lines a little less, so they still show), so it stays about a token's size at every zoom.
    private static readonly WM.Brush RouteShade = Solid(150, 40, 26, 6), RouteGold = Solid(255, 214, 72), RouteFaint = Solid(150, 255, 214, 72);
    private static readonly WM.Brush BadgeBack = Solid(225, 30, 22, 14);
    private const double RouteSmallest = 0.45;
    private (int Start, int Goal, List<int>? Path) routeCache = (-1, -1, null);
    private (int Start, int Goal, int Tile, W.Point From, WM.Geometry? Shape) routeShape;
    private static readonly System.Drawing.Color OrderInk = System.Drawing.Color.FromArgb(255, 226, 150);

    private void DrawRoute(IMapCanvas dc, UnitRecord unit, int destination)
    {
        W.Point from = glides.TryGetValue((unit.Slot, unit.Commander), out Glide? glide) ? Position(glide, Now) : TileWorld(unit.Tile);
        W.Point to = TilePoint(destination);
        int start = unit.NextTile is var next && next < GameMemory.TileCount ? next : unit.Tile;
        if (routeCache.Start != start || routeCache.Goal != destination) routeCache = (start, destination, MapRoutes.Find(start, destination, TerrainTypes));
        if (routeShape.Shape is null || routeShape.Start != start || routeShape.Goal != destination || routeShape.Tile != unit.Tile || routeShape.From != from)
        {
            var points = new List<W.Point> { from };
            if (routeCache.Path is { } path) points.AddRange(path.Select(TileWorld));
            else
            {
                if (start != unit.Tile && start != destination) points.Add(TileWorld(start));
                points.Add(TileWorld(destination));
            }
            // World coordinates keep the same path across zoom/pan; movement changes its first point.
            var shape = new WM.StreamGeometry();
            using (WM.StreamGeometryContext g = shape.Open())
            {
                g.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count - 1; i++)
                    g.QuadraticBezierTo(points[i], new W.Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2), true, true);
                g.LineTo(points[^1], true, true);
            }
            shape.Freeze();
            routeShape = (start, destination, unit.Tile, from, shape);
        }
        double size = Math.Clamp(UnitRadius / UnitRadiusMax, RouteSmallest, 1), stroke = Math.Sqrt(size);
        WM.Pen edge = RoundLine(RouteShade, 6.5 * stroke);
        W.Point origin = ToScreen(new W.Point(0, 0));
        dc.DrawGeometryAt(null, edge, routeShape.Shape, origin, Scale);
        dc.DrawGeometryAt(null, RoundLine(RouteGold, 3.2 * stroke), routeShape.Shape, origin, Scale);
        double ring = 15 * size;
        dc.DrawEllipse(null, edge, to, ring, ring);
        dc.DrawEllipse(null, Line(RouteGold, 2.4 * stroke), to, ring, ring);
        dc.DrawEllipse(null, Line(RouteFaint, 1.6 * stroke), to, 20 * size, 20 * size);
        // The order's icon on a dark badge inside the rings.
        string icon = unit.Order switch { UnitFields.Order.Attack => MapArt.AttackOrder, UnitFields.Order.Return => MapArt.ReturnOrder, _ => MapArt.MoveOrder };
        dc.DrawEllipse(BadgeBack, null, to, 11.5 * size, 11.5 * size);
        double side = 17 * size;
        MapArt.Draw(dc, icon, OrderInk, new W.Rect(to.X - side / 2, to.Y - side / 2, side, side));
    }

    // "+N" on a dark pill at the token's lower right: N more units under it.
    private void DrawStackCount(IMapCanvas dc, W.Point p, double r, int count, double pixelsPerDip)
    {
        WM.FormattedText text = Mark($"+{count}", Math.Max(9.5, r * 0.66) + 1, TroopInk, pixelsPerDip, NumberFace);
        double height = Math.Ceiling(text.Height), width = Math.Max(height, Math.Ceiling(text.Width) + 7);
        // Under the troops' figures when those are shown.
        var pill = new W.Rect(Snap(p.X + r * 0.45, pixelsPerDip), Snap(p.Y + (zoom >= TroopFiguresZoom ? r + 4 : r * 0.2), pixelsPerDip), width, height);
        dc.DrawRoundedRectangle(TroopBack, StackEdge, pill, height / 2, height / 2);
        dc.DrawText(text, new W.Point(Snap(pill.X + (width - text.Width) / 2, pixelsPerDip), Snap(pill.Y + (height - text.Height) / 2, pixelsPerDip)));
    }

    // The units under a token, for its hover card.
    private string StackedUnder((int Slot, int Commander) key)
    {
        int index = unitTokens.FindIndex(t => (t.Unit.Slot, t.Unit.Commander) == key);
        if (index < 0 || unitTokens[index].Others is not { Count: > 0 } others || snapshot is not { } data) return "";
        string names = string.Join(", ", others.Take(4).Select(u => data.CommanderName(u.Commander)));
        return "\n" + (others.Count > 4 ? L.Map.StackedMore(others.Count, names) : L.Map.Stacked(others.Count, names));
    }

    private void DrawSelection(IMapCanvas dc, UnitRecord unit, W.Point p, double r)
    {
        var key = (unit.Slot, unit.Commander);
        bool selected = key == selectedUnit;
        if (!selected && key != hoverUnit) return;
        double ring = r + (selected ? 4 : 2.5);
        dc.DrawEllipse(null, selected ? SelectedRing : HoverRing, p, ring, ring);
    }

    // The force-coloured ring round a unit's portrait: thin at every zoom (the user found 20% of the radius heavy).
    private const double UnitRing = 2;

    // The glow outside it: white rings fading outwards.
    private static readonly WM.Pen[] UnitGlow = [Line(Solid(150, 255, 255, 255), 1.4), Line(Solid(75, 255, 255, 255), 1.4), Line(Solid(28, 255, 255, 255), 1.4)];

    // As in the game: the commander's portrait in a ring of the force's colour, the force's shield beside it.
    // The ring tells the troops, and close up they stand in figures in the portrait's foot. Transports carry a
    // small badge. Until the portrait is decoded, the token shows the commander's surname instead.
    private void DrawPortraitUnit(IMapCanvas dc, GameSnapshot data, UnitRecord unit, W.Point p, double r, double pixelsPerDip)
    {
        System.Drawing.Color color = MapView.ForceColor(data.ForceOfUnit(unit));
        (WM.Brush solid, WM.Brush gradient) = ForceBrushes(color);
        double ringWidth = UnitRing;
        DrawSelection(dc, unit, p, r + ringWidth / 2);

        data.OfficerById.TryGetValue(unit.Commander, out OfficerRecord? officer);
        WM.ImageBrush? face = officer is null ? null : FaceBrush(officer.Face);
        string surname = face is not null || officer is null ? "" : UnitSurname(data, officer);
        if (dc is IGpuMapCanvas gpuCanvas)
        {
            gpuCanvas.DrawSprite(UnitPortraitSprite(face ?? gradient, face is not null, r, pixelsPerDip), p);
            // Text must snap at its final screen position, rather than the command list's local origin.
            if (face is null && surname.Length > 0)
                DrawCentred(dc, Mark(surname, r * 1.05, WM.Brushes.White, pixelsPerDip), p);
        }
        else DrawPortraitBase(dc, face ?? gradient, face is not null, surname, p, r, pixelsPerDip);
        DrawTroopRing(dc, color, unit[UnitFields.Troops], p, r);
        if (zoom >= TroopFiguresZoom) DrawTroopFigures(dc, unit, p, r, pixelsPerDip);

        // The force's shield is placed with the others' (PlaceShields).

        // An army shows its 진형 at the upper right, as the game marks units (white on a dark disc).
        if (unit.IsArmy)
        {
            if (MapArt.FormationIcon(unit.Formation) is { } formationArt && MapArt.Has(formationArt))
            {
                var badge = new W.Point(p.X + r * 0.85, p.Y - r * 0.6);
                dc.DrawEllipse(IconBack, null, badge, r * 0.42, r * 0.42);
                MapArt.Draw(dc, formationArt, IconInk, new W.Rect(badge.X - r * 0.34, badge.Y - r * 0.34, r * 0.68, r * 0.68));
            }
        }
        // Transports carry the game's supplies icon (or a small "수" badge without the game's art).
        else
        {
            var badge = new W.Point(p.X + r * 0.85, p.Y - r * 0.6);
            if (MapArt.Has(MapArt.Supplies))
            {
                dc.DrawEllipse(IconBack, null, badge, r * 0.5, r * 0.5);
                MapArt.Draw(dc, MapArt.Supplies, IconInk, new W.Rect(badge.X - r * 0.45, badge.Y - r * 0.45, r * 0.9, r * 0.9));
            }
            else
            {
                dc.DrawEllipse(solid, Outline, badge, r * 0.42, r * 0.42);
                DrawCentred(dc, Mark(L.Map.TransportMark, r * 0.5, WM.Brushes.White, pixelsPerDip), badge);
            }
        }
    }
}
