using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;

internal static partial class RenderChecks
{
    // Real WPF layout and input controls, with an in-memory game. No window, browser or live game writes.
    private static void CheckPanes()
    {
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Click(PlateButton button)
        {
            Require(button.IsEnabled, "Cannot click a disabled pane tab");
            ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button)!).Invoke();
        }
        static void SelectTab(PaneTab tab) => tab.RaiseEvent(new W.RoutedEventArgs(WC.Button.ClickEvent));
        static void Layout(W.FrameworkElement element, double width = 1500, double height = 900)
        {
            element.Measure(new W.Size(width, height));
            element.Arrange(new W.Rect(0, 0, width, height));
            element.UpdateLayout();
        }

        var map = new MapView();
        Layout(map);
        var surface = Field<MapSurface>(map, "surface");
        var pane = Field<WC.Grid>(map, "pane");
        var gameTab = Field<PaneTab>(map, "listTab");
        var dataTab = Field<PaneTab>(map, "dataTab");
        var aiTab = Field<PaneTab>(map, "aiTab");
        var ancientTab = Field<PaneTab>(map, "ancientTab");
        foreach (ListKind kind in Enum.GetValues<ListKind>())
        {
            PaneSection section = kind switch
            {
                ListKind.Personalities or ListKind.Items or ListKind.Formations or ListKind.Tactics or ListKind.Policies or ListKind.PolicyEffects or ListKind.Offices or ListKind.Ranks or ListKind.Doctrines => PaneSection.Data,
                ListKind.Ancients => PaneSection.Ancients, _ => PaneSection.Game
            };
            map.Lists.AddPage(kind.ToString(), new GameTable([new("Name", GameTable.Kind.Text)], 28), [], new WC.TextBlock(), section);
        }
        Require(pane.Visibility == W.Visibility.Collapsed && surface.ActualWidth == 1500, "Pane must start closed");
        map.ShowLists(true);
        Layout(map);
        Require(map.Lists.Shown && surface.ActualWidth > 0 && surface.ActualWidth + pane.ActualWidth + 6 == 1500, "List must use a separate column");
        W.Point paneOrigin = pane.TranslatePoint(new W.Point(), map);
        Require(paneOrigin.X >= surface.ActualWidth, "Pane overlaps the map");
        Require(gameTab.IsEnabled && dataTab.IsEnabled && aiTab.IsEnabled && ancientTab.IsEnabled, "Initial task tabs are disabled");
        Require(gameTab.Content.Equals(L.Navigation.Game) && dataTab.Content.Equals(L.Navigation.Data)
            && aiTab.Content.Equals(L.Ai.Tool), "Task navigation labels are missing");
        Require(((WC.WrapPanel)W.LogicalTreeHelper.GetParent(gameTab)).Children.OfType<PaneTab>().Select(t => t.Content)
            .SequenceEqual(new object[] { L.Navigation.Game, L.Navigation.Data, L.Ai.Tool, L.Lists.AncientTab }),
            "Ancient tab must follow AI without a Tools menu");
        SelectTab(dataTab);
        Layout(map);
        Require(dataTab.Selected && map.Lists.Shown && map.Lists.Tab == (int)ListKind.Personalities,
            "Data task did not open its first list");
        var targets = Field<WC.WrapPanel>(map.Lists, "tabStrip").Children.OfType<PaneTab>().ToArray();
        Require(targets.Where(t => t.Visibility == W.Visibility.Visible).Select(t => t.Content).SequenceEqual(new object[] { "Personalities", "Items" }),
            "Officer/item category contains unrelated targets");
        var categories = Field<WC.WrapPanel>(map.Lists, "categories").Children.OfType<PaneTab>().ToArray();
        SelectTab(categories[1]);
        Require(map.Lists.Tab == (int)ListKind.Formations && targets.Where(t => t.Visibility == W.Visibility.Visible)
            .Select(t => t.Content).SequenceEqual(new object[] { "Formations", "Tactics" }), "Combat category is incomplete");
        map.Lists.ShowTab((int)ListKind.Tactics);
        SelectTab(categories[2]);
        Require(targets.Count(t => t.Visibility == W.Visibility.Visible) == 5, "Domestic category is incomplete");
        SelectTab(categories[1]);
        Require(map.Lists.Tab == (int)ListKind.Tactics, "Category lost previous selection");
        map.Lists.ShowTab((int)ListKind.Items);
        SelectTab(gameTab);
        Require(map.Lists.Tab == (int)ListKind.Forces, "Game task did not restore its previous list");
        SelectTab(dataTab);
        Require(map.Lists.Tab == (int)ListKind.Items, "Data task did not retain the selected list");
        SelectTab(ancientTab);
        Layout(map);
        Require(map.Lists.Shown && map.Lists.Tab == (int)ListKind.Ancients && map.Section == PaneSection.Ancients && ancientTab.Selected,
            "Ancient tab did not open its list directly");
        map.HidePane();
        Require(!map.PaneShown, "Ancient list could not be folded");
        SelectTab(gameTab);

        var data = new TestGame().Officer(11, "Officer", 0, 1, 4).With(11, OfficerFields.Leadership, 65).Leading(11).Build();
        map.CityEditor.Open(new CityEditorCard.View(1, "City", 0, "", System.Drawing.Color.Gray, "", 0, "",
            CityFields.All.ToDictionary(f => f, f => 0L), [], [], [], false));
        var fields = Field<Dictionary<FieldDef, NumberField>>(map.CityEditor, "fields");
        fields[CityFields.Gold].Set(450);
        int pending = map.CityEditor.Pending;
        Require(pending > 0, "Pending edit fixture is empty");
        map.ShowEditor(map.CityEditor);
        Layout(map);
        Require(!map.Lists.Shown && map.CityEditor.Shown && map.CityEditor.ActualHeight > 0, "Editor did not replace the list");
        map.HidePane();
        Layout(map);
        Require(!map.PaneShown && surface.ActualWidth == 1500 && map.CityEditor.Pending == pending,
            "Folding the detail pane discarded edits or left the map narrowed");
        map.ShowPane();
        Layout(map);
        Require(map.PaneShown && gameTab.Selected && map.EditorShown && map.CityEditor.Pending == pending && !map.Lists.Shown,
            "Reopening the pane did not restore the detail editor and its edits");
        Click(Field<PlateButton>(map, "backToList"));
        Layout(map);
        Require(map.Lists.Shown && map.Lists.Tab == (int)ListKind.Cities && map.CityEditor.Pending == pending && map.CityEditor.Shown,
            "Return to list discarded pending edits or lost the target category");
        SelectTab(dataTab);
        Require(map.CityEditor.Pending == pending, "Switching task groups discarded a hidden edit");
        SelectTab(gameTab);
        Require(map.Lists.Tab == (int)ListKind.Cities, "Game task lost the return list");
        var resume = Field<PlateButton>(map.Lists, "resume");
        Require(resume.Visibility == W.Visibility.Visible && resume.Badge == pending, "List has no contextual resume action");
        Click(resume);
        Layout(map);
        Require(map.EditorShown && map.CityEditor.Pending == pending && !map.Lists.Shown, "Resume reloaded or discarded pending edits");
        foreach (EditorCard editor in new EditorCard[] { map.ForceEditor, map.GateEditor, map.FacilityEditor, map.OfficerEditor, map.UnitEditor, map.PersonalityEditor, map.ItemEditor })
        {
            map.ShowEditor(editor);
            Layout(map, 1000, 680);
            Require(editor.ActualWidth >= 500 && surface.ActualWidth >= 300 && !map.Lists.Shown, "An editor escaped its pane");
            Require(editor.TranslatePoint(new W.Point(), map).X >= surface.ActualWidth, "An editor covers the map");
        }
        map.HideEditors();
        map.ShowLists(false);
        Layout(map);
        Require(surface.ActualWidth == 1500 && pane.Visibility == W.Visibility.Collapsed, "Closing the pane did not restore map width");

        AiTree[] trees = Enum.GetValues<AiTreeKind>().Select(k => new AiTree(k, 0, 0, [new AiNode(0, 1, 0, null, -1, 0, [], null, null)])).ToArray();
        var ai = new AiView(trees, () => null, (_, _) => Task.FromResult(false));
        ai.ExpandedChanged += _ => map.ShowAi(ai);
        map.ShowAi(ai);
        Layout(map);
        Require(map.AiShown && pane.Visibility == W.Visibility.Visible && ai.ActualWidth == pane.ActualWidth, "AI settings are not in the pane");
        ai.ShowTreeView();
        Layout(map);
        Require(ai.Expanded && ai.ActualWidth == 1500 && pane.Visibility == W.Visibility.Collapsed, "AI tree did not expand to the workspace");
        map.HidePane();
        Layout(map);
        Require(!map.PaneShown && surface.ActualWidth == 1500 && Field<WC.Border>(map, "expandedHost").Visibility == W.Visibility.Collapsed,
            "Folding the full AI tree left the map covered");
        map.ShowPane();
        Layout(map);
        Require(map.PaneShown && ai.Expanded && ai.ActualWidth == 1500 && aiTab.Selected,
            "Reopening the pane did not restore the full AI tree");
        ai.ShowSettings();
        Layout(map);
        Require(!ai.Expanded && ai.ActualWidth == pane.ActualWidth && surface.ActualWidth > 0, "AI did not return to the pane");
        map.HideAi();
        Layout(map);
        Require(pane.Visibility == W.Visibility.Collapsed, "Closing AI left an empty pane");

        // The single toolbar button opens the pane; AI initializes only through its tab.
        var window = OfflineEditor(aiStorageRoot: Path.GetFullPath(Path.Combine("source/RenderChecks/artifacts", "menu-ai-fixture-" + Guid.NewGuid().ToString("N"))));
        var initialMap = Field<MapView>(window, "map");
        FieldInfo treesField = typeof(EditorWindow).GetField("aiTrees", Hidden)!;
        treesField.SetValue(window, trees); // No game-file dependency or live writes in this check.
        var panelButton = Field<PlateButton>(window, "panelButton");
        Click(panelButton);
        Require(initialMap.Lists.Shown && panelButton.Selected, "The panel button did not open the initial list");
        var firstAiTab = Field<PaneTab>(initialMap, "aiTab");
        Require(typeof(EditorWindow).GetField("aiView", Hidden)!.GetValue(window) is null && firstAiTab.IsEnabled,
            "Initial AI tab depends on an already-created view");
        Call(window, "SetBusy", true, false);
        initialMap.ShowLists(true);
        Require(!firstAiTab.IsEnabled, "The AI tab is enabled while busy");
        Call(window, "SetBusy", false, false);
        SelectTab(firstAiTab);
        Console.WriteLine(JsonSerializer.Serialize(new { scenario = "first-ai-tab", shown = initialMap.AiShown,
            created = typeof(EditorWindow).GetField("aiView", Hidden)!.GetValue(window) is AiView,
            selected = firstAiTab.Selected, section = initialMap.Section.ToString(), enabled = firstAiTab.IsEnabled,
            busy = Field<bool>(window, "busy") }));
        Require(initialMap.AiShown && typeof(EditorWindow).GetField("aiView", Hidden)!.GetValue(window) is AiView && firstAiTab.Selected,
            "First AI tab click did not initialize and open the AI view");
        var firstAi = Field<AiView>(window, "aiView");
        var pageList = Field<WC.ComboBox>(firstAi, "pageList");
        pageList.SelectedIndex = 1;
        Click(panelButton);
        Require(!initialMap.PaneShown && !panelButton.Selected && !(bool)Call(window, "Escape")!,
            "The panel button did not fold AI or Escape changed a hidden pane");
        Click(panelButton);
        Require(initialMap.PaneShown && initialMap.AiShown && firstAiTab.Selected && panelButton.Selected && pageList.SelectedIndex == 1,
            "The panel button did not restore the selected AI page");
        SelectTab(Field<PaneTab>(initialMap, "listTab"));
        initialMap.Lists.ShowTab(2);
        Click(panelButton);
        Click(panelButton);
        Require(initialMap.Lists.Shown && initialMap.Lists.Tab == 2 && panelButton.Selected,
            "The panel button did not restore the selected list tab");
        SelectTab(Field<PaneTab>(initialMap, "ancientTab"));
        Require(initialMap.Lists.Shown && initialMap.Section == PaneSection.Ancients
            && (bool)Call(window, "Escape")! && !initialMap.PaneShown,
            "Window Escape did not fold the ancient tab's list");
        Require(W.LogicalTreeHelper.GetParent(Field<PlateButton>(window, "registeredButton")) is WC.StackPanel,
            "Registered editor is missing from the global toolbar");
        var playButton = Field<PlateButton>(window, "playButton");
        Require(playButton.Visibility == W.Visibility.Visible && !playButton.IsEnabled,
            "Auto stop must remain visible and disabled before a demo connection");

        var navigationData = new TestGame().Force(1, 11, 1).City(1, 1, 11)
            .Officer(11, "조조", 1, 1, 1).Officer(2001, "고대 장수", 0, 1, 4).Play(0).Build();
        typeof(GameSnapshot).GetProperty(nameof(GameSnapshot.DemoFlag), Hidden)!.SetValue(navigationData, true);
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, navigationData);
        Call(window, "ObservePlayerForce", navigationData);
        Require(playButton.IsEnabled, "Live demo entry did not enable the persistent auto-stop button");
        Call(window, "SetBusy", true, false);
        Require(!playButton.IsEnabled && playButton.Visibility == W.Visibility.Visible, "Busy state hid or enabled auto stop");
        Call(window, "SetBusy", false, false);
        typeof(GameSnapshot).GetProperty(nameof(GameSnapshot.DemoFlag), Hidden)!.SetValue(navigationData, false);
        Call(window, "ObservePlayerForce", navigationData);
        Require(!playButton.IsEnabled, "Live demo exit did not disable auto stop");
        Call(window, "FillLists");
        initialMap.ShowSection(PaneSection.Game);
        initialMap.Lists.ShowTab((int)ListKind.Cities);
        var citySearch = Field<SearchBox>(window, "citySearch");
        var cityForce = Field<WC.ComboBox>(window, "cityForceBox");
        citySearch.Text = navigationData.Cities[0].Name;
        cityForce.SelectedIndex = 1;
        Call(window, "FillLists");
        var cities = Field<GameTable>(window, "cityTable");
        Require(cities.Select(1), "Filtered city fixture is absent");
        Require(Call(window, "Show", new CityTarget(1)) is true, "City fixture could not open");
        var draft = Field<Dictionary<FieldDef, NumberField>>(initialMap.CityEditor, "fields")[CityFields.Gold];
        long drafted = draft.Value + 123;
        draft.Set(drafted);
        int draftCount = initialMap.CityEditor.Pending;
        initialMap.ReturnToList();
        Require(citySearch.Text == navigationData.Cities[0].Name && cityForce.SelectedIndex == 1
            && Field<WC.ListBox>(cities, "list").SelectedItem is GameTable.Row selectedCity && Equals(selectedCity.Tag, 1),
            "Return to list lost search, filter or selected row");
        ((Task)Call(window, "OpenPickedAsync", cities, 1)!).GetAwaiter().GetResult();
        Require(draft.Value == drafted && initialMap.CityEditor.Pending == draftCount,
            "Selecting the same list row reloaded and discarded the pending draft");
        initialMap.ReturnToList();
        initialMap.ShowSection(PaneSection.Data);
        initialMap.ShowSection(PaneSection.Game);
        Click(Field<PlateButton>(initialMap.Lists, "resume"));
        Require(draft.Value == drafted && initialMap.CityEditor.Pending == draftCount,
            "Resume read the game again and replaced the pending draft");
        initialMap.ReturnToList();
        initialMap.HideEditors();
        SelectTab(Field<PaneTab>(initialMap, "ancientTab"));
        Require(Call(window, "Show", new OfficerTarget(2001)) is true && initialMap.Section == PaneSection.Ancients,
            "Ancient detail lost its list origin");
        initialMap.ReturnToList();
        Require(initialMap.Lists.Tab == (int)ListKind.Ancients && initialMap.Section == PaneSection.Ancients,
            "Ancient detail returned to the general officer list");
        SelectTab(Field<PaneTab>(initialMap, "ancientTab"));
        Click(Field<PlateButton>(initialMap.Lists, "resume"));
        Require(initialMap.EditorShown && initialMap.OfficerEditor.Target == new OfficerTarget(2001),
            "Ancient list could not resume the selected ancient officer");
        SelectTab(Field<PaneTab>(initialMap, "ancientTab"));
        var windowContent = (W.FrameworkElement)window.Content;
        Layout(windowContent, 1000, 680);
        var seasonOn = Field<WC.RadioButton>(window, "seasonOn");
        var seasonOff = Field<WC.RadioButton>(window, "seasonOff");
        var seasonStatus = Field<WC.TextBlock>(window, "seasonStatus");
        var settingsMenu = Field<PlateButton>(window, "settingsButton");
        Require(settingsMenu.ActualWidth > 0 && seasonStatus.ActualWidth > 0 && !IsDescendant((W.DependencyObject)window.Content,seasonOn) && !IsDescendant((W.DependencyObject)window.Content,seasonOff)
            && seasonStatus.TranslatePoint(new W.Point(), windowContent).Y < initialMap.TranslatePoint(new W.Point(), windowContent).Y,
            "Menu layout removed or covered the environment menu or season status");

        var table = new GameTable([new("Name", GameTable.Kind.Text), new("Leadership", GameTable.Kind.Number, 1, OfficerFields.Leadership)], 40);
        var memory = new FakeMemory(data);
        int writes = 0;
        EditPlan? lastPlan = null;
        table.EditingEnabled = true;
        table.CellWriter = (tag, field, value) =>
        {
            writes++;
            table.EditingEnabled = false; // The real window does this while writing.
            lastPlan = OfficerEdits.Plan(data, (int)tag, new([new(field, value)], null, null, []));
            GameMemory.Apply(memory, TestGame.Classes, lastPlan.Edits);
            table.EditingEnabled = true;
            return Task.FromResult(true);
        };
        table.SetRows([new GameTable.Row(11, ["Officer", 65L])]);
        Layout(table, 500, 240);
        var list = Field<WC.ListBox>(table, "list");
        list.ApplyTemplate();
        Layout(table, 500, 240);
        object item = list.ItemContainerGenerator.ContainerFromIndex(0) ?? throw new InvalidOperationException("No WPF row container");
        Require((bool)Call(table, "BeginInput", item, 1)!, "Numeric cell did not enter edit mode");
        object input = Field<object>(table, "input");
        NumberField number = (NumberField)input.GetType().GetProperty("Number")!.GetValue(input)!;
        number.Box.Text = "999999";
        ((Task)Call(table, "CommitInputAsync", input)!).GetAwaiter().GetResult();
        Require(writes == 0 && memory[data.OfficerById[11].Address]!.SequenceEqual(data.OfficerById[11].Data), "Invalid input changed memory");
        number.Box.Text = "95";
        Require(writes == 0, "Typing wrote before Enter");
        ((Task)Call(table, "CommitInputAsync", input)!).GetAwaiter().GetResult();
        ((Task)Call(table, "CommitInputAsync", input)!).GetAwaiter().GetResult();
        Require(writes == 1 && OfficerFields.Leadership.Read(memory[data.OfficerById[11].Address]!) == 95, "Cell write was lost or duplicated");
        Require(lastPlan!.Edits.Single().Changes.Any(c => OfficerFields.IsExperience(c.Field)), "Ability edit lost linked experience");
        GameMemory.Apply(memory, TestGame.Classes, Undo.Of(lastPlan.Edits));
        Require(OfficerFields.Leadership.Read(memory[data.OfficerById[11].Address]!) == 65, "Cell edit could not be undone");
        Require((bool)Call(table, "BeginInput", item, 1)!, "Cannot re-edit the cell");
        Call(table, "CancelInput");
        Require(writes == 1, "Cancel wrote to memory");
        table.SetRows([new GameTable.Row(11, ["Officer", 95L])]);
        Call(table, "BeginInput", item, 1);
        input = Field<object>(table, "input");
        ((Task)Call(table, "CommitInputAsync", input)!).GetAwaiter().GetResult();
        Require(writes == 1, "Unchanged value wrote to memory");
        var followed = new NumberField(CityFields.Gold);
        followed.Show(100);
        followed.Set(200);
        followed.Follow(200); // A list write has caught up to the same pending field in the detail editor.
        followed.Follow(300);
        Require(followed.Value == 300 && !followed.IsChanged, "A satisfied pending input stopped following live values");

        Console.WriteLine(JsonSerializer.Serialize(new { PaneDefaultClosed = true, MapOverlap = false, AllEditorsDocked = true,
            PendingPreserved = true, InitialTabsEnabled = true, TaskGroupsSeparated = true, ContextualResumePreservesInputs = true,
            ToolsMenuRemoved = true, AncientTabAfterAi = true, RegisteredEditorOnToolbar = true, FirstAiTabOpensView = true,
            SearchFilterSelectionPreserved = true, SameTargetPickPreservesInputs = true,
            AncientDetailReturnsToOrigin = true, SeasonToolbarPreserved = true,
            AiBusyStateMatched = true, AiSettingsDocked = true, AiTreeFullWidth = true,
            PanelToggleRestoresList = true, PanelToggleRestoresEditorEdits = true, PanelToggleRestoresAiPage = true,
            PanelToggleRestoresFullTree = true, HiddenPaneEscapeIgnored = true, CellWrites = writes,
            InvalidAndCancelledWrites = 0, ExperienceLinked = true, UndoPassed = true }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
