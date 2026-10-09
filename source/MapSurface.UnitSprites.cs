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

    private readonly Dictionary<(WM.Brush Fill, bool Face, double Dpi), (double Radius, MapSprite Sprite)> portraitSprites = [];

    private MapSprite UnitPortraitSprite(WM.Brush fill, bool face, double radius, double pixelsPerDip)
    {
        var key = (fill, face, pixelsPerDip);
        if (portraitSprites.TryGetValue(key, out var cached) && cached.Radius == radius) return cached.Sprite;
        var drawing = new MapDrawList(8);
        DrawPortraitBase(drawing, fill, face, "", default, radius, pixelsPerDip);
        double reach = radius + UnitRing / 2 + 0.75 + (UnitGlow.Length - 1) * 1.3 + 2;
        var sprite = new MapSprite(drawing, new W.Rect(-reach, -reach, reach * 2, reach * 2));
        if (portraitSprites.Count >= 1024) portraitSprites.Clear();
        portraitSprites[key] = (radius, sprite);
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
