using W = System.Windows;
using WM = System.Windows.Media;
using WI = System.Windows.Interop;

namespace San14Tenkazu;

// MapSurface owns explicit pixel/DPI dimensions. Changing the completed image redraws this
// element without Image.Source's AffectsMeasure invalidating the editor layout every frame.
internal sealed class GpuImageHost : W.FrameworkElement
{
    private WI.D3DImage? source;
    internal WI.D3DImage? Source
    {
        get => source;
        set { if (ReferenceEquals(source, value)) return; source = value; InvalidateVisual(); }
    }

    internal int MeasurePasses { get; private set; }
    protected override W.Size MeasureOverride(W.Size availableSize)
    {
        MeasurePasses++;
        return base.MeasureOverride(availableSize);
    }

    protected override void OnRender(WM.DrawingContext dc)
    {
        if (source is not null) dc.DrawImage(source, new W.Rect(RenderSize));
    }
}
