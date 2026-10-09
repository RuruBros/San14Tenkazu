using System.Drawing.Text;

namespace San14Tenkazu;

// The colours of the editor's buttons and tabs: every one is drawn from one recipe (a rounded plate with
// a gold edge, in the Theme's colours) by PlateButton, so that they look alike wherever they sit.
// The editor's icons, one name per meaning, so that a meaning looks the same on every button.
internal static class Icons
{
    internal const string Connect = LineIcons.Prefix + "plug", Reload = "000_cmn_icon_auto", Stop = "000_cmn_icon_advance_stop",
        Play = "000_cmn_icon_advance_play", Undo = LineIcons.Prefix + "undo", Lists = LineIcons.Prefix + "notebook",
        Close = "000_cmn_icon_close", Apply = LineIcons.Prefix + "check", Fill = LineIcons.Prefix + "fill", Revert = LineIcons.Prefix + "revert",
        Details = LineIcons.Prefix + "details", Heal = LineIcons.Prefix + "heal",
        Force = LineIcons.Prefix + "flag", City = "000_cmn_icon_kyoten", Officer = "000_cmn_icon_busyo", Unit = "000_cmn_icon_butai",
        Personality = LineIcons.Prefix + "sparkles", Item = LineIcons.Prefix + "gem", Ancient = LineIcons.Prefix + "scroll", Unlock = LineIcons.Prefix + "unlock",
        GoTo = LineIcons.Prefix + "locate", Ai = LineIcons.Prefix + "network", Calendar = LineIcons.Prefix + "calendar";
}

internal static class GameSkin
{
    private static FaceLibrary? art;
    private static readonly Dictionary<string, Bitmap?> icons = [];

    internal static event Action? Changed;

    internal static void Load(FaceLibrary library)
    {
        lock (icons)
        {
            art = library;
            icons.Clear();
        }
        Changed?.Invoke();
    }

    // Buttons' icons: the game's UI art (white pictograms such as 000_cmn_icon_close; null until the game's
    // art is loaded, or when it lacks the icon), or for "line:..." names the editor's own line icons.
    internal static Bitmap? Icon(string name)
    {
        lock (icons)
        {
            if (icons.TryGetValue(name, out Bitmap? known)) return known;
            if (name.StartsWith(LineIcons.Prefix)) return icons[name] = LineIcons.Render(name[LineIcons.Prefix.Length..]);
            if (art is null) return null;
            return icons[name] = art.Sprite(name);
        }
    }

    internal enum State { Normal, Hover, Pressed, Disabled }

    // The plate's gradient (top, bottom) and edge. Ordinary buttons are dark wood, the one main action of an area
    // red lacquer; both brighten on hover.
    internal static (Color Top, Color Bottom, Color Edge) PlateColors(State state, bool isPrimary)
    {
        (Color top, Color bottom) = (isPrimary, state) switch
        {
            (_, State.Disabled) => (Theme.WithAlpha(Theme.Wood, 150), Theme.WithAlpha(Theme.DarkWood, 150)),
            (true, State.Hover) => (Shade.Light(Theme.LacquerLight, 0.15f), Theme.LacquerLight),
            (true, State.Pressed) => (Theme.Lacquer, Theme.Lacquer),
            (true, _) => (Theme.LacquerLight, Theme.Lacquer),
            (false, State.Hover) => (Shade.Light(Theme.WoodLight, 0.15f), Theme.WoodLight),
            (false, State.Pressed) => (Theme.DarkWood, Theme.DarkWood),
            _ => (Theme.WoodLight, Theme.DarkWood)
        };
        Color edge = state switch { State.Disabled => Theme.WithAlpha(Theme.Gold, 70), State.Hover => Theme.GoldLight, _ => Theme.Gold };
        return (top, bottom, edge);
    }

    internal static Color TextColor(State state, bool isPrimary) =>
        state == State.Disabled ? Theme.WithAlpha(Theme.InkOnDark, 110) : isPrimary ? Color.White : Theme.InkOnDark;

    // Tabs share the buttons' plate; the selected one is gold with dark ink, like the map's mode buttons.
    internal static (Color Top, Color Bottom, Color Edge) TabColors => (Theme.GoldLight, Theme.Gold, Shade.Dark(Theme.Gold, 0.1f));
}
