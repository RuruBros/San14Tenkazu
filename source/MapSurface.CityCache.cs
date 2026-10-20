using W = System.Windows;
using WM = System.Windows.Media;

namespace San14Tenkazu;

internal sealed partial class MapSurface
{
    private readonly MapDrawList cityCommands = new(1024);
    private bool cityCommandsValid;
    private (W.Point Center, double Zoom, double Scale, double Width, double Height, double Dpi, double TextDpi,
        UnitMode Mode, int Force) cityCommandsView;
    internal int CityLayoutBuilds { get; private set; }
    internal int CityLayoutReuses { get; private set; }

    private void InvalidateCityCommands() => cityCommandsValid = false;

    // GPU frames still composite every layer. Retain CPU layout and frozen drawing commands when
    // only the units' interpolated positions change: city obstacles use glide.To, not Position().
    // The hit rectangles and tooltip spots belong to that same retained layout.
    private void DrawCitiesCached(IGpuMapCanvas canvas)
    {
        if (canvas is not MapDrawList frame)
        {
            DrawCities(canvas);
            return;
        }
        // Retain the render target DPI as well as the text layer DPI used for glyphs and snapping.
        var view = (center, zoom, Scale, ActualWidth, ActualHeight,
            WM.VisualTreeHelper.GetDpi(this).PixelsPerDip,
            WM.VisualTreeHelper.GetDpi(cities).PixelsPerDip, unitMode, highlightForce);
        if (!cityCommandsValid || cityCommandsView != view)
        {
            cityCommandsValid = false;
            cityCommands.Reset();
            DrawCities(cityCommands);
            cityCommandsView = view;
            // A temporary glyph/size must finish loading before it can become a retained frame.
            cityCommandsValid = !labelsPending;
            CityLayoutBuilds++;
        }
        else CityLayoutReuses++;
        cityCommands.AppendTo(frame);
    }
}
