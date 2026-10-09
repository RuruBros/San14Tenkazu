using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// What the map's labels and units are drawn with: the calls of WPF's DrawingContext that the map uses, so
// the same drawing code runs on the GPU renderer (D2DCanvas) or, where that is not available, on WPF.
internal interface IMapCanvas
{
    void DrawImage(WMI.BitmapSource image, W.Rect area);
    void DrawRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area);
    void DrawRoundedRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area, double radiusX, double radiusY);
    void DrawEllipse(WM.Brush? brush, WM.Pen? pen, W.Point centre, double radiusX, double radiusY);
    void DrawGeometry(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry);
    void DrawGeometryAt(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry, W.Point origin, double scale = 1);
    void DrawLine(WM.Pen pen, W.Point from, W.Point to);
    void DrawText(WM.FormattedText text, W.Point origin);
    void PushTransform(WM.Transform transform);
    void Pop();
}

// The UI recorder and the GPU executor expose the same extended operations.
internal interface IGpuMapCanvas : IMapCanvas
{
    void Clear(WM.Color colour);
    void PushMatrix(WM.Matrix matrix);
    void SetLayerImage(object layer, WMI.BitmapSource? image);
    void DrawBitmap(WMI.BitmapSource image, W.Rect area, double opacity, WM.BitmapScalingMode mode, bool snap = false, bool mayDefer = false);
    void FillRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area, double opacity);
    void DrawOutlined(WM.Geometry shape, W.Rect bounds, WM.Brush ink, WM.Pen halo, double x, double y, double scale, double pixelsPerDip);
    void DrawTinted(WMI.BitmapSource source, System.Drawing.Color colour, W.Rect area);
    void DrawShadowed(GameModels.Sprite sprite, W.Point origin, double opacity = 1);
    void DrawSprite(MapSprite sprite, W.Point origin);
}

// Captured on the UI thread; the renderer never accesses FormattedText or a WPF control.
internal sealed record MapTextRun(string Text, string Family, int Weight, int Style, int Stretch, double Size,
    WM.Brush Brush, double Width, double Height, double Baseline);

internal sealed record MapSprite(MapDrawList Drawing, W.Rect Bounds);

// What a FormattedText made for the map says, so the GPU renderer can set the same text with DirectWrite.
internal sealed record TextSpec(string Text, WM.Typeface Face, double Size, WM.Brush Brush);

internal static class MapText
{
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WM.FormattedText, TextSpec> Specs = new();
}

// The map drawn by WPF (the fallback): every call goes to the element's DrawingContext.
internal sealed class WpfCanvas(WM.DrawingContext dc) : IMapCanvas
{
    public void DrawImage(WMI.BitmapSource image, W.Rect area) => dc.DrawImage(image, area);
    public void DrawRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area) => dc.DrawRectangle(brush, pen, area);
    public void DrawRoundedRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area, double radiusX, double radiusY) => dc.DrawRoundedRectangle(brush, pen, area, radiusX, radiusY);
    public void DrawEllipse(WM.Brush? brush, WM.Pen? pen, W.Point centre, double radiusX, double radiusY) => dc.DrawEllipse(brush, pen, centre, radiusX, radiusY);
    public void DrawGeometry(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry) => dc.DrawGeometry(brush, pen, geometry);
    public void DrawGeometryAt(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry, W.Point origin, double scale = 1)
    {
        dc.PushTransform(new WM.MatrixTransform(scale, 0, 0, scale, origin.X, origin.Y));
        if (pen is not null && scale != 1) { pen = pen.Clone(); pen.Thickness /= scale; }
        dc.DrawGeometry(brush, pen, geometry);
        dc.Pop();
    }
    public void DrawLine(WM.Pen pen, W.Point from, W.Point to) => dc.DrawLine(pen, from, to);
    public void DrawText(WM.FormattedText text, W.Point origin) => dc.DrawText(text, origin);
    public void PushTransform(WM.Transform transform) => dc.PushTransform(transform);
    public void Pop() => dc.Pop();
}
