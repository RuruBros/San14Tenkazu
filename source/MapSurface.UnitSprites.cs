using W = System.Windows;
using WM = System.Windows.Media;

namespace San14Tenkazu;

internal sealed partial class MapSurface
{
    private GameSnapshot? surnameSnapshot;
    private readonly Dictionary<int, string> unitSurnames = [];
    private readonly Dictionary<long, string> troopTexts = [];

    private string UnitSurname(GameSnapshot data, OfficerRecord officer)
    {
        if (!ReferenceEquals(surnameSnapshot, data)) { unitSurnames.Clear(); surnameSnapshot = data; }
        if (!unitSurnames.TryGetValue(officer.Id, out string? surname))
        {
            surname = GameMemory.ReadName(officer.Data, OfficerFields.SurnameOffset, 7);
            unitSurnames[officer.Id] = surname = surname.Length > 0 ? surname[..1] : "";
        }
        return surname;
    }

    private string TroopText(long troops)
    {
        if (troopTexts.TryGetValue(troops, out string? text)) return text;
        if (troopTexts.Count >= 4096) troopTexts.Clear();
        return troopTexts[troops] = troops.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private readonly record struct PortraitStyle(WM.Brush Fill, bool Face, double Radius, double Dpi,
        System.Drawing.Color RingColor, long Troops);
    private readonly Dictionary<(int Slot, WM.Brush? Fallback), (PortraitStyle Style, MapSprite Sprite)> portraitSprites = [];

    private MapSprite UnitPortraitSprite(int slot, WM.Brush fill, bool face, double radius, double pixelsPerDip,
        System.Drawing.Color color, long troops)
    {
        // A slot retains only its latest vector drawing. A new count, portrait, radius or DPI replaces it.
        var style = new PortraitStyle(fill, face, radius, pixelsPerDip, color, face ? troops : 0);
        var key = face ? (slot, (WM.Brush?)null) : (-1, fill);
        if (portraitSprites.TryGetValue(key, out var cached) && cached.Style == style) return cached.Sprite;
        var drawing = new MapDrawList(8);
        DrawPortraitBase(drawing, fill, face, "", default, radius, pixelsPerDip);
        if (face) DrawTroopRing(drawing, color, troops, default, radius);
        double reach = radius + UnitRing / 2 + 0.75 + (UnitGlow.Length - 1) * 1.3 + 2;
        var sprite = new MapSprite(drawing, new W.Rect(-reach, -reach, reach * 2, reach * 2));
        if (portraitSprites.Count >= 1024 && !portraitSprites.ContainsKey(key)) portraitSprites.Clear();
        portraitSprites[key] = (style, sprite);
        return sprite;
    }

    private void DrawPortraitBase(IMapCanvas dc, WM.Brush fill, bool face, string surname, W.Point p, double r, double pixelsPerDip)
    {
        for (int i = 0; i < UnitGlow.Length; i++)
            dc.DrawEllipse(null, UnitGlow[i], p, r + UnitRing / 2 + 0.75 + i * 1.3, r + UnitRing / 2 + 0.75 + i * 1.3);
        if (face) dc.DrawEllipse(PortraitBack, null, p, r, r);
        dc.DrawEllipse(fill, null, p, r, r);
        if (!face && surname.Length > 0)
            DrawCentred(dc, Mark(surname, r * 1.05, WM.Brushes.White, pixelsPerDip), p);
    }
}
