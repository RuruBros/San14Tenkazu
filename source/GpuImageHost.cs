using W = System.Windows;
using WM = System.Windows.Media;
using WI = System.Windows.Interop;

namespace San14Tenkazu;

// Publish into a drawing visual so only its content changes when the next frame is ready.
// MapSurface owns the host's dimensions and the layout is independent of the shared surface.
internal sealed class GpuImageHost : W.FrameworkElement
{
    private readonly WM.DrawingVisual imageVisual = new();
    private WI.D3DImage? source;
    internal GpuImageHost() => AddVisualChild(imageVisual);

    protected override int VisualChildrenCount => 1;
    protected override WM.Visual GetVisualChild(int index) => index == 0 ? imageVisual : throw new ArgumentOutOfRangeException(nameof(index));
    internal WI.D3DImage? Source
    {
        get => source;
        set { if (!ReferenceEquals(source, value)) { source = value; DrawImage(); } }
    }

    internal int MeasurePasses { get; private set; }
    protected override W.Size MeasureOverride(W.Size availableSize)
    {
        MeasurePasses++;
        return base.MeasureOverride(availableSize);
    }

    protected override void OnRenderSizeChanged(W.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        DrawImage();
    }

    private void DrawImage()
    {
        using var dc = imageVisual.RenderOpen();
        if (source is not null) dc.DrawImage(source, new W.Rect(RenderSize));
    }
}
