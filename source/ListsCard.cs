using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;

namespace San14Tenkazu;

// The right pane's lists, grouped by task, each with its search box, filters and
// count over its table. Number cells share drafts across filters/tabs and save through the footer.
internal sealed class ListsCard : PanePage
{
    private readonly WC.WrapPanel tabStrip = new();
    private readonly WC.WrapPanel categories = new();
    private readonly Dictionary<string, PaneTab> categoryTabs = [];
    private readonly Dictionary<string, int> lastCategoryTabs = [];
    private readonly WC.Grid pages = new();
    private readonly List<(PaneTab Tab, W.UIElement Page, GameTable Table, PaneSection Section)> tabs = [];
    private readonly Dictionary<PaneSection, int> lastTabs = [];
    private readonly PlateButton resume = new(L.Navigation.Resume, icon: Icons.Details) { Visibility = W.Visibility.Collapsed };
    private readonly WC.TextBlock ancientTitle = new() { Text = L.Navigation.AncientTool, VerticalAlignment = W.VerticalAlignment.Center, Visibility = W.Visibility.Collapsed };
    private readonly PlateButton apply = new(L.Card.Apply, primary: true, icon: Icons.Apply) { ToolTip = L.Lists.DraftApplyTip };
    private readonly WC.TextBlock draftScope = new() { Text = L.Lists.DraftScope, Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
    private bool canApply;
    internal TableDrafts TableDrafts { get; } = new();
    internal int Pending => TableDrafts.Pending;
    internal event Action? ApplyClicked;
    internal bool ApplyEnabled { set { canApply = value; UpdateDrafts(); } }

    private void UpdateDrafts()
    {
        apply.Badge = Pending;
        apply.Text = Pending > 0 ? $"{L.Card.Apply} ({Pending:N0})" : L.Card.Apply;
        apply.IsEnabled = canApply && Pending > 0;
    }

    internal event Action? TabChanged, ResumeSelected;

    internal ListsCard()
    {
        TableDrafts.Changed += UpdateDrafts;
        var navigation = new WC.DockPanel();
        WC.DockPanel.SetDock(resume, WC.Dock.Right);
        navigation.Children.Add(resume);
        navigation.Children.Add(ancientTitle);
        navigation.Children.Add(tabStrip);
        foreach (string name in new[] { "무장·물품", "전투", "내정·세력" })
        {
            var category = new PaneTab(name);
            category.Click += (_, _) => ShowTab(lastCategoryTabs.TryGetValue(name, out int previous)
                ? previous : tabs.FindIndex(t => t.Section == PaneSection.Data && Category(tabs.IndexOf(t)) == name));
            categories.Children.Add(category); categoryTabs.Add(name, category);
        }
        var navigationRows = new WC.StackPanel();
        navigationRows.Children.Add(categories); navigationRows.Children.Add(navigation);
        var top = PaneChrome.Toolbar(navigationRows);
        pages.Margin = new W.Thickness(12, 8, 12, 12);
        var layout = new WC.DockPanel();
        var footer = new WC.DockPanel();
        WC.DockPanel.SetDock(apply, WC.Dock.Right); footer.Children.Add(apply); footer.Children.Add(draftScope);
        var bottom = PaneChrome.Footer(footer); WC.DockPanel.SetDock(bottom, WC.Dock.Bottom); layout.Children.Add(bottom);
        WC.DockPanel.SetDock(top, WC.Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(pages);
        Inner.Child = layout;
        resume.Click += () => ResumeSelected?.Invoke();
        apply.Click += () => ApplyClicked?.Invoke();
        UpdateDrafts();
    }

    internal void FocusInvalidInput()
    {
        if (TableDrafts.InvalidCell is not { } cell) return;
        for (int i = 0; i < tabs.Count; i++)
        {
            int index = i;
            if (tabs[i].Table.FocusDraft(cell.Target, cell.Field, () => { ShowTab(index); UpdateLayout(); })) return;
        }
    }

    // The tab shown (the order the pages were added in).
    internal int Tab { get; private set; }
    internal PaneSection Section { get; private set; } = PaneSection.Game;

    // A page under its tab: the filters and the count over the table, in a frame named as the tab.
    internal void AddPage(string name, GameTable table, W.UIElement[] filters, WC.TextBlock count, PaneSection section = PaneSection.Game)
    {
        int index = tabs.Count;
        var tab = new PaneTab(name, capsule: section == PaneSection.Data);
        tab.Click += (_, _) => ShowTab(index);
        tabStrip.Children.Add(tab);

        var bar = new WC.WrapPanel { Margin = new W.Thickness(0, 0, 0, 2) };
        foreach (W.UIElement filter in filters)
        {
            if (filter is W.FrameworkElement element) element.Margin = new W.Thickness(0, 0, 6, 4);
            bar.Children.Add(filter);
        }
        count.Foreground = CardSkin.InkMuted;
        count.Margin = new W.Thickness(0, 2, 0, 6);
        count.VerticalAlignment = W.VerticalAlignment.Center;

        var page = new WC.DockPanel();
        WC.DockPanel.SetDock(bar, WC.Dock.Top);
        page.Children.Add(bar);
        WC.DockPanel.SetDock(count, WC.Dock.Top);
        page.Children.Add(count);
        page.Children.Add(table);
        pages.Children.Add(page);
        tabs.Add((tab, page, table, section));
        ShowTab(Tab);
    }

    internal void ShowTab(int index)
    {
        if (tabs.Count == 0) return;
        int before = Tab;
        Tab = Math.Clamp(index, 0, tabs.Count - 1);
        Section = tabs[Tab].Section;
        lastTabs[Section] = Tab;
        string selectedCategory = Category(Tab);
        if (Section == PaneSection.Data) lastCategoryTabs[selectedCategory] = Tab;
        categories.Visibility = Section == PaneSection.Data ? W.Visibility.Visible : W.Visibility.Collapsed;
        tabStrip.Margin = Section == PaneSection.Data ? new W.Thickness(0, 3, 0, 0) : new W.Thickness(0);
        foreach (var pair in categoryTabs) pair.Value.Selected = pair.Key == selectedCategory;
        for (int i = 0; i < tabs.Count; i++)
        {
            tabs[i].Tab.Selected = i == Tab;
            tabs[i].Tab.Visibility = tabs[i].Section == Section && Section != PaneSection.Ancients
                && (Section != PaneSection.Data || Category(i) == selectedCategory) ? W.Visibility.Visible : W.Visibility.Collapsed;
            tabs[i].Page.Visibility = i == Tab ? W.Visibility.Visible : W.Visibility.Collapsed;
        }
        ancientTitle.Visibility = Section == PaneSection.Ancients ? W.Visibility.Visible : W.Visibility.Collapsed;
        tabStrip.Visibility = Section == PaneSection.Ancients ? W.Visibility.Collapsed : W.Visibility.Visible;
        if (Tab != before) TabChanged?.Invoke();
    }

    private static string Category(int index) => (ListKind)index switch
    {
        ListKind.Formations or ListKind.Tactics => "전투",
        ListKind.Policies or ListKind.PolicyEffects or ListKind.Offices or ListKind.Ranks or ListKind.Doctrines => "내정·세력",
        _ => "무장·물품"
    };

    internal void ShowSection(PaneSection section)
    {
        if (lastTabs.TryGetValue(section, out int previous)) ShowTab(previous);
        else
        {
            int first = tabs.FindIndex(page => page.Section == section);
            if (first >= 0) ShowTab(first);
            else Section = section;
        }
    }

    internal void SetResume(string tooltip, int pending, bool shown)
    {
        resume.ToolTip = tooltip;
        resume.Badge = pending;
        resume.Visibility = shown ? W.Visibility.Visible : W.Visibility.Collapsed;
    }

    // Portraits decoded since the tables were drawn.
    internal void RefreshFaces()
    {
        foreach (var page in tabs) page.Table.RefreshFaces();
    }

}

// A search box on paper with its hint in grey while it is empty.
internal sealed class SearchBox : WC.Grid
{
    internal SearchBox(string hint, double width = 150)
    {
        Width = width;
        Box = CardSkin.TextBox();
        var shown = new WC.TextBlock { Text = hint, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(5, 0, 0, 0), VerticalAlignment = W.VerticalAlignment.Center, IsHitTestVisible = false };
        Box.TextChanged += (_, _) => shown.Visibility = Box.Text.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        Children.Add(Box);
        Children.Add(shown);
    }

    internal WC.TextBox Box { get; }

    internal string Text
    {
        get => Box.Text;
        set => Box.Text = value;
    }

    // The keyboard in the box, what is typed in it picked, so typing replaces it. Once laid out: a page just shown
    // cannot take the keyboard yet.
    internal void FocusAll() => Dispatcher.BeginInvoke(() =>
    {
        Box.Focus();
        WI.Keyboard.Focus(Box);
        Box.SelectAll();
    }, System.Windows.Threading.DispatcherPriority.Input);
}
