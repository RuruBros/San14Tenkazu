using W = System.Windows;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// The map's view: zoom and pan and their animation, the mouse, and what is under the pointer.
internal sealed partial class MapSurface
{
    // The whole view (zoom 1) shows the map's width and the field's whole height: the game's land and sea
    // north and south of the map too, and the northern banners whole.
    private double FitScale => Math.Min(ActualWidth / WorldWidth, ActualHeight / panBounds.Height);
    private double Scale => FitScale * zoom;
    private bool CanPan => ActualWidth / Scale < panBounds.Width || ActualHeight / Scale < panBounds.Height;
    private double Now => clock.Elapsed.TotalSeconds;

    private W.Point ToScreen(W.Point w) => new((w.X - center.X) * Scale + ActualWidth / 2, (w.Y - center.Y) * Scale + ActualHeight / 2);
    private W.Point ToWorld(W.Point s) => new((s.X - ActualWidth / 2) / Scale + center.X, (s.Y - ActualHeight / 2) / Scale + center.Y);

    // A hex's centre, in the world and on screen.
    private static W.Point TileWorld(int tile)
    {
        System.Drawing.PointF world = MapTerrain.TileCenter(tile);
        return new W.Point(world.X, world.Y);
    }

    private W.Point TilePoint(int tile) => ToScreen(TileWorld(tile));

    // To a whole device pixel, so that text drawn there stays sharp.
    private static double Snap(double v, double pixelsPerDip) => Math.Round(v * pixelsPerDip) / pixelsPerDip;

    // How far the view may go: the map, and past it as far as the game's own field texture reaches (some
    // 28 units north, east and south; west only until its mountains have faded into the frame's wood).
    private W.Rect panBounds = new(0, 0, WorldWidth, WorldHeight);

    private void ClampCenter()
    {
        double halfW = ActualWidth / 2 / Scale, halfH = ActualHeight / 2 / Scale;
        // Clamp each axis to the whole field. When it fits, use that same field's midpoint;
        // otherwise every remaining strip must be reachable at the current zoom and viewport size.
        center.X = halfW * 2 >= panBounds.Width ? (panBounds.Left + panBounds.Right) / 2
            : Math.Clamp(center.X, panBounds.Left + halfW, panBounds.Right - halfW);
        center.Y = halfH * 2 >= panBounds.Height ? (panBounds.Top + panBounds.Bottom) / 2
            : Math.Clamp(center.Y, panBounds.Top + halfH, Math.Max(panBounds.Top + halfH, panBounds.Bottom - halfH));
    }

    private void ApplyView()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        ClampCenter();
        double s = Scale;
        worldTransform.Matrix = new WM.Matrix(s, 0, 0, s, ActualWidth / 2 - center.X * s, ActualHeight / 2 - center.Y * s);
        ShowTerritoryLayers();
        cities.Follow(IsMoving);
        units.Follow(IsMoving);
        environmentLayer.Follow(IsMoving);
        // While the map moves, the hit test's places (drawn for the labels' view) lag; the pointer is looked at
        // again once it stops (Settle).
        if (IsMouseOver && !IsMoving) UpdateHover(WI.Mouse.GetPosition(this));
        var visible = new W.Rect(ToWorld(new W.Point(0, 0)), ToWorld(new W.Point(ActualWidth, ActualHeight)));
        double devicePixelsPerUnit = s * WM.VisualTreeHelper.GetDpi(this).DpiScaleX;
        tiles.Update(visible, devicePixelsPerUnit);
        ground.Update(visible, devicePixelsPerUnit);
    }

    internal bool IsMoving => looping || dragged;

    // ---- Animation -----------------------------------------------------------------------------------

    private sealed record Flight(double Start, double Duration, double FromLogZoom, double ToLogZoom, W.Point From, W.Point To);

    private void StartLoop()
    {
        if (looping) return;
        looping = true;
        lastFrame = -1;
        animationUpdatedAt = Now;
        // Advance the camera on the same composition tick that checks completed GPU frames.
        // Keep that subscription alive for the animation, including ticks without a new image.
        if (Gpu) HookGpuFrames();
        else WM.CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!looping) return;
        looping = false;
        WM.CompositionTarget.Rendering -= OnFrame;
        if (!dragged) Settle();
    }

    // The map stopped: what waited for it is done, the labels are drawn for where it is now and the pointer's
    // hover is looked at again once they are (Input runs after the render pass).
    private void Settle()
    {
        ApplyPending();
        cities.Redraw();
        units.Redraw();
        environmentLayer.Redraw();
        Dispatcher.BeginInvoke(() =>
        {
            if (IsMouseOver && !IsMoving) UpdateHover(WI.Mouse.GetPosition(this));
        }, WT.DispatcherPriority.Input);
    }

    // Deduplicate composition callbacks, then advance using the monotonic animation clock.
    private void OnFrame(object? sender, EventArgs e)
    {
        if (!looping) return;
        double frameTime = ((WM.RenderingEventArgs)e).RenderingTime.TotalSeconds;
        if (frameTime == lastFrame) return;    // called again for the same frame
        lastFrame = frameTime;
        AdvanceAnimation(Now);
    }

    private void AdvanceAnimation(double now)
    {
        double dt = Math.Max(0, now - animationUpdatedAt);
        animationUpdatedAt = now;
        bool active = false;
        if (flight is { } f)
        {
            double t = Math.Min(1, (now - f.Start) / f.Duration), ease = 1 - Math.Pow(1 - t, 3);
            zoom = Math.Exp(f.FromLogZoom + (f.ToLogZoom - f.FromLogZoom) * ease);
            center = f.From + (f.To - f.From) * ease;
            if (t >= 1) flight = null;
            else active = true;
        }
        else if (zooming)
        {
            double logZoom = Math.Log(zoom);
            logZoom += (targetLogZoom - logZoom) * (1 - Math.Exp(-dt / ZoomTime));
            if (Math.Abs(targetLogZoom - logZoom) < 0.002)
            {
                logZoom = targetLogZoom;
                zooming = false;
            }
            else active = true;
            zoom = Math.Exp(logZoom);
            // Keep the point under the cursor fixed.
            center = new W.Point(anchorWorld.X - (anchorScreen.X - ActualWidth / 2) / Scale, anchorWorld.Y - (anchorScreen.Y - ActualHeight / 2) / Scale);
        }
        if (velocity != default && !dragged)
        {
            double untilStop = Math.Max(0, CoastTime * Math.Log(velocity.Length / 15));
            double decay = Math.Exp(-Math.Min(dt, untilStop) / CoastTime);
            // Integrate the exponentially decaying speed exactly. A delayed frame must cover its
            // actual elapsed time, and changing frame cadence must not change the coast's distance.
            W.Point moved = center - velocity * (CoastTime * (1 - decay)) / Scale;
            center = moved;
            ClampCenter();
            // Stop coasting along an edge the view has hit.
            if (Math.Abs(center.X - moved.X) > 1e-9) velocity.X = 0;
            if (Math.Abs(center.Y - moved.Y) > 1e-9) velocity.Y = 0;
            velocity *= decay;
            if (dt >= untilStop || velocity.Length <= 15) velocity = default;
            else active = true;
        }
        ApplyView();
        if (gpuDirty) RequestFrame();
        if (!active) StopLoop();
    }

    private void FlyHome()
    {
        ViewChanging?.Invoke();
        zooming = false;
        velocity = default;
        flight = new Flight(Now, 0.35, Math.Log(zoom), 0, center, new W.Point(WorldWidth / 2, WorldHeight / 2));
        StartLoop();
    }

    // Centres the map on a unit, zooming in when the map shows too much to see it.
    internal void ShowUnit((int Slot, int Commander) unit)
    {
        SelectedUnit = unit;
        if (!glides.TryGetValue(unit, out Glide? glide)) return;
        if (!ShownUnits().Any(u => (u.Slot, u.Commander) == unit)) SetUnitMode(UnitMode.All);
        FlyTo(glide.To, 4);
    }

    // Centres the map on a city, zooming in when the map shows too much to see it.
    internal void ShowCity(int id) => ShowTile(MapTerrain.CityTiles[id]);

    // Centres the map on a hex (a base, an officer's whereabouts), zooming in when the map shows too much.
    internal void ShowTile(int tile) => FlyTo(TileWorld(tile), 3);

    // Flies to a point of the world, zooming in to at least the given zoom when the map shows too much, and
    // centres the point in the map's own column.
    private void FlyTo(W.Point world, double minimumZoom)
    {
        zooming = false;
        velocity = default;
        double logZoom = Math.Max(Math.Log(zoom), Math.Log(minimumZoom));
        ViewChanging?.Invoke();
        flight = new Flight(Now, 0.45, Math.Log(zoom), logZoom, center, world);
        StartLoop();
    }

    // ---- Input ---------------------------------------------------------------------------------------

    protected override void OnKeyDown(WI.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != WI.Key.Escape) return;
        e.Handled = true;
        EscapePressed?.Invoke();
    }

    protected override void OnMouseWheel(WI.MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        ZoomAt(e.Delta, e.GetPosition(this));
    }

    // Native wheel input and the WPF composition replay share the same zoom path.
    private void ZoomAt(int delta, W.Point pointer)
    {
        if (dragged) return;
        flight = null;
        velocity = default;
        double from = zooming ? targetLogZoom : Math.Log(zoom);
        double next = Math.Clamp(from + Math.Log(ZoomStep) * delta / 120.0, 0, Math.Log(MaxZoom));
        if (!zooming && Math.Abs(next - Math.Log(zoom)) < 1e-4) return;
        targetLogZoom = next;
        ViewChanging?.Invoke();
        anchorScreen = pointer;
        anchorWorld = ToWorld(anchorScreen);
        zooming = true;
        StartLoop();
    }

    protected override void OnMouseDown(WI.MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == WI.MouseButton.Right)
        {
            e.Handled = true;
            RightClickAt(e.GetPosition(this));
            return;
        }
        if (unitModes.IsMouseOver)
        {
            if (e.ChangedButton == WI.MouseButton.Left && ModeButtonAt(e.OriginalSource) is { } mode) SetUnitMode(mode);
            return;
        }
        W.Point p = e.GetPosition(this);
        if (e.ChangedButton == WI.MouseButton.Left && e.ClickCount == 2)
        {
            if (HitTest(p).IsEmpty) FlyHome();
            return;
        }
        BeginDrag(p);
        CaptureMouse();
    }

    private void BeginDrag(W.Point p)
    {
        // Freeze the view before taking the drag's origin: a zoom or flight must not move it afterward.
        zooming = false;
        flight = null;
        velocity = default;
        StopLoop();
        dragStart = p;
        dragCenter = center;
        dragged = false;
        dragSamples.Clear();
    }

    protected override void OnMouseMove(WI.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        W.Point p = e.GetPosition(this);
        if (IsMouseCaptured && MoveDrag(p)) return;
        UpdateHover(p);
        if (hoverCity != 0 || hoverUnit is not null) PlaceCard(p);
    }

    // The pointer coordinates are shared by native input and the off-screen navigation replay.
    private bool MoveDrag(W.Point p)
    {
        if (dragStart is { } start)
        {
            if (!dragged && Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) > 4 && CanPan)
            {
                dragged = true;
                ViewChanging?.Invoke();
                zooming = false;
                flight = null;
                SetHover(default);
                Cursor = WI.Cursors.SizeAll;
            }
            if (dragged)
            {
                double now = Now;
                dragSamples.Enqueue((now, p));
                while (dragSamples.Count > 2 && now - dragSamples.Peek().Time > 0.1) dragSamples.Dequeue();
                center = new W.Point(dragCenter.X - (p.X - start.X) / Scale, dragCenter.Y - (p.Y - start.Y) / Scale);
                ApplyView();
                // Use the clamped view and this pointer position for the next delta. Movement beyond an edge
                // must not accumulate a distance the pointer has to retrace before the map can move again.
                dragStart = p;
                dragCenter = center;
                return true;
            }
        }
        return false;
    }

    protected override void OnMouseUp(WI.MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == WI.MouseButton.Right)
        {
            e.Handled = true;
            return;
        }
        W.Point p = e.GetPosition(this);
        if (ReleaseDrag(p)) return;
        if (e.ChangedButton != WI.MouseButton.Left || unitModes.IsMouseOver) return;
        ClickAt(p);
    }

    // Resolve the image the user actually sees, including a GPU frame that is
    // still behind the current zoom/pan animation. Right clicks never begin a drag.
    private void RightClickAt(W.Point p)
    {
        if (unitModes.IsMouseOver || dragStart is not null ||
            p.X < 0 || p.Y < 0 || p.X >= ActualWidth || p.Y >= ActualHeight) return;
        WM.Matrix transform = Gpu && presentedView.Zoom > 0 ? presentedTransform : worldTransform.Matrix;
        if (!transform.HasInverse) return;
        transform.Invert();
        W.Point world = transform.Transform(p);
        if (MapTerrain.TileAt(world.X, world.Y) is { } tile) TileRightClicked?.Invoke(tile);
    }

    // Native mouse-up and the facility input checks dispatch the same map hit.
    private void ClickAt(W.Point p)
    {
        MapHit hit = HitTest(p);
        if (hit.Unit is { } unit)
        {
            SelectedUnit = unit;
            UnitClicked?.Invoke(unit.Slot, unit.Commander);
        }
        else if (hit.City != 0) CityClicked?.Invoke(hit.City);
        else if (hit.Gate != 0) GateClicked?.Invoke(hit.Gate);
        else if (hit.Facility is { } facility) FacilityClicked?.Invoke(facility);
        else EmptyClicked?.Invoke();
    }

    // Native mouse-up and the coordinate replay use this same release/velocity path.
    private bool ReleaseDrag(W.Point p)
    {
        bool wasDragged = dragged;
        EndDrag();
        if (wasDragged)
        {
            // Coast with the speed of the last 0.1 s of movement.
            double now = Now;
            var recent = dragSamples.Where(s => now - s.Time <= 0.1).ToList();
            if (recent.Count > 0 && now - recent[0].Time > 0.01)
            {
                velocity = (p - recent[0].Position) / (now - recent[0].Time);
                if (velocity.Length > 150) StartLoop();
                else velocity = default;
            }
            if (!looping) Settle();
            return true;
        }
        return false;
    }

    protected override void OnLostMouseCapture(WI.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        bool wasDragged = dragged;
        dragStart = null;
        dragged = false;
        if (wasDragged && !looping) Settle();
    }

    protected override void OnMouseLeave(WI.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!dragged) SetHover(default);
    }

    private void EndDrag()
    {
        dragStart = null;
        dragged = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // ---- Hover ---------------------------------------------------------------------------------------

    private void UpdateHover(W.Point p)
    {
        MapHit hit = unitModes.IsMouseOver ? default : HitTest(p);
        SetHover(hit);
        Cursor = hit.City != 0 || hit.Unit is not null || hit.Gate != 0 || hit.Facility is not null ? WI.Cursors.Hand : CanPan ? WI.Cursors.SizeAll : WI.Cursors.Arrow;
    }

    private void SetHover(MapHit hit)
    {
        if (hit.City == hoverCity && hit.Unit == hoverUnit && hit.Spot == hoverSpot && hit.Gate == hoverGate && hit.Facility == hoverFacility) return;
        hoverCity = hit.City;
        hoverUnit = hit.Unit;
        hoverSpot = hit.Spot;
        hoverGate = hit.Gate;
        hoverFacility = hit.Facility;
        if (hit.IsEmpty) card.Visibility = W.Visibility.Collapsed;
        else
        {
            cardText.Text = HoverText();
            card.Visibility = W.Visibility.Visible;
            PlaceCard(WI.Mouse.GetPosition(this));
        }
        cities.Redraw();
        units.Redraw();
    }

    // The hover card follows the cursor and stays inside the map. It is moved by a render transform:
    // a margin would be counted in DesiredSize (so the card flipped sides on every move) and re-layout the map.
    private void PlaceCard(W.Point p)
    {
        card.MaxWidth = Math.Min(TooltipPresentation.MaximumWidth, Math.Max(1, ActualWidth - 8));
        card.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
        W.Size size = card.DesiredSize;
        double x = p.X + 16, y = p.Y + 18;
        if (x + size.Width > ActualWidth - 4) x = p.X - size.Width - 10;
        if (y + size.Height > ActualHeight - 4) y = p.Y - size.Height - 10;
        cardOffset.X = Math.Max(4, x);
        cardOffset.Y = Math.Max(4, y);
    }

    private string Describe(int id)
    {
        CityRecord? city = snapshot?.Cities[id];
        if (city is null || snapshot is null) return $"{CityNames.ById[id]} (ID {id})";
        string scale = CityFields.ScaleName(city[CityFields.Scale]);
        string governor = snapshot.OfficerName(city.Governor);
        return L.Map.CityTip(city.Name, snapshot.ForceName(snapshot.ForceOfCity(id)), scale) + (governor.Length > 0 ? (" · " + L.Map.GovernorPart(governor)) : "") + "\n" +
            L.Map.CityStores(city[CityFields.Troops], city[CityFields.Gold], city[CityFields.Food]);
    }

    private string HoverText() => hoverSpot ?? (hoverUnit is { } unit ? DescribeUnit(unit) + StackedUnder(unit) : Describe(hoverCity));

    private string DescribeUnit((int Slot, int Commander) key)
    {
        if (snapshot is not { } data || data.Unit(key.Slot, key.Commander) is not { } unit) return "";
        string commander = data.CommanderName(unit.Commander);
        string place = data.PlaceName(unit), home = data.UnitBase(unit);
        // A force as the target is its number; whether to help or to fight it is not known.
        string going = data.Destination(unit) is { } destination && data.DestinationName(unit, destination) is { Length: > 0 } target
            ? ("\n" + L.Map.UnitHeading(UnitFields.OrderName(unit.Order), target, MapRoutes.Distance(unit.Tile, destination)))
            : unit.TargetKind == UnitFields.ForceTarget && unit.Target > 0 ? ("\n" + L.Map.UnitAgainst(UnitFields.OrderName(unit.Order), data.ForceName(unit.Target))) : "";
        string formation = UnitFields.FormationName(unit.Formation) is { Length: > 0 } f && unit.IsArmy ? (" · " + L.Map.FormationPart(f)) : "";
        return (L.Map.UnitTip(commander, UnitFields.KindName(unit.Kind), formation) + "\n") +
            L.Map.UnitForce(data.ForceName(data.ForceOfUnit(unit))) + (home.Length > 0 ? (" · " + L.Map.HomePart(home)) : "") + (place.Length > 0 ? (" · " + L.Map.PlacePart(place)) : "") + "\n" +
            L.Map.UnitStrength(unit[UnitFields.Troops], unit[UnitFields.Wounded], unit[UnitFields.Morale]) + going;
    }

    // Gates and facilities open their cards; other spots provide a description under the pointer.
    private readonly record struct MapHit(int City, (int Slot, int Commander)? Unit, string? Spot = null, int Gate = 0, FacilityTarget? Facility = null)
    {
        internal bool IsEmpty => City == 0 && Unit is null && Spot is null;
    }

    // The city or unit under the pointer; when both are, the one whose centre is nearer. Without either, the gate,
    // tribe, landmark or facility.
    private MapHit HitTest(W.Point location)
    {
        foreach(var hit in environmentHits)if(hit.Area.Contains(location))return new MapHit(hit.City,null,hit.Text);
        int city = 0;
        double cityDistance = double.MaxValue;
        for (int id = 1; id <= GameMemory.CityCount; id++)
        {
            double distance = (CityOnScreen(id) - location).Length;
            if (distance <= 14 && distance < cityDistance)
            {
                city = id;
                cityDistance = distance;
            }
        }
        // A city's model or banner picks it too (units standing over them still win).
        for (int id = 1; city == 0 && id <= GameMemory.CityCount; id++)
            if (ModelOnScreen(MapTerrain.CityTiles[id], ModelShowWidth) is { } model && model.Contains(location))
            {
                city = id;
                cityDistance = 14;
            }
        if (city == 0 && bannerRects.FirstOrDefault(b => b.Value.Contains(location)) is { Key: > 0 } banner)
        {
            city = banner.Key;
            cityDistance = 14;
        }
        (int Slot, int Commander)? unit = null;
        double unitDistance = double.MaxValue;
        foreach (var token in unitTokens)
        {
            double distance = (token.Point - location).Length;
            if (distance <= token.Radius + 3 && distance < unitDistance)
            {
                unit = (token.Unit.Slot, token.Unit.Commander);
                unitDistance = distance;
            }
        }
        if (unit is not null || city != 0) return unitDistance < cityDistance ? new MapHit(0, unit) : new MapHit(city, null);
        for (int i = spots.Count - 1; i >= 0; i--)
            if (spots[i].Area.Contains(location)) return new MapHit(0, null, spots[i].Text, spots[i].Gate, spots[i].Facility);
        return default;
    }
}
