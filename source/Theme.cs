namespace San14Tenkazu;

// The editor's one palette and fonts, after the game's own UI: dark lacquered wood with gold trim for
// bars and anything laid over the map, parchment for the pages and tables, ink for text. Every control,
// the map overlays and the officer card take their colours from here.
internal static class Theme
{
    // Dark surfaces: the toolbar, the log, the map legend, map overlays, title plates.
    internal static readonly Color Lacquer = Color.FromArgb(120, 30, 24);      // red lacquer: city banner, title plates, primary buttons
    internal static readonly Color DarkWood = Color.FromArgb(46, 34, 24);
    internal static readonly Color Wood = Color.FromArgb(94, 70, 48);          // table headers
    internal static readonly Color Gold = Color.FromArgb(214, 170, 90);        // trim and rules on dark surfaces
    internal static readonly Color InkOnDark = Color.FromArgb(242, 230, 204);  // text on dark surfaces
    // Top of the button and tab gradients (GameSkin): each fades down to DarkWood, Lacquer or Gold.
    internal static readonly Color WoodLight = Color.FromArgb(84, 64, 46), LacquerLight = Color.FromArgb(158, 48, 36), GoldLight = Color.FromArgb(244, 210, 132);

    // Light surfaces: pages, tables, inputs.
    internal static readonly Color Parchment = Color.FromArgb(250, 246, 236);  // page background
    internal static readonly Color Paper = Color.FromArgb(252, 250, 244);      // tables, inputs
    internal static readonly Color PaperShade = Color.FromArgb(246, 240, 226); // alternate rows, card shading
    internal static readonly Color Rule = Color.FromArgb(214, 200, 170);       // grid lines and thin borders
    internal static readonly Color Ink = Color.FromArgb(52, 38, 24);
    internal static readonly Color InkMuted = Color.FromArgb(128, 110, 88);    // hints, counts, secondary text

    // States.
    internal static readonly Color Highlight = Color.FromArgb(217, 72, 15);    // the selected city or unit, values being edited
    internal static readonly Color Pending = Color.FromArgb(255, 236, 153);    // cells waiting for [변경 적용]
    internal static readonly Color Selection = Color.FromArgb(222, 200, 160);  // selected table cell
    internal static readonly Color Good = Color.FromArgb(86, 150, 70), Fair = Color.FromArgb(214, 160, 50), Poor = Color.FromArgb(196, 88, 64);

    internal const string UiFontName = "Malgun Gothic";
    // Names on the map: a clean sans-serif face, crisp at small sizes.
    internal const string MapFontName = "Malgun Gothic";
    // Numbers on the map (troops, counts, the date): narrow, square figures of even width, as in games, in a
    // face every Windows 10 and 11 has. It has no Hangul, so text with Hangul falls back to the UI font.
    internal const string NumberFontName = "Bahnschrift";

    // Bar colour for a value as a share of its maximum (abilities, loyalty, order, development, morale).
    internal static Color BarColor(double ratio) => ratio >= 0.8 ? Good : ratio >= 0.5 ? Fair : Poor;

    // Ability, loyalty, order and development values get a bar proportional to the value, in the lists and cards.
    internal static double? BarMaximum(FieldDef field) =>
        OfficerFields.Abilities.Contains(field) || field == OfficerFields.Loyalty || field == CountyFields.PublicOrder ? 100
        : CountyFields.Development.Contains(field) ? 1000
        : field == UnitFields.Morale ? UnitFields.MoraleCap : null;

    internal static Color WithAlpha(Color color, int alpha) => Color.FromArgb(alpha, color);
}
