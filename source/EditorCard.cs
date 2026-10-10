using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// What an editor card shows: a force, a city, a gate, an officer, a unit (by slot and commander: a slot is used again
// once its unit is gone), a 개성 or a 명품. The cards' links and their ◀ ▶ history are made of these.
internal abstract record EditorTarget;
internal sealed record ForceTarget(int Force) : EditorTarget;
internal sealed record CityTarget(int City) : EditorTarget;
internal sealed record GateTarget(int Gate) : EditorTarget;
internal sealed record FacilityTarget(int Index, int Kind, int Tile, int Force) : EditorTarget
{
    internal static FacilityTarget Of(Facility facility) => new(facility.Index, facility.Kind, facility.Tile, facility.Force);
    internal bool Matches(Facility facility) => Index == facility.Index && Kind == facility.Kind && Tile == facility.Tile && Force == facility.Force;
    internal Facility? Find(GameSnapshot data) => data.Facility(Index, Kind, Tile, Force);
}
internal sealed record OfficerTarget(int Officer) : EditorTarget;
internal sealed record UnitTarget(int Slot, int Commander) : EditorTarget;
internal sealed record PersonalityTarget(int Personality) : EditorTarget;
internal sealed record ItemTarget(int Item) : EditorTarget;
internal sealed record FormationTarget(int Formation) : EditorTarget;
internal sealed record TacticTarget(int Tactic) : EditorTarget;
internal sealed record PolicyTarget(string Table, int Id) : EditorTarget;
internal sealed record OfficeTarget(int Office) : EditorTarget;
internal sealed record PlainRuleTarget(string Table,int Record) : EditorTarget;

// The frame of every editor card: ◀ ▶ back and forth through the cards opened before, the force's shield, a
// title and a subtitle whose names open their own cards, [위치로 가기] for what has a place on the map, inner tabs for
// a long card, and [변경 적용] with the count of the values it will write. One editor opens at a time in MapView's right pane.
internal abstract class EditorCard : PanePage
{
    private readonly WC.TextBlock back = HistoryMark("◀", L.Card.BackTip), forward = HistoryMark("▶", L.Card.ForwardTip);
    protected readonly ForceShield Shield = new() { Width = 17, Height = 22, Margin = new W.Thickness(0, 0, 6, 0), VerticalAlignment = W.VerticalAlignment.Top };
    protected readonly WC.TextBlock Title = new() { FontSize = 16, FontWeight = W.FontWeights.Bold, TextWrapping = W.TextWrapping.Wrap };
    protected readonly WC.TextBlock Subtitle = new() { Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 2, 0, 0), TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.WrapPanel tabStrip = new() { Margin = new W.Thickness(0, 8, 0, 0) };
    private readonly WC.Grid pages = new();
    private readonly List<(PaneTab Tab, W.UIElement Page)> tabs = [];
    private readonly WC.ScrollViewer scroll = new() { VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, Margin = new W.Thickness(12, 8, 8, 8), Padding = new W.Thickness(0, 0, 4, 0) };
    private readonly PlateButton apply = new(L.Card.Apply, primary: true, Icons.Apply, iconOnly: true) { ToolTip = L.Card.ApplyTip };
    // Buttons that fill in values, left of [변경 적용].
    protected readonly WC.WrapPanel Shortcuts = new() { VerticalAlignment = W.VerticalAlignment.Center };
    // [위치로 가기] under the close mark, on the cards of things with a place on the map (OffersGoTo).
    private readonly W.FrameworkElement goTo;
    private int shown;

    internal event Action? ApplyClicked, CloseClicked, BackClicked, ForwardClicked, GoToClicked;
    internal event Action<EditorTarget>? LinkClicked;
    internal event Action? NavigationChanged;
    internal string NavigationTitle => Title.Text;

    protected EditorCard()
    {
        var header = new WC.Grid { Margin = new W.Thickness(0, 0, 0, 4) };
        foreach (W.GridLength length in new[] { W.GridLength.Auto, W.GridLength.Auto, new W.GridLength(1, W.GridUnitType.Star), W.GridLength.Auto })
            header.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = length });
        var history = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, VerticalAlignment = W.VerticalAlignment.Top, Margin = new W.Thickness(0, 1, 8, 0) };
        history.Children.Add(back);
        history.Children.Add(forward);
        var names = new WC.StackPanel();
        names.Children.Add(Title);
        names.Children.Add(Subtitle);
        // The pane owns closing; this header contains only the selected target and its actions.
        var corner = new WC.StackPanel { Margin = new W.Thickness(10, 0, 0, 0) };
        goTo = CardSkin.CornerMark(Icons.GoTo, "◎", L.Card.GoToTip, () => GoToClicked?.Invoke());
        goTo.Visibility = W.Visibility.Collapsed;
        corner.Children.Add(goTo);
        foreach ((W.UIElement element, int column) in new (W.UIElement, int)[] { (history, 0), (Shield, 1), (names, 2), (corner, 3) })
        {
            WC.Grid.SetColumn(element, column);
            header.Children.Add(element);
        }
        back.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (back.IsEnabled) BackClicked?.Invoke(); };
        forward.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (forward.IsEnabled) ForwardClicked?.Invoke(); };

        scroll.Content = pages;
        var footer = new WC.DockPanel { LastChildFill = true };
        WC.DockPanel.SetDock(apply, WC.Dock.Right);
        apply.Margin = new W.Thickness(8, 0, 0, 0);
        footer.Children.Add(apply);
        footer.Children.Add(Shortcuts);
        var bottom = PaneChrome.Footer(footer);
        var topContent = new WC.StackPanel();
        topContent.Children.Add(header);
        topContent.Children.Add(tabStrip);
        var top = PaneChrome.Toolbar(topContent);
        var layout = new WC.DockPanel();
        WC.DockPanel.SetDock(top, WC.Dock.Top);
        WC.DockPanel.SetDock(bottom, WC.Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(scroll);
        Inner.Child = layout;
        apply.Click += () => ApplyClicked?.Invoke();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != WI.Key.Escape) return;
            e.Handled = true;
            CloseClicked?.Invoke();
        };
    }

    // What the card shows now, null when nothing.
    internal abstract EditorTarget? Target { get; }

    // How many values [변경 적용] would write (the badge).
    internal abstract int Pending { get; }

    internal bool ApplyEnabled
    {
        set => apply.IsEnabled = value;
    }

    internal void SetHistory(bool canGoBack, bool canGoForward)
    {
        back.IsEnabled = canGoBack;
        forward.IsEnabled = canGoForward;
        back.Foreground = canGoBack ? CardSkin.Ink : CardSkin.Rule;
        forward.Foreground = canGoForward ? CardSkin.Ink : CardSkin.Rule;
    }

    // The badge follows the values typed in.
    protected void Count()
    {
        apply.Badge = Pending;
        NavigationChanged?.Invoke();
    }

    // The card shows [위치로 가기], which sends the game's own view to what it shows.
    protected void OffersGoTo() => goTo.Visibility = W.Visibility.Visible;

    // Another thing to show: the card starts at the top of its page (the page itself stays, as the user picked it).
    protected void StartOver() => scroll.ScrollToTop();

    // A page under a tab of its own; a card with one page shows no tabs.
    protected void AddPage(string name, W.UIElement page)
    {
        int index = tabs.Count;
        var tab = new PaneTab(name);
        tab.Click += (_, e) =>
        {
            e.Handled = true;
            ShowPage(index);
        };
        tabs.Add((tab, page));
        tabStrip.Children.Add(tab);
        pages.Children.Add(page);
        tabStrip.Visibility = tabs.Count > 1 ? W.Visibility.Visible : W.Visibility.Collapsed;
        ShowPage(shown);
    }

    protected void ShowPage(int index)
    {
        shown = Math.Clamp(index, 0, tabs.Count - 1);
        for (int i = 0; i < tabs.Count; i++)
        {
            bool on = i == shown;
            tabs[i].Page.Visibility = on ? W.Visibility.Visible : W.Visibility.Collapsed;
            tabs[i].Tab.Selected = on;
        }
        scroll.ScrollToTop();
    }

    // A name that opens its own card.
    protected WD.Hyperlink Link(string text, EditorTarget target)
    {
        var link = new WD.Hyperlink(new WD.Run(text)) { Foreground = CardSkin.Ink, TextDecorations = W.TextDecorations.Underline, Cursor = WI.Cursors.Hand };
        link.Click += (_, _) => LinkClicked?.Invoke(target);
        return link;
    }

    protected void RaiseLink(EditorTarget target) => LinkClicked?.Invoke(target);

    // A text block of plain text and links, such as "조조군 · 일반 · 복양".
    protected static void Fill(WC.TextBlock block, IEnumerable<WD.Inline> parts)
    {
        block.Inlines.Clear();
        bool first = true;
        foreach (WD.Inline part in parts)
        {
            if (!first) block.Inlines.Add(new WD.Run(" · "));
            block.Inlines.Add(part);
            first = false;
        }
    }

    protected static WC.TextBlock HistoryMark(string text, string tip) => new()
    {
        Text = text, FontSize = 13, Padding = new W.Thickness(3, 1, 3, 1), Cursor = WI.Cursors.Hand, ToolTip = tip, Foreground = CardSkin.Rule
    };

    // ---- What the cards are made of -------------------------------------------------------------------

    internal static WC.ComboBox Choice(double width)
    {
        WC.ComboBox box = CardSkin.ChoiceBox();
        box.Width = width;
        box.MaxDropDownHeight = 320;
        box.Margin = new W.Thickness(0, 2, 0, 2);
        box.HorizontalAlignment = W.HorizontalAlignment.Left;
        // WPF picks the next name when the wheel turns over a closed box that has focus (as a click to look at the
        // names leaves it), so scrolling a card typed values unseen: the wheel scrolls what holds the box instead,
        // and only an open list takes it.
        box.PreviewMouseWheel += (_, e) =>
        {
            if (box.IsDropDownOpen) return;
            e.Handled = true;
            if (WM.VisualTreeHelper.GetParent(box) is W.UIElement parent)
                parent.RaiseEvent(new WI.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = W.UIElement.MouseWheelEvent, Source = box });
        };
        return box;
    }

    // A box for picking one of some thousand officers: typing a name finds it (the list scrolls lazily).
    internal static WC.ComboBox OfficerChoice()
    {
        WC.ComboBox box = Choice(196);
        box.IsEditable = true;
        box.IsTextSearchEnabled = true;
        box.ItemsPanel = new WC.ItemsPanelTemplate(new W.FrameworkElementFactory(typeof(WC.VirtualizingStackPanel)));
        return box;
    }

    internal static WC.Border Heading(string text) => new()
    {
        BorderBrush = CardSkin.Gold, BorderThickness = new W.Thickness(0, 0, 0, 1), Margin = new W.Thickness(0, 10, 0, 4), Padding = new W.Thickness(0, 0, 0, 2),
        Child = new WC.TextBlock { Text = text, FontWeight = W.FontWeights.Bold, Foreground = CardSkin.Wood }
    };

    internal static WC.TextBlock Label(string text, double left = 0) => new()
    {
        Text = text, VerticalAlignment = W.VerticalAlignment.Center, Margin = new W.Thickness(left, 0, 8, 0)
    };

    // One line of a card: a label in a column of its own, then the controls.
    internal static WC.DockPanel Line(string label, params W.UIElement[] content)
        => Line(Label(label), content);

    internal static WC.DockPanel Line(WC.TextBlock text, params W.UIElement[] content)
    {
        var line = new WC.DockPanel { Margin = new W.Thickness(0, 2, 0, 2) };
        text.Width = 84;
        text.TextWrapping = W.TextWrapping.Wrap;
        text.Foreground = CardSkin.InkMuted;
        WC.DockPanel.SetDock(text, WC.Dock.Left);
        line.Children.Add(text);
        var controls = new WC.WrapPanel { VerticalAlignment = W.VerticalAlignment.Center };
        foreach (W.UIElement element in content)
        {
            if (element is WC.TextBox or WC.ComboBox) ((W.FrameworkElement)element).MinHeight = 26;
            controls.Children.Add(element);
        }
        line.Children.Add(controls);
        return line;
    }

    // A name on a small paper plate; added ones are ringed, and those that can be taken back have a ✕.
    internal static WC.Border Chip(string text, string tip, bool added, Action? remove, Action? open = null)
    {
        var content = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        var name = new WC.TextBlock { Text = text };
        if (open is not null)
        {
            name.Cursor = WI.Cursors.Hand;
            name.TextDecorations = W.TextDecorations.Underline;
            name.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                open();
            };
        }
        content.Children.Add(name);
        if (remove is not null)
        {
            var mark = new WC.TextBlock { Text = "✕", Foreground = CardSkin.InkMuted, Margin = new W.Thickness(5, 0, 0, 0), Cursor = WI.Cursors.Hand, ToolTip = L.Card.RemoveTip };
            mark.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                remove();
            };
            content.Children.Add(mark);
        }
        return new WC.Border
        {
            Child = content, Background = CardSkin.Paper, BorderBrush = added ? CardSkin.Highlight : CardSkin.Rule, BorderThickness = new W.Thickness(added ? 2 : 1),
            CornerRadius = new W.CornerRadius(3), Padding = new W.Thickness(5, 1, 5, 1), Margin = new W.Thickness(0, 1, 4, 1), ToolTip = tip.Length > 0 ? tip : null
        };
    }

    // A small plate button for a shortcut that types values (they are written by [변경 적용]).
    internal static PlateButton Shortcut(string text, string tip, Action run, string? icon = null)
    {
        var button = new PlateButton(text, icon: icon ?? Icons.Fill) { ToolTip = tip, Margin = new W.Thickness(0, 2, 6, 2) };
        button.Click += run;
        return button;
    }
}

// A number box for one field. It follows the game until it is typed in; a value that differs from the game's is
// yellow (and red when it is not a valid number).
internal sealed class NumberField
{
    private bool typed, filling;

    internal NumberField(FieldDef field, double width = 88)
    {
        Field = field;
        Box = CardSkin.NumberBox(width);
        Box.Margin = new W.Thickness(0, 2, 6, 2);
        Box.TextChanged += (_, _) =>
        {
            if (filling) return;
            typed = !MatchesCurrent;
            Mark();
            Edited?.Invoke();
        };
        Box.LostKeyboardFocus += (_, _) => { if (!typed) Write(Current); };
    }

    internal FieldDef Field { get; }
    internal WC.TextBox Box { get; }
    // The game's value.
    internal long Current { get; private set; }

    internal event Action? Edited;

    // A new thing to show: the box takes the game's value and nothing is typed.
    internal void Show(long value)
    {
        Current = value;
        typed = false;
        Write(value);
    }

    // The game's value now: a box nobody typed in (and that is not being typed in) follows it.
    internal void Follow(long value)
    {
        Current = value;
        if (typed && TypedNumber.TryParse(Box.Text, out long shown) && shown == value) typed = false;
        if (!typed && !Box.IsKeyboardFocused) Write(value);
        else Mark();
    }

    // A shortcut types a value, as a person would.
    internal void Set(long value)
    {
        string text = Format(Math.Clamp(value, Field.Min, Field.Max));
        if (Box.Text != text) { Box.Text = text; return; }
        // An untouched focused box can already display this value while Current has advanced.
        typed = !MatchesCurrent;
        Mark();
        Edited?.Invoke();
    }

    // A parsed draft, or the latest game value for an untouched or invalid box.
    internal long Value => typed && TypedNumber.TryParse(Box.Text, out long value) ? value : Current;

    private bool MatchesCurrent => TypedNumber.TryParse(Box.Text, out long value) && value == Current;

    // Focus can retain an older display during refresh; only an actual input creates a draft.
    internal bool IsChanged => typed && !MatchesCurrent;

    internal bool IsValid => TypedNumber.TryParse(Box.Text, out long value) && value >= Field.Min && value <= Field.Max;

    // The value to write, or false (with the box focused) when it is not valid.
    internal bool TryRead(out long value, out string error)
    {
        error = "";
        value = Current;
        if ((!typed || TypedNumber.TryParse(Box.Text, out value)) && value >= Field.Min && value <= Field.Max) return true;
        error = Field.RangeMessage;
        Box.Focus();
        return false;
    }

    private void Write(long value)
    {
        filling = true;
        Box.Text = Format(value);
        filling = false;
        Mark();
    }

    private string Format(long value) => value.ToString(OfficerFields.Years.Contains(Field) ? "0" : "N0", CultureInfo.CurrentCulture);

    private void Mark()
    {
        bool changed = IsChanged;
        Box.Background = changed ? CardSkin.Pending : CardSkin.Paper;
        Box.Foreground = IsValid ? CardSkin.Ink : CardSkin.Poor;
        Box.ToolTip = changed ? L.Card.GameValue(Format(Current)) : $"{Field.Label}: {Field.Min:N0}~{Field.Max:N0}" + (Field.Hint.Length > 0 ? $" ({Field.Hint})" : "");
    }
}

// A choice among names the game gives numbers ("없음" first unless none is false). A number the list lacks
// (an officer has a 개성 the table leaves unnamed) is added while the card shows it, so it is never lost.
internal sealed class NameChoice(double width, bool none = true)
{
    internal readonly WC.ComboBox Box = EditorCard.Choice(width);
    private readonly List<int> values = [];
    private readonly List<WC.ComboBoxItem> items = [];
    private readonly List<string> descriptions = [];
    private int named;

    internal int Current { get; private set; }

    internal void SetNames(IEnumerable<GameName> names)
    {
        values.Clear();
        items.Clear();
        descriptions.Clear();
        if (none) Add(0, L.Terms.None, "");
        foreach (GameName name in names) Add(name.Id, name.Name, name.Description);
        named = values.Count;
        Box.ItemsSource = items.ToList();
    }

    private void Add(int id, string name, string description)
    {
        values.Add(id);
        descriptions.Add(description);
        items.Add(new WC.ComboBoxItem { Content = name, ToolTip = description.Length > 0 ? description : null });
    }

    internal void Show(int value)
    {
        Current = value;
        values.RemoveRange(named, values.Count - named);
        items.RemoveRange(named, items.Count - named);
        descriptions.RemoveRange(named, descriptions.Count - named);
        if (!values.Contains(value)) Add(value, $"#{value}", L.Card.UnnamedNumberTip);
        Box.ItemsSource = items.ToList();
        Box.SelectedIndex = values.IndexOf(value);
    }

    // Names that cannot be picked are greyed, with why as their tooltip (problem gives "" for one that can); the
    // game's own stays as it is.
    internal void Allow(Func<int, string> problem)
    {
        for (int i = 0; i < items.Count; i++)
        {
            string why = values[i] == Current ? "" : problem(values[i]);
            items[i].IsEnabled = why.Length == 0;
            items[i].ToolTip = why.Length > 0 ? why : descriptions[i].Length > 0 ? descriptions[i] : null;
            WC.ToolTipService.SetShowOnDisabled(items[i], true);
        }
    }

    // Picks a value as a person would (nothing when the list lacks it).
    internal void Pick(int value)
    {
        int index = values.IndexOf(value);
        if (index >= 0) Box.SelectedIndex = index;
    }

    internal int Picked => Box.SelectedIndex >= 0 ? values[Box.SelectedIndex] : Current;

    internal void Follow(int value)
    {
        if (value == Current) return;
        int picked = Picked;
        bool pending = picked != Current;
        Show(value);
        if (pending) Pick(picked);
    }

    // Yellow when changed; the box's tooltip is the picked name's description. True when changed.
    internal bool Mark()
    {
        bool changed = Picked != Current;
        Box.Background = changed ? CardSkin.Pending : CardSkin.Paper;
        Box.ToolTip = Box.SelectedItem is WC.ComboBoxItem { ToolTip: string description } ? description : null;
        return changed;
    }
}
