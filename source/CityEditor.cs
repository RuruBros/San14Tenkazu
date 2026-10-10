using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// The city's editor card, in three pages: 도시 (its values, with a fill to the caps of its scale), 군 (its
// counties' population, order and development, with a 일괄 row that fills a column) and 소속 장수 (its officers in a table
// for each status, as the officers' list shows them; a row opens his own card). The force and the governor named
// under the title open their cards too. A city no force holds offers [소속 주기] beside its 태수 (the window gives it).
internal sealed class CityEditorCard : EditorCard
{
    // A county of the city: its index, name (the city's own county is starred), the city that holds it now when
    // another does (""), and its values.
    internal sealed record County(int Index, string Name, bool Home, string Occupier, IReadOnlyDictionary<FieldDef, long> Values);

    // Everything the card shows for a city. Values holds CityFields.All (the castle's durability is missing when
    // the castle was not found). Officers are by status (OfficerTables.RowOf). Governors are the officers who
    // may govern it (its force's, in the city now); a city whose 태수 is its ruler or a 군단장 keeps him (GovernorFixed).
    internal sealed record View(int City, string Name, int Force, string ForceName, System.Drawing.Color Colour, string Scale, int Governor, string GovernorName,
        IReadOnlyDictionary<FieldDef, long> Values, IReadOnlyList<County> Counties, IReadOnlyList<(string Status, GameTable.Row Officer)> Officers,
        IReadOnlyList<GameName> Governors, bool GovernorFixed, string? IndependenceBlock = null);



    private readonly Dictionary<FieldDef, NumberField> fields = CityFields.All.ToDictionary(f => f, f => new NumberField(f, 110));
    private readonly WC.Grid countyGrid = new();
    private readonly List<(int Index, NumberField[] Fields, WC.TextBlock Level)> counties = [];
    private readonly List<(FieldDef Field, WC.TextBox Box)> bulk = [];
    private readonly OfficerTables officers = new(L.City.NoOfficers);
    private readonly NameChoice governor = new(170);
    private readonly WC.TextBlock governorNote = new() { Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 240 };
    private readonly PlateButton give;
    private readonly PlateButton independence;
    private bool actionsEnabled = true;
    private string? independenceBlock;
    private int pending;

    // The city being edited (0 when none).
    internal int City { get; private set; }

    internal override EditorTarget? Target => City == 0 ? null : new CityTarget(City);

    // The officers' portraits, by face number (null until decoded: RefreshFaces).
    internal Func<int, WM.Brush?>? Faces
    {
        set => officers.Faces = value;
    }

    internal override int Pending => pending;

    // [소속 주기] was pressed: the window gives the city to a force.
    internal event Action? GiveClicked;
    internal event Action? IndependenceClicked;

    internal CityEditorCard()
    {
        give = Shortcut(L.Give.Button, L.Give.ButtonTip, () => GiveClicked?.Invoke(), Icons.Force);
        give.Margin = new W.Thickness(8, 2, 0, 2);
        independence = Shortcut(L.Independence.Button, L.Independence.ButtonTip, () => IndependenceClicked?.Invoke(), Icons.Force);
        WC.ToolTipService.SetShowOnDisabled(independence, true);
        OffersGoTo();
        AddPage(L.City.CityPage, CityPage());
        AddPage(L.City.CountyPage, CountyPage());
        AddPage(L.City.OfficersPage, OfficerPage());
        foreach (NumberField field in fields.Values) field.Edited += Mark;
        governor.Box.SelectionChanged += (_, _) => Mark();
        officers.OfficerPicked += officer => RaiseLink(new OfficerTarget(officer));
    }

    private W.UIElement CityPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(Line(L.City.Governor, governor.Box, governorNote, give));
        governor.Box.ToolTip = L.City.GovernorTip;
        foreach (FieldDef field in CityFields.All)
            page.Children.Add(Line(field.Label, fields[field].Box, new WC.TextBlock { Text = field.Hint, Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 250 }));
        var shortcuts = new WC.WrapPanel { Margin = new W.Thickness(0, 6, 0, 0) };
        shortcuts.Children.Add(Shortcut(L.City.FillCaps, L.City.FillCapsTip, FillCaps));
        shortcuts.Children.Add(independence);
        shortcuts.Children.Add(Shortcut(L.Card.RevertInput, L.Card.RevertInputTip, Revert, Icons.Revert));
        page.Children.Add(shortcuts);
        page.Children.Add(new WC.TextBlock
        {
            Text = CapsText(),
            Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        return page;
    }

    // 규모별 상한 - 소: 금 50,000, 군량 100,000, ... / 중: ..., from CityFields.Caps.
    private static string CapsText() => L.City.CapsByScale(string.Join(" / ", Enumerable.Range(1, 4).Select(scale => CityFields.Caps(scale) is { } c
        ? $"{CityFields.ScaleName(scale)}: {CityFields.Gold.Label} {c.Gold:N0}, {CityFields.Food.Label} {c.Food:N0}, {CityFields.Troops.Label} {c.Troops:N0}"
        : "")));

    private W.UIElement CountyPage()
    {
        var page = new WC.StackPanel();
        string[] headers = [L.Columns.County, L.Columns.Base, .. CountyFields.All.Select(f => f.Label), L.Columns.Levels];
        double[] widths = [84, 40, 62, 44, 54, 54, 54, 60];
        for (int column = 0; column < widths.Length; column++)
            countyGrid.ColumnDefinitions.Add(new WC.ColumnDefinition { MinWidth = widths[column], Width = column == 0 ? new W.GridLength(1, W.GridUnitType.Star) : new W.GridLength(widths[column]) });
        countyGrid.RowDefinitions.Add(new WC.RowDefinition { Height = W.GridLength.Auto });
        for (int column = 0; column < headers.Length; column++)
        {
            var header = new WC.TextBlock { Text = headers[column], Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 4, 2) };
            WC.Grid.SetColumn(header, column);
            countyGrid.Children.Add(header);
        }
        page.Children.Add(countyGrid);
        page.Children.Add(BulkRow(widths));
        page.Children.Add(new WC.TextBlock
        {
            Text = L.City.LevelsNote(string.Join(" / ", CountyFields.LevelSteps), string.Join(" / ", Enumerable.Range(2, CountyFields.LevelSteps.Length))),
            Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        return page;
    }

    // 일괄: a value typed under a column goes into every county's box of that column with [적용]; an empty box
    // leaves its column alone. Like the other shortcuts it only types: [변경 적용] writes.
    private W.UIElement BulkRow(double[] widths)
    {
        var row = new WC.Grid();
        for (int column = 0; column < widths.Length; column++)
            row.ColumnDefinitions.Add(new WC.ColumnDefinition { MinWidth = widths[column], Width = column == 0 ? new W.GridLength(1, W.GridUnitType.Star) : new W.GridLength(widths[column]) });
        row.Children.Add(new WC.TextBlock { Text = L.City.BulkRow, FontWeight = W.FontWeights.Bold, VerticalAlignment = W.VerticalAlignment.Center });
        for (int i = 0; i < CountyFields.All.Length; i++)
        {
            FieldDef field = CountyFields.All[i];
            WC.TextBox box = CardSkin.NumberBox(CountyBoxWidth(field));
            box.MinHeight = 26;
            box.Margin = new W.Thickness(0, 1, 4, 1);
            box.VerticalAlignment = W.VerticalAlignment.Center;
            box.ToolTip = L.City.BulkTip(field.Label, field.Min, field.Max);
            box.TextChanged += (_, _) => box.Foreground = box.Text.Trim().Length == 0 || BulkValue(field, box) is not null ? CardSkin.Ink : CardSkin.Poor;
            box.KeyDown += (_, e) =>
            {
                if (e.Key != WI.Key.Enter) return;
                e.Handled = true;
                ApplyBulk();
            };
            WC.Grid.SetColumn(box, i + 2);
            row.Children.Add(box);
            bulk.Add((field, box));
        }
        var apply = new PlateButton(L.City.BulkApply) { ToolTip = L.City.BulkApplyTip, HorizontalAlignment = W.HorizontalAlignment.Left };
        apply.Click += ApplyBulk;
        WC.Grid.SetColumn(apply, widths.Length - 1);
        row.Children.Add(apply);
        return new WC.Border { BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(0, 1, 0, 0), Margin = new W.Thickness(0, 4, 0, 0), Padding = new W.Thickness(0, 4, 0, 0), Child = row };
    }

    private static double CountyBoxWidth(FieldDef field) => field == CountyFields.Population ? 58 : field == CountyFields.PublicOrder ? 40 : 50;

    private static long? BulkValue(FieldDef field, WC.TextBox box) =>
        TypedNumber.TryParse(box.Text, out long value) && value >= field.Min && value <= field.Max ? value : null;

    // A box that holds something not valid stops it (focused); the columns filled are emptied.
    private void ApplyBulk()
    {
        foreach ((FieldDef field, WC.TextBox box) in bulk)
            if (box.Text.Trim().Length > 0 && BulkValue(field, box) is null)
            {
                box.Focus();
                box.SelectAll();
                return;
            }
        foreach ((FieldDef field, WC.TextBox box) in bulk)
            if (BulkValue(field, box) is { } value)
            {
                FillCounties([field], value);
                box.Clear();
            }
    }

    private W.UIElement OfficerPage() => officers;

    // The 소속 장수 page's tables (their fills are written by the window).
    internal OfficerTables Officers => officers;

    // The fills write at once (after asking): only while connected and not busy.
    internal bool FillsEnabled
    {
        set
        {
            officers.FillsEnabled = value;
            actionsEnabled = value;
            give.IsEnabled = value;
            independence.IsEnabled = value && independenceBlock is null;
        }
    }

    private void ShowIndependence(View view)
    {
        independenceBlock = view.IndependenceBlock;
        independence.Visibility = view.Force > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        independence.IsEnabled = actionsEnabled && independenceBlock is null;
        independence.ToolTip = independenceBlock ?? L.Independence.ButtonTip;
    }

    // Portraits decoded since the tables were drawn.
    internal void RefreshFaces() => officers.RefreshFaces();

    internal void Open(View view)
    {
        if (view.City != City)
        {
            StartOver();
            foreach ((_, WC.TextBox box) in bulk) box.Clear();
        }
        City = view.City;
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
        governorNote.Text = view.GovernorFixed ? L.City.GovernedByLord : "";
        ShowIndependence(view);
        give.Visibility = view.Force == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;

        // Rows past the header go; the counties come in again.
        foreach ((_, NumberField[] boxes, WC.TextBlock level) in counties)
        {
            foreach (NumberField box in boxes) countyGrid.Children.Remove(box.Box);
            countyGrid.Children.Remove(level);
        }
        var stale = countyGrid.Children.OfType<W.UIElement>().Where(e => WC.Grid.GetRow(e) > 0).ToList();
        foreach (W.UIElement element in stale) countyGrid.Children.Remove(element);
        while (countyGrid.RowDefinitions.Count > 1) countyGrid.RowDefinitions.RemoveAt(1);
        counties.Clear();
        foreach (County county in view.Counties)
        {
            int row = countyGrid.RowDefinitions.Count;
            countyGrid.RowDefinitions.Add(new WC.RowDefinition { Height = W.GridLength.Auto });
            var name = new WC.TextBlock { Text = county.Home ? $"★ {county.Name}" : county.Name, TextWrapping = W.TextWrapping.Wrap, FontWeight = county.Home ? W.FontWeights.Bold : W.FontWeights.Normal, VerticalAlignment = W.VerticalAlignment.Center };
            var occupier = new WC.TextBlock { Text = county.Occupier, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.Poor, VerticalAlignment = W.VerticalAlignment.Center, ToolTip = county.Occupier.Length > 0 ? L.City.OccupiedBy(county.Occupier) : null };
            NumberField[] boxes = CountyFields.All.Select(f => new NumberField(f, CountyBoxWidth(f))).ToArray();
            var level = new WC.TextBlock { VerticalAlignment = W.VerticalAlignment.Center };
            var cells = new List<W.UIElement> { name, occupier };
            for (int i = 0; i < boxes.Length; i++)
            {
                boxes[i].Box.Margin = new W.Thickness(0, 1, 4, 1);
                boxes[i].Box.MinHeight = 26;
                boxes[i].Show(county.Values[CountyFields.All[i]]);
                boxes[i].Edited += Mark;
                cells.Add(boxes[i].Box);
            }
            cells.Add(level);
            for (int column = 0; column < cells.Count; column++)
            {
                WC.Grid.SetRow(cells[column], row);
                WC.Grid.SetColumn(cells[column], column);
                countyGrid.Children.Add(cells[column]);
            }
            counties.Add((county.Index, boxes, level));
        }

        officers.Show(view.Officers);
        Mark();
    }

    // The game's values now: boxes nobody typed in follow them.
    internal void Follow(View view)
    {
        if (view.City != City) return;
        ShowSummary(view);
        give.Visibility = view.Force == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        ShowIndependence(view);
        foreach ((FieldDef field, NumberField box) in fields)
            if (view.Values.TryGetValue(field, out long value)) box.Follow(value);
        foreach (County county in view.Counties)
            if (counties.FirstOrDefault(c => c.Index == county.Index) is { Fields: not null } row)
                for (int i = 0; i < row.Fields.Length; i++) row.Fields[i].Follow(county.Values[CountyFields.All[i]]);
        officers.Show(view.Officers);
        Mark();
    }

    private void ShowSummary(View view)
    {
        var parts = new List<WD.Inline>
        {
            view.Force > 0 ? Link(view.ForceName, new ForceTarget(view.Force)) : new WD.Run(view.ForceName),
            new WD.Run(L.City.ScaleOf(view.Scale))
        };
        if (view.GovernorName.Length > 0)
        {
            var governor = new WD.Span();
            governor.Inlines.Add(new WD.Run(L.City.GovernorLabel + " "));
            governor.Inlines.Add(Link(view.GovernorName, new OfficerTarget(view.Governor)));
            parts.Add(governor);
        }
        parts.Add(new WD.Run(L.City.CountyCount(view.Counties.Count)));
        Fill(Subtitle, parts);
    }

    private void FillCaps()
    {
        if (CityFields.Caps(fields[CityFields.Scale].Value) is not { } caps) return;
        fields[CityFields.Gold].Set(caps.Gold);
        fields[CityFields.Food].Set(caps.Food);
        fields[CityFields.Troops].Set(caps.Troops);
    }

    private void Revert()
    {
        foreach (NumberField field in fields.Values) field.Show(field.Current);
        foreach ((_, NumberField[] boxes, _) in counties)
            foreach (NumberField box in boxes) box.Show(box.Current);
        Mark();
    }

    private void FillCounties(FieldDef[] which, long value)
    {
        foreach ((_, NumberField[] boxes, _) in counties)
            foreach (NumberField box in boxes)
                if (which.Contains(box.Field)) box.Set(value);
    }

    // What to write, or null (with the reason in error and the box focused) when something typed is not valid.
    internal CityEdits.Request? Changes(out string error)
    {
        error = "";
        var city = new List<FieldChange>();
        foreach (NumberField field in fields.Values.Where(f => f.Box.IsEnabled && f.IsChanged))
        {
            if (!field.TryRead(out long value, out error)) return null;
            city.Add(new FieldChange(field.Field, value));
        }
        var countyChanges = new List<(int, List<FieldChange>)>();
        foreach ((int index, NumberField[] boxes, _) in counties)
        {
            var changes = new List<FieldChange>();
            foreach (NumberField box in boxes.Where(b => b.IsChanged))
            {
                if (!box.TryRead(out long value, out error)) return null;
                changes.Add(new FieldChange(box.Field, value));
            }
            if (changes.Count > 0) countyChanges.Add((index, changes));
        }
        return new CityEdits.Request(city, countyChanges, governor.Box.IsEnabled && governor.Picked != governor.Current ? governor.Picked : null);
    }

    private void Mark()
    {
        int changed = fields.Values.Count(f => f.IsChanged) + (governor.Mark() ? 1 : 0);
        foreach ((_, NumberField[] boxes, WC.TextBlock level) in counties)
        {
            changed += boxes.Count(b => b.IsChanged);
            string Level(FieldDef field) => CountyFields.Level(boxes.First(b => b.Field == field).Value).ToString(CultureInfo.InvariantCulture);
            level.Text = $"{Level(CountyFields.Commerce)} · {Level(CountyFields.Agriculture)} · {Level(CountyFields.Barracks)}";
        }
        pending = changed;
        Count();
    }
}
