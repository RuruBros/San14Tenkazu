using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WP = System.Windows.Controls.Primitives;

namespace San14Tenkazu;

// The force's editor card, in five pages: 기본·외교 (국호, 작위, 군사, colour, 친선 with every force and the
// months left of an agreement), 정책 (who carries out which policy in each of the five fields), 도시·관문 (its cities
// in a table, as the cities' list shows them, and its gates in another), 장수 (its officers in a table for each
// status, with their fills) and 부대 (its units in a table, with [부상병을 병사로] and [사기 130] for all of them). A
// row opens that city's, gate's, officer's or unit's card.
internal sealed class ForceEditorCard : EditorCard
{
    // Another force as this one sees it: 친선 (0~255, usually up to 100) and the months left of an agreement between them (0 for none).
    internal sealed record Relation(int Force, string Name, System.Drawing.Color Colour, int Friendship, int Agreement, bool CanAlly = true);

    // An officer of the force who can carry out a policy: the policy's field (-1 for the ruler and the 군사, whose
    // policies work without a slot), and how the card names him.
    internal readonly record struct PolicyHolder(int Officer, int Field, string Label, string Tip);

    // Everything the card shows for a force. Countries and Officers name the choices of 국호 and 군사; Slots are each
    // policy field's seven (officer, days); Cities are rows of CityColumns, Gates of GateColumns, Members by status
    // (OfficerTables.RowOf).
    internal sealed record View(int Force, string Name, string Summary, System.Drawing.Color Colour, IReadOnlyList<GameName> Officers,
        int Country, int ColorIndex, int Strategist, int Rank, IReadOnlyList<Relation> Others,
        IReadOnlyList<(int Officer, int Days)>[] Slots, IReadOnlyList<PolicyHolder> Holders, string FixedPolicies,
        IReadOnlyList<GameTable.Row> Cities, IReadOnlyList<GameTable.Row> Gates, IReadOnlyList<(string Status, GameTable.Row Row)> Members,
        IReadOnlyList<GameTable.Row> Units, IReadOnlyList<MilitaryRow>? MilitaryPoints = null, MilitaryPointCaps? MilitaryCaps = null);
    internal sealed record MilitaryRow(int Corps, int Number, int Leader, string LeaderName, int Points);

    // The cities' table: as the cities' list, without the force.
    internal static readonly GameTable.Column[] CityColumns = [new(L.Columns.City, GameTable.Kind.Text, 10), new(L.Columns.Scale, GameTable.Kind.Text, 6),
        new(L.Columns.Governor, GameTable.Kind.Text, 10), new(L.Columns.Troops, GameTable.Kind.Number, 10, CityFields.Troops), new(L.Columns.Gold, GameTable.Kind.Number, 10, CityFields.Gold), new(L.Columns.Food, GameTable.Kind.Number, 10, CityFields.Food)];

    // The gates' table: what the gate card edits.
    internal static readonly GameTable.Column[] GateColumns = [new(L.Columns.Gate, GameTable.Kind.Text, 10), new(L.Columns.Governor, GameTable.Kind.Text, 10),
        new(L.Columns.Troops, GameTable.Kind.Number, 10, GateFields.Troops), new(L.Columns.Morale, GameTable.Kind.Number, 6, GateFields.Morale), new(L.Columns.Durability, GameTable.Kind.Number, 12, GateFields.Durability)];

    // The units' table: as the units' list, without the force.
    internal static readonly GameTable.Column[] UnitColumns = [new("", GameTable.Kind.Face), new(L.Columns.Commander, GameTable.Kind.Text, 13), new(L.Columns.Kind, GameTable.Kind.Text, 7),
        new(L.Columns.Location, GameTable.Kind.Text, 16), .. UnitFields.All.Select(f => new GameTable.Column(f.Label, GameTable.Kind.Number, 10, f))];


    private readonly NameChoice country = new(196), rank = new(196, none: false), strategist = new(196);
    private readonly WC.WrapPanel palette = new() { Width = 15 * 21 };
    private readonly WC.Border[] colours = new WC.Border[MapView.GamePalette.Length];
    private readonly WC.Grid relationRows = new();
    private readonly WC.StackPanel militaryRows = new();
    private readonly WC.TextBlock militaryNote = new() { TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted };
    private sealed record MilitaryInput(MilitaryRow Row, NumberField Number, WC.TextBlock Limit);
    private List<MilitaryInput> military = [];
    private MilitaryPointCaps? militaryCaps;
    private readonly PolicyRow[] policyRows = ForceFields.PolicyFields.Select((_, field) => new PolicyRow(field)).ToArray();
    private readonly WC.TextBlock policyNote = new() { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 4) };
    private readonly GameTable cities = new(CityColumns, 28);
    private readonly WC.TextBlock noCities = new() { Text = L.Force.NoCities, Foreground = CardSkin.InkMuted };
    private readonly GameTable gates = new(GateColumns, 28) { Margin = new W.Thickness(0, 10, 0, 0) };
    private readonly OfficerTables members = new(L.Force.NoOfficers);
    private readonly GameTable units = new(UnitColumns, 46);
    private PlateButton surrenderButton = null!;
    internal GameTable CitiesTable => cities;
    internal GameTable GatesTable => gates;
    internal GameTable UnitsTable => units;
    private readonly WC.TextBlock unitCount = new() { Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Bottom };
    private readonly PlateButton heal = new(L.Force.Heal, icon: Icons.Heal)
    {
        Height = 26, IsEnabled = false, Margin = new W.Thickness(6, 0, 0, 0), ToolTip = L.Force.HealTip
    };
    private readonly PlateButton morale = new(L.Force.Morale(UnitFields.MoraleCap), icon: Icons.Fill)
    {
        Height = 26, IsEnabled = false, Margin = new W.Thickness(6, 0, 0, 0),
        ToolTip = L.Force.MoraleTip(UnitFields.MoraleCap)
    };
    private sealed record RelationRow(Relation Relation, WC.TextBox Box, WC.TextBox Months);
    private List<RelationRow> relations = [];
    private Dictionary<int, PolicyHolder> holders = [];
    private int currentColor, pickedColor, pending;
    private bool filling;

    // The force being edited (0 when none).
    internal int Force { get; private set; }

    internal override EditorTarget? Target => Force == 0 ? null : new ForceTarget(Force);

    internal override int Pending => pending + TableDrafts.Pending;

    internal ForceEditorCard()
    {
        AddPage(L.Force.BasicPage, BasicPage());
        AddPage(L.Force.PolicyPage, PolicyPage());
        AddPage(L.Force.CityPage, CityPage());
        AddPage(L.Force.OfficerPage, members);
        AddPage(L.Force.UnitPage, UnitPage());
        AddPage(L.Force.MilitaryPage, MilitaryPage());
        foreach (NameChoice choice in new[] { country, rank, strategist }) choice.Box.SelectionChanged += (_, _) => Mark();
        cities.RowPicked += city => RaiseLink(new CityTarget((int)city));
        gates.RowPicked += gate => RaiseLink(new GateTarget((int)gate));
        members.OfficerPicked += officer => RaiseLink(new OfficerTarget(officer));
        units.RowPicked += unit => { if (unit is (int slot, int commander)) RaiseLink(new UnitTarget(slot, commander)); };
        heal.Click += () => HealClicked?.Invoke(UnitList());
        morale.Click += () => MoraleClicked?.Invoke(UnitList());
    }

    private W.UIElement BasicPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(Line(L.Force.Country, country.Box));
        country.Box.ToolTip = L.Force.CountryTip;
        page.Children.Add(Line(L.Force.Rank, rank.Box));
        page.Children.Add(Line(L.Force.Strategist, strategist.Box));
        strategist.Box.ToolTip = L.Force.StrategistTip;
        for (int i = 0; i < colours.Length; i++)
        {
            int index = i;
            var colour = System.Drawing.Color.FromArgb(unchecked((int)0xFF000000) | MapView.GamePalette[i]);
            colours[i] = new WC.Border
            {
                Width = 19, Height = 19, Margin = new W.Thickness(1), BorderThickness = new W.Thickness(2), CornerRadius = new W.CornerRadius(3),
                Background = CardSkin.Brush(colour), BorderBrush = WM.Brushes.Transparent, Cursor = WI.Cursors.Hand, ToolTip = L.Force.ColourTip(i)
            };
            colours[i].MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                pickedColor = index;
                Mark();
            };
            palette.Children.Add(colours[i]);
        }
        palette.Margin = new W.Thickness(0, 4, 0, 0);
        WC.DockPanel colourLine = Line(L.Force.Colour, palette);
        ((WC.TextBlock)colourLine.Children[0]).VerticalAlignment = W.VerticalAlignment.Top;
        page.Children.Add(colourLine);

        page.Children.Add(Heading(L.Force.Relations));
        for (int column = 0; column < 5; column++)
            relationRows.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = column == 1 ? new W.GridLength(1, W.GridUnitType.Star) : W.GridLength.Auto });
        page.Children.Add(relationRows);
        var buttons = new WP.UniformGrid { Rows = 1, Columns = 2, Margin = new W.Thickness(0, 6, 0, 0) };
        PlateButton all = Shortcut(L.Force.AllFriends, L.Force.AllFriendsTip, () => FillFriendship(100));
        PlateButton none = Shortcut(L.Force.NoFriends, L.Force.NoFriendsTip, () => FillFriendship(0), Icons.Revert);
        buttons.Children.Add(all);
        buttons.Children.Add(none);
        page.Children.Add(buttons);
        page.Children.Add(new WC.TextBlock
        {
            Text = L.Force.RelationsNote,
            Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        PlateButton surrender = surrenderButton = Shortcut(L.Surrender.Button, L.Surrender.ButtonTip, () => SurrenderClicked?.Invoke(), Icons.Force);
        surrender.IsEnabled = false;
        surrender.HorizontalAlignment = W.HorizontalAlignment.Left;
        surrender.Margin = new W.Thickness(0, 12, 0, 0);
        page.Children.Add(surrender);
        return page;
    }

    private W.UIElement MilitaryPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(new WC.TextBlock { Text = L.Force.MilitaryHint, TextWrapping = W.TextWrapping.Wrap,
            Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 0, 0, 12) });
        page.Children.Add(militaryRows);
        page.Children.Add(militaryNote);
        return page;
    }

    private void FillMilitary(IReadOnlyList<MilitaryRow>? rows, MilitaryPointCaps? caps, bool open = false)
    {
        rows ??= [];
        militaryCaps = caps;
        bool same = military.Select(m => (m.Row.Corps, m.Row.Number, m.Row.Leader))
            .SequenceEqual(rows.Select(r => (r.Corps, r.Number, r.Leader)));
        if (open || !same && !military.Any(m => m.Number.IsChanged))
        {
            same = true;
            military.Clear(); militaryRows.Children.Clear();
            foreach (var row in rows)
            {
                var number = new NumberField(CorpsFields.MilitaryPoints, 110);
                var limit = new WC.TextBlock { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap,
                    VerticalAlignment = W.VerticalAlignment.Center };
                var group = new WC.StackPanel { Margin = new W.Thickness(0, 0, 0, 10) };
                group.Children.Add(new WC.TextBlock { Text = L.Force.MilitaryCorps(row.Number, row.LeaderName),
                    Foreground = CardSkin.Ink, TextWrapping = W.TextWrapping.Wrap });
                group.Children.Add(Line(L.Force.MilitaryPoints, number.Box, limit));
                militaryRows.Children.Add(group);
                military.Add(new(row, number, limit));
                number.Show(row.Points); number.Edited += Mark;
            }
        }
        else if (same)
            foreach (var input in military) input.Number.Follow(rows.Single(r => r.Corps == input.Row.Corps).Points);
        militaryNote.Text = caps is { Valid: true } && (same || open) && rows.Count > 0 ? "" : L.Force.MilitaryUnavailable;
        Mark();
    }

    private W.UIElement PolicyPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(policyNote);
        foreach (PolicyRow row in policyRows)
        {
            page.Children.Add(row.Panel);
            row.Changed += Mark;
            row.Taken += Render;
        }
        PlateButton hurry = Shortcut(L.Force.Hurry, L.Force.HurryTip, () =>
        {
            foreach (PolicyRow row in policyRows) row.Hurry();
            Render();
        });
        hurry.HorizontalAlignment = W.HorizontalAlignment.Left;
        page.Children.Add(hurry);
        page.Children.Add(new WC.TextBlock
        {
            Text = L.Force.PolicyNote,
            Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        return page;
    }

    private W.UIElement CityPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(cities);
        page.Children.Add(noCities);
        page.Children.Add(gates);
        return page;
    }

    private W.UIElement UnitPage()
    {
        var page = new WC.StackPanel();
        var title = new WC.DockPanel { Margin = new W.Thickness(0, 4, 0, 2) };
        foreach (PlateButton fill in new[] { morale, heal })
        {
            WC.DockPanel.SetDock(fill, WC.Dock.Right);
            title.Children.Add(fill);
        }
        title.Children.Add(unitCount);
        page.Children.Add(title);
        page.Children.Add(units);
        page.Children.Add(new WC.TextBlock
        {
            Text = L.Unit.ComeAndGo, Foreground = CardSkin.InkMuted,
            TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        return page;
    }

    // [부상병을 병사로] and [사기 130]: the force's units as listed.
    internal event Action<IReadOnlyList<(int Slot, int Commander)>>? HealClicked, MoraleClicked;
    internal event Action? SurrenderClicked;

    private List<(int Slot, int Commander)> UnitList() => units.Rows.Select(r => r.Tag).OfType<(int Slot, int Commander)>().ToList();

    // The fills write at once (after asking): only while connected and not busy.
    internal bool FillsEnabled
    {
        set
        {
            members.FillsEnabled = value;
            cities.EditingEnabled = gates.EditingEnabled = units.EditingEnabled = value;
            heal.IsEnabled = morale.IsEnabled = value;
            surrenderButton.IsEnabled = value;
        }
    }

    // The portraits of the officers and of the units' commanders, by face number (null until decoded: RefreshFaces).
    internal Func<int, WM.Brush?>? Faces
    {
        set
        {
            members.Faces = value;
            units.Faces = value;
        }
    }

    // Portraits decoded since the tables were drawn.
    internal void RefreshFaces()
    {
        members.RefreshFaces();
        units.RefreshFaces();
    }

    // The 장수 page's tables (their fills are written by the window).
    internal OfficerTables Members => members;

    private void ShowMembers(IReadOnlyList<GameTable.Row> cityRows, IReadOnlyList<GameTable.Row> gateRows, IReadOnlyList<(string Status, GameTable.Row Row)> officerRows,
        IReadOnlyList<GameTable.Row> unitRows)
    {
        cities.SetRows(cityRows);
        cities.Visibility = cityRows.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        noCities.Visibility = cityRows.Count > 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        // A force without gates shows no gates' table.
        gates.SetRows(gateRows);
        gates.Visibility = gateRows.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        members.Show(officerRows);
        units.SetRows(unitRows);
        units.Visibility = unitRows.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        unitCount.Text = unitRows.Count > 0 ? L.Force.UnitCount(unitRows.Count) : L.Force.NoUnits;
    }

    // The game's names, once per connection: the 국호 (some repeat: those carry their number) and the 작위.
    internal void SetNames(IReadOnlyList<(int Index, string Name)> countries, GameCatalog catalog)
    {
        country.SetNames(countries.Select(c => new GameName(c.Index, countries.Count(o => o.Name == c.Name) > 1 ? $"{c.Name} (#{c.Index})" : c.Name, "")));
        rank.SetNames(catalog.Ranks);
    }

    internal void Open(View view)
    {
        TableDrafts.Clear();
        filling = true;
        if (view.Force != Force) StartOver();
        Force = view.Force;
        Title.Text = view.Name;
        Subtitle.Text = view.Summary;
        Shield.Colour = view.Colour;
        country.Show(view.Country);
        rank.Show(view.Rank);
        strategist.SetNames(view.Officers);
        strategist.Show(view.Strategist);
        currentColor = pickedColor = view.ColorIndex;
        FillRelations(view.Others);
        holders = view.Holders.ToDictionary(h => h.Officer);
        for (int field = 0; field < policyRows.Length; field++) policyRows[field].Open(view.Slots[field]);
        policyNote.Text = view.FixedPolicies;
        FillMilitary(view.MilitaryPoints, view.MilitaryCaps, open: true);

        ShowMembers(view.Cities, view.Gates, view.Members, view.Units);
        filling = false;
        Render();
    }

    // Untouched policy slots follow the game; explicit additions, removals and countdown edits remain pending.
    internal void Follow(View view)
    {
        if (view.Force != Force) return;
        Subtitle.Text = view.Summary;
        ShowMembers(view.Cities, view.Gates, view.Members, view.Units);
        filling = true;
        rank.Follow(view.Rank);
        filling = false;
        FillMilitary(view.MilitaryPoints, view.MilitaryCaps);
        var nextHolders = view.Holders.ToDictionary(h => h.Officer);
        bool redraw = policyNote.Text != view.FixedPolicies || holders.Count != nextHolders.Count
            || holders.Any(h => !nextHolders.TryGetValue(h.Key, out var next) || next != h.Value);
        holders = nextHolders;
        policyNote.Text = view.FixedPolicies;
        for (int field = 0; field < policyRows.Length; field++) redraw |= policyRows[field].Follow(view.Slots[field]);
        if (redraw) Render();
        else Mark();
    }

    // Each row's chips and the officers it offers (those of its field not yet in any row).
    private void Render()
    {
        var placed = policyRows.SelectMany(r => r.Picked.Select(p => p.Officer)).ToHashSet();
        foreach (PolicyRow row in policyRows)
            row.Render(holders, holders.Values.Where(h => h.Field == row.Field && !placed.Contains(h.Officer)).OrderBy(h => h.Label, StringComparer.CurrentCulture).ToList());
        Mark();
    }

    private void FillRelations(IReadOnlyList<Relation> others)
    {
        relationRows.Children.Clear();
        relationRows.RowDefinitions.Clear();
        relations = [];
        foreach (Relation other in others)
        {
            int index = relationRows.RowDefinitions.Count;
            relationRows.RowDefinitions.Add(new WC.RowDefinition { Height = W.GridLength.Auto });
            var shield = new ForceShield { Colour = other.Colour, Width = 13, Height = 17, Margin = new W.Thickness(0, 0, 5, 0), VerticalAlignment = W.VerticalAlignment.Center };
            var name = new WC.TextBlock { VerticalAlignment = W.VerticalAlignment.Center, TextTrimming = W.TextTrimming.CharacterEllipsis };
            name.Inlines.Add(Link(other.Name, new ForceTarget(other.Force)));
            WC.TextBox box = CardSkin.NumberBox(46);
            box.Margin = new W.Thickness(6, 1, 6, 1);
            box.Text = other.Friendship.ToString(CultureInfo.CurrentCulture);
            WC.TextBox months = CardSkin.NumberBox(38);
            months.Margin = new W.Thickness(0, 1, 4, 1);
            months.Text = other.Agreement.ToString(CultureInfo.CurrentCulture);
            months.Visibility = other.CanAlly ? W.Visibility.Visible : W.Visibility.Collapsed;
            var state = new WC.TextBlock { Text = other.CanAlly ? L.Force.AllianceMonths : L.Force.AllianceUnavailableLabel,
                Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center,
                HorizontalAlignment = W.HorizontalAlignment.Left,
                ToolTip = other.CanAlly ? L.Force.AllianceTip(ForceFields.MaxAgreement) : L.Force.AllianceUnavailable };
            WC.Grid.SetColumnSpan(state, other.CanAlly ? 1 : 2);
            var row = new RelationRow(other, box, months);
            box.TextChanged += (_, _) => Mark();
            months.TextChanged += (_, _) => Mark();
            relations.Add(row);
            foreach ((W.UIElement element, int column) in new (W.UIElement, int)[] { (shield, 0), (name, 1), (box, 2), (months, 3), (state, other.CanAlly ? 4 : 3) })
            {
                WC.Grid.SetRow(element, index);
                WC.Grid.SetColumn(element, column);
                relationRows.Children.Add(element);
            }
        }
    }

    // Shortcuts type into the boxes, like a person would; [변경 적용] still writes them. 100 only raises (a 친선 past
    // 100 stays); 0 sets every one.
    private void FillFriendship(int value)
    {
        foreach (RelationRow row in relations)
            if (value == 0 || !TypedNumber.TryParse(row.Box.Text, out long typed) || typed < value) row.Box.Text = value.ToString(CultureInfo.CurrentCulture);
    }

    // The friendships typed in that differ from the game's, or none (with the box focused) when one is not a valid number.
    internal List<(int Force, int Value)> FriendshipChanges(out string error)
    {
        error = "";
        var changes = new List<(int, int)>();
        foreach (RelationRow row in relations)
        {
            if (!TypedNumber.TryParse(row.Box.Text, out long value) || value is < 0 or > ForceFields.MaxFriendship)
            {
                error = L.Force.FriendshipError(row.Relation.Name, ForceFields.MaxFriendship);
                row.Box.Focus();
                return [];
            }
            if (value != row.Relation.Friendship) changes.Add((row.Relation.Force, (int)value));
        }
        return changes;
    }

    internal List<(int Force, int Value)> AllianceChanges(out string error)
    {
        error = "";
        var changes = new List<(int, int)>();
        foreach (RelationRow row in relations.Where(r => r.Relation.CanAlly))
        {
            if (!TypedNumber.TryParse(row.Months.Text, out long value) || value is < 0 or > ForceFields.MaxAgreement)
            {
                error = L.Force.AllianceError(row.Relation.Name, ForceFields.MaxAgreement);
                row.Months.Focus();
                return [];
            }
            if (value != row.Relation.Agreement) changes.Add((row.Relation.Force, (int)value));
        }
        return changes;
    }

    internal List<FieldChange> Changes()
    {
        var changes = new List<FieldChange>();
        if (country.Picked != country.Current) changes.Add(new FieldChange(ForceFields.Country, country.Picked));
        if (pickedColor != currentColor) changes.Add(new FieldChange(ForceFields.Color, pickedColor));
        if (strategist.Picked != strategist.Current) changes.Add(new FieldChange(ForceFields.Strategist, strategist.Picked));
        if (rank.Picked != rank.Current) changes.Add(new FieldChange(ForceFields.Rank, rank.Picked));
        foreach (PolicyRow row in policyRows) changes.AddRange(row.Changes());
        return changes;
    }

    internal List<MilitaryPointRequest>? MilitaryChanges(out string error)
    {
        error = ""; var changes = new List<MilitaryPointRequest>();
        foreach (var input in military.Where(m => m.Number.IsChanged))
        {
            if (militaryCaps is not { Valid: true }) { error = L.Force.MilitaryUnavailable; return null; }
            int maximum = militaryCaps.ForRank(rank.Picked);
            if (!input.Number.TryRead(out long value, out error)) return null;
            if (value > maximum) { error = L.Force.MilitaryRange(maximum); input.Number.Box.Focus(); return null; }
            changes.Add(new(input.Row.Corps, input.Row.Number, input.Row.Leader, (int)value));
        }
        return changes;
    }

    private void Mark()
    {
        if (filling) return;
        country.Mark();
        rank.Mark();
        strategist.Mark();
        foreach (PolicyRow row in policyRows) row.Mark();
        for (int i = 0; i < colours.Length; i++)
            colours[i].BorderBrush = i == pickedColor ? (pickedColor != currentColor ? CardSkin.Highlight : CardSkin.DarkWood) : WM.Brushes.Transparent;
        Shield.Colour = System.Drawing.Color.FromArgb(unchecked((int)0xFF000000) | MapView.GamePalette[pickedColor]);
        int typed = 0;
        foreach (RelationRow row in relations)
        {
            bool number = TypedNumber.TryParse(row.Box.Text, out long value);
            bool valid = number && value is >= 0 and <= ForceFields.MaxFriendship;
            bool changed = !number || value != row.Relation.Friendship;
            row.Box.Background = changed ? CardSkin.Pending : CardSkin.Paper;
            row.Box.Foreground = valid ? CardSkin.Ink : CardSkin.Poor;
            row.Box.ToolTip = changed ? L.Card.GameValue(row.Relation.Friendship) : L.Force.FriendshipTip(ForceFields.MaxFriendship);
            if (changed) typed++;
            if (!row.Relation.CanAlly) continue;
            number = TypedNumber.TryParse(row.Months.Text, out value);
            valid = number && value is >= 0 and <= ForceFields.MaxAgreement;
            changed = !number || value != row.Relation.Agreement;
            row.Months.Background = changed ? CardSkin.Pending : CardSkin.Paper;
            row.Months.Foreground = valid ? CardSkin.Ink : CardSkin.Poor;
            row.Months.ToolTip = changed ? L.Card.GameValue(row.Relation.Agreement) : L.Force.AllianceTip(ForceFields.MaxAgreement);
            if (changed) typed++;
        }
        foreach (var input in military)
        {
            int maximum = militaryCaps is { Valid: true } caps ? caps.ForRank(rank.Picked) : 0;
            input.Limit.Text = L.Force.MilitaryLimit(maximum);
            input.Number.Box.IsEnabled = militaryCaps is { Valid: true };
            input.Number.Box.Foreground = input.Number.IsValid && input.Number.Value <= maximum ? CardSkin.Ink : CardSkin.Poor;
            input.Number.Box.ToolTip = L.Force.MilitaryRange(maximum);
            if (input.Number.IsChanged) typed++;
        }
        pending = Changes().Count + typed;
        Count();
    }

    // One field of 정책: retain all seven native slot positions, including empty slots.
    private sealed class PolicyRow
    {
        internal readonly WC.StackPanel Panel = new() { Margin = new W.Thickness(0, 2, 0, 4) };
        private readonly WC.WrapPanel chips = new();
        private readonly WC.Border frame = new() { BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(1), Padding = new W.Thickness(3), MinHeight = 26 };
        private readonly WC.ComboBox add = Choice(170);
        private (int Officer, int Days)[] current = new (int, int)[ForceFields.PolicySlots];
        private List<PolicyHolder> offered = [];
        private bool filling;

        internal int Field { get; }
        internal List<(int Officer, int Days)> Picked { get; private set; } = [];

        internal event Action? Changed, Taken;

        internal PolicyRow(int field)
        {
            Field = field;
            add.Margin = new W.Thickness(8, 0, 0, 0);
            add.ToolTip = L.Force.AddPolicyHolderTip(ForceFields.PolicyFields[field].Name);
            var line = new WC.DockPanel { LastChildFill = false };
            line.Children.Add(new WC.TextBlock { Text = ForceFields.PolicyFields[field].Name, FontWeight = W.FontWeights.SemiBold, Width = 40, VerticalAlignment = W.VerticalAlignment.Center });
            line.Children.Add(add);
            frame.Child = chips;
            Panel.Children.Add(line);
            Panel.Children.Add(frame);
            add.SelectionChanged += (_, _) =>
            {
                if (filling || add.SelectedIndex < 0) return;
                PolicyHolder holder = offered[add.SelectedIndex];
                int empty = Picked.FindIndex(p => p.Officer == 0);
                if (empty >= 0 && !Picked.Any(p => p.Officer == holder.Officer)) Picked[empty] = (holder.Officer, 1);
                add.Dispatcher.BeginInvoke(() => Taken?.Invoke());
            };
        }

        internal void Open(IReadOnlyList<(int Officer, int Days)> slots)
        {
            current = [.. slots];
            Picked = [.. slots];
        }

        internal bool Follow(IReadOnlyList<(int Officer, int Days)> slots)
        {
            bool changed = !current.SequenceEqual(slots);
            for (int slot = 0; slot < ForceFields.PolicySlots; slot++)
                if (Picked[slot] == current[slot]) Picked[slot] = slots[slot];
            current = [.. slots];
            return changed;
        }

        // Days left over one go to one, so that the game puts them to work the next day itself.
        internal void Hurry() => Picked = Picked.Select(p => p.Officer != 0 && p.Days > 1 ? (p.Officer, 1) : p).ToList();

        internal void Render(Dictionary<int, PolicyHolder> holders, List<PolicyHolder> candidates)
        {
            filling = true;
            chips.Children.Clear();
            for (int i = 0; i < Picked.Count; i++)
            {
                int index = i;
                (int officer, int days) = Picked[i];
                if (officer == 0) continue;
                PolicyHolder? holder = holders.TryGetValue(officer, out PolicyHolder known) ? known : null;
                string text = (holder?.Label ?? L.Terms.OfficerNumber(officer)) + (days > 0 ? (" · " + L.Force.PolicyDays(days)) : "");
                bool changed = i >= current.Length || current[i] != Picked[i];
                chips.Children.Add(Chip(text, (holder?.Tip ?? "") + (days > 0 ? ("\n" + L.Force.PolicyDaysTip(days)) : ("\n" + L.Force.PolicyInEffectTip)), changed, () =>
                {
                    Picked[index] = (0, 0);
                    Taken?.Invoke();
                }));
            }
            if (chips.Children.Count == 0) chips.Children.Add(new WC.TextBlock { Text = L.Terms.None, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(2, 1, 0, 1) });
            offered = candidates;
            add.ItemsSource = offered.Select(h => h.Label).ToList();
            add.SelectedIndex = -1;
            add.IsEnabled = offered.Count > 0 && Picked.Any(p => p.Officer == 0);
            filling = false;
            Changed?.Invoke();
        }

        private bool IsChanged => Enumerable.Range(0, ForceFields.PolicySlots).Any(slot => Slot(slot) != current[slot]);

        private (int Officer, int Days) Slot(int slot) => slot < Picked.Count ? Picked[slot] : (0, 0);

        internal void Mark() => frame.Background = IsChanged ? CardSkin.Pending : WM.Brushes.Transparent;

        internal IEnumerable<FieldChange> Changes()
        {
            for (int slot = 0; slot < ForceFields.PolicySlots; slot++)
            {
                (int officer, int days) = Slot(slot);
                if (officer != current[slot].Officer) yield return new FieldChange(ForceFields.PolicyOfficer(Field, slot), officer);
                if (days != current[slot].Days) yield return new FieldChange(ForceFields.PolicyDays(Field, slot), days);
            }
        }
    }
}
