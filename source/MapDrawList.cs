using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// A reusable frame buffer. All UI state is captured before the render thread receives it.
internal sealed class MapDrawList(int capacity = 2048) : IGpuMapCanvas
{
    private enum Kind { Clear, Push, Pop, LayerImage, Image, Rectangle, Rounded, Ellipse, Geometry, GeometryAt, Line, Text, Outlined, Tint, Shadow, Sprite, Ground, Borders }
    private struct Command
    {
        internal Kind Kind;
        internal object? Source;
        internal object? Layer;
        internal WM.Brush? Brush;
        internal WM.Pen? Pen;
        internal W.Rect Area;
        internal W.Point A, B;
        internal WM.Matrix Matrix;
        internal double X, Y, Opacity;
        internal WM.BitmapScalingMode Mode;
        internal bool Snap, MayDefer;
        internal WM.Color Colour;
        internal MapShaders.Camera Camera;
        internal byte[]? Owners;
        internal byte[]? Terrain;
        internal ushort[]? Counties;
        internal System.Drawing.Color[]? Palette;
    }
    private readonly List<Command> commands = new(capacity);
    private readonly System.Drawing.Color[] palette = new System.Drawing.Color[256];
    private WM.BitmapScalingMode imageMode = WM.BitmapScalingMode.HighQuality;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WM.FormattedText, MapTextRun> texts = new();
    internal int Count => commands.Count;
    internal void CopyTo(MapDrawList copy)
    {
        copy.Reset(); palette.CopyTo(copy.palette, 0);
        foreach (var command in commands)
        {
            var captured = command;
            if (captured.Palette is not null) captured.Palette = copy.palette;
            copy.commands.Add(captured);
        }
    }
    // Append a retained layer without clearing the frame's ground/border commands or palette.
    internal void AppendTo(MapDrawList frame)
    {
        foreach (var command in commands)
        {
            var captured = command;
            if (captured.Palette is not null)
            {
                palette.CopyTo(frame.palette, 0);
                captured.Palette = frame.palette;
            }
            frame.commands.Add(captured);
        }
    }
    internal string CompareCommands(MapDrawList other)
    {
        if (commands.Count != other.commands.Count) return $"count {commands.Count}/{other.commands.Count}";
        for (int i = 0; i < commands.Count; i++)
        {
            var a = commands[i]; var b = other.commands[i];
            if (a.Palette is not null && b.Palette is not null)
            {
                if (!a.Palette.AsSpan().SequenceEqual(b.Palette)) return $"command {i} palette changed";
                b.Palette = a.Palette;
            }
            if (!a.Equals(b))
            {
                return $"command {i} {a.Kind}, area {a.Area}/{b.Area}, point {a.A}/{b.A}, x {a.X}/{b.X}, sourceSame {Equals(a.Source, b.Source)}";
            }
        }
        return "identical";
    }
    internal void Reset() { commands.Clear(); imageMode = WM.BitmapScalingMode.HighQuality; }
    internal WM.BitmapScalingMode ImageMode { set => imageMode = value; }

    private static T Share<T>(T value) where T : W.Freezable
    {
        if (value.IsFrozen) return value;
        var frozen = (T)value.CloneCurrentValue();
        frozen.Freeze();
        return frozen;
    }
    private static WM.Brush? Shared(WM.Brush? value) => value is null ? null : Share(value);
    private static WM.Pen? Shared(WM.Pen? value) => value is null ? null : Share(value);

    internal static MapTextRun TextRun(WM.FormattedText text) => texts.GetValue(text, t =>
    {
        TextSpec spec = MapText.Specs.TryGetValue(t, out var found) ? found
            : throw new InvalidOperationException("Map text must have a captured font specification.");
        WM.Typeface face = spec.Face;
        return new(spec.Text, face.FontFamily.Source.Split(',')[0].Trim(), face.Weight.ToOpenTypeWeight(),
            face.Style == W.FontStyles.Italic ? 2 : face.Style == W.FontStyles.Oblique ? 1 : 0,
            face.Stretch.ToOpenTypeStretch(), spec.Size, Share(spec.Brush), t.Width, t.Height, t.Baseline);
    });

    public void Clear(WM.Color colour) => commands.Add(new() { Kind = Kind.Clear, Colour = colour });
    public void PushTransform(WM.Transform value) => PushMatrix(value.Value);
    public void PushMatrix(WM.Matrix matrix) => commands.Add(new() { Kind = Kind.Push, Matrix = matrix });
    public void Pop() => commands.Add(new() { Kind = Kind.Pop });
    public void SetLayerImage(object layer, WMI.BitmapSource? image)
        => commands.Add(new() { Kind = Kind.LayerImage, Layer = layer, Source = image is null ? null : Share(image) });
    public void DrawImage(WMI.BitmapSource image, W.Rect area) => DrawBitmap(image, area, 1, imageMode, mayDefer: true);
    public void DrawBitmap(WMI.BitmapSource image, W.Rect area, double opacity, WM.BitmapScalingMode mode, bool snap = false, bool mayDefer = false)
        => commands.Add(new() { Kind = Kind.Image, Source = Share(image), Area = area, Opacity = opacity, Mode = mode, Snap = snap, MayDefer = mayDefer });
    public void DrawRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area) => FillRectangle(brush, pen, area, 1);
    public void FillRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area, double opacity)
        => commands.Add(new() { Kind = Kind.Rectangle, Brush = Shared(brush), Pen = Shared(pen), Area = area, Opacity = opacity });
    public void DrawRoundedRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area, double radiusX, double radiusY)
        => commands.Add(new() { Kind = Kind.Rounded, Brush = Shared(brush), Pen = Shared(pen), Area = area, X = radiusX, Y = radiusY });
    public void DrawEllipse(WM.Brush? brush, WM.Pen? pen, W.Point centre, double radiusX, double radiusY)
        => commands.Add(new() { Kind = Kind.Ellipse, Brush = Shared(brush), Pen = Shared(pen), A = centre, X = radiusX, Y = radiusY });
    public void DrawGeometry(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry)
        => commands.Add(new() { Kind = Kind.Geometry, Brush = Shared(brush), Pen = Shared(pen), Source = Share(geometry) });
    public void DrawGeometryAt(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry, W.Point origin, double scale = 1)
        => commands.Add(new() { Kind = Kind.GeometryAt, Brush = Shared(brush), Pen = Shared(pen), Source = Share(geometry), A = origin, X = scale });
    public void DrawLine(WM.Pen pen, W.Point from, W.Point to)
        => commands.Add(new() { Kind = Kind.Line, Pen = Share(pen), A = from, B = to });
    public void DrawText(WM.FormattedText text, W.Point origin)
    {
        if (MapText.Specs.TryGetValue(text, out _)) commands.Add(new() { Kind = Kind.Text, Source = TextRun(text), A = origin });
        else DrawGeometry(WM.Brushes.White, null, text.BuildGeometry(origin));
    }
    public void DrawOutlined(WM.Geometry shape, W.Rect bounds, WM.Brush ink, WM.Pen halo, double x, double y, double scale, double pixelsPerDip)
        => commands.Add(new() { Kind = Kind.Outlined, Source = Share(shape), Area = bounds, Brush = Share(ink), Pen = Share(halo), A = new(x, y), X = scale, Y = pixelsPerDip });
    public void DrawTinted(WMI.BitmapSource source, System.Drawing.Color colour, W.Rect area)
        => commands.Add(new() { Kind = Kind.Tint, Source = Share(source), Colour = WM.Color.FromArgb(colour.A, colour.R, colour.G, colour.B), Area = area });
    public void DrawShadowed(GameModels.Sprite sprite, W.Point origin, double opacity = 1)
        => commands.Add(new() { Kind = Kind.Shadow, Source = sprite.Image.IsFrozen ? sprite : sprite with { Image = Share(sprite.Image) }, A = origin, Opacity = opacity });
    public void DrawSprite(MapSprite sprite, W.Point origin) => commands.Add(new() { Kind = Kind.Sprite, Source = sprite, A = origin });
    internal void DrawGround(MapShaders shaders, MapShaders.Camera camera, byte[]? terrain = null)
        => commands.Add(new() { Kind = Kind.Ground, Source = shaders, Camera = camera, Terrain = terrain });
    internal void DrawBorders(MapShaders shaders, MapShaders.Camera camera, byte[]? owners, ushort[]? counties, byte[]? terrain = null)
    {
        for (int force = 1; force < palette.Length; force++) palette[force] = MapView.ForceColor(force);
        commands.Add(new() { Kind = Kind.Borders, Source = shaders, Camera = camera, Owners = owners, Counties = counties, Terrain = terrain, Palette = palette });
    }

    internal void Play(D2DCanvas canvas, GpuMap? gpu = null)
    {
        foreach (var c in commands)
            switch (c.Kind)
            {
                case Kind.Clear: canvas.Clear(c.Colour); break;
                case Kind.Push: canvas.PushMatrix(c.Matrix); break;
                case Kind.Pop: canvas.Pop(); break;
                case Kind.LayerImage: canvas.SetLayerImage(c.Layer!, (WMI.BitmapSource?)c.Source); break;
                case Kind.Image: canvas.DrawBitmap((WMI.BitmapSource)c.Source!, c.Area, c.Opacity, c.Mode, c.Snap, c.MayDefer); break;
                case Kind.Rectangle: canvas.FillRectangle(c.Brush, c.Pen, c.Area, c.Opacity); break;
                case Kind.Rounded: canvas.DrawRoundedRectangle(c.Brush, c.Pen, c.Area, c.X, c.Y); break;
                case Kind.Ellipse: canvas.DrawEllipse(c.Brush, c.Pen, c.A, c.X, c.Y); break;
                case Kind.Geometry: canvas.DrawGeometry(c.Brush, c.Pen, (WM.Geometry)c.Source!); break;
                case Kind.GeometryAt: canvas.DrawGeometryAt(c.Brush, c.Pen, (WM.Geometry)c.Source!, c.A, c.X); break;
                case Kind.Line: canvas.DrawLine(c.Pen!, c.A, c.B); break;
                case Kind.Text: canvas.DrawText((MapTextRun)c.Source!, c.A); break;
                case Kind.Outlined: canvas.DrawOutlined((WM.Geometry)c.Source!, c.Area, c.Brush!, c.Pen!, c.A.X, c.A.Y, c.X, c.Y); break;
                case Kind.Tint: canvas.DrawTinted((WMI.BitmapSource)c.Source!, System.Drawing.Color.FromArgb(c.Colour.A, c.Colour.R, c.Colour.G, c.Colour.B), c.Area); break;
                case Kind.Shadow: canvas.DrawShadowed((GameModels.Sprite)c.Source!, c.A, c.Opacity); break;
                case Kind.Sprite: canvas.DrawSprite((MapSprite)c.Source!, c.A); break;
                case Kind.Ground:
                    gpu!.DrawGround((MapShaders)c.Source!, c.Camera, c.Terrain); break;
                case Kind.Borders:
                    gpu!.DrawBorders((MapShaders)c.Source!, c.Camera, c.Owners, c.Counties, c.Palette, c.Terrain); break;
            }
    }
}
