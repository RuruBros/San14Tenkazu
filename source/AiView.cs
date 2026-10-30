using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// The AI view in the right pane ([AI] tab). A decision picker opens the settings for that decision,
// with the numbers that set it: the file's value and, when the editor is connected to the verified build, the game's
// in a box to change (AiGuide): [적용] writes the boxes changed through the editor's write (the question, [되돌리기]),
// [기본값으로] fills the current page's inputs; applying saves drafts from all decision pages. 트리 그대로 보기 shows the trees themselves for checking: the four trees and a
// search, a node a line opened as far as asked, and the node picked with each of its params (Core/Ai,
// source/Reverse/notes/ai.md).
internal sealed partial class AiView : WC.DockPanel
{
    private const int SearchLimit = 300;

    private readonly AiTree[] trees;
    private readonly Func<GameIdentity?> game;
    private readonly Func<string, IReadOnlyList<(AiGuideValue Value, int Raw)>, Task<bool>> write;
    private AiLiveTree[]? live;
    // The decision shown and its boxes: each with the value the game held when shown and the file's.
    private AiGuidePage? page;
    private readonly Dictionary<AiGuideValue, (WC.TextBox Box, int? Now, int? File)> boxes = [];
    private readonly PlateButton apply = new(L.Ai.Apply, primary: true, Icons.Apply) { ToolTip = L.Ai.ApplyTip, MinWidth = 110 };
    private readonly PlateButton defaults = new(L.Ai.Defaults, icon: Icons.Revert) { ToolTip = L.Ai.DefaultsTip };
    private readonly WC.ComboBox pageList = EditorCard.Choice(double.NaN);
    private readonly WC.Border host = new();
    private readonly WC.StackPanel guide = new() { Margin = new W.Thickness(12, 10, 12, 16) };
    private readonly WC.ScrollViewer guidePage;
    private readonly WC.Grid treePage = new();
    private readonly WC.ListBox treeList = List(), results = List(fill: true);
    private readonly WC.TreeView view = new() { Background = CardSkin.Paper, BorderBrush = CardSkin.Rule, Padding = new W.Thickness(2) };
    private readonly WC.TextBox search = CardSkin.TextBox();
    private readonly WC.StackPanel details = new() { Margin = new W.Thickness(10, 6, 10, 10) };
    private readonly WC.TextBlock status = new() { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.TextBlock found = Muted();
    private readonly PlateButton reread = new(L.Ai.Reread, icon: Icons.Reload, iconOnly: true) { ToolTip = L.Ai.RereadTip };
    private readonly WT.DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Dictionary<int, WC.TreeViewItem> items = [];
    private readonly Dictionary<AiTreeKind, string[]> lines = [];
    private AiTree tree;
    private AiNode? picked;
    private readonly PlateButton treeButton = new(L.AiGuide.TreeView, icon: Icons.Ai);
    private readonly PlateButton settingsButton = new(L.Ai.BackToSettings, icon: Icons.Revert);
    private readonly WC.DockPanel settingsNavigation = new();
    private readonly WC.DockPanel settingsActions = new();
    private bool loaded, writing;
    internal OperationProgressSlot ProgressSlot { get; } = new();
    internal bool ApplyShown => settingsActions.IsVisible;

    internal event Action<bool>? ExpandedChanged;
    internal event Action? CloseClicked;
    internal bool Expanded { get; private set; }
    internal bool Pending => GuidePending || ExtensionPending;
    private bool GuidePending => boxes.Any(b => IsDraft(b.Key, b.Value));
    internal void DiscardDrafts()
    {
        guide.Children.Clear();
        boxes.Clear();
        supplyProtection.IsChecked = extensionSaved.SupplyProtection;
        supplyFailure.SelectedIndex = (int)extensionSaved.SupplyFailure;
        extensionScope.SelectedIndex = (int)extensionSaved.Scope;
        UpdateExtensionActions();
    }
    internal Task RereadAsync() => ReadGameAsync();

    internal void Disconnect()
    {
        live = null;
        extensionRuntime = null;
        extensionReadError = null;
        status.Text = L.Ai.LiveNotConnected;
        Refresh();
    }

    internal void ShowSettings()
    {
        Expanded = false;
        extensionTabs.Visibility = W.Visibility.Visible;
        settingsNavigation.Visibility = extensionSelected ? W.Visibility.Collapsed : W.Visibility.Visible;
        settingsButton.Visibility = W.Visibility.Collapsed;
        settingsActions.Visibility = extensionSelected || boxes.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        host.Child = extensionSelected ? extensionPage : guidePage;
        UpdateExtensionActions();
        ExpandedChanged?.Invoke(false);
    }

    internal void ShowTreeView()
    {
        Expanded = true;
        extensionTabs.Visibility = W.Visibility.Collapsed;
        settingsNavigation.Visibility = W.Visibility.Collapsed;
        settingsButton.Visibility = W.Visibility.Visible;
        settingsActions.Visibility = W.Visibility.Collapsed;
        host.Child = treePage;
        ExpandedChanged?.Invoke(true);
    }

    internal AiView(AiTree[] trees, Func<GameIdentity?> game, Func<string, IReadOnlyList<(AiGuideValue Value, int Raw)>, Task<bool>> write,
        AiExtensionSettings? extensions = null, Func<AiExtensionSettings, Task<bool>>? writeExtensions = null)
    {
        this.trees = trees;
        this.game = game;
        this.write = write;
        this.writeExtensions = writeExtensions;
        extensionSaved = extensions ?? AiExtensionSettings.Default;
        tree = trees[(int)AiTreeKind.Corps];
        Background = CardSkin.Parchment;
        WD.TextElement.SetFontFamily(this, CardSkin.Font);
        WD.TextElement.SetFontSize(this, CardSkin.FontSize);
        WD.TextElement.SetForeground(this, CardSkin.Ink);
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
        UseLayoutRounding = true;

        // Settings fit the pane; the original tree gets the full workspace when requested.
        foreach (AiGuidePage page in AiGuide.Pages) pageList.Items.Add(AiLabels.Decision(page.Decision));
        pageList.SelectionChanged += (_, _) => ShowPage();
        guidePage = new WC.ScrollViewer { VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, Content = guide, Background = CardSkin.Paper };
        BuildExtensions();
        BuildTreePage();

        pageList.HorizontalAlignment = W.HorizontalAlignment.Stretch;
        pageList.MinHeight = 28;
        treeButton.Margin = new W.Thickness(6, 0, 0, 0);
        WC.DockPanel.SetDock(treeButton, WC.Dock.Right);
        settingsNavigation.Children.Add(treeButton);
        settingsNavigation.Children.Add(pageList);
        settingsButton.HorizontalAlignment = W.HorizontalAlignment.Left;
        settingsButton.Visibility = W.Visibility.Collapsed;
        treeButton.Click += ShowTreeView;
        settingsButton.Click += ShowSettings;
        host.Background = CardSkin.Paper;

        var bottom = new WC.DockPanel { LastChildFill = true };
        WC.DockPanel.SetDock(reread, WC.Dock.Right);
        bottom.Children.Add(reread);
        bottom.Children.Add(status);
        apply.Margin = new W.Thickness(8, 0, 0, 0);
        WC.DockPanel.SetDock(apply, WC.Dock.Right);
        settingsActions.Children.Add(apply);
        defaults.HorizontalAlignment = W.HorizontalAlignment.Left;
        ProgressSlot.SetRestingContent(defaults);
        settingsActions.Children.Add(ProgressSlot);
        settingsActions.Margin = new W.Thickness(0, 0, 0, 8);
        settingsActions.Visibility = W.Visibility.Collapsed;
        var footer = new WC.StackPanel();
        footer.Children.Add(settingsActions);
        footer.Children.Add(bottom);
        var bar = PaneChrome.Footer(footer);
        WC.DockPanel.SetDock(bar, WC.Dock.Bottom);
        Children.Add(bar);
        var navigation = new WC.StackPanel();
        navigation.Children.Add(extensionTabs);
        navigation.Children.Add(settingsNavigation);
        navigation.Children.Add(settingsButton);
        AddTop(this, PaneChrome.Toolbar(navigation));
        Children.Add(host);

        reread.Click += () => _ = ReadGameAsync();
        apply.Click += () => _ = extensionSelected ? ApplyExtensionsAsync() : ApplyAsync();
        defaults.Click += () => { if (extensionSelected) ResetExtensions(); else _ = DefaultsAsync(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != WI.Key.Escape || CardSkin.InOpenChoice(e.OriginalSource)) return;
            e.Handled = true;
            if (Expanded) ShowSettings(); else CloseClicked?.Invoke();
        };
        pageList.SelectedIndex = 0;
        Loaded += async (_, _) => { if (!loaded) { loaded = true; await ReadGameAsync(); } };
    }

    private void ShowPage()
    {
        int index = pageList.SelectedIndex;
        if (index < 0) return;
        if (index < AiGuide.Pages.Count)
        {
            host.Child = guidePage;
            ShowGuide(AiGuide.Pages[index]);
        }
    }

    // The game's trees, read again: they are the file's, but a value changed in this run shows on them.
    private async Task ReadGameAsync()
    {
        if (game() is not { } identity)
        {
            Disconnect();
            return;
        }
        reread.IsEnabled = false;
        status.Text = L.Ai.LiveReading;
        try
        {
            (AiLiveTree[] read, int differ, AiExtensionStatus? extensions, string? extensionError) = await Task.Run(() =>
            {
                AiLiveTree[] read = GameMemory.ReadAiTrees(identity, trees);
                int differ = GameMemory.ReadAi(identity, memory => read.Sum(t => AiMemory.Differences(memory, t).Count));
                try { return (read, differ, (AiExtensionStatus?)GameMemory.ReadAiExtensions(identity, trees), (string?)null); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { return (read, differ, (AiExtensionStatus?)null, ex.Message); }
            });
            if (game() != identity) return;
            live = read;
            extensionRuntime = extensions;
            extensionReadError = extensionError;
            status.Text = differ == 0 ? L.Ai.LiveSame : L.Ai.LiveDiffers(differ);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (game() != identity) return;
            live = null;
            extensionRuntime = null;
            extensionReadError = ex.Message;
            status.Text = L.Ai.LiveFailed(ex.Message);
        }
        finally { reread.IsEnabled = true; }
        Refresh();
    }

    private void Refresh()
    {
        if (extensionSelected && !Expanded) { ShowExtensionState(); UpdateExtensionActions(); }
        else if (host.Child == guidePage) ShowPage();
        else if (picked is not null) ShowDetails(picked);
    }

    // --- the guide: a decision in words, with its numbers

    private void ShowGuide(AiGuidePage page)
    {
        this.page = page;
        guide.Children.Clear();
        guide.Children.Add(new WC.TextBlock { Text = AiLabels.Decision(page.Decision), FontSize = CardSkin.FontSize + 3, FontWeight = W.FontWeights.Bold });
        guide.Children.Add(new WC.TextBlock { Text = page.Summary, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 4, 0, 6) });
        bool connected = live is not null && game() is not null;
        foreach (AiGuideSection section in page.Sections)
        {
            guide.Children.Add(new WC.TextBlock { Text = section.Heading, FontWeight = W.FontWeights.Bold, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 12, 0, 4) });
            var grid = new WC.Grid();
            grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(1, W.GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(96) });
            // The game column must include the number box, unit and margins at their measured widths.
            grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = connected ? W.GridLength.Auto : new W.GridLength(0) });
            grid.RowDefinitions.Add(new WC.RowDefinition { Height = W.GridLength.Auto });
            Cell(grid, 0, 1, L.AiGuide.ColumnDefault, CardSkin.InkMuted, right: true);
            if (connected) Cell(grid, 0, 2, L.AiGuide.ColumnGame, CardSkin.InkMuted, right: true);
            int row = 1;
            foreach (AiGuideValue value in section.Values)
            {
                grid.RowDefinitions.Add(new WC.RowDefinition { Height = W.GridLength.Auto });
                (string file, int? fileRaw, int? nowRaw, string? now, bool changed) = Numbers(value, connected);
                Cell(grid, row, 0, value.Label, CardSkin.Ink, wrap: true);
                Cell(grid, row, 1, file, CardSkin.Ink, right: true);
                if (connected && nowRaw is int raw) Box(grid, row, value, raw, fileRaw, changed);
                else if (now is not null) Cell(grid, row, 2, now, changed ? CardSkin.Pending : CardSkin.Ink, right: true, bold: changed);
                row++;
            }
            guide.Children.Add(grid);
        }
        guide.Children.Add(new WC.TextBlock { Text = L.AiGuide.Shared, Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 14, 0, 0) });
        UpdateExtensionActions();
        settingsActions.Visibility = boxes.Count > 0 && !Expanded ? W.Visibility.Visible : W.Visibility.Collapsed;
    }

    // The game's value in a box to change: shown as the guide reads it, its unit beside it; yellow once it differs
    // from what the game holds (as the cards mark a value to write).
    private void Box(WC.Grid grid, int row, AiGuideValue value, int now, int? fileRaw, bool changed)
    {
        bool existing = boxes.TryGetValue(value, out var previous);
        bool pending = existing && IsDraft(value, previous);
        WC.TextBox box = existing ? previous.Box : CardSkin.NumberBox(72);
        if (box.Parent is WC.Panel parent) parent.Children.Remove(box);
        box.MinHeight = 26;
        box.VerticalAlignment = W.VerticalAlignment.Center;
        string shown = AiText.Number(value.Shown(now));
        boxes[value] = (box, now, fileRaw);
        if (!pending) box.Text = shown;
        box.FontWeight = changed ? W.FontWeights.Bold : W.FontWeights.Normal;
        if (fileRaw is int file) box.ToolTip = L.Ai.OutOfRange(value.Label, AiEdits.Range(value, file).Min, AiEdits.Range(value, file).Max);
        if (!existing) box.TextChanged += (_, _) =>
        {
            if (!boxes.TryGetValue(value, out var current) || current.Box != box) return;
            UpdateDraft(value);
            UpdateExtensionActions();
        };
        UpdateDraft(value);
        var cell = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right, Margin = new W.Thickness(0, 1, 6, 1) };
        cell.Children.Add(box);
        cell.Children.Add(new WC.TextBlock { Text = value.Unit, Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center, MinWidth = 22, Margin = new W.Thickness(3, 0, 0, 0) });
        WC.Grid.SetRow(cell, row);
        WC.Grid.SetColumn(cell, 2);
        grid.Children.Add(cell);
    }

    private static bool TryRaw(AiGuideValue value, string text, out int raw)
    {
        raw = 0;
        if (!long.TryParse(text.Replace(",", "").Trim(), out long shown) || AiEdits.Raw(value, shown) is not int parsed) return false;
        raw = parsed; return true;
    }

    private static bool IsDraft(AiGuideValue value, (WC.TextBox Box, int? Now, int? File) input) =>
        !TryRaw(value, input.Box.Text, out int raw) || raw != input.Now;

    private static bool InRange(AiGuideValue value, int raw, int file)
    {
        long shown = (long)raw * value.Scale + value.Shift;
        var range = AiEdits.Range(value, file);
        return shown >= range.Min && shown <= range.Max;
    }

    private void UpdateDraft(AiGuideValue value)
    {
        var input = boxes[value];
        bool valid = TryRaw(value, input.Box.Text, out int raw) && (input.File ?? AiEdits.FileRaw(trees, value)) is int file && InRange(value, raw, file);
        input.Box.Background = IsDraft(value, input) ? CardSkin.Pending : CardSkin.Paper;
        input.Box.Foreground = valid ? CardSkin.Ink : CardSkin.Poor;
    }

    // [적용]: the boxes changed, as raw values, written through the editor (which asks first).
    private async Task ApplyAsync()
    {
        if (page is null || writing) return;
        var requests = new List<(AiGuideValue Value, int Raw)>();
        foreach ((AiGuideValue value, var input) in boxes)
        {
            if (!IsDraft(value, input)) continue;
            if (!TryRaw(value, input.Box.Text, out int raw) || (input.File ?? AiEdits.FileRaw(trees, value)) is not int file ||
                !InRange(value, raw, file))
            {
                status.Text = L.Ai.NotAStep(value.Label, value.Scale);
                int index = AiGuide.Pages.ToList().FindIndex(p => p.Sections.Any(s => s.Values.Contains(value)));
                if (index >= 0) pageList.SelectedIndex = index;
                input.Box.Focus();
                return;
            }
            if (raw != input.Now) requests.Add((value, raw));
        }
        if (requests.Count > 0) await WriteAsync(requests);
    }

    // [기본값으로] fills this decision's inputs only; the footer writes them with drafts from other pages.
    private Task DefaultsAsync()
    {
        if (page is not null && !writing)
            foreach (var value in page.Sections.SelectMany(s => s.Values))
                if (boxes.TryGetValue(value, out var input) && input.File is int file)
                    input.Box.Text = AiText.Number(value.Shown(file));
        return Task.CompletedTask;
    }

    private async Task WriteAsync(IReadOnlyList<(AiGuideValue Value, int Raw)> requests)
    {
        if (page is null) return;
        writing = true;
        pageList.IsEnabled = apply.IsEnabled = defaults.IsEnabled = reread.IsEnabled = treeButton.IsEnabled = false;
        try
        {
            if (await write(L.Ai.PendingDecisions, requests))
            {
                foreach (var request in requests)
                {
                    var input = boxes[request.Value];
                    boxes[request.Value] = (input.Box, request.Raw, input.File);
                    UpdateDraft(request.Value);
                }
                await ReadGameAsync();
            }
        }
        finally
        {
            writing = false;
            pageList.IsEnabled = defaults.IsEnabled = reread.IsEnabled = treeButton.IsEnabled = true;
            UpdateExtensionActions();
        }
    }

    // A number as the file and the game hold it (in every place and copy, AiEdits.Slots), in the guide's reading;
    // "제각각" where its places disagree.
    private (string File, int? FileRaw, int? NowRaw, string? Now, bool Changed) Numbers(AiGuideValue value, bool connected)
    {
        if (value.Anchors.Any(a => a.Slot(trees) is null)) return (L.AiGuide.Missing, null, null, connected ? L.AiGuide.Missing : null, false);
        int[] fileValues = AiEdits.Slots(trees, value).Select(s => trees[(int)s.Tree].Nodes[s.Node].Params[s.Param]).Distinct().ToArray();
        int? fileRaw = fileValues.Length == 1 ? fileValues[0] : null;
        string file = fileRaw is int f ? Shown(value, f) : L.AiGuide.Differs;
        if (!connected || live is null || game() is not { } identity) return (file, fileRaw, null, null, false);
        int? now;
        try { now = GameMemory.ReadAi(identity, memory => AiEdits.GameRaw(memory, trees, live, value)); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception) { return (file, fileRaw, null, null, false); }
        if (now is not int n) return (file, fileRaw, null, L.AiGuide.Differs, true);
        return (file, fileRaw, n, Shown(value, n), n != fileRaw);
    }

    private static string Shown(AiGuideValue value, int raw) => AiText.Number(value.Shown(raw)) + value.Unit;

    // --- the trees themselves

    private void BuildTreePage()
    {
        foreach (AiTree t in trees) treeList.Items.Add(L.Ai.TreeItem(AiLabels.Tree(t.Kind), t.Nodes.Length));
        treeList.SelectedIndex = (int)AiTreeKind.Corps;
        treeList.SelectionChanged += (_, _) => { if (treeList.SelectedIndex >= 0) ShowTree(trees[treeList.SelectedIndex]); };
        results.SelectionChanged += (_, _) => { if (results.SelectedItem is WC.ListBoxItem { Tag: (AiTreeKind kind, int node) }) Jump(kind, node); };
        search.ToolTip = L.Ai.SearchTip;
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Search(); };
        view.SelectedItemChanged += (_, e) => { if (e.NewValue is WC.TreeViewItem { Tag: AiNode node }) ShowDetails(node); };
        view.Resources[W.SystemColors.HighlightBrushKey] = CardSkin.Gold;
        view.Resources[W.SystemColors.HighlightTextBrushKey] = CardSkin.Ink;
        view.Resources[W.SystemColors.InactiveSelectionHighlightBrushKey] = CardSkin.Rule;
        // Not virtualized: only the opened nodes have items, and a jump must find its item laid out to scroll to it.
        WC.VirtualizingPanel.SetIsVirtualizing(view, false);

        treePage.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(1, W.GridUnitType.Star), MinWidth = 180, MaxWidth = 260 });
        treePage.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(2, W.GridUnitType.Star), MinWidth = 240 });
        treePage.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(1.5, W.GridUnitType.Star), MinWidth = 260, MaxWidth = 420 });
        var left = new WC.DockPanel { Margin = new W.Thickness(12, 8, 8, 8) };
        AddTop(left, Heading(L.Ai.Trees), treeList, Heading(L.Ai.Search), search, found);
        left.Children.Add(results);
        var right = new WC.ScrollViewer { VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, Content = details };
        WC.Grid.SetColumn(right, 2);
        treePage.Children.Add(left);
        treePage.Children.Add(right);
        var nodes = new WC.Border { Margin = new W.Thickness(0, 8, 0, 8), Child = view };
        WC.Grid.SetColumn(nodes, 1);
        treePage.Children.Add(nodes);
        ShowTree(tree);
    }

    private void ShowTree(AiTree shown)
    {
        tree = shown;
        if (treeList.SelectedIndex != (int)shown.Kind) treeList.SelectedIndex = (int)shown.Kind;
        view.Items.Clear();
        items.Clear();
        WC.TreeViewItem root = Item(shown.Root);
        view.Items.Add(root);
        root.IsExpanded = true;
        if (shown.Root.Children is [var only]) items[only.Index].IsExpanded = true;
    }

    // A node's line, its children made when it is first opened.
    private WC.TreeViewItem Item(AiNode node)
    {
        var item = new WC.TreeViewItem { Header = Line(tree, node), Tag = node, Foreground = CardSkin.Ink };
        if (node.Children.Count > 0) item.Items.Add(new WC.TreeViewItem());
        item.Expanded += (_, e) =>
        {
            if (e.OriginalSource != item || item.Items is not [WC.TreeViewItem { Tag: null }]) return;
            item.Items.Clear();
            foreach (AiNode child in node.Children) item.Items.Add(Item(child));
        };
        items[node.Index] = item;
        return item;
    }

    private string Line(AiTree t, AiNode node)
    {
        if (!lines.TryGetValue(t.Kind, out string[]? all)) lines[t.Kind] = all = new string[t.Nodes.Length];
        return all[node.Index] ??= $"{L.Ai.NodeNumber(node.Index)}  {AiText.Line(t, node)}";
    }

    // Opens the tree down to a node and picks it.
    private void Jump(AiTreeKind kind, int index)
    {
        if (tree.Kind != kind) ShowTree(trees[(int)kind]);
        var path = new List<AiNode>();
        for (AiNode? n = tree.Nodes[index]; n is not null; n = tree.ParentOf(n)) path.Add(n);
        path.Reverse();
        foreach (AiNode n in path.SkipLast(1))
        {
            items[n.Index].IsExpanded = true;
            items[n.Index].UpdateLayout();
        }
        if (!items.TryGetValue(index, out WC.TreeViewItem? target)) return;
        target.IsSelected = true;
        view.UpdateLayout();
        // Once the opened branches are laid out, the item is on the view's panel to scroll to.
        Dispatcher.BeginInvoke(WT.DispatcherPriority.Loaded, () => target.BringIntoView());
    }

    // Nodes whose line holds the words searched (a class, a variable, a label), or that hold the number searched.
    private void Search()
    {
        results.Items.Clear();
        string query = search.Text.Trim();
        found.Text = "";
        if (query.Length == 0) return;
        int? number = int.TryParse(query.Replace(",", ""), out int n) ? n : null;
        var hits = new List<(AiTreeKind Kind, int Node, string Line)>();
        foreach (AiTree t in trees)
            foreach (AiNode node in t.Nodes)
            {
                string line = Line(t, node);
                bool hit = number is int value ? node.Params.Contains(value)
                    : line.Contains(query, StringComparison.OrdinalIgnoreCase) || AiCatalog.Class(node.Class)?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                if (hit) hits.Add((t.Kind, node.Index, line));
            }
        foreach (var hit in hits.Take(SearchLimit))
            results.Items.Add(new WC.ListBoxItem { Content = $"{AiLabels.Tree(hit.Kind)} {hit.Line}", Tag = (hit.Kind, hit.Node), ToolTip = hit.Line });
        found.Text = hits.Count == 0 ? L.Ai.FoundNone : hits.Count > SearchLimit ? L.Ai.FoundMany(hits.Count, SearchLimit) : "";
    }

    // The node picked: its line, its class, each param (the structure of a condition left out: its args say it all),
    // and the other places of its block.
    private void ShowDetails(AiNode node)
    {
        picked = node;
        details.Children.Clear();
        details.Children.Add(new WC.TextBlock { Text = AiText.Line(tree, node), TextWrapping = W.TextWrapping.Wrap, FontWeight = W.FontWeights.Bold, Margin = new W.Thickness(0, 0, 0, 4) });
        string className = AiCatalog.Class(node.Class)?.Name ?? node.Class.ToString("X8");
        details.Children.Add(Muted($"{L.Ai.NodeNumber(node.Index)} · {AiLabels.Tree(tree.Kind)} · {className}"));

        AiLiveTree? here = live?[(int)tree.Kind];
        int?[] now = here is null || game() is not { } identity ? new int?[node.Params.Length]
            : Safe(() => GameMemory.ReadAi(identity, memory => Enumerable.Range(0, node.Params.Length).Select(p => AiMemory.Value(memory, here, node.Index, p)).ToArray()), node.Params.Length);
        var rows = Enumerable.Range(0, node.Params.Length).Select(p => (Param: p, Text: AiText.Describe(node, p)))
            .Where(r => node.Class != AiCatalog.Condition || r.Text.Role != L.Ai.RoleStructure).ToList();
        if (rows.Count > 0) details.Children.Add(ParamTable(node, rows, now, here is not null));

        var copies = node.Params.Length > 0 ? AiValues.Counterparts(tree, new AiSlot(tree.Kind, node.Index, 0)) : [];
        if (copies.Count > 0)
        {
            details.Children.Add(Heading(L.Ai.Copies));
            int[] numbers = rows.Where(r => r.Text.Number).Select(r => r.Param).ToArray();
            foreach (AiSlot copy in copies)
            {
                AiNode other = tree.Nodes[copy.Node];
                string place = AiText.CorpsType(tree, other) is int type ? L.Ai.CorpsType(type) : AiLabels.Tree(tree.Kind);
                string values = string.Join(", ", numbers.Select(p => AiText.Number(other.Params[p])));
                var link = new WC.TextBlock { Text = values.Length > 0 ? L.Ai.CopyItem(copy.Node, place, values) : L.Ai.CopyAt(copy.Node, place), Cursor = WI.Cursors.Hand, Foreground = CardSkin.Highlight, Margin = new W.Thickness(0, 1, 0, 1) };
                link.MouseLeftButtonUp += (_, _) => Jump(tree.Kind, copy.Node);
                details.Children.Add(link);
            }
        }
    }

    private static WC.Grid ParamTable(AiNode node, List<(int Param, (string Role, string Meaning, bool Number) Text)> rows, int?[] now, bool connected)
    {
        var grid = new WC.Grid { Margin = new W.Thickness(0, 10, 0, 8) };
        foreach (W.GridLength width in new[] { W.GridLength.Auto, W.GridLength.Auto, W.GridLength.Auto, W.GridLength.Auto, new W.GridLength(1, W.GridUnitType.Star) })
            grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = width });
        string[] heads = [L.Ai.ColumnParam, L.Ai.ColumnRole, L.Ai.ColumnFile, connected ? L.Ai.ColumnGame : "", L.Ai.ColumnMeaning];
        grid.RowDefinitions.Add(new WC.RowDefinition());
        for (int c = 0; c < heads.Length; c++) Cell(grid, 0, c, heads[c], CardSkin.InkMuted, bold: true);
        AiClass? cls = AiCatalog.Class(node.Class);
        int row = 1;
        foreach ((int p, (string role, string meaning, bool number)) in rows)
        {
            grid.RowDefinitions.Add(new WC.RowDefinition());
            char letter = node.Class == AiCatalog.Condition ? (number ? 'n' : 'v') : cls?.Role(p) ?? '?';
            Cell(grid, row, 0, p.ToString(System.Globalization.CultureInfo.CurrentCulture), CardSkin.InkMuted);
            Cell(grid, row, 1, role, number ? CardSkin.Ink : CardSkin.InkMuted, bold: number);
            Cell(grid, row, 2, AiText.Value(node.Params[p], letter), CardSkin.Ink, bold: number);
            if (connected && now[p] is int value)
                Cell(grid, row, 3, AiText.Value(value, letter), value == node.Params[p] ? CardSkin.Ink : CardSkin.Pending, bold: value != node.Params[p]);
            Cell(grid, row, 4, meaning, CardSkin.Ink, wrap: true);
            row++;
        }
        return grid;
    }

    private static void Cell(WC.Grid grid, int row, int column, string text, WM.Brush ink, bool bold = false, bool wrap = false, bool right = false)
    {
        var block = new WC.TextBlock
        {
            Text = text, Foreground = ink, FontWeight = bold ? W.FontWeights.Bold : W.FontWeights.Normal, Margin = new W.Thickness(0, 2, 10, 2),
            VerticalAlignment = W.VerticalAlignment.Center,
            TextWrapping = wrap ? W.TextWrapping.Wrap : W.TextWrapping.NoWrap, TextAlignment = right ? W.TextAlignment.Right : W.TextAlignment.Left
        };
        WC.Grid.SetRow(block, row);
        WC.Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static int?[] Safe(Func<int?[]> read, int count)
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception) { return new int?[count]; }
    }

    // A list on paper; the last on the left (the search's results) fills what is left of the column.
    private static WC.ListBox List(bool fill = false) => new()
    {
        Background = CardSkin.Paper, BorderBrush = CardSkin.Rule, Foreground = CardSkin.Ink, Margin = new W.Thickness(0, 0, 0, 4),
        MaxHeight = fill ? double.PositiveInfinity : 220
    };

    private static WC.TextBlock Heading(string text) => new() { Text = text, FontWeight = W.FontWeights.Bold, Margin = new W.Thickness(0, 8, 0, 3) };

    private static WC.TextBlock Muted(string text = "") => new() { Text = text, Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap };

    private static void AddTop(WC.DockPanel panel, params W.UIElement[] elements)
    {
        foreach (W.UIElement element in elements)
        {
            WC.DockPanel.SetDock(element, WC.Dock.Top);
            panel.Children.Add(element);
        }
    }
}
