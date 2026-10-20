using W = System.Windows;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// A force's mark beside its name, as the map's labels and the legend under the map mark a force: the game's shield
// (its 40x52 sprite) in the force's colour inside a thin dark rim, which keeps light colours apart from the paper.
// Without the game's art, a drawn shield in the sprite's place.
internal sealed class ForceShield : W.FrameworkElement
{
    private static readonly System.Drawing.Color Rim = Theme.DarkWood;
    private static readonly WM.Pen DrawnRim = Freeze(new WM.Pen(CardSkin.DarkWood, 1));
    private static readonly Dictionary<System.Drawing.Color, WM.Brush> Fills = [];
    // Shields drawn before the game's art was there, drawn again once it is (held weakly: without the game's art
    // they would pile up as the cards make new ones).
    private static readonly List<WeakReference<ForceShield>> waiting = [];

    static ForceShield() => MapArt.Changed += () => W.Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        WeakReference<ForceShield>[] shields = [.. waiting];
        waiting.Clear();
        foreach (WeakReference<ForceShield> shield in shields)
            if (shield.TryGetTarget(out ForceShield? alive))
            {
                alive.waitingForArt = false;
                alive.InvalidateVisual();
            }
    });

    internal ForceShield() => WM.RenderOptions.SetBitmapScalingMode(this, WM.BitmapScalingMode.HighQuality);

    internal System.Drawing.Color Colour
    {
        get => colour;
        set
        {
            if (colour == value) return;
            colour = value;
            InvalidateVisual();
        }
    }
    private System.Drawing.Color colour = System.Drawing.Color.Gray;
    private bool waitingForArt;

    protected override void OnRender(WM.DrawingContext dc)
    {
        if (Draw(dc, new W.Rect(0, 0, ActualWidth, ActualHeight), colour)) return;
        if (waitingForArt) return;
        if (waiting.Count >= 64) waiting.RemoveAll(shield => !shield.TryGetTarget(out _));
        waiting.Add(new WeakReference<ForceShield>(this));
        waitingForArt = true;
    }

    // The shield over an area as tall as the sprite is (40 wide to 52 high). False when it is the drawn one.
    internal static bool Draw(WM.DrawingContext dc, W.Rect area, System.Drawing.Color colour)
    {
        if (MapArt.Get(MapArt.ShieldOutline, Rim) is { } rim && MapArt.Get(MapArt.Shield, colour) is { } art)
        {
            dc.DrawImage(rim, area);
            dc.DrawImage(art, area);
            return true;
        }
        // Where the sprite's glyph is: from y 6, its sides straight down to y 38, its point at y 45, x 5~35.
        double X(double x) => area.X + area.Width * x / 40;
        double Y(double y) => area.Y + area.Height * y / 52;
        var shield = new WM.StreamGeometry();
        using (WM.StreamGeometryContext g = shield.Open())
        {
            g.BeginFigure(new W.Point(X(5), Y(6)), true, true);
            g.PolyLineTo([new W.Point(X(35), Y(6)), new W.Point(X(35), Y(38)), new W.Point(X(20), Y(45)), new W.Point(X(5), Y(38))], true, true);
        }
        shield.Freeze();
        if (!Fills.TryGetValue(colour, out WM.Brush? fill)) Fills[colour] = fill = CardSkin.Brush(colour);
        dc.DrawGeometry(fill, DrawnRim, shield);
        return false;
    }

    private static WM.Pen Freeze(WM.Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
