using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// The officers of a city or a force on its card: a table for each status (군주, 태수, 일반, ...) under its name and
// count, as the officers' list shows them (the portrait, the name over the 자, loyalty and the abilities with their
// bars). Each table is as tall as its rows, so the card's page is the one thing that scrolls (a table scrolling
// inside it read oddly); a row opens the officer's card. Where loyalty counts (군단장, 태수,
// 일반), [충성 150] at the right of the table's name fills drafts for the officers of that table.
internal sealed class OfficerTables : WC.StackPanel
{
    internal static readonly FieldDef[] Values = [OfficerFields.Loyalty, .. OfficerFields.Abilities];
    private static readonly GameTable.Column[] Columns =
        [new("", GameTable.Kind.Face), new(L.Columns.NameCourtesy, GameTable.Kind.TwoLines, 13), .. Values.Select(f => new GameTable.Column(f.Label, GameTable.Kind.Number, 8, f))];
    private const double RowHeight = 58;
    private static readonly HashSet<string> Vassals = [.. Enumerable.Range(2, 3).Where(OfficerFields.IsVassal).Select(OfficerFields.StatusName)];

    private readonly string empty;
    private readonly List<GameTable> tables = [];
    private readonly List<PlateButton> fills = [];
    private List<(int Officer, string Status)> listed = [];
    private bool fillsEnabled;
    internal Func<int, FieldDef, long, Task<bool>>? CellWriter { get; set; }
    internal Func<IReadOnlyList<int>, FieldDef, long, Task<bool>>? ColumnWriter { get; set; }
    internal Func<int, FieldDef, bool>? CanEditCell { get; set; }
    internal TableDrafts? Drafts { get; set; }

    // empty: what the page says when there are no officers.
    internal OfficerTables(string empty) => this.empty = empty;

    internal event Action<int>? OfficerPicked;

    // [충성 150] of a table: its officers and their status.
    internal event Action<IReadOnlyList<int>, string>? LoyaltyClicked;

    // Inputs and fills are available only while connected and not busy.
    internal bool FillsEnabled
    {
        set
        {
            fillsEnabled = value;
            foreach (PlateButton fill in fills) fill.IsEnabled = value;
            foreach (GameTable table in tables) table.EditingEnabled = value;
        }
    }

    // The portraits, by face number (null until decoded: RefreshFaces).
    internal Func<int, WM.Brush?>? Faces { get; set; }

    // The officers' list's order (EditorWindow.OfficerRows): by status, the abler first.
    internal static IEnumerable<OfficerRecord> Order(IEnumerable<OfficerRecord> officers) =>
        officers.OrderBy(o => o.Status).ThenByDescending(o => o[OfficerFields.Leadership] + o[OfficerFields.Strength] + o[OfficerFields.Intelligence]);

    // An officer's row under his status.
    internal static (string Status, GameTable.Row Row) RowOf(OfficerRecord officer) =>
        (OfficerFields.StatusName(officer.Status), new GameTable.Row(officer.Id, [officer.Face, officer.Name + "\n" + officer.CourtesyName, .. Values.Select(f => (object?)officer[f])]));

    // The same officers keep their tables (their order and where they are scrolled to) and take the new values;
    // others make the tables again.
    internal void Show(IReadOnlyList<(string Status, GameTable.Row Row)> officers)
    {
        if (officers.Select(o => ((int)o.Row.Tag, o.Status)).SequenceEqual(listed) && tables.Count > 0)
        {
            var groups = officers.GroupBy(o => o.Status).ToList();
            for (int i = 0; i < tables.Count; i++) tables[i].SetRows(groups[i].Select(o => o.Row).ToList());
            return;
        }
        foreach (GameTable table in tables) table.Drafts = null;
        Children.Clear();
        tables.Clear();
        fills.Clear();
        listed = officers.Select(o => ((int)o.Row.Tag, o.Status)).ToList();
        foreach (var group in officers.GroupBy(o => o.Status))
        {
            var title = new WC.DockPanel { Margin = new W.Thickness(0, 4, 0, 2) };
            List<int> ids = group.Select(o => (int)o.Row.Tag).ToList();
            string status = group.Key;
            if (Vassals.Contains(status))
                AddFill(title, L.Officer.Loyalty150, L.Officer.Loyalty150Tip(status),
                    () => LoyaltyClicked?.Invoke(ids, status));
            title.Children.Add(new WC.TextBlock { Text = L.Officer.GroupCount(group.Key, group.Count()), Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Bottom });
            Children.Add(title);
            var table = new GameTable(Columns, RowHeight)
            {
                Faces = face => Faces?.Invoke(face), Margin = new W.Thickness(0, 0, 0, 6), EditingEnabled = fillsEnabled,
                DraftTarget = tag => new OfficerTarget((int)tag), Drafts = Drafts,
                CanEditCell = (tag, field) => CanEditCell?.Invoke((int)tag, field) ?? false,
                CellWriter = (tag, field, value) => CellWriter?.Invoke((int)tag, field, value) ?? Task.FromResult(false),
                ColumnWriter = ColumnWriter is null ? null : (tags, field, value) => ColumnWriter(tags.Cast<int>().ToList(), field, value)
            };
            table.RowPicked += officer => OfficerPicked?.Invoke((int)officer);
            table.SetRows(group.Select(o => o.Row).ToList());
            tables.Add(table);
            Children.Add(table);
        }
        if (officers.Count == 0) Children.Add(new WC.TextBlock { Text = empty, Foreground = CardSkin.InkMuted });
    }

    // A fill button at the right of a table's title.
    private void AddFill(WC.DockPanel title, string text, string tip, Action run)
    {
        var fill = new PlateButton(text, icon: Icons.Fill) { Height = 26, IsEnabled = fillsEnabled, VerticalAlignment = W.VerticalAlignment.Bottom, Margin = new W.Thickness(6, 0, 0, 0), ToolTip = tip };
        fill.Click += run;
        WC.DockPanel.SetDock(fill, WC.Dock.Right);
        title.Children.Add(fill);
        fills.Add(fill);
    }

    // Portraits decoded since the tables were drawn.
    internal void RefreshFaces()
    {
        foreach (GameTable table in tables) table.RefreshFaces();
    }
}
