using System.Globalization;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// What the map's labels are drawn with: the fonts, brushes and pens, and the texts kept from frame to frame.
internal sealed partial class MapSurface
{
    private static readonly WM.FontFamily UiFont = new(Theme.UiFontName);
    // Overlays on the map (the chips at the top left, the hover card): black at about two thirds, with a faint edge.
    private static readonly WM.Brush HudBack = Solid(170, 6, 5, 4), HudEdge = Solid(55, 255, 255, 255);
    private static readonly WM.Brush HudInk = Solid(Theme.InkOnDark), HudOn = Solid(Theme.Gold), HudOnInk = Solid(Theme.Ink);
    private static readonly WM.Typeface NameFace = new(UiFont, W.FontStyles.Normal, W.FontWeights.Bold, W.FontStretches.Normal);
    // Map labels in a serif face like the game's, falling back to the UI font.
    private static readonly WM.Typeface LabelFace = new(new WM.FontFamily(Theme.MapFontName), W.FontStyles.Normal, W.FontWeights.Bold, W.FontStretches.Normal);
    // Numbers: Bahnschrift, with the UI font for anything it lacks (or on a Windows without it).
    private static readonly WM.FontFamily NumberFont = new($"{Theme.NumberFontName}, {Theme.UiFontName}");
    private static readonly WM.Typeface NumberFace = new(NumberFont, W.FontStyles.Normal, W.FontWeights.SemiBold, W.FontStretches.Normal);
    private static readonly WM.Brush CastleShadow = Solid(70, 0, 0, 0), Gate = Solid(120, 40, 30, 20);
    private static readonly WM.Brush SelectedInk = Solid(255, 200, 140), DarkInk = Solid(24, 20, 16);
    private static readonly WM.Brush LabelInk = Solid(255, 252, 240), TroopBar = Solid(255, 233, 160), BarTrack = Solid(150, 20, 16, 12),
        DurabilityBar = Solid(226, 222, 190);   // the game's walls bar: pale cream
    private static readonly WM.Pen Outline = Line(Solid(55, 45, 35), 1.2);
    private static readonly WM.Pen BannerBorderSelected = Line(Solid(MapView.Highlight), 1.8);
    private static readonly WM.Pen LabelHalo = RoundLine(Solid(215, 26, 20, 14), 2.8);
    private static readonly WM.Pen SelectedRing = Line(Solid(MapView.Highlight), 3), HoverRing = Line(Solid(160, 217, 72, 15), 2);
    // A banner's glow: lines on its outline, wider and fainter outwards (the selected city's stronger than the hovered one's).
    private static readonly System.Drawing.Color GlowColor = Shade.Light(MapView.Highlight, 0.6f);
    private static readonly WM.Pen[] SelectedGlow = Glow(GlowColor, 1), HoverGlow = Glow(GlowColor, 0.6);

    private double textDpi;

    // Cached text is drawn for one DPI; a move to another monitor starts over.
    private void CheckTextDpi(double pixelsPerDip)
    {
        if (pixelsPerDip == textDpi) return;
        marks.Clear();
        outlines.Clear();
        outlineSizes.Clear();
        labelImages.Clear();
        textDpi = pixelsPerDip;
    }

    // ---- Outlined labels -----------------------------------------------------------------------------

    // An outlined label: its glyphs, its size on screen, and the scale its glyphs are drawn at (1 unless it
    // stands in for another size).
    private readonly record struct Label(WM.Geometry Shape, double Width, double Height, W.Rect Bounds, double Scale = 1);

    private readonly Dictionary<(string Text, int Size, WM.Typeface Face), Label> outlines = [];
    // The sizes each text has been drawn at (outlines' Size), to stand in for a size not drawn yet.
    private readonly Dictionary<(string Text, WM.Typeface Face), List<int>> outlineSizes = [];
    // A frame makes only so many new outlined labels (each is a glyph outline drawn into a bitmap, about a
    // millisecond): while the map zooms, every size step would make all of them in one frame. The rest
    // stand in at the nearest size made, scaled, and are made over the next frames.
    private int labelBudget;
    private bool labelsPending;

    // Outlined labels come in whole DIP sizes: each size is drawn into a bitmap once, so fewer sizes mean
    // fewer labels to draw while the map zooms.
    private Label OutlinedText(string text, double size, double pixelsPerDip, WM.Typeface? face = null)
    {
        face ??= LabelFace;
        int rounded = (int)Math.Round(size) * 2;
        if (outlines.TryGetValue((text, rounded, face), out Label cached)) return cached;
        if (labelBudget <= 0)
        {
            labelsPending = true;
            if (outlineSizes.TryGetValue((text, face), out List<int>? sizes) && sizes.Count > 0)
            {
                int near = sizes.MinBy(made => Math.Abs(made - rounded));
                Label stand = outlines[(text, near, face)];
                double scale = (double)rounded / near;
                return stand with { Width = stand.Width * scale, Height = stand.Height * scale, Scale = scale };
            }
            // Never drawn yet: it keeps its place (about its size) and shows on a later frame.
            return new Label(WM.Geometry.Empty, text.Length * rounded / 2.0 * 0.9, rounded / 2.0 * 1.35, W.Rect.Empty);
        }
        labelBudget--;
        if (outlines.Count > 3000)
        {
            outlines.Clear();
            outlineSizes.Clear();
            labelImages.Clear();
        }
        var formatted = new WM.FormattedText(text, CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, face, rounded / 2.0, LabelInk, pixelsPerDip);
        WM.Geometry shape = formatted.BuildGeometry(new W.Point(0, 0));
        shape.Freeze();
        if (!outlineSizes.TryGetValue((text, face), out List<int>? made)) outlineSizes[(text, face)] = made = [];
        made.Add(rounded);
        return outlines[(text, rounded, face)] = new Label(shape, formatted.WidthIncludingTrailingWhitespace, formatted.Height, shape.Bounds);
    }

    // White text with a dark halo, readable on any ground. Glyph outlines with a halo are slow to draw anew
    // each frame (zooming redraws every label), so each label is drawn once into a bitmap at the screen's
    // resolution and then only copied, on whole device pixels so that it stays sharp.
    private readonly Dictionary<WM.Geometry, (WMI.BitmapSource Image, W.Point Offset)> labelImages = [];

    private void DrawOutlined(IMapCanvas dc, Label textLabel, double x, double y)
    {
        if (textLabel.Bounds.IsEmpty) return;
        WM.Geometry text = textLabel.Shape;
        double scale = textLabel.Scale;
        double pixelsPerDip = textDpi;
        if (dc is IGpuMapCanvas gpuCanvas)
        {
            gpuCanvas.DrawOutlined(text, textLabel.Bounds, LabelInk, LabelHalo, Snap(x, pixelsPerDip), Snap(y, pixelsPerDip), scale, pixelsPerDip);
            return;
        }
        if (!labelImages.TryGetValue(text, out var label))
        {
            if (labelImages.Count > 3000) labelImages.Clear();
            W.Rect bounds = textLabel.Bounds;
            bounds.Inflate(LabelHalo.Thickness / 2 + 1, LabelHalo.Thickness / 2 + 1);
            // The bitmap starts on a whole device pixel of the text's own coordinates.
            var offset = new W.Point(Math.Floor(bounds.X * pixelsPerDip) / pixelsPerDip, Math.Floor(bounds.Y * pixelsPerDip) / pixelsPerDip);
            int width = (int)Math.Ceiling((bounds.Right - offset.X) * pixelsPerDip), height = (int)Math.Ceiling((bounds.Bottom - offset.Y) * pixelsPerDip);
            var visual = new WM.DrawingVisual();
            using (WM.DrawingContext v = visual.RenderOpen())
            {
                v.PushTransform(new WM.TranslateTransform(-offset.X, -offset.Y));
                v.DrawGeometry(null, LabelHalo, text);
                v.DrawGeometry(LabelInk, null, text);
            }
            var bitmap = new WMI.RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96 * pixelsPerDip, 96 * pixelsPerDip, WM.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            labelImages[text] = label = (bitmap, offset);
        }
        dc.DrawImage(label.Image, new W.Rect(Snap(x, pixelsPerDip) + label.Offset.X * scale, Snap(y, pixelsPerDip) + label.Offset.Y * scale,
            label.Image.PixelWidth / pixelsPerDip * scale, label.Image.PixelHeight / pixelsPerDip * scale));
    }

    // ---- Short texts, pens and brushes ---------------------------------------------------------------

    private readonly Dictionary<(string Text, int Size, WM.Brush Brush, WM.Typeface Face), WM.FormattedText> marks = [];

    // Short labels on the map, cached by text, size (rounded to half DIPs), brush and face.
    private WM.FormattedText Mark(string text, double size, WM.Brush brush, double pixelsPerDip, WM.Typeface? face = null)
    {
        face ??= NameFace;
        int rounded = (int)Math.Round(size * 2);
        if (marks.TryGetValue((text, rounded, brush, face), out WM.FormattedText? formatted)) return formatted;
        if (marks.Count > 4000) marks.Clear();
        formatted = new WM.FormattedText(text, CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, face, rounded / 2.0, brush, pixelsPerDip);
        MapText.Specs.AddOrUpdate(formatted, new TextSpec(text, face, rounded / 2.0, brush));
        return marks[(text, rounded, brush, face)] = formatted;
    }

    private static void DrawCentred(IMapCanvas dc, WM.FormattedText text, W.Point p) =>
        dc.DrawText(text, new W.Point(p.X - text.Width / 2, p.Y - text.Height / 2));

    private readonly Dictionary<(System.Drawing.Color Color, double Width), WM.Pen> forcePens = [];

    private WM.Pen ForcePen(System.Drawing.Color color, double width)
    {
        double rounded = Math.Round(width * 2) / 2;
        if (forcePens.TryGetValue((color, rounded), out WM.Pen? pen)) return pen;
        return forcePens[(color, rounded)] = Line(Solid(color), rounded);
    }

    private readonly Dictionary<System.Drawing.Color, (WM.Brush Solid, WM.Brush Gradient)> forceBrushes = [];

    private (WM.Brush Solid, WM.Brush Gradient) ForceBrushes(System.Drawing.Color color)
    {
        if (forceBrushes.TryGetValue(color, out var brushes)) return brushes;
        var gradient = new WM.LinearGradientBrush(ToWpf(Shade.Light(color, 0.35f)), ToWpf(Shade.Dark(color, 0.05f)), 90);
        gradient.Freeze();
        return forceBrushes[color] = (Solid(color), gradient);
    }

    // An image as a brush at an opacity. Drawn with it, a shadow needs no layer of its own as with PushOpacity.
    private static readonly Dictionary<(WMI.BitmapSource Image, double Opacity), WM.ImageBrush> fadedImages = [];

    private static WM.ImageBrush Faded(WMI.BitmapSource image, double opacity)
    {
        if (fadedImages.TryGetValue((image, opacity), out WM.ImageBrush? brush)) return brush;
        brush = new WM.ImageBrush(image) { Opacity = opacity };
        WM.RenderOptions.SetBitmapScalingMode(brush, WM.BitmapScalingMode.HighQuality);
        brush.Freeze();
        return fadedImages[(image, opacity)] = brush;
    }

    private static WM.Color ToWpf(System.Drawing.Color c) => WM.Color.FromArgb(c.A, c.R, c.G, c.B);

    private static WM.Brush Solid(System.Drawing.Color c) => Solid(c.A, c.R, c.G, c.B);

    private static WM.Brush Solid(byte r, byte g, byte b) => Solid(255, r, g, b);

    private static WM.Brush Solid(byte a, byte r, byte g, byte b)
    {
        WM.Color color = WM.Color.FromArgb(a, r, g, b);
        if (DrawingResources.Brushes.TryGetValue(color, out WM.Brush? cached)) return cached;
        var brush = new WM.SolidColorBrush(color);
        brush.Freeze();
        return DrawingResources.Brushes[color] = brush;
    }

    private static class DrawingResources
    {
        internal static readonly Dictionary<WM.Color, WM.Brush> Brushes = [];
        internal static readonly Dictionary<(WM.Brush Brush, double Width, bool Round), WM.Pen> Pens = [];
    }

    private static WM.Pen RoundLine(WM.Brush brush, double width)
    {
        if (DrawingResources.Pens.TryGetValue((brush, width, true), out WM.Pen? cached)) return cached;
        var pen = new WM.Pen(brush, width) { LineJoin = WM.PenLineJoin.Round };
        pen.Freeze();
        if (DrawingResources.Pens.Count < 4096) DrawingResources.Pens[(brush, width, true)] = pen;
        return pen;
    }

    private static WM.Pen[] Glow(System.Drawing.Color c, double strength) =>
        [RoundLine(Solid((byte)(34 * strength), c.R, c.G, c.B), 13), RoundLine(Solid((byte)(56 * strength), c.R, c.G, c.B), 9),
            RoundLine(Solid((byte)(90 * strength), c.R, c.G, c.B), 5.5), RoundLine(Solid((byte)(150 * strength), c.R, c.G, c.B), 2.5)];

    private static WM.Pen Line(WM.Brush brush, double width)
    {
        if (DrawingResources.Pens.TryGetValue((brush, width, false), out WM.Pen? cached)) return cached;
        var pen = new WM.Pen(brush, width);
        pen.Freeze();
        if (DrawingResources.Pens.Count < 4096) DrawingResources.Pens[(brush, width, false)] = pen;
        return pen;
    }
}
