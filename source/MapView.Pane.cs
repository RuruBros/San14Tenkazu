using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

// The map and its legend occupy their own column. The resizable right pane switches between the list and
// the open editor; hiding the editor's parent preserves its pending values while the list is being used.
internal sealed partial class MapView
{
    internal ForceEditorCard ForceEditor { get; } = new();
    internal CityEditorCard CityEditor { get; } = new();
    internal GateEditorCard GateEditor { get; } = new();
    internal FacilityEditorCard FacilityEditor { get; } = new();
    internal OfficerEditorCard OfficerEditor { get; } = new();
    internal UnitEditorCard UnitEditor { get; } = new();
    internal PersonalityEditorCard PersonalityEditor { get; } = new();
    internal ItemEditorCard ItemEditor { get; } = new();
    internal FormationEditorCard FormationEditor { get; } = new();
    internal TacticEditorCard TacticEditor { get; } = new();
    internal PolicyEditorCard PolicyEditor { get; } = new("policy");
    internal PolicyEditorCard PolicyEffectEditor { get; } = new("policyeffect");
    internal OfficeEditorCard OfficeEditor { get; } = new();
    internal PlainRuleEditorCard DoctrineEditor { get; } = new(DoctrineRules.Spec);
    internal PlainRuleEditorCard RankEditor { get; } = new(RankRules.Spec);
    internal ListsCard Lists { get; } = new();
    private EditorCard[] Editors => [ForceEditor, CityEditor, GateEditor, FacilityEditor, OfficerEditor, UnitEditor, PersonalityEditor, ItemEditor, FormationEditor, TacticEditor, PolicyEditor, PolicyEffectEditor, OfficeEditor, RankEditor, DoctrineEditor];
    private readonly WC.Grid pane = new() { Background = CardSkin.Parchment, ClipToBounds = true };
    private readonly WC.Grid editorHost = new();
    private readonly WC.Border aiHost = new(), expandedHost = new();
    private readonly PlateButton backToList = new(L.Navigation.BackToList, icon: Icons.Revert);
    private readonly WC.TextBlock editorLocation = new() { Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.DockPanel mapColumn = new();
    private readonly PaneTab aiTab = new(L.Ai.Tool, primary: true) { ToolTip = L.Ai.ToolTip };
    private AiView? ai;
    private readonly WC.ColumnDefinition paneColumn = new();
    private readonly WC.ColumnDefinition dividerColumn = new();
    private readonly WC.GridSplitter divider = new()
    {
        HorizontalAlignment = W.HorizontalAlignment.Stretch, VerticalAlignment = W.VerticalAlignment.Stretch,
        ResizeDirection = WC.GridResizeDirection.Columns, ResizeBehavior = WC.GridResizeBehavior.PreviousAndNext,
        Background = CardSkin.DarkWood
    };
    private readonly PaneTab listTab = new(L.Navigation.Game, primary: true) { ToolTip = L.Navigation.GameTip };
    private readonly PaneTab dataTab = new(L.Navigation.Data, primary: true) { ToolTip = L.Navigation.DataTip };
    private readonly PaneTab ancientTab = new(L.Lists.AncientTab, primary: true) { ToolTip = L.Navigation.AncientNote };
    private bool listsAvailable, showingEditor, showingAi, editorBeforeAi, paneHidden;
    private PaneSection section = PaneSection.Game, editorSection = PaneSection.Game, sectionBeforeAi = PaneSection.Game;
    private ListKind editorList = ListKind.Forces;
    private EditorCard? navigationCard;
    private EditorTarget? navigationTarget;
    private double paneWidth = 600;
    private const double MapMinimum = 300, DividerWidth = 6;

    internal event Action? ListsSelected;
    internal event Action? EditorResumed;
    internal event Action? AiSelected;
    internal event Action<bool>? PaneChanged;
    internal bool PaneShown => !paneHidden && (showingEditor || listsAvailable || showingAi);
    internal bool AiShown => showingAi;
    internal bool EditorShown => PaneShown && showingEditor && !showingAi;
    internal PaneSection Section => section;
    internal ListKind EditorList => editorList;
    internal bool AiEnabled { get => aiTab.IsEnabled; set => aiTab.IsEnabled = value; }

    private void BuildPane(W.UIElement legendBar)
    {
        var body = new WC.Grid();
        body.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(1, W.GridUnitType.Star), MinWidth = MapMinimum });
        body.ColumnDefinitions.Add(dividerColumn);
        body.ColumnDefinitions.Add(paneColumn);
        WC.DockPanel.SetDock(legendBar, WC.Dock.Bottom);
        mapColumn.Children.Add(legendBar);
        mapColumn.Children.Add(surface);
        body.Children.Add(mapColumn);
        WC.Grid.SetColumn(divider, 1);
        body.Children.Add(divider);
        WC.Grid.SetColumn(pane, 2);
        body.Children.Add(pane);

        var tabs = new WC.WrapPanel();
        tabs.Children.Add(listTab);
        tabs.Children.Add(dataTab);
        tabs.Children.Add(aiTab);
        tabs.Children.Add(ancientTab);
        var close = new PlateButton(L.Window.Close, icon: Icons.Close, iconOnly: true) { Height = 30, Margin = new W.Thickness(8, 0, 0, 0), ToolTip = L.Window.PanelCloseTip };
        close.Click += HidePane;
        var navigation = new WC.DockPanel();
        WC.DockPanel.SetDock(close, WC.Dock.Right);
        navigation.Children.Add(close);
        navigation.Children.Add(tabs);
        pane.RowDefinitions.Add(new WC.RowDefinition { Height = W.GridLength.Auto });
        pane.RowDefinitions.Add(new WC.RowDefinition { Height = new W.GridLength(1, W.GridUnitType.Star) });
        pane.Children.Add(PaneChrome.Toolbar(navigation));
        Lists.HorizontalAlignment = W.HorizontalAlignment.Stretch;
        Lists.Margin = new W.Thickness(0);
        WC.Grid.SetRow(Lists, 1);
        pane.Children.Add(Lists);
        editorHost.RowDefinitions.Add(new WC.RowDefinition { Height = W.GridLength.Auto });
        editorHost.RowDefinitions.Add(new WC.RowDefinition { Height = new W.GridLength(1, W.GridUnitType.Star) });
        var editorNavigation = new WC.DockPanel();
        WC.DockPanel.SetDock(backToList, WC.Dock.Left);
        backToList.Margin = new W.Thickness(0, 0, 10, 0);
        editorNavigation.Children.Add(backToList);
        editorNavigation.Children.Add(editorLocation);
        editorHost.Children.Add(PaneChrome.Toolbar(editorNavigation));
        WC.Grid.SetRow(editorHost, 1);
        pane.Children.Add(editorHost);
        WC.Grid.SetRow(aiHost, 1);
        pane.Children.Add(aiHost);
        WC.Grid.SetColumnSpan(expandedHost, 3);
        body.Children.Add(expandedHost);
        foreach (EditorCard editor in Editors)
        {
            editor.Width = double.NaN;
            editor.HorizontalAlignment = W.HorizontalAlignment.Stretch;
            editor.VerticalAlignment = W.VerticalAlignment.Stretch;
            editor.Margin = new W.Thickness(0);
            WC.Grid.SetRow(editor, 1);
            editorHost.Children.Add(editor);
            editor.NavigationChanged += UpdateEditorNavigation;
        }
        listTab.Click += (_, _) => ShowSection(PaneSection.Game);
        dataTab.Click += (_, _) => ShowSection(PaneSection.Data);
        aiTab.Click += (_, _) => AiSelected?.Invoke();
        ancientTab.Click += (_, _) => ShowAncients();
        backToList.Click += ReturnToList;
        Lists.ResumeSelected += ResumeEditor;
        Lists.TabChanged += () =>
        {
            if (!Lists.Shown) return;
            section = Lists.Section;
            UpdatePane();
        };
        divider.DragCompleted += (_, _) => paneWidth = paneColumn.ActualWidth;
        SizeChanged += (_, _) => SizePane();
        Children.Add(body);
        UpdatePane();
    }

    internal void ShowLists(bool shown)
    {
        if (shown) paneHidden = false;
        listsAvailable = shown;
        showingAi = false;
        showingEditor = !shown && Editors.Any(e => e.Shown);
        if (shown)
        {
            if (section == PaneSection.Ai) section = sectionBeforeAi;
            Lists.ShowSection(section);
        }
        else if (showingEditor) section = editorSection;
        UpdatePane();
    }

    internal void ShowSection(PaneSection selected)
    {
        if (selected == PaneSection.Ai) { AiSelected?.Invoke(); return; }
        paneHidden = false;
        section = selected;
        showingEditor = showingAi = false;
        listsAvailable = true;
        Lists.ShowSection(selected);
        UpdatePane();
        ListsSelected?.Invoke();
    }

    internal void ShowAncients()
        => ShowList(ListKind.Ancients);

    internal void ShowList(ListKind kind)
    {
        paneHidden = false;
        showingEditor = showingAi = false;
        listsAvailable = true;
        Lists.ShowTab((int)kind);
        section = Lists.Section;
        UpdatePane();
        ListsSelected?.Invoke();
    }

    internal void ReturnToList()
        => ShowList(editorList);

    internal void ResumeEditor()
    {
        if (!Editors.Any(editor => editor.Shown)) return;
        paneHidden = false;
        showingEditor = true;
        showingAi = false;
        section = editorSection;
        UpdatePane();
        EditorResumed?.Invoke();
    }

    internal void ShowEditor(EditorCard card, ListKind? origin = null)
    {
        ListKind kind = card switch
        {
            ForceEditorCard => ListKind.Forces, OfficerEditorCard => ListKind.Officers, UnitEditorCard => ListKind.Units,
            PersonalityEditorCard => ListKind.Personalities, ItemEditorCard => ListKind.Items, FormationEditorCard => ListKind.Formations, TacticEditorCard => ListKind.Tactics,
            PolicyEditorCard p => p.Table == "policy" ? ListKind.Policies : ListKind.PolicyEffects, OfficeEditorCard => ListKind.Offices, PlainRuleEditorCard c => c.Table=="rank" ? ListKind.Ranks : ListKind.Doctrines, _ => ListKind.Cities
        };
        bool fromList = Lists.Shown && (Lists.Tab == (int)kind || Lists.Tab == (int)ListKind.Ancients && card is OfficerEditorCard);
        if (origin is not null || fromList || navigationCard != card || !Equals(navigationTarget, card.Target))
        {
            editorList = origin ?? (fromList ? (ListKind)Lists.Tab : kind);
            editorSection = editorList switch
            {
                ListKind.Personalities or ListKind.Items or ListKind.Formations or ListKind.Tactics or ListKind.Policies or ListKind.PolicyEffects or ListKind.Offices or ListKind.Ranks or ListKind.Doctrines => PaneSection.Data,
                ListKind.Ancients => PaneSection.Ancients, _ => PaneSection.Game
            };
            navigationCard = card;
            navigationTarget = card.Target;
        }
        paneHidden = false;
        foreach (EditorCard editor in Editors) editor.Visibility = editor == card ? W.Visibility.Visible : W.Visibility.Collapsed;
        showingEditor = true;
        showingAi = false;
        section = editorSection;
        UpdatePane();
    }

    internal void ShowAi(AiView view)
    {
        paneHidden = false;
        if (!showingAi)
        {
            editorBeforeAi = showingEditor;
            sectionBeforeAi = section;
        }
        ai = view;
        showingAi = true;
        section = PaneSection.Ai;
        WC.Border target = view.Expanded ? expandedHost : aiHost;
        if (target.Child != view)
        {
            aiHost.Child = expandedHost.Child = null;
            target.Child = view;
        }
        UpdatePane();
    }

    internal void HidePane()
    {
        paneHidden = true;
        UpdatePane();
    }

    internal void ShowPane()
    {
        paneHidden = false;
        if (!showingEditor && !showingAi && !listsAvailable) listsAvailable = true;
        UpdatePane();
    }

    internal void HideAi()
    {
        showingAi = false;
        showingEditor = editorBeforeAi && Editors.Any(editor => editor.Shown);
        section = sectionBeforeAi;
        UpdatePane();
    }

    internal bool EscapeAi()
    {
        if (!showingAi || ai is null) return false;
        if (ai.Expanded) ai.ShowSettings(); else HideAi();
        return true;
    }

    internal void HideEditors()
    {
        foreach (EditorCard editor in Editors) editor.Visibility = W.Visibility.Collapsed;
        showingEditor = false;
        navigationCard = null;
        navigationTarget = null;
        UpdatePane();
    }

    private void UpdatePane()
    {
        bool expanded = PaneShown && showingAi && ai?.Expanded == true;
        bool shown = !expanded && PaneShown;
        mapColumn.Visibility = expanded ? W.Visibility.Collapsed : W.Visibility.Visible;
        expandedHost.Visibility = expanded ? W.Visibility.Visible : W.Visibility.Collapsed;
        pane.Visibility = divider.Visibility = shown ? W.Visibility.Visible : W.Visibility.Collapsed;
        Lists.Visibility = shown && !showingEditor && !showingAi ? W.Visibility.Visible : W.Visibility.Collapsed;
        editorHost.Visibility = PaneShown && showingEditor && !showingAi ? W.Visibility.Visible : W.Visibility.Collapsed;
        aiHost.Visibility = PaneShown && showingAi ? W.Visibility.Visible : W.Visibility.Collapsed;
        listTab.Selected = PaneShown && section == PaneSection.Game;
        dataTab.Selected = PaneShown && section == PaneSection.Data;
        ancientTab.Selected = PaneShown && section == PaneSection.Ancients;
        aiTab.Selected = PaneShown && showingAi;
        UpdateEditorNavigation();
        SizePane();
        PaneChanged?.Invoke(PaneShown);
    }

    private void UpdateEditorNavigation()
    {
        EditorCard? editor = Editors.FirstOrDefault(card => card.Shown);
        string category = editor switch
        {
            ForceEditorCard => L.Lists.ForcesTab, OfficerEditorCard => L.Lists.OfficersTab, UnitEditorCard => L.Lists.UnitsTab,
            PersonalityEditorCard => L.Lists.PersonalitiesTab, ItemEditorCard => L.Lists.ItemsTab,
            FormationEditorCard => L.Rules.Formation,
            TacticEditorCard => L.Rules.TacticPage,
            PolicyEditorCard p => p.Table == "policy" ? L.Rules.PolicyPage : L.Rules.PolicyEffects,
            OfficeEditorCard => L.Rules.OfficePage,
            PlainRuleEditorCard c => c.Table=="rank" ? L.Rules.RankPage : L.Rules.DoctrinePage,
            GateEditorCard => L.Gate.Kind, FacilityEditorCard => L.Facility.Page, _ => L.Lists.CitiesTab
        };
        string name = editor?.NavigationTitle ?? "";
        int pending = editor?.Pending ?? 0;
        editorLocation.Text = editor is null ? "" : L.Navigation.Location(category, name);
        string tooltip = L.Navigation.ResumeTip(category, name, pending);
        bool canResume = editor?.Target is not null && !showingEditor;
        Lists.SetResume(tooltip, pending, canResume && Lists.Section == editorSection);
    }

    private void SizePane()
    {
        bool shown = pane.Visibility == W.Visibility.Visible;
        // CityEditor has the widest page (452 DIPs), plus its frame and scroll bar.
        paneColumn.MinWidth = shown ? 500 : 0;
        paneColumn.MaxWidth = shown ? Math.Max(500, ActualWidth - MapMinimum - DividerWidth) : double.PositiveInfinity;
        paneColumn.Width = new W.GridLength(shown ? Math.Clamp(paneWidth, paneColumn.MinWidth, paneColumn.MaxWidth) : 0);
        dividerColumn.Width = new W.GridLength(shown ? DividerWidth : 0);
    }
}
