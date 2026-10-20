using System.Globalization;
using W = System.Windows;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// The cities and the other places on the map: icons, banners and names, placed so that they do not
// cover one another.
internal sealed partial class MapSurface
{
    private readonly Dictionary<int, W.Rect> bannerRects = [];   // as last drawn, so a click on a banner picks its city

    private static readonly System.Drawing.Color PlayerOutline = System.Drawing.Color.FromArgb(0xFF, 0x63, 0x42), PaleOutline = System.Drawing.Color.FromArgb(238, 230, 214);
    private static readonly System.Drawing.Color IconInk = System.Drawing.Color.FromArgb(255, 232, 150);
    // The game's plates are see-through: the ground shows under a name or a figure (about two thirds dark).
    private static readonly WM.Brush IconBack = Solid(170, 24, 20, 14), PlateBack = Solid(168, 32, 27, 18);
    private static readonly WM.Brush PavilionFill = Solid(232, 160, 58);
    // Icons of what belongs to no force (the scenario's own walls, a scenic spot in no one's land).
    private static readonly System.Drawing.Color NeutralIcon = System.Drawing.Color.FromArgb(226, 220, 204), NeutralRim = System.Drawing.Color.FromArgb(70, 62, 50);
    // Facilities' names show from this zoom (their icons from CountyLabelZoom, as the counties').
    private const double FacilityLabelZoom = 4;
    private const double CountyLabelZoom = 3;       // county names appear from this zoom on
    private const double CityTroopsZoom = 1.8;      // and the troops under each city banner
    private const double ModelShowWidth = 26;       // DIPs from which a city's model replaces its icon

    private sealed class CityLayer(MapSurface map) : LabelLayer(map)
    {
        protected override void Draw(MapSurface map, IMapCanvas dc) => map.DrawCities(dc);
        internal override void Redraw()
        {
            Map.InvalidateCityCommands();
            base.Redraw();
        }
    }

    // Labels claim their place on screen in order of importance: the selected city, the other cities (big ones
    // first), the tribes' bases and the gates, then county names and landmarks. The city under the pointer takes
    // no other place than it had, so the map holds still under the hover card. Icons and
    // models claim theirs before any label. A label that would cover one already placed tries its other
    // side; a lesser label with no room is left out (its icon or model stays, and the hover card still names
    // it), while a city always keeps its banner. Units' tokens only push labels aside: a label whose every
    // place has units on it still shows, in the place they cover least (they move every read, and a gate's
    // name should not come and go with them). Everything is drawn least important first, so that the
    // important sits on top.
    private readonly MapAreas claimed = new();
    private readonly List<Action> iconDraws = [], labelDraws = [];
    private readonly MapAreas unitAreas = new();   // units' tokens on screen

    // Free of every claimed area but the base's own icon (a banner may touch the base it names), and of the
    // units' tokens unless they are let in.
    private bool IsFree(W.Rect area, W.Rect own, bool overUnits = false)
    {
        area.Inflate(-1.5, -1.5);
        if (area.IsEmpty) return true;
        foreach (W.Rect taken in claimed.Near(area))
            if (taken != own && taken.IntersectsWith(area)) return false;
        if (!overUnits)
            foreach (W.Rect token in unitAreas.Near(area))
                if (token.IntersectsWith(area)) return false;
        return true;
    }

    // The first free choice; when units take them all, the one they cover least of those free of labels and
    // icons; when none is, null (the label is left out) or, for a label that must show, the choice that
    // covers the least of the others.
    private W.Rect? Claim(bool always, W.Rect own, params W.Rect[] choices)
    {
        foreach (W.Rect choice in choices)
            if (IsFree(choice, own)) return Take(choice);
        var underUnits = choices.Where(choice => IsFree(choice, own, overUnits: true)).ToList();
        if (underUnits.Count > 0) return Take(underUnits.MinBy(choice => Covered(choice, own, unitAreas)));
        if (!always) return null;
        return Take(choices.MinBy(choice => Covered(choice, own, claimed) + Covered(choice, own, unitAreas)));

        W.Rect Take(W.Rect area)
        {
            claimed.Add(area);
            return area;
        }
    }

    private static double Covered(W.Rect area, W.Rect own, MapAreas taken)
    {
        double covered = 0;
        foreach (W.Rect other in taken.Near(area))
            if (other != own && W.Rect.Intersect(other, area) is { IsEmpty: false } both) covered += both.Width * both.Height;
        return covered;
    }

    // An icon or model keeps labels off it; a model's corners are empty, so a little less of its area.
    // Returns the area claimed.
    private W.Rect ClaimIcon(W.Rect area, bool model)
    {
        if (model) area.Inflate(-area.Width * 0.15, -area.Height * 0.15);
        if (!area.IsEmpty) claimed.Add(area);
        return area;
    }

    // Where a base's icon or model is on screen: its centre, the edges a banner beside it keeps to, and the
    // area its icon claimed (which its own banner may touch).
    private readonly record struct Anchor(W.Point Point, double Top, double Bottom, double Left, double Right, W.Rect Own)
    {
        internal Anchor(W.Point point, W.Rect icon, W.Rect own) : this(point, icon.Top, icon.Bottom, icon.Left, icon.Right, own) { }
    }

    // A tribe's base or a gate (by its base ID), with its banner still to place.
    private sealed record BasePlace(int Id, string Name, Anchor At, int Force, System.Drawing.Color Owner, long? Troops, bool Gate, W.Rect Icon, string Spot,
        double? Durability = null);

    private void DrawCities(IMapCanvas dc)
    {
        double pixelsPerDip = WM.VisualTreeHelper.GetDpi(cities).PixelsPerDip;
        CheckTextDpi(pixelsPerDip);
        bannerRects.Clear();
        spots.Clear();
        claimed.Clear();
        unitAreas.Clear();
        labelBudget = looping ? 6 : 24;
        labelsPending = false;
        // Units' tokens are drawn over the labels, so the labels keep clear of them where they can: a city with
        // units on it takes its banner below or beside itself, and county and landmark names step aside. Where
        // the units are going counts (a unit glides to its new hex after each read, while the labels stay put).
        double r = UnitRadius * 0.9;
        foreach (UnitRecord unit in ShownUnits())
            if (glides.TryGetValue((unit.Slot, unit.Commander), out Glide? glide) && ToScreen(glide.To) is var p &&
                p.X > -40 - LabelPad && p.Y > -40 - LabelPad && p.X < ActualWidth + 40 + LabelPad && p.Y < ActualHeight + 40 + LabelPad)
                unitAreas.Add(new W.Rect(p.X - r, p.Y - r, r * 2, r * 2));
        iconDraws.Clear();
        labelDraws.Clear();
        int[] order = CityOrder();
        var cityIcons = order.Select(id => (Id: id, At: CityIcon(dc, id))).ToList();
        List<BasePlace> bases = BaseIcons(dc);
        foreach ((int id, Anchor? at) in cityIcons)
            if (at is { } anchor) PlaceCityBanner(dc, id, anchor, pixelsPerDip);
        foreach (BasePlace place in bases) PlaceBaseBanner(dc, place, pixelsPerDip);
        PlaceCounties(dc, pixelsPerDip);
        for (int i = iconDraws.Count - 1; i >= 0; i--) iconDraws[i]();
        for (int i = labelDraws.Count - 1; i >= 0; i--) labelDraws[i]();
        // The hit test looks from the end: the important spots last.
        spots.Reverse();
        if (labelsPending) Dispatcher.BeginInvoke(cities.Redraw, WT.DispatcherPriority.Background);
    }

    // A city's icon (or its model, close up). The selected or hovered city is marked on its banner.
    private Anchor? CityIcon(IMapCanvas dc, int id)
    {
        W.Point p = CityOnScreen(id);
        if (p.X < -80 - LabelPad || p.Y < -40 - LabelPad || p.X > ActualWidth + 80 + LabelPad || p.Y > ActualHeight + 60 + LabelPad) return null;
        CityRecord? city = snapshot?.Cities[id];
        double size = CastleSize(city?[CityFields.Scale] ?? 1);
        System.Drawing.Color owner = CityOwner(id);
        // Close up, the game's model stands for the city; the icon only while the model is too small to see.
        W.Rect? model = ModelOnScreen(MapTerrain.CityTiles[id], ModelShowWidth);
        W.Rect area = model ?? CastleArea(p, size);
        W.Rect own = ClaimIcon(area, model is not null);
        if (model is null) iconDraws.Add(() => DrawCastle(dc, p, size, owner));
        return new Anchor(p, model is { } m ? Inner(m) : area, own);
    }

    // A model's sprite has empty margins: banners keep to the inner part.
    private static W.Rect Inner(W.Rect model) => new(model.X + model.Width * 0.15, model.Y + 4, model.Width * 0.7, Math.Max(0, model.Height - 8));

    private System.Drawing.Color CityOwner(int id) =>
        snapshot is { } data ? MapView.ForceColor(data.ForceOfCity(id)) : System.Drawing.Color.FromArgb(150, 150, 150);

    private double CastleSize(long scale) => (7 + 1.8 * Math.Clamp(scale, 1, 4)) * (1 + 0.12 * (zoom - 1));

    // The part of the screen DrawCastle covers.
    private static W.Rect CastleArea(W.Point p, double size)
    {
        if (MapArt.Has(MapArt.City))
        {
            double side = size * 1.6;
            return new W.Rect(p.X - side / 2, p.Y - side * 0.47, side, side * 0.92);
        }
        double top = p.Y - size / 2 + size * 0.12 - size / 5 * 0.9;
        return new W.Rect(p.X - size / 2, top, size, p.Y - size / 2 + size * 0.9 - top);
    }

    // A city's banner above its icon, or below, right or left of it when that place is taken.
    private void PlaceCityBanner(IMapCanvas dc, int id, Anchor at, double pixelsPerDip)
    {
        CityRecord? city = snapshot?.Cities[id];
        int force = snapshot is { } data && city is not null ? data.ForceOfCity(id) : 0;
        System.Drawing.Color owner = CityOwner(id);
        long scale = city?[CityFields.Scale] ?? 1;
        (int Gold, int Food, int Troops, int Durability)? caps = CityFields.Caps(scale);
        double? ratio = city is not null && caps is { } c ? city[CityFields.Troops] / (double)c.Troops : null;
        bool selected = id == selectedCity;
        double? walls = city is not null && zoom >= CityTroopsZoom && snapshot!.CastleByCity.TryGetValue(id, out BuildingRecord? castle)
            && caps is { } full ? castle[CityFields.Durability] / (double)full.Durability : null;
        Banner banner = MeasureBanner(CityNames.ById[id], force, owner, city is not null && zoom >= CityTroopsZoom ? city[CityFields.Troops] : null,
            selected, gate: false, pixelsPerDip, walls);
        W.Rect area = Claim(true, at.Own, banner.Places(at))!.Value;
        bannerRects[id] = area;
        WM.Pen[]? glow = selected ? SelectedGlow : id == hoverCity ? HoverGlow : null;
        labelDraws.Add(() => DrawBanner(dc, banner, area, force, owner, ratio, selected, gate: false, glow));
    }

    // Gates and the tribes' bases, which the game labels like its cities: their icons (or models) now,
    // their banners once the cities have theirs.
    private List<BasePlace> BaseIcons(IMapCanvas dc)
    {
        var places = new List<BasePlace>();
        if (snapshot is not { } data) return places;
        bool troops = zoom >= CityTroopsZoom;
        foreach (CityRecord tribe in data.Tribes)
        {
            if (!data.CastleByCity.TryGetValue(tribe.CityId, out BuildingRecord? castle)) continue;
            int tile = castle.Tile;
            if (tile >= GameMemory.TileCount || !OnScreen(tile, out W.Point p, 80, 80)) continue;
            int force = data.ForceOfBase(tribe.Corps, tile);
            System.Drawing.Color owner = MapView.ForceColor(force);
            double size = CastleSize(3);
            W.Rect? model = ModelOnScreen(tile, ModelShowWidth);
            W.Rect area = model ?? CastleArea(p, size);
            W.Rect own = ClaimIcon(area, model is not null);
            if (model is null) iconDraws.Add(() => DrawCastle(dc, p, size, owner));
            var at = new Anchor(p, model is { } m ? Inner(m) : area, own);
            places.Add(new BasePlace(tribe.CityId, tribe.Name, at, force, owner, troops ? tribe[CityFields.Troops] : null, false, area,
                L.Map.TribeTip(tribe.Name, data.ForceName(force), tribe[CityFields.Troops], tribe[CityFields.Gold]),
                troops && CityFields.Caps(tribe[CityFields.Scale]) is { } caps ? castle[CityFields.Durability] / (double)caps.Durability : null));
        }
        foreach (GateRecord gate in data.Gates)
        {
            if (gate.Tile < 0 || !OnScreen(gate.Tile, out W.Point p, 80, 80)) continue;
            int force = data.ForceOfGate(gate);
            System.Drawing.Color owner = MapView.ForceColor(force);
            W.Rect area;
            Anchor at;
            // The gate itself: its model close up, otherwise the game's gate icon in the owner's colour with a soft shadow.
            if (ModelOnScreen(gate.Tile, ModelShowWidth * 1.5) is { } model)
            {
                area = model;
                at = new Anchor(p, p.Y - 6, p.Y + 8, p.X - 8, p.X + 8, ClaimIcon(model, model: true));
            }
            else
            {
                double side = (12 + 2.4 * 3) * (1 + 0.12 * (zoom - 1));
                area = new W.Rect(p.X - side / 2, p.Y - side * 0.55, side, side);
                W.Rect own = ClaimIcon(area, model: false);
                if (MapArt.Has(MapArt.GateIcon) && MapArt.Get(MapArt.GateIcon, System.Drawing.Color.Black) is { } shadow)
                {
                    W.Rect iconArea = area;
                    iconDraws.Add(() =>
                    {
                        WM.Brush shade = Faded(shadow, 0.4);
                        dc.DrawRectangle(shade, null, new W.Rect(iconArea.X + 1.2, iconArea.Y + 1.6, side, side));
                        dc.DrawRectangle(shade, null, new W.Rect(iconArea.X - 0.8, iconArea.Y - 0.8, side + 1.6, side + 1.6));
                        MapArt.Draw(dc, MapArt.GateIcon, owner, iconArea);
                    });
                }
                at = new Anchor(p, area, own);
            }
            long gateBase = gate[GateFields.BaseDurability];
            places.Add(new BasePlace(gate.Id, gate.Name, at, force, owner, troops ? gate[GateFields.Troops] : null, true, area,
                L.Map.GateTip(gate.Name, data.ForceName(force), gate[GateFields.Troops], gate[GateFields.Morale], gate.Durability) +
                (gateBase > 0 ? $" / {gateBase:N0}" : ""),
                troops && gateBase > 0 ? gate.Durability / (double)gateBase : null));
        }
        return places;
    }

    // A gate's or tribe's banner above it, below, right or left of it, or left out when all are taken. A gate opens its
    // card when pressed, so it is marked as a city is while its card is open or the pointer is on it.
    private void PlaceBaseBanner(IMapCanvas dc, BasePlace place, double pixelsPerDip)
    {
        bool selected = place.Gate && place.Id == selectedGate;
        Banner banner = MeasureBanner(place.Name, place.Force, place.Owner, place.Troops, selected, place.Gate, pixelsPerDip, place.Durability);
        W.Rect spot = place.Icon;
        if (Claim(selected, place.At.Own, banner.Places(place.At)) is { } area)
        {
            WM.Pen[]? glow = selected ? SelectedGlow : place.Gate && place.Id == hoverGate ? HoverGlow : null;
            labelDraws.Add(() => DrawBanner(dc, banner, area, place.Force, place.Owner, null, selected, place.Gate, glow));
            spot.Union(area);
        }
        spots.Add((spot, place.Spot, place.Gate ? place.Id : 0, null));
    }

    // County names beside a small shield in the owner's colour, as the game labels its settlements (a city's
    // own county is labelled by the city), and landmarks with a pavilion; their names go right of the mark,
    // or left of it when that side is taken. Built facilities and ports are icons only.
    private void PlaceCounties(IMapCanvas dc, double pixelsPerDip)
    {
        if (snapshot is not { } data || zoom < CountyLabelZoom) return;
        var seen = new HashSet<int>();
        double s = Math.Min(2, 1 + 0.09 * (zoom - CountyLabelZoom));
        // Their names hardly grow: close up the place names stay small, as the game's do (the user found them
        // too big), and always smaller than a city's name on its banner, which keeps one size.
        double t = Math.Min(1.1, 1 + 0.045 * (zoom - CountyLabelZoom));
        const double placeName = 9;      // landmarks and facilities: one size
        foreach (int tile in MapTerrain.SeatTiles)
        {
            if (data.County(data.TileCounties[tile]) is not { } county || !seen.Add(county.Index)) continue;
            string name = county.Name;
            if (name.Length == 0 || name == CityNames.ById[county.HomeCity]) continue;
            if (!OnScreen(tile, out W.Point p, 20, 80)) continue;
            if (ModelOnScreen(tile, 14) is { } village) p = new W.Point(p.X, village.Top - 4 * s);
            int owner = data.TileOwners[tile];
            var label = OutlinedText(name, 9 * t, pixelsPerDip);
            var shield = new W.Rect(p.X - 5 * s, p.Y - 6.7 * s, 10 * s, 13 * s);
            var right = new W.Rect(p.X + 7 * s, p.Y - label.Height / 2, label.Width, label.Height);
            var left = new W.Rect(p.X - 7 * s - label.Width, p.Y - label.Height / 2, label.Width, label.Height);
            if (Claim(false, W.Rect.Empty, W.Rect.Union(shield, right), W.Rect.Union(shield, left)) is not { } area) continue;
            double x = area.Right > shield.Right ? right.X : left.X;
            labelDraws.Add(() =>
            {
                DrawShield(dc, shield, MapView.ForceColor(owner), OutlineOf(owner));
                DrawOutlined(dc, label, x, right.Y);
            });
        }
        // 명소: a pavilion and the name, as the game marks them.
        foreach ((int id, int tile) in data.Landmarks)
        {
            if (!OnScreen(tile, out W.Point p, 20, 120)) continue;
            int owner = data.TileOwners[tile];
            var mark = new W.Rect(p.X - 7.5 * s, p.Y - 7.5 * s, 15 * s, 15 * s);
            W.Rect spot = mark;
            System.Drawing.Color colour = owner > 0 ? MapView.ForceColor(owner) : NeutralIcon, rim = owner > 0 ? OutlineOf(owner) : NeutralRim;
            iconDraws.Add(() =>
            {
                if (!DrawIcon(dc, mark, MapArt.Scenic, MapArt.ScenicOutline, colour, rim)) DrawPavilion(dc, p, s);
            });
            var label = OutlinedText(CityNames.Landmarks[id], placeName, pixelsPerDip);
            var right = new W.Rect(p.X + 8.5 * s, p.Y - label.Height / 2, label.Width, label.Height);
            var left = new W.Rect(p.X - 8.5 * s - label.Width, p.Y - label.Height / 2, label.Width, label.Height);
            if (Claim(false, W.Rect.Empty, right, left) is { } area)
            {
                labelDraws.Add(() => DrawOutlined(dc, label, area.X, area.Y));
                spot.Union(area);
            }
            spots.Add((spot, L.Map.LandmarkTip(CityNames.Landmarks[id]) + (owner > 0 ? (" · " + L.Map.Territory(data.ForceName(owner))) : ""), 0, null));
        }
        // Facilities forces built, as the game marks them: its icon in the builder's colour on top of the model
        // with the name beside it; walls (토성·석벽·철쇄) by their name alone. Unlike the game, which hides other
        // forces' traps, every trap shows (the user's choice: an editor shows what is there).
        foreach (Facility facility in data.Facilities)
        {
            if (!OnScreen(facility.Tile, out W.Point p, 20, 20)) continue;
            MapArt.FacilityType? type = MapArt.Type(facility.Kind);
            // Close up, the game's model stands there and the icon sits on top of it.
            W.Rect? model = ModelOnScreen(facility.Tile, 18);
            if (model is { } m) p = new W.Point(p.X, m.Top + 2 * s);
            var area = new W.Rect(p.X - 8 * s, p.Y - 8 * s, 16 * s, 16 * s);
            WM.Pen[]? glow = selectedFacility?.Matches(facility) == true ? SelectedGlow : hoverFacility?.Matches(facility) == true ? HoverGlow : null;
            if (glow is not null)
                foreach (WM.Pen pen in glow) dc.DrawEllipse(null, pen, p, 10 * s, 10 * s);
            bool icon = type?.Kind != MapArt.FacilityKind.Wall;
            System.Drawing.Color colour = facility.Force > 0 ? MapView.ForceColor(facility.Force) : NeutralIcon;
            System.Drawing.Color edge = facility.Force > 0 ? OutlineOf(facility.Force) : NeutralRim;
            if (icon)
                iconDraws.Add(() =>
                {
                    if (MapArt.Facility(facility.Kind) is not { } art || !DrawIcon(dc, area, art.Base, art.Outline, colour, edge))
                    {
                        W.Rect inner = area;
                        inner.Inflate(-3 * s, -3 * s);
                        dc.DrawRectangle(ForceBrushes(colour).Solid, Outline, inner);
                    }
                    if (!facility.Built)
                        MapArt.Draw(dc, MapArt.Build, IconInk, new W.Rect(area.Right - 6 * s, area.Bottom - 6 * s, 9 * s, 9 * s));
                });
            W.Rect spot = icon ? area : model ?? area;
            if (zoom >= FacilityLabelZoom)
            {
                string name = MapArt.FacilityName(facility.Kind) + (facility.Built ? "" : (" " + L.Map.BuildingNote));
                var label = OutlinedText(name, placeName, pixelsPerDip);
                W.Rect[] places = icon
                    ? [new(area.Right + 1, p.Y - label.Height / 2, label.Width, label.Height), new(area.Left - 1 - label.Width, p.Y - label.Height / 2, label.Width, label.Height)]
                    : [new(p.X - label.Width / 2, p.Y - label.Height, label.Width, label.Height)];
                if (Claim(false, icon ? area : W.Rect.Empty, places) is { } at)
                {
                    labelDraws.Add(() => DrawOutlined(dc, label, at.X, at.Y));
                    spot.Union(at);
                }
            }
            string durability = type is { } full ? L.Map.DurabilityOf(facility.Durability, full.Durability) : L.Map.Durability(facility.Durability);
            spots.Add((model is { } shown ? W.Rect.Union(spot, shown) : spot,
                $"{MapArt.FacilityName(facility.Kind)} · {data.ForceName(facility.Force)} · {durability}" + (facility.Built ? "" : (" · " + L.Map.Building)),
                0, facility.Building is not null ? FacilityTarget.Of(facility) : null));
        }
        foreach (int tile in MapTerrain.PortTiles)
        {
            if (!OnScreen(tile, out W.Point p, 20, 20) || ModelOnScreen(tile, 14) is not null || !MapArt.Has(MapArt.Port)) continue;
            iconDraws.Add(() =>
            {
                dc.DrawEllipse(IconBack, null, p, 8 * s, 8 * s);
                MapArt.Draw(dc, MapArt.Port, System.Drawing.Color.White, new W.Rect(p.X - 6 * s, p.Y - 6 * s, 12 * s, 12 * s));
            });
        }
    }

    private bool OnScreen(int tile, out W.Point p, double left, double right)
    {
        p = TilePoint(tile);
        return p.X >= -left - LabelPad && p.Y >= -20 - LabelPad && p.X <= ActualWidth + right + LabelPad && p.Y <= ActualHeight + 20 + LabelPad;
    }

    // The game rings the player's own force's shields in orange-red.
    private System.Drawing.Color OutlineOf(int force)
    {
        if (snapshot is { PlayerForce: > 0 } data && force == data.PlayerForce) return PlayerOutline;
        // Every other force's shields and flags have the game's pale rim, light colours too (진, 사마).
        return PaleOutline;
    }

    // Dark text on light force colours (the game pairs each colour with a light or dark ink too).
    private static WM.Brush InkOn(System.Drawing.Color color) =>
        0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 165 ? DarkInk : LabelInk;

    // The game's shield sprite (outline behind, fill in the force's colour), or a drawn shield without the game.
    // The game's rim is about a pixel wide on its map; the outline sprites reach 3 px (of 40) past their glyph,
    // so they are drawn that much smaller, less RimWidth (margin x and y are the sprite's, as fractions).
    private const double RimWidth = 1.1;

    private static W.Rect ThinRim(W.Rect area, double marginX, double marginY)
    {
        area.Inflate(-Math.Max(0, area.Width * marginX - RimWidth), -Math.Max(0, area.Height * marginY - RimWidth));
        return area;
    }

    private static void DrawShield(IMapCanvas dc, W.Rect area, System.Drawing.Color fill, System.Drawing.Color edge)
    {
        if (MapArt.Has(MapArt.ShieldOutline) && MapArt.Has(MapArt.Shield))
        {
            MapArt.Draw(dc, MapArt.ShieldOutline, edge, ThinRim(area, 3 / 40.0, 3 / 52.0));
            MapArt.Draw(dc, MapArt.Shield, fill, area);
            return;
        }
        area.Inflate(-area.Width * 0.1, -area.Height * 0.08);
        double aspect = area.Height / area.Width;
        if (!shieldShapes.TryGet(aspect, out WM.Geometry shield))
            shield = shieldShapes.Add(aspect, Polygon(new(0, 0), new(1, 0), new(1, aspect * 0.72), new(0.5, aspect), new(0, aspect * 0.72)));
        dc.DrawGeometryAt(Solid(fill), Line(Solid(edge), 1.6), shield, area.TopLeft, area.Width);
    }

    // A game icon: its outline in the rim colour behind its glyph in the fill colour. Without an outline
    // sprite the glyph itself, a little larger, is the rim. False when the art is not loaded.
    private static bool DrawIcon(IMapCanvas dc, W.Rect area, string glyph, string? outline, System.Drawing.Color fill, System.Drawing.Color rim)
    {
        if (!MapArt.Has(glyph)) return false;
        if (outline is null || !MapArt.Draw(dc, outline, rim, area))
        {
            W.Rect around = area;
            around.Inflate(area.Width * 0.08, area.Height * 0.08);
            MapArt.Draw(dc, glyph, rim, around);
        }
        MapArt.Draw(dc, glyph, fill, area);
        return true;
    }

    // The game's city icon in the owner's colour, or without the game's art a small castle with three
    // merlons.
    private void DrawCastle(IMapCanvas dc, W.Point p, double size, System.Drawing.Color owner)
    {
        if (MapArt.Has(MapArt.City) && MapArt.Get(MapArt.City, System.Drawing.Color.Black) is { } shadow)
        {
            double side = size * 1.6;
            var area = new W.Rect(p.X - side / 2, p.Y - side * 0.55, side, side);
            dc.DrawRectangle(Faded(shadow, 0.45), null, new W.Rect(area.X + 1.2, area.Y + 1.6, side, side));
            MapArt.Draw(dc, MapArt.City, owner, area);
            return;
        }
        var body = new W.Rect(p.X - size / 2, p.Y - size / 2 + size * 0.12, size, size * 0.78);
        dc.DrawGeometryAt(CastleShadow, null, CastleShape, new W.Point(p.X + 1.2, p.Y + 1.5), size);
        dc.DrawGeometryAt(ForceBrushes(owner).Gradient, Outline, CastleShape, p, size);
        dc.DrawRectangle(Gate, null, new W.Rect(p.X - size * 0.12, body.Bottom - size * 0.32, size * 0.24, size * 0.32));
    }

    // Banner over a base, as in the game: the force's mark on a shield of its colour (a gate's flag for a
    // gate), the name on a see-through plate with a pointed end, exactly as tall as the shield's straight
    // sides (its point hangs below), and, when troops are given, a second row: the soldier over the shield's
    // lower right corner, the troops, and a bar for the ratio (troops against the cap). Measured first so it
    // can be placed.
    // Durability: the walls' share left (a city's against its scale's cap, a gate's against its base durability),
    // shown as a bar above the name as in the game.
    // One size at every zoom (the user did not want banners larger than they are on the whole map), and
    // small enough to sit with the units' shields and troops (the user found the first size too big
    // beside the rest of the map). The troops are a size larger than the mark: the user wanted the map's
    // figures larger by one.
    private sealed record Banner(WM.FormattedText Name, WM.FormattedText? Mark, Label? Troops,
        double PlateHeight, double ShieldWidth, double ShieldHeight, double Width, double Height,
        double? Durability = null, bool Gate = false)
    {
        internal const double NameSize = 10.5, MarkSize = 10, TroopSize = 11, MinShieldWidth = 18;
        internal const double SoldierSize = 12.5, BarThickness = 2.2;
        internal const double NameGap = 4;     // from the shield to the name

        // The bar's height and the gap under it (none without a bar).
        internal double BarHeight => Durability is null ? 0 : 3;

        // Where the shield is seen and the plate's top (they line up).
        internal double PlateTop => 2 + (Durability is null ? 0 : BarHeight + 2);
        internal ShieldShape Shape => ShieldShape.Of(Gate);
        internal double ShieldTop => PlateTop - (Shape.Top * ShieldHeight - RimWidth);   // the sprite's frame
        internal double ShieldLeft => Shape.Left * ShieldWidth - RimWidth;              // the shield seen, from the frame's left
        internal double ShieldRight => Shape.Right * ShieldWidth + RimWidth;
        internal double PlateLeft => ShieldRight - 6;                          // the plate starts under the shield

        internal double PlateWidth => Name.WidthIncludingTrailingWhitespace + 6 + NameGap + 3 + PlateHeight / 2;

        // The troops' row when the banner's top left is (left, top), as the game lays it out: under the plate,
        // the soldier over the shield's lower right corner, then the figures, and one bar under both.
        internal W.Rect TroopArea(double left, double top)
        {
            if (Troops is not { } label) return W.Rect.Empty;
            double icon = SoldierSize;
            return new W.Rect(left + ShieldRight - icon * 0.55, top + PlateTop + PlateHeight + 1, icon * 1.05 + label.Width + 1, Math.Max(icon, label.Height) + BarThickness + 1);
        }

        // Above the base (the usual place), below it, right of it, left of it.
        internal W.Rect[] Places(Anchor at) =>
        [
            new(at.Point.X - Width / 2, at.Top - 2 - Height, Width, Height), new(at.Point.X - Width / 2, at.Bottom + 2, Width, Height),
            new(at.Right + 2, at.Point.Y - Height / 2, Width, Height), new(at.Left - 2 - Width, at.Point.Y - Height / 2, Width, Height)
        ];
    }

    // Where the game's shield glyphs show inside their frames (measured from the sprites, as fractions; the
    // thin rim, RimWidth, is around them): the shield (40x52) from y 6, its sides straight down to y 38, its
    // point at y 45, x 5~35; the gate's flag (40x40) from y 4 to y 36, x 5~35. Width over height: the frame's.
    private readonly record struct ShieldShape(double Top, double Sides, double Bottom, double Left, double Right, double Aspect)
    {
        private static readonly ShieldShape Shield = new(6 / 52.0, 38 / 52.0, 45 / 52.0, 5 / 40.0, 35 / 40.0, 52 / 40.0);
        private static readonly ShieldShape GateFlag = new(4 / 40.0, 36 / 40.0, 36 / 40.0, 5 / 40.0, 35 / 40.0, 1);

        internal static ShieldShape Of(bool gate) => gate ? GateFlag : Shield;
    }

    // A banner over a base: always the game's two rows (name, then troops). Where units stand on the usual
    // place above the base, the whole banner takes another side (Claim keeps it off their tokens).
    private Banner MeasureBanner(string title, int force, System.Drawing.Color owner, long? troopCount, bool selected, bool gate, double pixelsPerDip,
        double? durability)
    {
        string mark = force > 0 && !gate && snapshot is { } data ? data.ForceMark(force) : "";
        WM.FormattedText name = Mark(title, Banner.NameSize, selected ? SelectedInk : LabelInk, pixelsPerDip, LabelFace);
        WM.FormattedText? markText = mark.Length == 0 ? null : Mark(mark, Banner.MarkSize, InkOn(owner), pixelsPerDip, LabelFace);
        ShieldShape shape = ShieldShape.Of(gate);
        // The plate is as tall as the shield's straight sides; a name too tall for them makes the shield larger.
        double sides = shape.Sides - shape.Top;
        double shieldWidth = Math.Max(Math.Max(Banner.MinShieldWidth, (markText?.WidthIncludingTrailingWhitespace ?? 0) + 8),
            (Math.Ceiling(name.Height) + 1 - RimWidth) / (sides * shape.Aspect));
        double shieldHeight = shieldWidth * shape.Aspect, plateHeight = sides * shieldHeight + RimWidth;
        // The game writes troops without separators (17772).
        Label? troops = troopCount is { } count
            ? OutlinedText(count.ToString(CultureInfo.InvariantCulture), Banner.TroopSize, pixelsPerDip, NumberFace) : null;
        var banner = new Banner(name, markText, troops, plateHeight, shieldWidth, shieldHeight, 0, 0, durability, gate);
        double bottom = Math.Max(banner.ShieldTop + shape.Bottom * shieldHeight, banner.PlateTop + plateHeight);
        if (troops is not null) bottom = Math.Max(bottom, banner.TroopArea(0, 0).Bottom + 1);
        double right = Math.Max(banner.PlateLeft + banner.PlateWidth, troops is null ? 0 : banner.TroopArea(0, 0).Right + 1);
        return banner with { Width = right, Height = bottom + 1 };
    }

    private void DrawBanner(IMapCanvas dc, Banner banner, W.Rect area, int force, System.Drawing.Color owner, double? ratio, bool selected, bool gate,
        WM.Pen[]? glow = null)
    {
        double left = area.X, top = area.Y, plateHeight = banner.PlateHeight, shieldWidth = banner.ShieldWidth, shieldHeight = banner.ShieldHeight;
        double plateLeft = left + banner.PlateLeft, plateTop = top + banner.PlateTop, plateRight = plateLeft + banner.PlateWidth;
        // The glow of the selected or hovered city, under everything: round the banner's outline (the shield's
        // left side and point, the plate's top, end and bottom).
        if (glow is not null)
        {
            double shieldLeft = banner.ShieldLeft, shieldRight = banner.ShieldRight, plateBottom = banner.PlateTop + plateHeight;
            double shieldBottom = banner.ShieldTop + banner.Shape.Bottom * shieldHeight + RimWidth;
            double right = banner.PlateLeft + banner.PlateWidth;
            var key = (plateHeight, shieldLeft, banner.PlateTop, right, shieldRight, shieldBottom);
            WM.Geometry outline = bannerOutlines.Get(key, () => Polygon(new(shieldLeft, banner.PlateTop), new(right - plateHeight / 2, banner.PlateTop),
                new(right, banner.PlateTop + plateHeight / 2), new(right - plateHeight / 2, plateBottom), new(shieldRight, plateBottom),
                new((shieldLeft + shieldRight) / 2, shieldBottom), new(shieldLeft, plateBottom)));
            foreach (WM.Pen pen in glow) dc.DrawGeometryAt(null, pen, outline, area.TopLeft);
        }
        // The walls' bar over the shield and the plate, from the shield's left edge to the plate's point.
        if (banner.Durability is { } walls)
        {
            double barLeft = left + banner.ShieldLeft, barWidth = plateRight - plateHeight / 2 - barLeft, barTop = top + 1;
            dc.DrawRectangle(BarTrack, null, new W.Rect(barLeft, barTop, barWidth, banner.BarHeight));
            dc.DrawRectangle(DurabilityBar, null, new W.Rect(barLeft, barTop, barWidth * Math.Clamp(walls, 0, 1), banner.BarHeight));
        }
        WM.Geometry plate = plates.Get((banner.PlateWidth, plateHeight), () => Polygon(new(0, 0), new(banner.PlateWidth - plateHeight / 2, 0),
            new(banner.PlateWidth, plateHeight / 2), new(banner.PlateWidth - plateHeight / 2, plateHeight), new(0, plateHeight)));
        dc.DrawGeometryAt(PlateBack, selected ? BannerBorderSelected : null, plate, new W.Point(plateLeft, plateTop));
        dc.DrawText(banner.Name, new W.Point(left + banner.ShieldRight + Banner.NameGap, plateTop + (plateHeight - banner.Name.Height) / 2));
        var shieldArea = new W.Rect(left, top + banner.ShieldTop, shieldWidth, shieldHeight);
        // A gate's flag, as the game draws it: a thin square in the rim colour (it also shows through the
        // gateway and the battlements) behind the gate in the force's colour, both one shield wide.
        if (gate && MapArt.Has(MapArt.GateOutline) && MapArt.Has(MapArt.GateFlag))
        {
            MapArt.Draw(dc, MapArt.GateOutline, OutlineOf(force), ThinRim(shieldArea, 2 / 40.0, 2 / 40.0));
            MapArt.Draw(dc, MapArt.GateFlag, owner, shieldArea);
        }
        else DrawShield(dc, shieldArea, owner, OutlineOf(force));
        // The mark in the middle of the shield's straight part, level with the name.
        if (banner.Mark is { } markText) DrawCentred(dc, markText, new W.Point(left + shieldWidth / 2, plateTop + plateHeight / 2));

        if (banner.Troops is { } label)
        {
            // The troops' row as the game draws it: the soldier over the shield's lower right corner, the
            // figures after it, and one bar under both (troops against the cap).
            W.Rect row = banner.TroopArea(left, top);
            double iconSize = Banner.SoldierSize, textLeft = row.X + iconSize * 1.05;
            double textTop = row.Y + (Math.Max(iconSize, label.Height) - label.Height) / 2;
            MapArt.Draw(dc, MapArt.Soldier, IconInk, new W.Rect(row.X, row.Y + (Math.Max(iconSize, label.Height) - iconSize) / 2, iconSize, iconSize));
            DrawOutlined(dc, label, textLeft, textTop);
            if (ratio is { } share)
            {
                var bar = new W.Rect(row.X + iconSize * 0.2, row.Bottom - Banner.BarThickness - 0.5, textLeft + label.Width - row.X - iconSize * 0.2, Banner.BarThickness);
                dc.DrawRectangle(BarTrack, null, bar);
                dc.DrawRectangle(TroopBar, null, new W.Rect(bar.X, bar.Y, bar.Width * Math.Clamp(share, 0, 1), bar.Height));
            }
        }
    }

    // A pavilion: an upswept roof on two pillars, in gold, as the game's landmark icon.
    private static void DrawPavilion(IMapCanvas dc, W.Point p, double s)
    {
        dc.DrawGeometryAt(CastleShadow, null, PavilionShape, new W.Point(p.X + 0.8, p.Y + 1), s);
        dc.DrawGeometryAt(PavilionFill, Outline, PavilionShape, p, s);
    }
}
