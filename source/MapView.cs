using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Buffers;
using System.Runtime.InteropServices;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WS = System.Windows.Shapes;

namespace San14Tenkazu;

// The game's hex map: terrain, territory by force, and clickable cities.
// World units: hex circumradius = 1; flat-topped hexes, even columns sit half a hex lower.
// The layers are drawn on worker threads with GDI+ and shown by MapSurface, which moves and
// scales them on the GPU so that zooming and panning stay smooth. This element holds the surface
// and the legend below it.
internal sealed partial class MapView : WC.DockPanel
{
    internal static readonly Color SeaColor = MapPainter.DeepSea;
    internal static readonly Color Highlight = Theme.Highlight;
    private static readonly Color Unowned = Color.FromArgb(206, 212, 218);
    // Dark over all but the force shown alone.
    internal static readonly Color VeilColor = Color.FromArgb(150, 12, 9, 6);
    private static readonly Color[] ForcePalette =
    [
        Color.FromArgb(224, 49, 49), Color.FromArgb(25, 113, 194), Color.FromArgb(47, 158, 68), Color.FromArgb(240, 140, 0),
        Color.FromArgb(112, 72, 232), Color.FromArgb(12, 133, 153), Color.FromArgb(194, 37, 92), Color.FromArgb(92, 148, 13),
        Color.FromArgb(232, 89, 12), Color.FromArgb(59, 91, 219), Color.FromArgb(156, 54, 181), Color.FromArgb(8, 127, 91),
        Color.FromArgb(214, 51, 108), Color.FromArgb(16, 152, 173), Color.FromArgb(174, 62, 201), Color.FromArgb(116, 184, 22),
        Color.FromArgb(245, 159, 0), Color.FromArgb(66, 99, 235), Color.FromArgb(230, 73, 128), Color.FromArgb(18, 184, 134)
    ];

    // Terrain type -> colour. Types were identified from their distribution on the map.
    private static readonly Color[] TerrainColors = BuildTerrainColors();

    private static Color[] BuildTerrainColors()
    {
        // Muted parchment tones so that the territory colours stay readable on top.
        var colors = Enumerable.Repeat(Color.FromArgb(222, 212, 176), 32).ToArray();
        colors[1] = Color.FromArgb(224, 216, 172);  // farmland plains
        colors[2] = Color.FromArgb(168, 136, 94);   // roads
        colors[3] = Color.FromArgb(234, 218, 172);  // dry plains / steppe
        colors[4] = Color.FromArgb(212, 214, 166);  // grassland
        colors[5] = Color.FromArgb(190, 198, 150);  // woods
        colors[6] = Color.FromArgb(166, 186, 136);  // jungle
        colors[7] = Color.FromArgb(180, 190, 152);  // marsh forest
        colors[8] = Color.FromArgb(216, 202, 162);  // low hills
        colors[9] = Color.FromArgb(204, 186, 146);  // hills
        colors[10] = Color.FromArgb(190, 170, 130); // highland
        colors[11] = Color.FromArgb(158, 126, 90);  // plank roads / passes
        colors[12] = Color.FromArgb(216, 206, 172);
        colors[13] = Color.FromArgb(124, 170, 204); // streams
        colors[14] = Color.FromArgb(136, 180, 212); // great rivers, shallows
        colors[15] = Color.FromArgb(218, 202, 162); // county seats
        colors[16] = Color.FromArgb(238, 228, 204); // city ground
        colors[17] = Color.FromArgb(182, 162, 130); // gates
        colors[18] = Color.FromArgb(182, 202, 208); // ports
        colors[19] = Color.FromArgb(148, 126, 100); // mountains
        colors[20] = Color.FromArgb(128, 110, 90);  // cliffs / walls
        colors[21] = Color.FromArgb(234, 230, 218);
        colors[22] = SeaColor;                      // map edge
        colors[23] = SeaColor;                      // sea
        colors[24] = Color.FromArgb(160, 192, 198); // wetland
        return colors;
    }

    // Territory tint resolution: soft edges are hidden under the sharp borders of the detail layer.
    private const float FillPixelsPerUnit = 5f;

    private readonly MapSurface surface = new();
    private readonly WC.WrapPanel legend = new() { Margin = new W.Thickness(7, 5, 0, 5) };
    private readonly List<(int Force, WC.Border Chip, WC.TextBlock Label)> chips = [];
    private GameSnapshot? snapshot;
    private byte[]? owners;
    private bool fromGame = true;
    private int fillVersion;
    private string legendKey = "";

    internal event Action<int>? CitySelected;
    internal event Action<int>? GateSelected;
    internal event Action<FacilityTarget>? FacilitySelected;
    internal event Action<int, int>? UnitSelected;  // unit slot, commander
    internal event Action? ViewChanging;            // the user starts to zoom or pan, or the map flies
    internal event Action? EscapePressed;           // Esc while the map has the keyboard
    internal event Action<int>? ForceClicked;       // a force's chip under the map
    internal event Action? EmptyClicked;            // a click on the map that hit no city or unit
    internal event Action<int>? TileRightClicked;

    internal MapView()
    {
        // The legend is a dark bar under the map with a gold rule, like the toolbar above it.
        var bar = new WC.Border { Background = CardSkin.DarkWood, BorderBrush = CardSkin.Gold, BorderThickness = new W.Thickness(0, 2, 0, 0), Child = legend };
        BuildPane(bar);
        surface.CityClicked += id => CitySelected?.Invoke(id);
        surface.GateClicked += id => GateSelected?.Invoke(id);
        surface.FacilityClicked += target => FacilitySelected?.Invoke(target);
        surface.UnitClicked += (slot, commander) => UnitSelected?.Invoke(slot, commander);
        surface.ViewChanging += () => ViewChanging?.Invoke();
        surface.EscapePressed += () => EscapePressed?.Invoke();
        surface.EmptyClicked += () => EmptyClicked?.Invoke();
        surface.TileRightClicked += tile => TileRightClicked?.Invoke(tile);
        BuildLegend();
        ShowBaseMap();
    }

    internal int SelectedCity
    {
        get => surface.SelectedCity;
        set => surface.SelectedCity = value;
    }

    internal int SelectedGate
    {
        get => surface.SelectedGate;
        set => surface.SelectedGate = value;
    }

    internal FacilityTarget? SelectedFacility
    {
        get => surface.SelectedFacility;
        set => surface.SelectedFacility = value;
    }

    internal (int Slot, int Commander)? SelectedUnit
    {
        get => surface.SelectedUnit;
        set => surface.SelectedUnit = value;
    }

    internal void ShowUnit((int Slot, int Commander) unit) => surface.ShowUnit(unit);

    internal void ShowCity(int id) => surface.ShowCity(id);

    internal void ShowTile(int tile) => surface.ShowTile(tile);

    // The force shown alone (0 for all), while its card is open.
    internal int HighlightForce
    {
        get => highlightForce;
        set => SetHighlight(value);
    }

    // Portraits for the unit tokens: the officer list's icons, by face number.
    internal Func<int, IconCache.Icon?>? FaceIcons
    {
        get => surface.FaceIcons;
        set => surface.FaceIcons = value;
    }

    internal void RefreshFaces()
    {
        surface.RefreshFaces();
        Lists.RefreshFaces();
        CityEditor.RefreshFaces();
        GateEditor.RefreshFaces();
        ForceEditor.RefreshFaces();
    }

    // A decoded portrait as a brush (null until it is decoded), for the tables' portrait cells.
    internal System.Windows.Media.Brush? FaceBrush(int face) => surface.FaceBrush(face);

    // The keyboard on the map, for Esc to reach it.
    internal void FocusMap() => surface.Focus();

    internal bool IsMoving => surface.IsMoving;

    // null before the game is connected: the map shows terrain only.
    internal void SetSnapshot(GameSnapshot? data)
    {
        byte[] terrain = data?.Terrain ?? MapTerrain.Terrain;
        bool terrainChanged = !ReferenceEquals(snapshot?.Terrain, data?.Terrain);
        snapshot = data;
        if (terrainChanged) { RequestFill(); RequestVeil(); }
        if (SetForceColors(data?.Forces))
        {
            RequestFill();
            surface.RedrawColors();
            ShowLegend();
        }
        surface.SetSnapshot(data);
        surface.SetCounties(data?.TileCounties);
        UpdateModels();
        byte[]? newOwners = data?.TileOwners;
        if (owners is null != newOwners is null || (owners is not null && newOwners is not null && !owners.AsSpan().SequenceEqual(newOwners)))
        {
            owners = newOwners;
            RequestFill();
            RequestVeil();
            surface.SetTerritory(owners);
        }
        // A force that is gone is no longer shown alone.
        if (highlightForce > 0 && (data is null || !data.CitiesOf(highlightForce).Any() && !data.Units.Any(u => data.ForceOfUnit(u) == highlightForce)))
            SetHighlight(0);
        string key = data is null ? "" : string.Join(",", data.CityIds.Select(data.ForceOfCity)) +
            "|" + string.Join(",", data.Forces.Values.Select(f => f.RulerId));
        if (key != legendKey)
        {
            legendKey = key;
            BuildLegend();
        }
    }

    // The game's force colours by colour index (force +0x49), as the game shows them on screen: the table
    // at exe+0x1A26590 after the game's gamma table, matched pixel for pixel against the running game.
    // Index 60 is the game's default white; higher indices are white too.
    internal static readonly int[] GamePalette =
    [
        0x2222E9, 0x357800, 0xCA0032, 0x774F0D, 0x160070, 0x585858, 0x72B8C0, 0xE5BD0D, 0xD6789A, 0x80D974,
        0x323232, 0xF76D16, 0xCBB6D7, 0x5A0D42, 0x811C2A, 0x9AC045, 0xC4437A, 0x7086D0, 0xCCE3AB, 0x22938D,
        0x582600, 0x403580, 0x9C9C9C, 0x7A6536, 0x005B9D, 0xAD556E, 0xE27070, 0xDCDE90, 0xE78C0D, 0xD0B059,
        0x91005F, 0x770094, 0x864216, 0x007150, 0xF69964, 0x00475C, 0x866BB7, 0x00A5C3, 0x00A53C, 0xB83B4F,
        0xEDD3AA, 0xE3A3A3, 0x8EB382, 0x42560D, 0xCDCCCB, 0xDDD400, 0xAFA616, 0xAB46A7, 0x929C51, 0xBE8EAE,
        0x7FA5CB, 0x006FA8, 0xA2730D, 0xBDD9E8, 0x76C7AD, 0xA6FF0D, 0x71FFFB, 0xB4402A, 0xFF876F, 0xAE86FF,
        0xFFFFFF
    ];

    // Force ID -> colour index of the connected game (-1 when unknown); replaced whole, read by the drawing threads.
    private static volatile int[]? forceColorIndex;

    // Returns true when a force's colour changed, so everything drawn in force colours must be redrawn.
    private static bool SetForceColors(IReadOnlyDictionary<int, ForceInfo>? forces)
    {
        int[]? indices = null;
        if (forces is not null)
        {
            indices = Enumerable.Repeat(-1, forces.Keys.DefaultIfEmpty(0).Max() + 1).ToArray();
            foreach (ForceInfo force in forces.Values) indices[force.Id] = force.ColorIndex;
        }
        int[]? old = forceColorIndex;
        if (old is null ? indices is null : indices is not null && old.AsSpan().SequenceEqual(indices)) return false;
        forceColorIndex = indices;
        return true;
    }

    internal static Color ForceColor(int force)
    {
        if (force <= 0) return Unowned;
        if (forceColorIndex is { } indices && force < indices.Length && indices[force] >= 0)
            return Color.FromArgb(unchecked((int)0xFF000000) | GamePalette[Math.Min(indices[force], GamePalette.Length - 1)]);
        return ForcePalette[(force - 1) % ForcePalette.Length];
    }

    // ---- Layers ---------------------------------------------------------------------------------

    // A frozen view in device pixels, for drawing a layer on a worker thread.
    internal readonly record struct View(float CenterX, float CenterY, float Scale, int Width, int Height)
    {
        internal PointF ToScreen(PointF w) => new((w.X - CenterX) * Scale + Width / 2f, (w.Y - CenterY) * Scale + Height / 2f);
        internal PointF ToWorld(PointF s) => new((s.X - Width / 2f) / Scale + CenterX, (s.Y - Height / 2f) / Scale + CenterY);
    }

    // The base map is built once in the background and shared: the game's own terrain textures
    // when the game is installed, otherwise the relief painted from the tile data.
    private sealed record BaseMap(WMI.BitmapSource Image, float PixelsPerUnit, bool FromGame, Scenery? Around = null);

    // The game's whole field, the playable map within it, placed with its top-left corner at Origin (world units).
    internal sealed record Scenery(WMI.BitmapSource Image, PointF Origin, float PixelsPerUnit);
    private static Task<BaseMap>? baseMap;
    private static readonly Lazy<(PointF A, PointF B)[]> Roads = new(() => MapPainter.RoadSegments());

    private static BaseMap BuildBaseMap()
    {
        try
        {
            if (FaceLibrary.FindGameDirectory(null) is { } directory)
            {
                string cache = BaseMapCache(directory);
                if (ReadBaseMap(cache) is { } cached) return cached;
                if (GameTerrain.Build(directory, out GameTerrain.Surroundings? around) is { } game)
                    using (game)
                    using (around?.Image)
                    {
                        var map = new BaseMap(ToImage(game), GameTerrain.PixelsPerUnit, true,
                            around is null ? null : new Scenery(ToImage(around.Image), new PointF(around.OriginX, around.OriginY), around.PixelsPerUnit));
                        WriteBaseMap(cache, map);
                        return map;
                    }
            }
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { }
        using Bitmap painted = MapPainter.Build(PaintColor);
        return new BaseMap(ToImage(painted), MapPainter.PixelsPerUnit, false);
    }

    // The lit base map comes out the same on every run of the same game data, so it is kept on disk (the whole
    // field, premultiplied BGRA, deflated; the map is the part of it the hexes cover) and read back in a fraction
    // of the time painting takes, so the map shows soon after the window. A game update, or a change of
    // BaseMapVersion when the painting changes, paints it again.
    private const int BaseMapVersion = 2;

    private static string BaseMapCache(string gameDirectory) =>
        Path.Combine(Program.DataFolder, "cache", $"basemap-{BaseMapVersion}-{GameModels.Stamp(gameDirectory)}.bin");

    private static BaseMap? ReadBaseMap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            if (reader.ReadInt32() != 0x32424D53) return null;   // "SMB2"
            float ppu = reader.ReadSingle(), originX = reader.ReadSingle(), originY = reader.ReadSingle();
            int mapWidth = reader.ReadInt32(), mapHeight = reader.ReadInt32();
            using var inflate = new System.IO.Compression.DeflateStream(file, System.IO.Compression.CompressionMode.Decompress);
            using var body = new BinaryReader(inflate);
            int width = body.ReadInt32(), height = body.ReadInt32();
            using var pixels = new NativePixelBuffer(checked(width * height * 4));
            pixels.ReadExactly(inflate);
            var field = new Scenery(Frozen(WMI.BitmapSource.Create(width, height, 96, 96, WM.PixelFormats.Pbgra32, null, pixels.Pointer, pixels.Length, width * 4)),
                new PointF(originX, originY), ppu);
            // The map's pixels within the field's (GameTerrain.Build lines them up).
            var area = new W.Int32Rect((int)MathF.Round(-originX * ppu), (int)MathF.Round(-originY * ppu), mapWidth, mapHeight);
            return new BaseMap(Frozen(new WMI.CroppedBitmap(field.Image, area)), ppu, true, field);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException or ArgumentException) { return null; }
    }

    private static WMI.BitmapSource Frozen(WMI.BitmapSource image)
    {
        image.Freeze();
        return image;
    }

    // The field holds the map, so only the field is kept (a map without it is not kept).
    private static void WriteBaseMap(string path, BaseMap map)
    {
        if (map.Around is not { } field) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string partial = path + ".part";
            using (var file = File.Create(partial))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(0x32424D53);
                writer.Write(field.PixelsPerUnit);
                writer.Write(field.Origin.X);
                writer.Write(field.Origin.Y);
                writer.Write(map.Image.PixelWidth);
                writer.Write(map.Image.PixelHeight);
                writer.Flush();
                using var deflate = new System.IO.Compression.DeflateStream(file, System.IO.Compression.CompressionLevel.Fastest);
                using var body = new BinaryWriter(deflate);
                using var pixels = NativePixelBuffer.Copy(field.Image, field.Image.PixelWidth * 4);
                body.Write(field.Image.PixelWidth);
                body.Write(field.Image.PixelHeight);
                body.Write(pixels.Bytes);
            }
            File.Move(partial, path, overwrite: true);
            // An earlier version's base map, or an earlier game's, goes.
            foreach (string old in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "basemap-*.bin"))
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Only an optimisation. */ }
    }

    private static Color PaintColor(int type) =>
        type is 2 or 11 or 12 or 15 or 16 or 17 or 18 or 21 ? Color.Empty // roads and towns take the colour around them
        : TerrainColors[Math.Min(type, TerrainColors.Length - 1)];

    private void ShowBaseMap()
    {
        baseMap ??= Task.Run(BuildBaseMap);
        baseMap.ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            BaseMap map = task.Result;
            OnUiThread(() =>
            {
                fromGame = map.FromGame;
                surface.SetTerrain(map.Image, map.PixelsPerUnit, map.Around);
                UpdateModels();
                if (map.FromGame)
                {
                    LoadTrees();
                    // Shaders prepare their native uploads from the first base-map frame. Only
                    // CPU tiles retain decoded ground data for later tiles.
                    if (!surface.UsesGroundShaders)
                        Task.Delay(1500).ContinueWith(_ => GroundDetail.Preload(), TaskScheduler.Default);
                }
                surface.SetFromGame(fromGame);
                RequestFill();
            });
        }, TaskScheduler.Default);
    }

    // Frozen WPF images can be made on any thread and shown on the UI thread.
    internal static WMI.BitmapSource ToImage(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var image = WMI.BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, WM.PixelFormats.Pbgra32, null,
                data.Scan0, data.Stride * bitmap.Height, data.Stride);
            image.Freeze();
            return image;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void OnUiThread(Action action)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeAsync(action);
    }

    // ---- One force alone ----------------------------------------------------------------------------

    // A click on a force's chip opens its card (and, while the card is open, the force is shown alone: the
    // rest of the map under a dark veil and only its units).
    private int highlightForce;
    private int veilVersion;

    private void SetHighlight(int force)
    {
        if (force == highlightForce) return;
        highlightForce = force;
        surface.HighlightForce = force;
        RequestVeil();
        ShowLegend();
    }

    private void RequestVeil()
    {
        int version = ++veilVersion;
        if (highlightForce == 0 || owners is not { } currentOwners)
        {
            surface.SetVeil(null, FillPixelsPerUnit);
            return;
        }
        int force = highlightForce;
        Task.Run(() =>
        {
            if (version != Volatile.Read(ref veilVersion)) return;
            using Bitmap bitmap = RenderVeil(currentOwners, force);
            WMI.BitmapSource image = ToImage(bitmap);
            OnUiThread(() => { if (version == veilVersion) surface.SetVeil(image, FillPixelsPerUnit); });
        });
    }

    // Dark over the whole map but the force's own hexes.
    // Winding, as OwnerAreas: with the alternate rule the strips where two hexes overlap stayed veiled.
    private static Bitmap RenderVeil(byte[] owners, int force)
    {
        View view = FillView();
        var bitmap = new Bitmap(view.Width, view.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(VeilColor);
        using var own = new GraphicsPath(FillMode.Winding);
        for (int tile = 0; tile < owners.Length; tile++)
            if (owners[tile] == force) own.AddPolygon(Hex(view, tile, 1.04f));
        g.CompositingMode = CompositingMode.SourceCopy;
        using var clear = new SolidBrush(Color.Transparent);
        g.FillPath(clear, own);
        return bitmap;
    }

    // Territory tint for the whole world, redrawn when tile ownership changes.
    private void RequestFill()
    {
        int version = ++fillVersion;
        if (owners is not { } currentOwners)
        {
            surface.SetFill(null, null, FillPixelsPerUnit);
            return;
        }
        // A light wash of each force's colour inside its borders; the game's textured ground needs a little
        // more of it than the plain painted map to show.
        int alpha = fromGame ? 72 : 60;
        byte[] terrain = snapshot?.Terrain ?? MapTerrain.Terrain;
        Task.Run(() =>
        {
            if (version != Volatile.Read(ref fillVersion)) return;
            using Bitmap bitmap = RenderFill(currentOwners, alpha, terrain);
            WMI.BitmapSource image = ToImage(bitmap);
            WMI.BitmapSource? strategicImage = GroundPixels() is { } ground ? RenderStrategic(currentOwners, ground, terrain) : null;
            OnUiThread(() => { if (version == fillVersion) surface.SetFill(image, strategicImage, FillPixelsPerUnit); });
        });
    }

    // The base map at the territory layers' resolution, made once it exists (null before).
    private static (int[] Pixels, int Width, int Height)? groundPixels;

    private static (int[] Pixels, int Width, int Height)? GroundPixels()
    {
        if (groundPixels is { } known) return known;
        if (baseMap is not { IsCompletedSuccessfully: true } task) return null;
        double scale = FillPixelsPerUnit / task.Result.PixelsPerUnit;
        var small = new WMI.FormatConvertedBitmap(new WMI.TransformedBitmap(task.Result.Image, new WM.ScaleTransform(scale, scale)), WM.PixelFormats.Bgra32, null, 0);
        var pixels = new int[small.PixelWidth * small.PixelHeight];
        small.CopyPixels(pixels, small.PixelWidth * 4, 0);
        return groundPixels = (pixels, small.PixelWidth, small.PixelHeight);
    }

    // The game's strategic view: each force's land in its colour, shaded by the ground's own light so that
    // mountains, cliffs and rivers still show. Opaque where a force owns the land, clear elsewhere; the
    // surface lays it over the map at partial strength.
    private static WMI.BitmapSource RenderStrategic(byte[] owners, (int[] Pixels, int Width, int Height) ground, byte[] terrain)
    {
        int width = ground.Width, height = ground.Height;
        View view = FillView(width, height);
        // The owner of each pixel, drawn as hexes (the red channel holds the force ID).
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.None;
            foreach ((int owner, GraphicsPath area) in OwnerAreas(owners, view, terrain))
            {
                using var brush = new SolidBrush(Color.FromArgb(255, owner, 0, 0));
                g.FillPath(brush, area);
                area.Dispose();
            }
        }
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            // Shade the owned GDI bitmap in place. Each worker needs one small row, not whole-map
            // owner/output arrays (and WPF's additional managed copy of the output array).
            Parallel.For(0, height, () => ArrayPool<int>.Shared.Rent(width), (y, _, row) =>
            {
                IntPtr line = data.Scan0 + y * data.Stride;
                Marshal.Copy(line, row, 0, width);
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x, owner = row[x] >> 16 & 255;
                    if (owner == 0 || (row[x] >>> 24) == 0) { row[x] = 0; continue; }
                    Color c = ForceColor(owner);
                    int p = ground.Pixels[i];
                    float light = (0.3f * (p >> 16 & 255) + 0.59f * (p >> 8 & 255) + 0.11f * (p & 255)) / 255f;
                    float t = Math.Clamp(light * 1.6f, 0.08f, 1.25f);
                    int Channel(int value) => (int)Math.Clamp(t <= 1 ? value * t : value + (255 - value) * (t - 1) * 0.6f, 0, 255);
                    row[x] = unchecked((int)0xFF000000) | Channel(c.R) << 16 | Channel(c.G) << 8 | Channel(c.B);
                }
                Marshal.Copy(row, 0, line, width);
                return row;
            }, row => ArrayPool<int>.Shared.Return(row));
        }
        finally { bitmap.UnlockBits(data); }
        return ToImage(bitmap);
    }

    // One detail tile (borders, grid and map symbols); runs on a worker thread.
    // Tile (x, y) of a level covers device pixels [x, x + 1) * TileSize at that level's scale.
    internal static WMI.BitmapSource RenderTile(int level, int x, int y, byte[]? owners, ushort[]? counties, bool fromGame, byte[]? terrainData = null)
    {
        byte[] terrain = terrainData ?? MapTerrain.Terrain;
        float ppu = MapTiles.PixelsPerUnit(level);
        const int size = MapTiles.TileSize;
        using Bitmap bitmap = RenderDetail(new View((x + 0.5f) * size / ppu, (y + 0.5f) * size / ppu, ppu, size, size), owners, counties, fromGame, terrain);
        return ToImage(bitmap);
    }

    // ---- The game's models ---------------------------------------------------------------------------

    // The game's own models of cities, gates, county seats and ports, rendered once (or read from the cache
    // of an earlier run) and laid on the map where the game puts them.
    private const double ModelPixelsPerUnit = 80;   // sharp up to the closest zoom
    private const float ModelTilt = 35;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Model, int Yaw, double Scale), GameModels.Sprite?> modelSprites = new();
    private List<MapModels.Placement>? placedModels;
    private bool modelsLoading;

    private void UpdateModels()
    {
        if (baseMap is not { IsCompletedSuccessfully: true } task || !task.Result.FromGame) return;
        List<MapModels.Placement> wanted = MapModels.All(snapshot);
        if (placedModels is not null && placedModels.SequenceEqual(wanted)) return;
        var missing = wanted.Select(p => (p.Model, p.Yaw, p.Scale)).Distinct().Where(key => !modelSprites.ContainsKey(key)).ToList();
        if (missing.Count == 0)
        {
            placedModels = wanted;
            surface.SetModels(wanted, modelSprites);
            return;
        }
        if (modelsLoading) return;   // the next snapshot tries again
        modelsLoading = true;
        string? directory = FaceLibrary.FindGameDirectory(null);
        bool prepareCpu = !surface.UsesGpu;
        Task.Run(() =>
        {
            // A larger model is rendered with more pixels, so that it is as sharp on the map as the others.
            foreach (var key in missing)
            {
                GameModels.Sprite? placed = null;
                try
                {
                    placed = directory is null ? null
                        : GameModels.RenderCached(directory, key.Model, ModelPixelsPerUnit * key.Scale, ModelTilt, key.Yaw) is { } sprite
                            ? sprite with { World = new System.Windows.Rect(sprite.World.X * key.Scale, sprite.World.Y * key.Scale, sprite.World.Width * key.Scale, sprite.World.Height * key.Scale) }
                            : null;
                    if (prepareCpu && placed is not null) MapSurface.WithShadow(placed);
                }
                catch (Exception ex)
                {
                    // A model that fails is left out (its place keeps its icon). Without an entry it would be
                    // missing again below, and rendered and failing again without end.
                    System.Diagnostics.Debug.WriteLine($"Model {key.Model} not rendered: {ex.Message}");
                    placed = null;
                }
                modelSprites[key] = placed;
            }
        }).ContinueWith(_ => OnUiThread(() =>
        {
            modelsLoading = false;
            UpdateModels();
        }), TaskScheduler.Default);
    }

    // The game's tree models for the wooded hexes of the close-up ground (MapTrees), rendered once.
    private static bool treesLoading;

    private void LoadTrees()
    {
        if (treesLoading || MapTrees.Ready || FaceLibrary.FindGameDirectory(null) is not { } directory) return;
        treesLoading = true;
        Task.Run(() =>
        {
            MapTrees.Sprite? Tree(string name)
            {
                if (GameModels.RenderCached(directory, name, 160, ModelTilt, 0) is not { } sprite) return null;
                var pixels = new uint[sprite.Image.PixelWidth * sprite.Image.PixelHeight];
                sprite.Image.CopyPixels(pixels, sprite.Image.PixelWidth * 4, 0);
                return MapTrees.FromPixels(pixels, sprite.Image.PixelWidth, sprite.Image.PixelHeight,
                    (float)sprite.World.X, (float)sprite.World.Y, (float)sprite.World.Width, (float)sprite.World.Height);
            }
            MapTrees.Set(Tree("obj-n02-tree-01"), Tree("obj-n00-tree-05"));
        }).ContinueWith(_ => OnUiThread(() =>
        {
            treesLoading = false;
            if (MapTrees.Ready) surface.RedrawGround();
        }), TaskScheduler.Default);
    }

    // One close-up ground tile from the game's ground textures (null without them); runs on a worker thread.
    internal static WMI.BitmapSource? RenderGround(MapTiles.TileKey key, byte[]? terrain = null) =>
        baseMap is { IsCompletedSuccessfully: true } task && task.Result.FromGame
            ? GroundDetail.Render(key, task.Result.Image, task.Result.PixelsPerUnit, terrain) : null;

    private static (int Col0, int Col1, int Row0, int Row1) VisibleTiles(View view)
    {
        PointF topLeft = view.ToWorld(PointF.Empty), bottomRight = view.ToWorld(new PointF(view.Width, view.Height));
        int col0 = Math.Max(0, (int)((topLeft.X - 1) / 1.5f) - 1), col1 = Math.Min(MapTerrain.Columns - 1, (int)((bottomRight.X - 1) / 1.5f) + 1);
        int row0 = Math.Max(0, (int)(topLeft.Y / MapTerrain.Sqrt3) - 2), row1 = Math.Min(MapTerrain.Rows - 1, (int)(bottomRight.Y / MapTerrain.Sqrt3) + 1);
        return (col0, col1, row0, row1);
    }

    private static PointF[] Hex(View view, int tile, float scaleUp = 1f)
    {
        PointF c = view.ToScreen(MapTerrain.TileCenter(tile));
        float r = view.Scale * scaleUp;
        return
        [
            new(c.X + r, c.Y), new(c.X + r / 2, c.Y + r * MapTerrain.Sqrt3 / 2), new(c.X - r / 2, c.Y + r * MapTerrain.Sqrt3 / 2),
            new(c.X - r, c.Y), new(c.X - r / 2, c.Y - r * MapTerrain.Sqrt3 / 2), new(c.X + r / 2, c.Y - r * MapTerrain.Sqrt3 / 2)
        ];
    }

    private static bool IsOutside(int tile, byte[] terrain) => MapTerrain.IsSea(terrain[tile]);

    // The whole world at the territory layers' resolution: pixel (x, y) is world point (x, y) / FillPixelsPerUnit.
    private static View FillView() =>
        FillView((int)MathF.Ceiling(MapTerrain.WorldWidth * FillPixelsPerUnit), (int)MathF.Ceiling(MapTerrain.WorldHeight * FillPixelsPerUnit));

    private static View FillView(int width, int height) =>
        new(width / 2f / FillPixelsPerUnit, height / 2f / FillPixelsPerUnit, FillPixelsPerUnit, width, height);

    // Each force's land as one path of its hexes, drawn a little large so that they join (the caller disposes them).
    // Winding: with the default alternate rule the strips where two hexes overlap were left out, and magnified
    // they spotted the ground with dashes and dots.
    private static Dictionary<int, GraphicsPath> OwnerAreas(byte[] owners, View view, byte[] terrain)
    {
        var areas = new Dictionary<int, GraphicsPath>();
        for (int tile = 0; tile < owners.Length; tile++)
        {
            int owner = owners[tile];
            if (owner == 0 || IsOutside(tile, terrain)) continue;
            if (!areas.TryGetValue(owner, out GraphicsPath? area)) areas[owner] = area = new GraphicsPath(FillMode.Winding);
            area.AddPolygon(Hex(view, tile, 1.04f));
        }
        return areas;
    }

    private static Bitmap RenderFill(byte[] owners, int alpha, byte[] terrain)
    {
        View view = FillView();
        var bitmap = new Bitmap(view.Width, view.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        foreach ((int owner, GraphicsPath path) in OwnerAreas(owners, view, terrain))
        {
            using var brush = new SolidBrush(Color.FromArgb(alpha, ForceColor(owner)));
            g.FillPath(brush, path);
            path.Dispose();
        }
        return bitmap;
    }

    private static Bitmap RenderDetail(View view, byte[]? owners, ushort[]? counties, bool fromGame, byte[] terrain)
    {
        var bitmap = new Bitmap(view.Width, view.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        (int col0, int col1, int row0, int row1) = VisibleTiles(view);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // The game's textures already show roads, mountains and forests.
        if (!fromGame) DrawRoads(g, view, terrain);

        // Faint white hex grid when zoomed in, like the game's map.
        if (view.Scale >= 9)
        {
            using var gridPen = new Pen(Color.FromArgb(fromGame ? 28 : 50, 255, 255, 255), Math.Clamp(view.Scale * 0.03f, 1f, 1.5f));
            for (int row = row0; row <= row1; row++)
                for (int col = col0; col <= col1; col++)
                {
                    int tile = row * MapTerrain.Columns + col;
                    if (!MapTerrain.IsWater(terrain[tile])) g.DrawPolygon(gridPen, Hex(view, tile));
                }
        }
        if (counties is not null && view.Scale >= 6) DrawCountyBorders(g, view, counties, col0, col1, row0, row1, terrain);
        DrawSymbols(g, view, col0, col1, row0, row1, glyphs: !fromGame, terrain);
        if (owners is not null) DrawBorders(g, view, owners, col0, col1, row0, row1, terrain);
        return bitmap;
    }

    // A pen with round joins and ends, for lines drawn one hex edge at a time.
    private static Pen RoundPen(Color color, float width) => new(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

    // County boundaries as white lines, as the game draws them over the land.
    private static void DrawCountyBorders(Graphics g, View view, ushort[] counties, int col0, int col1, int row0, int row1, byte[] terrain)
    {
        using var path = new GraphicsPath();
        for (int row = row0; row <= row1; row++)
            for (int col = col0; col <= col1; col++)
            {
                int tile = row * MapTerrain.Columns + col;
                if (counties[tile] == 0 || IsOutside(tile, terrain) || MapTerrain.IsWater(terrain[tile])) continue;
                PointF[]? edge = null;
                // Each shared edge once: from the north, north-east and south-east neighbours.
                for (int direction = 0; direction < 3; direction++)
                {
                    int neighbour = MapTerrain.Neighbour(col, row, direction);
                    if (neighbour < 0 || counties[neighbour] == 0 || counties[neighbour] == counties[tile] ||
                        IsOutside(neighbour, terrain) || MapTerrain.IsWater(terrain[neighbour])) continue;
                    edge ??= Hex(view, tile);
                    (int a, int b) = EdgeVertices(direction);
                    path.StartFigure();
                    path.AddLine(edge[a], edge[b]);
                }
            }
        float width = Math.Clamp(view.Scale * 0.09f, 1.1f, 3f);
        using var shade = RoundPen(Color.FromArgb(42, 40, 30, 20), width * 2.2f);
        using var pen = RoundPen(Color.FromArgb(140, 250, 248, 240), width);
        g.DrawPath(shade, path);
        g.DrawPath(pen, path);
    }

    private static void DrawRoads(Graphics g, View view, byte[] terrain)
    {
        var bounds = new RectangleF(-20, -20, view.Width + 40, view.Height + 40);
        using var path = new GraphicsPath();
        foreach ((PointF a, PointF b) in ReferenceEquals(terrain, MapTerrain.Terrain) ? Roads.Value : MapPainter.RoadSegments(terrain))
        {
            PointF sa = view.ToScreen(a), sb = view.ToScreen(b);
            if (!bounds.Contains(sa) && !bounds.Contains(sb)) continue;
            path.StartFigure();
            path.AddLine(sa, sb);
        }
        float width = Math.Clamp(view.Scale * 0.34f, 1.1f, 7f);
        using var edge = RoundPen(Color.FromArgb(150, 96, 72, 46), width);
        g.DrawPath(edge, path);
        if (view.Scale >= 4)
        {
            using var surface = RoundPen(Color.FromArgb(220, 222, 200, 150), width * 0.5f);
            g.DrawPath(surface, path);
        }
    }

    // Mountains, forests and settlements as small map symbols; detail grows with zoom.
    private static void DrawSymbols(Graphics g, View view, int col0, int col1, int row0, int row1, bool glyphs, byte[] terrain)
    {
        float r = view.Scale;
        using var peakLight = new SolidBrush(Color.FromArgb(215, 236, 224, 200));
        using var peakShade = new SolidBrush(Color.FromArgb(215, 118, 98, 76));
        using var peakLine = new Pen(Color.FromArgb(170, 78, 62, 46), Math.Max(0.8f, r * 0.06f));
        using var tree = new SolidBrush(Color.FromArgb(190, 74, 110, 60));
        using var treeLight = new SolidBrush(Color.FromArgb(150, 150, 184, 110));
        using var seat = new SolidBrush(Color.FromArgb(235, 250, 244, 228));
        using var seatLine = new Pen(Color.FromArgb(220, 70, 52, 36), Math.Max(1f, r * 0.1f));
        using var port = new SolidBrush(Color.FromArgb(235, 70, 120, 170));
        for (int row = row0; row <= row1; row++)
            for (int col = col0; col <= col1; col++)
            {
                int tile = row * MapTerrain.Columns + col;
                int type = terrain[tile];
                PointF c = view.ToScreen(MapTerrain.TileCenter(tile));
                // Per-tile jitter so ranges don't look stamped.
                uint hash = (uint)tile * 2654435761u;
                if (glyphs && type == MapTerrain.Mountain && r >= 4.5f && (hash >> 28) % 4 != 0)
                {
                    float s = r * (0.85f + ((hash >> 16) & 0xFF) / 255f * 0.35f);
                    c = new PointF(c.X + ((hash & 0xFF) / 255f - 0.5f) * 0.35f * r, c.Y + (((hash >> 8) & 0xFF) / 255f - 0.5f) * 0.25f * r);
                    var top = new PointF(c.X + s * 0.05f, c.Y - s * 0.7f);
                    var left = new PointF(c.X - s * 0.8f, c.Y + s * 0.45f);
                    var right = new PointF(c.X + s * 0.8f, c.Y + s * 0.45f);
                    var foot = new PointF(c.X + s * 0.1f, c.Y + s * 0.45f);
                    g.FillPolygon(peakLight, [left, top, foot]);
                    g.FillPolygon(peakShade, [foot, top, right]);
                    g.DrawLines(peakLine, [left, top, right]);
                }
                else if (glyphs && type is MapTerrain.Woods or MapTerrain.Jungle or MapTerrain.MarshForest && r >= 6f && (tile * 7 % 3) != 0)
                {
                    float t = r * 0.26f;
                    foreach ((float dx, float dy) in new[] { (-0.3f, 0.1f), (0.28f, -0.12f) })
                    {
                        float x = c.X + dx * r, y = c.Y + dy * r;
                        g.FillEllipse(tree, x - t, y - t, t * 2, t * 2);
                        g.FillEllipse(treeLight, x - t * 0.6f, y - t * 0.8f, t, t);
                    }
                }
                else if (type is MapTerrain.Seat or MapTerrain.Gate or MapTerrain.Port)
                {
                    float s = Math.Clamp(r * 0.55f, 2.2f, 9f);
                    if (type == MapTerrain.Port)
                    {
                        g.FillEllipse(port, c.X - s / 2, c.Y - s / 2, s, s);
                        g.DrawEllipse(seatLine, c.X - s / 2, c.Y - s / 2, s, s);
                    }
                    else if (type == MapTerrain.Gate)
                    {
                        g.FillRectangle(peakShade, c.X - s / 2, c.Y - s / 2, s, s);
                        g.DrawRectangle(seatLine, c.X - s / 2, c.Y - s / 2, s, s);
                    }
                    else
                    {
                        g.FillEllipse(seat, c.X - s / 2, c.Y - s / 2, s, s);
                        g.DrawEllipse(seatLine, c.X - s / 2, c.Y - s / 2, s, s);
                    }
                }
            }
    }

    // Borders: a soft wide band in the owner's colour under a bright line with dark edges, like the game's.
    private static void DrawBorders(Graphics g, View view, byte[] owners, int col0, int col1, int row0, int row1, byte[] terrain)
    {
        var borders = new Dictionary<int, GraphicsPath>();
        for (int row = row0; row <= row1; row++)
            for (int col = col0; col <= col1; col++)
            {
                int tile = row * MapTerrain.Columns + col;
                int owner = owners[tile];
                if (owner == 0 || IsOutside(tile, terrain)) continue;
                PointF[]? edge = null;
                for (int direction = 0; direction < 6; direction++)
                {
                    int neighbour = MapTerrain.Neighbour(col, row, direction);
                    if (neighbour >= 0 && owners[neighbour] == owner && !IsOutside(neighbour, terrain)) continue;
                    edge ??= Hex(view, tile, 0.92f);
                    if (!borders.TryGetValue(owner, out GraphicsPath? border)) borders[owner] = border = new GraphicsPath();
                    (int a, int b) = EdgeVertices(direction);
                    border.StartFigure();
                    border.AddLine(edge[a], edge[b]);
                }
            }
        float width = Math.Clamp(view.Scale * 0.4f, 1.3f, 3.6f);
        foreach ((int owner, GraphicsPath path) in borders)
        {
            Color color = ForceColor(owner);
            // Translucent, as the game's borders: the territory colour and the ground show through.
            using var glow = RoundPen(Color.FromArgb(28, color), width * 3.4f);
            using var edge = RoundPen(Color.FromArgb(84, (int)(color.R * 0.45), (int)(color.G * 0.45), (int)(color.B * 0.45)), width * 1.7f);
            using var pen = RoundPen(Color.FromArgb(120, color), width);
            g.DrawPath(glow, path);
            g.DrawPath(edge, path);
            g.DrawPath(pen, path);
            path.Dispose();
        }
    }

    // Hex vertices (0 = east, clockwise with y down) shared with the neighbour in each direction (MapTerrain.Neighbour).
    private static (int, int) EdgeVertices(int direction) => direction switch
    {
        0 => (4, 5),
        1 => (5, 0),
        2 => (0, 1),
        3 => (1, 2),
        4 => (2, 3),
        _ => (3, 4)
    };

    // ---- Legend --------------------------------------------------------------------------------

    // A chip for each force, most cities first: a small shield in its colour, like the map's labels, its name
    // and its count of cities. A click opens the force's card.
    private void BuildLegend()
    {
        legend.Children.Clear();
        chips.Clear();
        if (snapshot is null)
        {
            legend.Children.Add(new WC.TextBlock
            {
                Text = L.Map.ConnectToSee, Foreground = CardSkin.InkOnDark, FontSize = LegendSize,
                Margin = new W.Thickness(-1, 4, 0, 4)
            });
            return;
        }
        var forces = snapshot.CityIds.GroupBy(snapshot.ForceOfCity).OrderByDescending(f => f.Count()).ThenBy(f => f.Key);
        foreach (var force in forces)
        {
            int id = force.Key;
            var shield = new WS.Polygon
            {
                Points = [new(0, 0), new(11, 0), new(11, 8.5), new(5.5, 13.75), new(0, 8.5)], Fill = CardSkin.Brush(ForceColor(id)),
                Stroke = CardSkin.Brush(Theme.WithAlpha(Theme.InkOnDark, 150)), StrokeThickness = 1, VerticalAlignment = W.VerticalAlignment.Center,
                Margin = new W.Thickness(0, 0, 4, 0)
            };
            var label = new WC.TextBlock { Text = $"{snapshot.ForceName(id)} · {force.Count()}", FontSize = LegendSize, VerticalAlignment = W.VerticalAlignment.Center };
            var chip = new WC.Border
            {
                CornerRadius = new W.CornerRadius(11), BorderThickness = new W.Thickness(1), Padding = new W.Thickness(7, 3, 8, 3), Margin = new W.Thickness(0, 2, 6, 2),
                Cursor = WI.Cursors.Hand, Child = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Children = { shield, label } }
            };
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ForceClicked?.Invoke(id);
            };
            chips.Add((id, chip, label));
            legend.Children.Add(chip);
        }
        ShowLegend();
        AddEnvironmentPreviewLegend();
    }

    private const double LegendSize = 11.5;   // 8.5 pt
    private static readonly WM.Brush ChipBack = CardSkin.Brush(Theme.WithAlpha(Theme.WoodLight, 200)), OpenBack = CardSkin.Brush(Theme.WithAlpha(Theme.Gold, 80)),
        ChipEdge = CardSkin.Brush(Theme.WithAlpha(Theme.Gold, 90)), FadedInk = CardSkin.Brush(Theme.WithAlpha(Theme.InkOnDark, 120));

    // The chips' look: gold while the force's card is open, the others' names faded then.
    private void ShowLegend()
    {
        foreach ((int force, WC.Border chip, WC.TextBlock label) in chips)
        {
            bool open = highlightForce > 0 && force == highlightForce;
            chip.Background = open ? OpenBack : ChipBack;
            chip.BorderBrush = open ? CardSkin.Gold : ChipEdge;
            label.Foreground = highlightForce > 0 && !open ? FadedInk : CardSkin.InkOnDark;
            chip.ToolTip = open ? L.Map.ForceChipOpenTip : L.Map.ForceChipTip;
            if (chip.Child is WC.StackPanel { Children: [WS.Polygon shield, ..] }) shield.Fill = CardSkin.Brush(ForceColor(force));
        }
    }
}
