using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// The gate's editor card, in two pages: 관문 (its 태수, troops, morale and durability, with a fill that mends its
// durability) and 소속 장수 (its officers in a table for each status, as on the city card; a row opens his own card).
// The force and the 태수 named under the title open their cards too. A gate no force holds offers [소속 주기] beside its
// 태수, as a city does.
internal sealed class GateEditorCard : EditorCard
{
    // Everything the card shows for a gate. Values holds GateFields.All (the durability is missing when the gate's
    // building was not found). Officers are by status (OfficerTables.RowOf). Governors are the officers who may govern
    // it (its force's, in the gate now); a gate whose 태수 is its ruler or a 군단장 keeps him (GovernorFixed).
    internal sealed record View(int Gate, string Name, int Force, string ForceName, System.Drawing.Color Colour, int Governor, string GovernorName,
        IReadOnlyDictionary<FieldDef, long> Values, IReadOnlyList<(string Status, GameTable.Row Officer)> Officers,
        IReadOnlyList<GameName> Governors, bool GovernorFixed);


    private readonly Dictionary<FieldDef, NumberField> fields = GateFields.All.ToDictionary(f => f, f => new NumberField(f, 110));
    private readonly OfficerTables officers = new(L.City.NoOfficers);
    private readonly NameChoice governor = new(170);
    private readonly WC.TextBlock governorNote = new() { Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 200 };
    private readonly PlateButton give;
    private int pending;

    // The gate being edited (0 when none).
    internal int Gate { get; private set; }

    internal override EditorTarget? Target => Gate == 0 ? null : new GateTarget(Gate);

    // The officers' portraits, by face number (null until decoded: RefreshFaces).
    internal Func<int, WM.Brush?>? Faces
    {
        set => officers.Faces = value;
    }

    internal override int Pending => pending;

    // [소속 주기] was pressed: the window gives the gate to a force.
    internal event Action? GiveClicked;

    internal GateEditorCard()
    {
        give = Shortcut(L.Give.Button, L.Give.ButtonTip, () => GiveClicked?.Invoke(), Icons.Force);
        give.Margin = new W.Thickness(8, 2, 0, 2);
        OffersGoTo();
        AddPage(L.Gate.GatePage, GatePage());
        AddPage(L.City.OfficersPage, officers);
        foreach (NumberField field in fields.Values) field.Edited += Mark;
        governor.Box.SelectionChanged += (_, _) => Mark();
        officers.OfficerPicked += officer => RaiseLink(new OfficerTarget(officer));
    }

    private W.UIElement GatePage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(Line(L.City.Governor, governor.Box, governorNote, give));
        governor.Box.ToolTip = L.Gate.GovernorTip;
        foreach (FieldDef field in GateFields.All)
            page.Children.Add(Line(field.Label, fields[field].Box, new WC.TextBlock { Text = field.Hint, Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 200 }));
        var shortcuts = new WC.WrapPanel { Margin = new W.Thickness(0, 6, 0, 0) };
        shortcuts.Children.Add(Shortcut(L.Gate.Mend, L.Gate.MendTip, Mend));
        shortcuts.Children.Add(Shortcut(L.Card.RevertInput, L.Card.RevertInputTip, Revert, Icons.Revert));
        page.Children.Add(shortcuts);
        return page;
    }

    // The 소속 장수 page's tables (their fills are written by the window).
    internal OfficerTables Officers => officers;

    // The fills write at once (after asking): only while connected and not busy.
    internal bool FillsEnabled
    {
        set => officers.FillsEnabled = value;
    }

    // Portraits decoded since the tables were drawn.
    internal void RefreshFaces() => officers.RefreshFaces();

    internal void Open(View view)
    {
        if (view.Gate != Gate) StartOver();
        Gate = view.Gate;
        Title.Text = view.Name;
        Shield.Colour = view.Colour;
        ShowSummary(view);
        foreach ((FieldDef field, NumberField box) in fields)
        {
            bool known = view.Values.TryGetValue(field, out long value);
            box.Box.IsEnabled = known;
            box.Show(known ? value : 0);
        }
        governor.SetNames(view.Governors);
        governor.Show(view.Governor);
        governor.Box.IsEnabled = !view.GovernorFixed && view.Force > 0;
        governorNote.Text = view.GovernorFixed ? L.Gate.GuardedByLord : "";
        give.Visibility = view.Force == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        officers.Show(view.Officers);
        Mark();
    }

    // The game's values now: boxes nobody typed in follow them.
    internal void Follow(View view)
    {
        if (view.Gate != Gate) return;
        ShowSummary(view);
        give.Visibility = view.Force == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        foreach ((FieldDef field, NumberField box) in fields)
            if (view.Values.TryGetValue(field, out long value)) box.Follow(value);
        officers.Show(view.Officers);
        Mark();
    }

    private void ShowSummary(View view)
    {
        var parts = new List<WD.Inline>
        {
            view.Force > 0 ? Link(view.ForceName, new ForceTarget(view.Force)) : new WD.Run(view.ForceName),
            new WD.Run(L.Gate.Kind)
        };
        if (view.GovernorName.Length > 0)
        {
            var governor = new WD.Span();
            governor.Inlines.Add(new WD.Run(L.City.GovernorLabel + " "));
            governor.Inlines.Add(Link(view.GovernorName, new OfficerTarget(view.Governor)));
            parts.Add(governor);
        }
        Fill(Subtitle, parts);
    }

    private void Mend()
    {
        if (fields[GateFields.Durability].Box.IsEnabled) fields[GateFields.Durability].Set(fields[GateFields.BaseDurability].Value);
    }

    private void Revert()
    {
        foreach (NumberField field in fields.Values) field.Show(field.Current);
        governor.Show(governor.Current);
        Mark();
    }

    // What to write, or null (with the reason in error and the box focused) when something typed is not valid.
    internal GateEdits.Request? Changes(out string error)
    {
        error = "";
        var changes = new List<FieldChange>();
        foreach (NumberField field in fields.Values.Where(f => f.Box.IsEnabled && f.IsChanged))
        {
            if (!field.TryRead(out long value, out error)) return null;
            changes.Add(new FieldChange(field.Field, value));
        }
        return new GateEdits.Request(changes, governor.Box.IsEnabled && governor.Picked != governor.Current ? governor.Picked : null);
    }

    private void Mark()
    {
        pending = fields.Values.Count(f => f.IsChanged) + (governor.Mark() ? 1 : 0);
        Count();
    }
}
