using W = System.Windows;
using WM = System.Windows.Media;

namespace San14Tenkazu;

internal sealed partial class MapSurface
{
    private UnitRecord[] layoutOrder = [];
    private readonly List<(int Slot, int Commander, int Tile)> layoutInputs = [];
    private readonly List<int> layoutGroups = [], layoutSources = [];
    private GameSnapshot? shieldSnapshot;
    private (int, int)? layoutSelected;
    private readonly List<int> layoutVisible = [], unitDrawOrder = [];
    private readonly List<List<UnitRecord>> spareStacks = [];
    private readonly List<(int Force, W.Point Token)> shieldForces = [];
    private readonly List<(int Token, int Choice, double Width, double Height, System.Drawing.Color Color, int Force, WM.FormattedText Text)> shieldDraws = [];
    private readonly Dictionary<int, W.Rect> shieldBanners = [];
    private readonly Dictionary<int, string> unitForceMarks = [];
    private GameSnapshot? markSnapshot;
    private double layoutScale, layoutRadius, layoutWidth, layoutHeight;
    private bool layoutSteady;
    private int unitLayoutInvalidation;
    private (int, int)? layoutHover;
    private W.Point shieldCenter;
    private double shieldScale, shieldDpi;

    private bool UnitVisible(W.Point p) => p.X >= -40 - LabelPad && p.Y >= -40 - LabelPad
        && p.X <= ActualWidth + 40 + LabelPad && p.Y <= ActualHeight + 40 + LabelPad;

    private bool PrepareUnitLayout(double radius)
    {
        UnitRecord[] order = UnitOrder();
        bool steady = !AnyGliding(Now);
        unitLayoutInvalidation = (layoutSteady ? 0 : 1) | (steady ? 0 : 2)
            | (SameLayoutInputs(order) ? 0 : 4) | (layoutScale == Scale ? 0 : 8)
            | (layoutRadius == radius ? 0 : 16) | (layoutWidth == ActualWidth && layoutHeight == ActualHeight ? 0 : 32);
        bool reused = unitLayoutInvalidation == 0;
        if (reused)
        {
            int visible = 0;
            for (int i = 0; i < order.Length; i++)
                if (UnitVisible(UnitPoint(order[i])))
                {
                    if (visible >= layoutVisible.Count || layoutVisible[visible++] != i) { reused = false; break; }
                }
            reused &= visible == layoutVisible.Count;
            if (!reused) unitLayoutInvalidation |= 64;
        }
        if (reused)
        {
            // Reproject from the original world position, avoiding accumulated translation roundoff.
            for (int i = 0; i < unitTokens.Count; i++)
            {
                var token = unitTokens[i];
                UnitRecord current = order[layoutSources[i]];
                unitTokens[i] = (current, UnitPoint(current), radius, token.Others);
            }
            if (!ReferenceEquals(layoutOrder, order))
            {
                foreach (var token in unitTokens) token.Others.Clear();
                foreach (int source in layoutVisible)
                {
                    int group = layoutGroups[source];
                    if (layoutSources[group] != source) unitTokens[group].Others.Add(order[source]);
                }
            }
        }
        else
        {
            foreach (var token in unitTokens) { token.Others.Clear(); spareStacks.Add(token.Others); }
            unitTokens.Clear(); layoutVisible.Clear(); layoutInputs.Clear(); layoutGroups.Clear(); layoutSources.Clear();
            tokenPoints.Clear(radius * 1.2);
            for (int i = 0; i < order.Length; i++)
            {
                UnitRecord unit = order[i];
                layoutInputs.Add((unit.Slot, unit.Commander, unit.Tile)); layoutGroups.Add(-1);
                W.Point p = UnitPoint(unit);
                if (!UnitVisible(p)) continue;
                layoutVisible.Add(i);
                int near = tokenPoints.FirstNear(p);
                if (near >= 0) { layoutGroups[i] = near; unitTokens[near].Others.Add(unit); }
                else
                {
                    List<UnitRecord> others;
                    if (spareStacks.Count == 0) others = [];
                    else { others = spareStacks[^1]; spareStacks.RemoveAt(spareStacks.Count - 1); }
                    layoutGroups[i] = unitTokens.Count; layoutSources.Add(i);
                    tokenPoints.Add(p, unitTokens.Count);
                    unitTokens.Add((unit, p, radius, others));
                }
            }
        }
        if (!reused || layoutHover != hoverUnit || layoutSelected != selectedUnit)
        {
            unitDrawOrder.Clear();
            for (int i = 0; i < unitTokens.Count; i++) unitDrawOrder.Add(i);
            unitDrawOrder.Sort(CompareUnitDrawOrder);
        }
        layoutOrder = order; layoutScale = Scale; layoutRadius = radius;
        layoutWidth = ActualWidth; layoutHeight = ActualHeight; layoutSteady = steady; layoutHover = hoverUnit; layoutSelected = selectedUnit;
        return reused;
    }

    private bool SameLayoutInputs(UnitRecord[] order)
    {
        if (layoutInputs.Count != order.Length) return false;
        for (int i = 0; i < order.Length; i++)
            if (layoutInputs[i] != (order[i].Slot, order[i].Commander, (int)order[i].Tile)) return false;
        return true;
    }

    private int CompareUnitDrawOrder(int a, int b)
    {
        int rank = UnitRank(unitTokens[b].Unit).CompareTo(UnitRank(unitTokens[a].Unit));
        if (rank != 0) return rank;
        int y = unitTokens[a].Point.Y.CompareTo(unitTokens[b].Point.Y);
        return y != 0 ? y : a.CompareTo(b); // Preserve stable ordering for equal keys.
    }

    private string UnitForceMark(GameSnapshot data, int force)
    {
        if (!ReferenceEquals(markSnapshot, data)) { unitForceMarks.Clear(); markSnapshot = data; }
        if (!unitForceMarks.TryGetValue(force, out string? mark)) unitForceMarks[force] = mark = data.ForceMark(force);
        return mark;
    }

    private bool ShieldAlreadyNear(int force, W.Point p, double distance)
    {
        foreach (var shield in shieldForces)
            if (shield.Force == force && (shield.Token - p).Length < distance) return true;
        return false;
    }

    private void SaveShieldLayout()
    {
        shieldCenter = center; shieldScale = Scale; shieldDpi = textDpi; shieldSnapshot = snapshot;
        shieldBanners.Clear();
        foreach (var pair in bannerRects) shieldBanners.Add(pair.Key, pair.Value);
    }

    private bool ReuseShields(IMapCanvas dc)
    {
        if (!ReferenceEquals(shieldSnapshot, snapshot) || shieldScale != Scale || shieldDpi != textDpi || shieldBanners.Count != bannerRects.Count) return false;
        W.Vector delta = (shieldCenter - center) * Scale;
        foreach (var pair in bannerRects)
        {
            if (!shieldBanners.TryGetValue(pair.Key, out W.Rect before)) return false;
            before.Offset(delta.X, delta.Y);
            // Only camera translation may differ, within floating-point projection roundoff.
            if (before.Width != pair.Value.Width || before.Height != pair.Value.Height
                || Math.Abs(before.X - pair.Value.X) > 1e-9 || Math.Abs(before.Y - pair.Value.Y) > 1e-9) return false;
        }
        foreach (var shield in shieldDraws)
        {
            var token = unitTokens[shield.Token];
            W.Rect at = TokenPlaces.AtChoice(new(token.Point, token.Radius), shield.Width, shield.Height, shield.Choice);
            DrawShield(dc, at, shield.Color, OutlineOf(shield.Force));
            DrawCentred(dc, shield.Text, new W.Point(at.X + at.Width / 2, at.Y + at.Height * 0.42));
        }
        return true;
    }
}
