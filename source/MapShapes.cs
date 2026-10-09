using W = System.Windows;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// Shapes in local coordinates: moving and zooming reuse their paths, with screen-space stroke widths.
internal sealed class MapShapeCache<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, (WM.Geometry Shape, long Used)> shapes = [];
    private long use;
    internal bool TryGet(TKey key, out WM.Geometry shape)
    {
        if (shapes.TryGetValue(key, out var entry))
        {
            shapes[key] = (entry.Shape, ++use); shape = entry.Shape; return true;
        }
        shape = null!; return false;
    }

    internal WM.Geometry Add(TKey key, WM.Geometry shape)
    {
        if (shapes.Count >= 2048)
            foreach (TKey old in shapes.OrderBy(p => p.Value.Used).Take(256).Select(p => p.Key).ToArray()) shapes.Remove(old);
        shape.Freeze(); shapes[key] = (shape, ++use); return shape;
    }

    internal WM.Geometry Get(TKey key, Func<WM.Geometry> make) => TryGet(key, out var shape) ? shape : Add(key, make());

}

internal sealed partial class MapSurface
{
    private readonly MapShapeCache<long> troopArcs = new();
    private static readonly MapShapeCache<double> shieldShapes = new();
    private static readonly MapShapeCache<(double Width, double Height)> plates = new();
    private static readonly MapShapeCache<(double PlateHeight, double Left, double Top, double Right, double ShieldRight, double Bottom)> bannerOutlines = new();
    private static readonly WM.Geometry PavilionShape = MakePavilion();
    private static readonly WM.Geometry CastleShape = MakeCastle();

    private GameSnapshot? cityOrderData;
    private int cityOrderSelected;
    private int[] cityOrder = [];
    private int[] CityOrder()
    {
        if (cityOrder.Length == 0 || !ReferenceEquals(cityOrderData, snapshot) || cityOrderSelected != selectedCity)
        {
            cityOrderData = snapshot;
            cityOrderSelected = selectedCity;
            cityOrder = Enumerable.Range(1, GameMemory.CityCount).OrderBy(id => id == selectedCity ? 0 : 1)
                .ThenByDescending(id => snapshot?.Cities[id][CityFields.Scale] ?? 0).ToArray();
        }
        return cityOrder;
    }

    private static WM.Geometry Polygon(params W.Point[] points)
    {
        var shape = new WM.StreamGeometry();
        using (WM.StreamGeometryContext g = shape.Open())
        {
            g.BeginFigure(points[0], true, true);
            g.PolyLineTo(points[1..], true, true);
        }
        return shape;
    }

    private static WM.Geometry MakeCastle()
    {
        var shape = new WM.GeometryGroup { FillRule = WM.FillRule.Nonzero };
        shape.Children.Add(new WM.RectangleGeometry(new W.Rect(-0.5, -0.38, 1, 0.78)));
        for (int i = 0; i < 3; i++) shape.Children.Add(new WM.RectangleGeometry(new W.Rect(-0.5 + i * 0.4, -0.56, 0.2, 0.18)));
        shape.Freeze();
        return shape;
    }

    private static WM.Geometry MakePavilion()
    {
        var shape = new WM.StreamGeometry();
        using (WM.StreamGeometryContext g = shape.Open())
        {
            g.BeginFigure(new W.Point(-7.5, -1.5), true, true);
            g.PolyLineTo([new(-5.2, -3.4), new(-3.4, -6.5), new(3.4, -6.5), new(5.2, -3.4), new(7.5, -1.5), new(4.8, -2.3), new(-4.8, -2.3)], true, true);
            g.BeginFigure(new W.Point(-4.4, -2.3), true, true);
            g.PolyLineTo([new(-2.8, -2.3), new(-2.8, 4), new(-4.4, 4)], true, true);
            g.BeginFigure(new W.Point(2.8, -2.3), true, true);
            g.PolyLineTo([new(4.4, -2.3), new(4.4, 4), new(2.8, 4)], true, true);
            g.BeginFigure(new W.Point(-6, 4), true, true);
            g.PolyLineTo([new(6, 4), new(6, 6), new(-6, 6)], true, true);
        }
        shape.Freeze();
        return shape;
    }
}
