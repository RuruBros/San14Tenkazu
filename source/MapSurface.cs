using System.Diagnostics;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WS = System.Windows.Shapes;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// The map area, drawn by WPF: the terrain, the territory tint and the detail tiles (borders, grid,
// map symbols) sit in world units under one transform, so the GPU moves and scales them. Wheel zoom
// animates toward its target, dragging follows the mouse and coasts a little after release, and
// cities are redrawn every frame at a constant screen size. Units on the map are drawn the same way
// above the cities and glide to their new tile when a read finds them moved.
internal sealed partial class MapSurface : WC.Grid
{
    private const double MaxZoom = 16;
    private const double ZoomStep = 1.25;          // per wheel notch
    private const double ZoomTime = 0.07;          // seconds for ~63% of the way to the target
    private const double CoastTime = 0.25;         // inertia decay after a drag
    private static readonly double WorldWidth = MapTerrain.WorldWidth, WorldHeight = MapTerrain.WorldHeight;

    internal event Action<int>? CityClicked;
    internal event Action<int>? GateClicked;
    internal event Action<FacilityTarget>? FacilityClicked;
    internal event Action<int, int>? UnitClicked;   // unit slot, commander
    internal event Action? ViewChanging;            // the user starts to zoom or pan, or the map flies
    internal event Action? EscapePressed;           // Esc while the map (or a card on it) has the keyboard
    internal event Action? EmptyClicked;            // a click on the map that hit no city or unit
    internal event Action<int>? TileRightClicked;   // the hex under a right click, for the game's camera

    private readonly WC.Canvas world = new() { HorizontalAlignment = W.HorizontalAlignment.Left, VerticalAlignment = W.VerticalAlignment.Top };
    private readonly WC.Image terrain = Layer(WM.BitmapScalingMode.HighQuality);
    private readonly WC.Canvas beyond = new() { IsHitTestVisible = false };   // land and sea past the map's edges
    private readonly WS.Rectangle westFade = new() { IsHitTestVisible = false };   // the western land into the frame's wood (ExtendTerrain)
    private MapView.Scenery? field;                                             // the game's whole field, the map within it
    private readonly ModelLayer models = new() { IsHitTestVisible = false };  // the game's models of cities, gates, villages, ports
    private readonly Dictionary<int, W.Rect> modelAreas = [];                  // world area of the model on each tile that has one
    private readonly WC.Image fill = Layer(WM.BitmapScalingMode.Linear);
    private readonly WC.Image strategic = Layer(WM.BitmapScalingMode.Linear);   // territory in force colours, zoomed out
    private readonly WC.Image veil = Layer(WM.BitmapScalingMode.Linear);        // dark over all but the force shown alone
    private readonly WC.Canvas veilAround = new() { IsHitTestVisible = false, Visibility = W.Visibility.Collapsed };   // and past the map's edges
    private readonly MapTiles tiles = new((key, owners, counties, game) => MapView.RenderTile(key.Level, key.X, key.Y, owners, counties, game), standInLevels: 1);
    // Close up, the game's ground textures replace the magnified base map (from 16 device pixels per unit).
    private readonly MapTiles ground = new((key, _, _, _) => MapView.RenderGround(key), firstLevel: 2, maxCached: 60);
    private readonly WM.MatrixTransform worldTransform = new();
    // The GPU renderer (null where Direct3D is not available: then WPF draws the layers above, as before).
    private GpuMap? gpu;
    private readonly WC.Image gpuImage = new() { Stretch = WM.Stretch.Fill, IsHitTestVisible = false, HorizontalAlignment = W.HorizontalAlignment.Left, VerticalAlignment = W.VerticalAlignment.Top };
    private bool gpuDirty, gpuHooked;
    private readonly MapDrawList gpuFrame = new();
    private readonly object terrainImageSlot = new(), fieldImageSlot = new(), fillImageSlot = new(), strategicImageSlot = new(), veilImageSlot = new();
    internal double LastPrepareMs { get; private set; }
    private double lastGpuFrame = -1;
    private double gpuDpi = 96;
    private readonly MapRenderLog? renderLog = MapRenderLog.Open();
    private (double World, double Cities, double Units) framePhases;
    private (double Zoom, bool Moving, W.Point Center, double At) submittedView, presentedView;
    private WM.Matrix submittedTransform, presentedTransform;
    private readonly CityLayer cities;
    private readonly UnitLayer units;
    private readonly WC.Border unitModes;
    private readonly WC.Border[] modeButtons = new WC.Border[3];
    private readonly WC.TextBlock unitCountText = ChipText(UiFont, 12, W.FontWeights.Normal, new W.Thickness(2, 0, 6, 0));
    // The game's date, first in the row of chips at the top left.
    private readonly WC.TextBlock dateText = ChipText(NumberFont, 13, W.FontWeights.SemiBold, default);
    private readonly WC.Border dateChip;
    private readonly WC.TextBlock cardText = new() { FontFamily = UiFont, FontSize = 12, Foreground = HudInk, TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.Border card;
    private readonly WM.TranslateTransform cardOffset = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private GameSnapshot? snapshot;
    private int selectedCity, hoverCity, selectedGate, hoverGate;
    private FacilityTarget? selectedFacility, hoverFacility;
    private string? hoverSpot;
    // Gates, tribes, landmarks and facilities as last drawn, with what the hover card says about them (and the gate's
    // ID for a gate, which a click opens; 0 for the others).
    private readonly List<(W.Rect Area, string Text, int Gate, FacilityTarget? Facility)> spots = [];
    private (int Slot, int Commander)? selectedUnit, hoverUnit;
    private UnitMode unitMode = UnitMode.All;
    private Dictionary<(int Slot, int Commander), Glide> glides = [];
    private bool gliding;
    private byte[]? territory;
    private ushort[]? counties;
    private float basePixelsPerUnit = 1;
    private bool fromGame = true;

    // zoom 1 fits the whole map; Scale is DIPs per world unit.
    private double zoom = 1;
    private W.Point center = new(WorldWidth / 2, WorldHeight / 2);

    private bool looping;
    private double lastFrame;
    private double animationUpdatedAt;
    private bool zooming;
    private double targetLogZoom;
    private W.Point anchorScreen, anchorWorld;
    private Flight? flight;
    private W.Vector velocity;                     // DIPs per second while coasting
    private W.Point? dragStart;
    private W.Point dragCenter;
    private bool dragged;
    private readonly Queue<(double Time, W.Point Position)> dragSamples = new();
    private (WMI.BitmapSource Fill, WMI.BitmapSource? Strategic, float PixelsPerUnit)? pendingFill;

    internal MapSurface()
    {
        Background = Solid(MapView.SeaColor);
        ClipToBounds = true;
        Focusable = true;
        world.RenderTransform = worldTransform;
        world.Children.Add(beyond);
        world.Children.Add(terrain);
        world.Children.Add(ground.Element);
        world.Children.Add(westFade);   // over the close-up ground, which covers the whole field
        world.Children.Add(fill);
        world.Children.Add(strategic);
        world.Children.Add(tiles.Element);
        world.Children.Add(models);
        world.Children.Add(veil);
        world.Children.Add(veilAround);
        world.Children.Add(environmentFill);
        // The veil's colour (MapView.RenderVeil) around the map: four bands reaching far past its edges.
        double far = WorldWidth * 3;
        foreach (W.Rect band in new W.Rect[] { new(-far, -far, far, WorldHeight + far * 2), new(WorldWidth, -far, far, WorldHeight + far * 2),
                     new(0, -far, WorldWidth, far), new(0, WorldHeight, WorldWidth, far) })
        {
            var rectangle = new WS.Rectangle { Width = band.Width, Height = band.Height, Fill = VeilBrush };
            WC.Canvas.SetLeft(rectangle, band.X);
            WC.Canvas.SetTop(rectangle, band.Y);
            veilAround.Children.Add(rectangle);
        }
        Children.Add(world);
        cities = new CityLayer(this);
        Children.Add(cities);
        units = new UnitLayer(this);
        Children.Add(units);
        environmentLayer=new EnvironmentLayer(this){IsHitTestVisible=false};Children.Add(environmentLayer);
        // The game's label sprites are scaled down; smooth them, and redraw once they have loaded.
        WM.RenderOptions.SetBitmapScalingMode(cities, WM.BitmapScalingMode.HighQuality);
        WM.RenderOptions.SetBitmapScalingMode(units, WM.BitmapScalingMode.HighQuality);
        VisualEventSubscription.Attach(this, static changed => MapArt.Changed += changed,
            static changed => MapArt.Changed -= changed, () =>
        {
            InvalidateCityCommands();
            cities.Redraw();
            units.Redraw();
        });
        unitModes = BuildUnitModes();
        dateChip = Chip(dateText, 10, 10);
        dateChip.Margin = new W.Thickness(0, 0, 6, 0);
        dateChip.IsHitTestVisible = false;
        dateChip.Visibility = W.Visibility.Collapsed;
        // Top left: the date and the unit modes.
        Children.Add(new WC.StackPanel
        {
            Orientation = WC.Orientation.Horizontal, Margin = new W.Thickness(8, 8, 0, 0),
            HorizontalAlignment = W.HorizontalAlignment.Left, VerticalAlignment = W.VerticalAlignment.Top,
            Children = { dateChip, unitModes }
        });
        ShowUnitModes();
        card = new WC.Border
        {
            Background = HudBack, BorderBrush = HudEdge, BorderThickness = new W.Thickness(1),
            CornerRadius = new W.CornerRadius(4), Padding = new W.Thickness(8, 5, 8, 5),
            HorizontalAlignment = W.HorizontalAlignment.Left, VerticalAlignment = W.VerticalAlignment.Top, RenderTransform = cardOffset,
            IsHitTestVisible = false, Visibility = W.Visibility.Collapsed, Child = cardText
        };
        Children.Add(card);
        SizeChanged += (_, _) => ApplyView();
        Loaded += (_, _) =>
        {
            if (gpu is null) StartGpu();
            StartGlides(Now);
            units.Redraw();
        };
        Unloaded += (_, _) =>
        {
            looping = false;
            WM.CompositionTarget.Rendering -= OnFrame;
            WM.CompositionTarget.Rendering -= OnGpuFrame;
            WM.CompositionTarget.Rendering -= OnGlide;
            gliding = false;
            gpuHooked = false;
            gpuImage.Source = null;
            gpu?.Dispose(); gpu = null;
            gpuFrame.Reset(); cityCommands.Reset(); InvalidateCityCommands();
            tiles.Enabled = ground.Enabled = false;
            renderLog?.Dispose();
        };
        StartGpu();
        tiles.Changed += RequestFrame;
        ground.Changed += RequestFrame;
        tiles.SetContent(null, fromGame);
        ApplyView();
    }

    // ---- GPU ----------------------------------------------------------------------------------

    // The map drawn by GpuMap into one picture. WPF's own layers stay in the tree, hidden, as what the frame
    // is drawn from (and as the fallback where there is no GPU renderer).
    private void StartGpu()
    {
        presentedView = default;
        presentedTransform = WM.Matrix.Identity;
        // SAN14_NO_GPU=1 keeps WPF drawing the map (for a machine where the GPU renderer misbehaves).
        gpu = Environment.GetEnvironmentVariable("SAN14_NO_GPU") == "1" ? null : GpuMap.TryCreate();
        bool on = gpu is not null;
        world.Visibility = cities.Visibility = units.Visibility = on ? W.Visibility.Collapsed : W.Visibility.Visible;
        environmentLayer.Visibility=on?W.Visibility.Collapsed:W.Visibility.Visible;
        UseTiles();
        if (gpu is null) return;
        GpuMap renderer = gpu;
        renderer.FrameReady += () => Dispatcher.BeginInvoke(WT.DispatcherPriority.Render, new Action(() =>
        {
            if (ReferenceEquals(gpu, renderer)) PublishGpuFrame();
        }));
        if (gpu.Shaders is { } shaders) shaders.Ready += () => Dispatcher.BeginInvoke(RequestFrame);
        gpuImage.Source = gpu.Image;
        WM.RenderOptions.SetBitmapScalingMode(gpuImage, WM.BitmapScalingMode.NearestNeighbor);
        if (!Children.Contains(gpuImage)) Children.Insert(Children.IndexOf(world) + 1, gpuImage);
        gpu.Image.IsFrontBufferAvailableChanged += (_, _) =>
        {
            if (gpu is { } renderer) renderLog?.Record("front-buffer", zoom, IsMoving,
                renderer.Image.PixelWidth, renderer.Image.PixelHeight, renderer, framePhases);
            if (gpu?.Image.IsFrontBufferAvailable == true) gpu.Bind();
            RequestFrame();
        };
        RequestFrame();
    }

    private bool Gpu => gpu is not null;
    internal bool UsesGpu => Gpu;
    internal bool UsesGroundShaders => gpu?.Shaders is not null;

    // The tiles draw the close-up ground and the borders only where the GPU's shaders do not (no GPU renderer,
    // or shaders it could not make); the borders' tiles also draw the painted map's roads and symbols.
    private void UseTiles()
    {
        bool shaders = gpu?.Shaders is not null;
        ground.Enabled = !shaders;
        tiles.Enabled = !shaders || !fromGame;
    }

    // Something on the map changed: the next frame draws it.
    private void RequestFrame()
    {
        if (gpu is null) return;
        gpuDirty = true;
        HookGpuFrames();
    }

    private void HookGpuFrames()
    {
        if (gpuHooked) return;
        gpuHooked = true;
        WM.CompositionTarget.Rendering += OnGpuFrame;
    }

    private void OnGpuFrame(object? sender, EventArgs e)
    {
        // Completion is checked even when WPF repeats its predicted presentation time. New work
        // is coalesced for that time, so Unlock cannot repeatedly cancel WPF's pending copy.
        PublishGpuFrame();
        double frameTime = ((WM.RenderingEventArgs)e).RenderingTime.TotalSeconds;
        if (frameTime != lastGpuFrame)
        {
            lastGpuFrame = frameTime;
            if (looping) OnFrame(sender, e);
            if (gpuDirty || gpu?.Pending == true) RenderGpu();
        }
        if (!looping && !gpuDirty && gpu?.Pending != true)
        {
            gpuHooked = false;
            WM.CompositionTarget.Rendering -= OnGpuFrame;
        }
    }

    private void RenderGpu()
    {
        if (gpu is null) return;
        if (ActualWidth < 1 || ActualHeight < 1) return;
        W.DpiScale dpi = WM.VisualTreeHelper.GetDpi(this);
        gpuDpi = 96 * dpi.DpiScaleX;
        int width = (int)Math.Round(ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        gpuImage.Width = width / dpi.DpiScaleX;
        gpuImage.Height = height / dpi.DpiScaleY;
        gpu.Resize(width, height, (float)(96 * dpi.DpiScaleX));
        PublishGpuFrame();
        if (gpu.Pending && !gpu.Lost)
        {
            return;
        }
        if (gpu.Lost)
        {
            renderLog?.Record("device-lost", zoom, IsMoving, width, height, gpu, framePhases, UnitProfile, LastPrepareMs, gpuFrame.Count);
            gpuImage.Source = null; gpu.Dispose(); StartGpu(); return;
        }
        if (!gpuDirty) return;
        gpuDirty = false;
        double viewAt = Now;
        long preparedAt = Stopwatch.GetTimestamp();
        gpuFrame.Reset();
        DrawGpuFrame(gpuFrame);
        LastPrepareMs = Stopwatch.GetElapsedTime(preparedAt).TotalMilliseconds;
        if (gpu.RenderAsync(gpuFrame))
        {
            submittedView = (zoom, IsMoving, center, viewAt);
            submittedTransform = worldTransform.Matrix;
            renderLog?.Record("submit", zoom, IsMoving, width, height, gpu, framePhases, UnitProfile, LastPrepareMs, gpuFrame.Count,
                center, (Now - viewAt) * 1000);
        }
        else gpuDirty = true;
        if (!gpu.Lost) return;
        // The device was lost: start over with a new one (everything is uploaded again).
        renderLog?.Record("device-lost", zoom, IsMoving, width, height, gpu, framePhases, UnitProfile, LastPrepareMs, gpuFrame.Count);
        gpuImage.Source = null;
        gpu.Dispose();
        StartGpu();
    }

    private void PublishGpuFrame()
    {
        if (gpu?.Present() != true) return;
        presentedView = submittedView;
        presentedTransform = submittedTransform;
        if (gpu.Canvas.Deferred) gpuDirty = true;
        renderLog?.Record("present", submittedView.Zoom, submittedView.Moving, gpu.Image.PixelWidth, gpu.Image.PixelHeight,
            gpu, framePhases, UnitProfile, LastPrepareMs, gpuFrame.Count, submittedView.Center, (Now - submittedView.At) * 1000);
    }

    private void DrawGpuFrame(IGpuMapCanvas canvas)
    {
        long start = renderLog is null ? 0 : Stopwatch.GetTimestamp();
        canvas.Clear(Background is WM.SolidColorBrush back ? back.Color : WM.Colors.Black);
        canvas.SetLayerImage(terrainImageSlot, terrain.Source as WMI.BitmapSource);
        canvas.SetLayerImage(fieldImageSlot, field?.Image);
        canvas.SetLayerImage(fillImageSlot, fill.Source as WMI.BitmapSource);
        canvas.SetLayerImage(strategicImageSlot, strategic.Source as WMI.BitmapSource);
        canvas.SetLayerImage(veilImageSlot, veil.Source as WMI.BitmapSource);
        canvas.SetLayerImage(environmentImageSlot,environmentFill.Source as WMI.BitmapSource);
        canvas.PushMatrix(worldTransform.Matrix);
        foreach (W.UIElement layer in world.Children)
        {
            // The shaders draw in the places of the layers of tiles they stand in for.
            if (layer == ground.Element) DrawGroundShaded(canvas);
            else if (layer == tiles.Element) DrawBordersShaded(canvas);
            DrawWorld(canvas, layer, 1, WM.BitmapScalingMode.Linear, false);
        }
        canvas.Pop();
        if (canvas is MapDrawList recorded) recorded.ImageMode = WM.BitmapScalingMode.HighQuality;
        else if (canvas is D2DCanvas direct) direct.ImageMode = WM.BitmapScalingMode.HighQuality;
        double worldMs = start == 0 ? 0 : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long citiesAt = start == 0 ? 0 : Stopwatch.GetTimestamp();
        DrawCitiesCached(canvas);
        double citiesMs = citiesAt == 0 ? 0 : Stopwatch.GetElapsedTime(citiesAt).TotalMilliseconds;
        long unitsAt = start == 0 ? 0 : Stopwatch.GetTimestamp();
        DrawUnits(canvas);
        DrawEnvironment(canvas);
        framePhases = (worldMs, citiesMs, unitsAt == 0 ? 0 : Stopwatch.GetElapsedTime(unitsAt).TotalMilliseconds);
    }

    // The view as the shaders take it: device pixels per world unit, and where the world's origin is.
    private MapShaders.Camera ShaderCamera()
    {
        WM.Matrix m = worldTransform.Matrix;
        double k = gpuDpi / 96;
        return new MapShaders.Camera((float)(m.M11 * k), (float)(m.OffsetX * k), (float)(m.OffsetY * k));
    }

    // The close-up ground and its trees over the base map, as zoomed in as the view is: over the whole field when
    // it is known, so that the land past the map's edges has it too. Asking whether it is ready also starts making
    // it, so it is made soon after the base map shows.
    private void DrawGroundShaded(IGpuMapCanvas canvas)
    {
        if (gpu?.Shaders is not { } shaders || !fromGame || terrain.Source is not WMI.BitmapSource image) return;
        MapShaders.Camera camera = ShaderCamera();
        bool ready = field is { } whole
            ? shaders.GroundReady(whole.Image, whole.PixelsPerUnit, whole.Origin.X, whole.Origin.Y)
            : shaders.GroundReady(image, basePixelsPerUnit);
        if (!ready || MapShaders.GroundOpacity(camera.Scale) <= 0) return;
        if (canvas is MapDrawList recorded) recorded.DrawGround(shaders, camera);
        else gpu.DrawGround(shaders, camera);
    }

    // The borders, county boundaries, grid and marks, when the tiles are not drawing them.
    private void DrawBordersShaded(IGpuMapCanvas canvas)
    {
        if (gpu?.Shaders is not { } shaders || tiles.Enabled) return;
        MapShaders.Camera camera = ShaderCamera();
        if (canvas is MapDrawList recorded) recorded.DrawBorders(shaders, camera, territory, counties);
        else gpu.DrawBorders(shaders, camera, territory, counties);
    }

    // The world's WPF layers (pictures, coloured bands, tiles, the models' drawing) as WPF would draw them.
    private static void DrawWorld(IGpuMapCanvas canvas, W.UIElement element, double opacity, WM.BitmapScalingMode mode, bool snap)
    {
        if (element.Visibility != W.Visibility.Visible) return;
        opacity *= element.Opacity;
        if (opacity <= 0.002) return;
        WM.BitmapScalingMode own = WM.RenderOptions.GetBitmapScalingMode(element);
        if (own != WM.BitmapScalingMode.Unspecified) mode = own;
        snap |= WM.RenderOptions.GetEdgeMode(element) == WM.EdgeMode.Aliased;
        static W.Rect Placed(W.FrameworkElement e)
        {
            double left = WC.Canvas.GetLeft(e), top = WC.Canvas.GetTop(e);
            return new W.Rect(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top, double.IsNaN(e.Width) ? 0 : e.Width, double.IsNaN(e.Height) ? 0 : e.Height);
        }
        switch (element)
        {
            case WC.Canvas layer:
                foreach (W.UIElement child in layer.Children.Cast<W.UIElement>().OrderBy(WC.Panel.GetZIndex))
                    DrawWorld(canvas, child, opacity, mode, snap);
                break;
            case WC.Image { Source: WMI.BitmapSource source } image:
                canvas.DrawBitmap(source, Placed(image), opacity, mode, snap);
                break;
            case WS.Rectangle { Fill: { } fill } band:
                canvas.FillRectangle(fill, null, Placed(band), opacity);
                break;
            case ModelLayer models:
                models.DrawGpu(canvas, opacity);
                break;
        }
    }

    internal int SelectedCity
    {
        get => selectedCity;
        set
        {
            selectedCity = value;
            if (value != 0) { selectedGate = 0; selectedFacility = null; }
            cities.Redraw();
            units.Redraw();
            ShowUnitModes();
        }
    }

    // The gate whose card is open, marked as the selected city is (one or the other).
    internal int SelectedGate
    {
        get => selectedGate;
        set
        {
            selectedGate = value;
            if (value != 0) { selectedCity = 0; selectedFacility = null; }
            cities.Redraw();
            units.Redraw();
            ShowUnitModes();
        }
    }

    internal FacilityTarget? SelectedFacility
    {
        get => selectedFacility;
        set
        {
            selectedFacility = value;
            if (value is not null) selectedCity = selectedGate = 0;
            cities.Redraw();
            units.Redraw();
            ShowUnitModes();
        }
    }

    internal (int Slot, int Commander)? SelectedUnit
    {
        get => selectedUnit;
        set
        {
            selectedUnit = value;
            units.Redraw();
        }
    }

    internal void SetSnapshot(GameSnapshot? data)
    {
        if (data?.Date is { } date)
            dateText.Text = L.Map.Date(date.Year, date.Month, date.Day) + data.Pace switch { GamePace.Paused => (" · " + L.Map.Paused), GamePace.Running => (" · " + L.Map.Running), _ => "" };
        dateChip.Visibility = data?.Date is null ? W.Visibility.Collapsed : W.Visibility.Visible;
        snapshot = data;
        SetEnvironment(environment);
        cityOrder = [];
        UpdateGlides(Now);
        if (hoverUnit is { } unit && !glides.ContainsKey(unit)) SetHover(default);
        else if (hoverCity != 0 || hoverUnit is not null)
        {
            cardText.Text = HoverText();
            PlaceCard(WI.Mouse.GetPosition(this));
        }
        cities.Redraw();
        units.Redraw();
        ShowUnitModes();
    }

    // A new base map: the close-up ground is drawn over it, so it is drawn again too.
    internal void SetTerrain(WMI.BitmapSource image, float pixelsPerUnit, MapView.Scenery? around = null)
    {
        Place(terrain, image, 0, 0, image.PixelWidth / pixelsPerUnit, image.PixelHeight / pixelsPerUnit);
        basePixelsPerUnit = pixelsPerUnit;
        ExtendTerrain(image, pixelsPerUnit, around);
        ground.Invalidate();
        ApplyView();    // the whole view's scale follows the field's height (panBounds)
        RequestFrame();
    }

    // The map's rectangle is narrower than a wide window, so the view shows past its edges. There the view
    // shows what the game itself has there: its field texture reaches past the playable map (mountains to
    // the west, sea to the east), drawn under the map. The western land fades into the dark wood of the
    // window's frame before the texture ends; past the east the sea goes on in the colour of the map's
    // eastern edge.
    private void ExtendTerrain(WMI.BitmapSource image, float pixelsPerUnit, MapView.Scenery? around)
    {
        beyond.Children.Clear();
        double width = image.PixelWidth / pixelsPerUnit, height = image.PixelHeight / pixelsPerUnit, far = width * 2;
        WM.Brush sea = Solid(EdgeColour(image));
        Background = sea;
        AddBeyond(new WS.Rectangle { Width = far, Height = height + far * 2, Fill = sea }, width, -far);

        // Where the known land ends to the west (x), and its vertical extent.
        double west = 0, top = 0, landHeight = height;
        field = around;
        if (around is not null)
        {
            // As sharp as the map: smoothed as the terrain is when far off.
            var scenery = new WC.Image { Source = around.Image, Stretch = WM.Stretch.Fill, Width = around.Image.PixelWidth / around.PixelsPerUnit, Height = around.Image.PixelHeight / around.PixelsPerUnit };
            WM.RenderOptions.SetBitmapScalingMode(scenery, WM.BitmapScalingMode.HighQuality);
            WC.Canvas.SetLeft(scenery, around.Origin.X);
            WC.Canvas.SetTop(scenery, around.Origin.Y);
            beyond.Children.Add(scenery);
            (west, top, landHeight) = (around.Origin.X, around.Origin.Y, scenery.Height);
            // The view may go over this scenery (to 45% of the western mountains, before they turn dark).
            panBounds = W.Rect.Union(new W.Rect(0, 0, WorldWidth, WorldHeight),
                new W.Rect(west * 0.45, top, scenery.Width + west * 0.55, scenery.Height));
        }

        // The land fades into the dark wood of the window's frame before it ends, so there is no edge.
        WM.Color wood = WM.Color.FromArgb(255, Theme.DarkWood.R, Theme.DarkWood.G, Theme.DarkWood.B);
        WM.Color Wood(byte alpha) => WM.Color.FromArgb(alpha, wood.R, wood.G, wood.B);
        var fade = new WM.LinearGradientBrush { StartPoint = new W.Point(0, 0.5), EndPoint = new W.Point(1, 0.5) };
        double Offset(double x) => Math.Clamp((x + far) / far, 0, 1);
        fade.GradientStops.Add(new WM.GradientStop(Wood(255), 0));
        fade.GradientStops.Add(new WM.GradientStop(Wood(255), Offset(west + 4)));
        fade.GradientStops.Add(new WM.GradientStop(Wood(175), Offset(west * 0.7)));
        fade.GradientStops.Add(new WM.GradientStop(Wood(60), Offset(west * 0.3)));
        fade.GradientStops.Add(new WM.GradientStop(Wood(0), 1));
        fade.Freeze();
        (westFade.Width, westFade.Height, westFade.Fill) = (far, landHeight + far * 2, fade);
        WC.Canvas.SetLeft(westFade, -far);
        WC.Canvas.SetTop(westFade, top - far);
    }

    private void AddBeyond(WS.Rectangle rectangle, double x, double y)
    {
        WC.Canvas.SetLeft(rectangle, x);
        WC.Canvas.SetTop(rectangle, y);
        beyond.Children.Add(rectangle);
    }

    // Average colour of the map's rightmost pixel columns (open sea).
    private static System.Drawing.Color EdgeColour(WMI.BitmapSource image)
    {
        const int columns = 4;
        var pixels = new int[columns * image.PixelHeight];
        new WMI.FormatConvertedBitmap(image, WM.PixelFormats.Bgra32, null, 0)
            .CopyPixels(new W.Int32Rect(image.PixelWidth - columns, 0, columns, image.PixelHeight), pixels, columns * 4, 0);
        long r = 0, g = 0, b = 0;
        foreach (int p in pixels)
        {
            r += p >> 16 & 255;
            g += p >> 8 & 255;
            b += p & 255;
        }
        return System.Drawing.Color.FromArgb((int)(r / pixels.Length), (int)(g / pixels.Length), (int)(b / pixels.Length));
    }

    // The force shown alone (0 for all): only its units, and the rest of the map under the veil.
    internal int HighlightForce
    {
        get => highlightForce;
        set
        {
            highlightForce = value;
            ShowUnitModes();
            units.Redraw();
        }
    }
    private int highlightForce;

    internal void SetVeil(WMI.BitmapSource? image, float pixelsPerUnit)
    {
        if (image is null) veil.Source = null;
        else Place(veil, image, 0, 0, image.PixelWidth / pixelsPerUnit, image.PixelHeight / pixelsPerUnit);
        veilAround.Visibility = image is null ? W.Visibility.Collapsed : W.Visibility.Visible;
        RequestFrame();
    }

    private static readonly WM.Brush VeilBrush = Solid(MapView.VeilColor);

    // The territory layers: a light wash for close views and the strategic colouring for distant ones.
    // New layers are held while the map moves: uploading a large texture mid-animation drops frames.
    internal void SetFill(WMI.BitmapSource? image, WMI.BitmapSource? strategicImage, float pixelsPerUnit)
    {
        pendingFill = null;
        if (image is null)
        {
            fill.Source = null;
            strategic.Source = null;
        }
        else if (IsMoving) pendingFill = (image, strategicImage, pixelsPerUnit);
        else
        {
            Place(fill, image, 0, 0, image.PixelWidth / pixelsPerUnit, image.PixelHeight / pixelsPerUnit);
            if (strategicImage is null) strategic.Source = null;
            else Place(strategic, strategicImage, 0, 0, strategicImage.PixelWidth / pixelsPerUnit, strategicImage.PixelHeight / pixelsPerUnit);
        }
        RequestFrame();
    }

    // Zoomed out the map is coloured by force, as the game's strategic view: strongly in the whole view, fading
    // with every wheel notch in, until the ground is back (by zoom 4.5) with only the light wash. The colour
    // itself is shaded by the ground's light (MapView.RenderStrategic), so the relief shows through it.
    private const double StrategicStrength = 0.9, StrategicGone = 4.5;

    private void ShowTerritoryLayers()
    {
        double t = Math.Clamp(Math.Log(zoom) / Math.Log(StrategicGone), 0, 1), distant = 1 - t * t * (3 - 2 * t);
        strategic.Opacity = StrategicStrength * distant;
        // Close up the wash fades to a trace (from zoom 5 to 11): magnified, its hexes of land taken one by one
        // in a war blotch the ground, and the borders show the territory well enough there.
        fill.Opacity = (1 - distant) * Math.Clamp(1 - (zoom - 5) / 6 * 0.7, 0.3, 1);
    }

    // The game's models: each sprite on its tile, northern ones first so that southern ones stand in front.
    // GPU sprites share cached shadows; the WPF fallback keeps one drawing for the whole layer.
    internal void SetModels(IReadOnlyList<MapModels.Placement> placements, IReadOnlyDictionary<(string Model, int Yaw, double Scale), GameModels.Sprite?> sprites)
    {
        modelAreas.Clear();
        var images = new List<(GameModels.Sprite Sprite, W.Point Origin)>();
        foreach (MapModels.Placement placement in placements.OrderBy(p => MapTerrain.TileCenter(p.Tile).Y))
        {
            if (!sprites.TryGetValue((placement.Model, placement.Yaw, placement.Scale), out GameModels.Sprite? sprite) || sprite is null) continue;
            System.Drawing.PointF centre = MapTerrain.TileCenter(placement.Tile);
            modelAreas[placement.Tile] = new W.Rect(centre.X + sprite.World.X, centre.Y + sprite.World.Y, sprite.World.Width, sprite.World.Height);
            images.Add((sprite, new W.Point(centre.X, centre.Y)));
        }
        models.Set(images);
        cities.Redraw();
    }

    // A model's sprite with a soft shadow to the south-east baked in (the light comes from the north-west).
    // Prepared on the loader for the WPF fallback. The GPU path combines it on its own device.
    internal static readonly W.Vector ShadowOffset = new(0.18, 0.24);   // world units
    internal const double ShadowStrength = 0.32;
    internal static readonly System.Drawing.Color ShadowColor = System.Drawing.Color.FromArgb(20, 14, 8);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<GameModels.Sprite, GameModels.Sprite> shadowedSprites = new();

    internal static GameModels.Sprite WithShadow(GameModels.Sprite sprite) => shadowedSprites.GetOrAdd(sprite, Shadowed);

    private static GameModels.Sprite Shadowed(GameModels.Sprite sprite)
    {
        WMI.BitmapSource image = sprite.Image;
        int w = image.PixelWidth, h = image.PixelHeight;
        double pixelsX = w / sprite.World.Width, pixelsY = h / sprite.World.Height;
        int dx = (int)Math.Round(ShadowOffset.X * pixelsX), dy = (int)Math.Round(ShadowOffset.Y * pixelsY);
        int width = w + dx, height = h + dy;
        var source = new uint[w * h];
        image.CopyPixels(source, w * 4, 0);
        var output = new uint[width * height];
        double R = ShadowColor.R, G = ShadowColor.G, B = ShadowColor.B;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint model = x < w && y < h ? source[y * w + x] : 0;
                int sx = x - dx, sy = y - dy;
                double shade = sx >= 0 && sy >= 0 ? ShadowStrength * (source[sy * w + sx] >> 24) / 255 : 0;
                // Premultiplied: the model over its shadow.
                double behind = shade * (1 - (model >> 24) / 255.0);
                uint a = (model >> 24) + (uint)Math.Round(255 * behind), r = ((model >> 16) & 0xFF) + (uint)Math.Round(R * behind),
                    g = ((model >> 8) & 0xFF) + (uint)Math.Round(G * behind), b = (model & 0xFF) + (uint)Math.Round(B * behind);
                output[y * width + x] = Math.Min(a, 255) << 24 | Math.Min(r, 255) << 16 | Math.Min(g, 255) << 8 | Math.Min(b, 255);
            }
        var result = WMI.BitmapSource.Create(width, height, 96, 96, WM.PixelFormats.Pbgra32, null, output, width * 4);
        result.Freeze();
        return new GameModels.Sprite(result, new W.Rect(sprite.World.X, sprite.World.Y, width / pixelsX, height / pixelsY));
    }

    // The model on a tile as it is on screen, when it is big enough to show itself (null otherwise).
    private W.Rect? ModelOnScreen(int tile, double minimumWidth)
    {
        if (!modelAreas.TryGetValue(tile, out W.Rect area)) return null;
        var screen = new W.Rect(ToScreen(area.TopLeft), ToScreen(area.BottomRight));
        return Math.Max(screen.Width, screen.Height) >= minimumWidth ? screen : null;
    }

    // County of each hex, for the county boundaries (null before the game is connected).
    internal void SetCounties(ushort[]? newCounties)
    {
        counties = newCounties;
        tiles.SetCounties(newCounties);
        RequestFrame();
    }

    // Force colours changed: the borders in the tiles are drawn again (cities and units redraw anyway; the
    // shaders take the colours every frame).
    internal void RedrawColors()
    {
        InvalidateCityCommands();
        layoutSteady = false;
        tiles.Invalidate();
        RequestFrame();
    }

    // Tile ownership (null before the game is connected); only tiles near changed hexes are redrawn.
    internal void SetTerritory(byte[]? owners)
    {
        territory = owners;
        tiles.SetContent(territory, fromGame);
        RequestFrame();
    }

    // The trees for the close-up ground arrived: its tiles are drawn again (the shaders take them on the next frame).
    internal void RedrawGround()
    {
        ground.Invalidate();
        RequestFrame();
    }

    // Without the game's textures, the tiles also draw roads, mountains and forests.
    internal void SetFromGame(bool game)
    {
        fromGame = game;
        UseTiles();
        tiles.SetContent(territory, fromGame);
        RequestFrame();
    }

    private void ApplyPending()
    {
        if (pendingFill is { } f) SetFill(f.Fill, f.Strategic, f.PixelsPerUnit);
    }

    private static void Place(WC.Image image, WMI.BitmapSource source, double x, double y, double width, double height)
    {
        image.Source = source;
        WC.Canvas.SetLeft(image, x);
        WC.Canvas.SetTop(image, y);
        image.Width = width;
        image.Height = height;
    }

    private static WC.Image Layer(WM.BitmapScalingMode mode)
    {
        var image = new WC.Image { Stretch = WM.Stretch.Fill, IsHitTestVisible = false };
        WM.RenderOptions.SetBitmapScalingMode(image, mode);
        return image;
    }

    // Where a city or a unit is on screen now (DIPs in this surface), for cards opened beside them.
    internal W.Point CityOnScreen(int id) => TilePoint(MapTerrain.CityTiles[id]);

    internal W.Point? UnitOnScreen((int Slot, int Commander) unit) =>
        glides.TryGetValue(unit, out Glide? glide) ? ToScreen(Position(glide, Now)) : null;

    private sealed class ModelLayer : W.FrameworkElement
    {
        private WM.Drawing? drawing;
        private IReadOnlyList<(GameModels.Sprite Sprite, W.Point Origin)> images = [];

        internal void Set(IReadOnlyList<(GameModels.Sprite Sprite, W.Point Origin)> value)
        {
            images = value;
            drawing = null;
            InvalidateVisual();
        }

        internal void DrawGpu(IGpuMapCanvas canvas, double opacity)
        {
            foreach (var image in images) canvas.DrawShadowed(image.Sprite, image.Origin, opacity);
        }

        protected override void OnRender(WM.DrawingContext dc)
        {
            if (drawing is null)
            {
                var made = new WM.DrawingGroup();
                WM.RenderOptions.SetBitmapScalingMode(made, WM.BitmapScalingMode.HighQuality);
                using (WM.DrawingContext fallback = made.Open())
                    foreach (var image in images)
                    {
                        GameModels.Sprite shadow = WithShadow(image.Sprite);
                        W.Rect area = shadow.World; area.Offset(image.Origin.X, image.Origin.Y);
                        fallback.DrawImage(shadow.Image, area);
                    }
                made.Freeze();
                drawing = made;
            }
            if (drawing is not null) dc.DrawDrawing(drawing);
        }
    }

    // While the map only moves (a drag, the coast after it, a flight at one zoom), the labels drawn for the
    // last view are shifted with it instead of being placed and drawn again every frame, which was most of a
    // frame's work at low zoom (the map's own pictures are kept the same way). They are drawn LabelPad past
    // the screen's edges so that what comes into view is there; they are drawn again when the shift nears
    // that, when the zoom changes and when the map stops.
    private double LabelPad => Gpu ? 0 : Math.Round(Math.Max(ActualWidth, ActualHeight) * 0.5);

    private abstract class LabelLayer : W.FrameworkElement
    {
        private readonly MapSurface map;
        protected MapSurface Map => map;
        private readonly WM.TranslateTransform shift = new();
        private W.Point drawnCenter;
        private double drawnScale = double.NaN;

        protected LabelLayer(MapSurface map)
        {
            this.map = map;
            RenderTransform = shift;
        }

        protected abstract void Draw(MapSurface map, IMapCanvas dc);

        protected override void OnRender(WM.DrawingContext dc)
        {
            drawnCenter = map.center;
            drawnScale = map.Scale;
            shift.X = shift.Y = 0;
            Draw(map, new WpfCanvas(dc));
        }

        // Draw again: on the GPU the next frame draws every layer.
        internal virtual void Redraw()
        {
            if (map.Gpu) map.RequestFrame();
            else InvalidateVisual();
        }

        // The view changed: shift the drawing to it (on whole device pixels, so the text stays sharp), or draw again.
        internal void Follow(bool moving)
        {
            if (map.Gpu)
            {
                map.RequestFrame();
                return;
            }
            double scale = map.Scale, reach = map.LabelPad * 0.8;
            W.Vector offset = (drawnCenter - map.center) * scale;
            if (moving && scale == drawnScale && Math.Abs(offset.X) < reach && Math.Abs(offset.Y) < reach)
            {
                double pixelsPerDip = WM.VisualTreeHelper.GetDpi(this).PixelsPerDip;
                shift.X = Snap(offset.X, pixelsPerDip);
                shift.Y = Snap(offset.Y, pixelsPerDip);
            }
            else InvalidateVisual();
        }
    }

    // The chips at the top left share one height, with their text centred in it.
    private const double ChipHeight = 26;

    private static WC.Border Chip(W.UIElement child, double left, double right) => new()
    {
        Background = HudBack, BorderBrush = HudEdge, BorderThickness = new W.Thickness(1), CornerRadius = new W.CornerRadius(5),
        Height = ChipHeight, Padding = new W.Thickness(left, 0, right, 0), Child = child
    };

    // Text centred in a chip: its line box is sized to the font (BlockLineHeight) so the glyphs, not the
    // font's tall line gap, decide where the middle is.
    private static WC.TextBlock ChipText(WM.FontFamily family, double size, W.FontWeight weight, W.Thickness margin)
    {
        var text = new WC.TextBlock
        {
            FontFamily = family, FontSize = size, FontWeight = weight, Foreground = HudInk, Margin = margin,
            VerticalAlignment = W.VerticalAlignment.Center, LineStackingStrategy = W.LineStackingStrategy.BlockLineHeight, LineHeight = Math.Round(size * 1.25)
        };
        WM.TextOptions.SetTextFormattingMode(text, WM.TextFormattingMode.Display);
        return text;
    }
}
