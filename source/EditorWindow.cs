using System.ComponentModel;
using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// The editor's window: the toolbar, the map, the right pane for lists or an editor, and the log under them.
// Number cells can be edited directly; a name in a list or on the map opens its editor. What each part does is in the other
// EditorWindow.*.cs files.
internal sealed partial class EditorWindow : W.Window
{
    internal static string AppTitle => L.Window.Title;
    // Row heights in 96-DPI units: an officer's row holds two lines of text beside a portrait of that height
    // (the portrait icons are made this big: StartFaceLibrary); a unit's row has one line.
    private const int OfficerRowHeight = 58, UnitRowHeight = 46;

    private readonly MapView map = new();
    // One icon per meaning (Icons), from the game's art or, where the game has no picture, a line icon
    // (LineIcons): tools show the icon alone (named in their tooltips), buttons that fill in values keep their text.
    private readonly PlateButton panelButton = new(L.Window.Panel, icon: Icons.Lists) { ToolTip = L.Window.PanelTip };
    private readonly PlateButton registeredButton = new(L.Registered.Button, icon: Icons.Ancient) { ToolTip = L.Registered.Tip };
    private readonly WC.Border registeredSeparator = new()
    {
        Width = 1, Height = 22, Background = CardSkin.Gold, Opacity = 0.65, IsHitTestVisible = false
    };
    private readonly PlateButton undoButton = new(L.Window.Undo, icon: Icons.Undo);
    // What each write changed, newest last: [되돌리기] writes the old values back, one write at a time.
    private readonly List<EditorUndo> undoHistory = [];
    private const int UndoLimit = 30;
    private readonly PlateButton connectButton = Tool(L.Window.Connect, Icons.Connect, L.Window.ConnectTip);
    private readonly PlateButton reloadButton = Tool(L.Window.Reread, Icons.Reload, L.Window.RereadTip);
    private readonly PlateButton cancelButton = Tool(L.Window.CancelSearch, Icons.Stop, L.Window.CancelSearchTip);
    private readonly PlateButton playButton = new(L.Auto.Stop, primary: true, Icons.Stop)
    {
        ToolTip = L.Auto.StopTip
    };
    private readonly WC.ProgressBar progress = new() { IsIndeterminate = true, Height = 3, Visibility = W.Visibility.Collapsed, Margin = new W.Thickness(0, 2, 0, 0) };
    private readonly WC.TextBlock status = new() { Foreground = CardSkin.InkOnDark, TextTrimming = W.TextTrimming.CharacterEllipsis, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly LogPanel log = new();
    private readonly EditorLogFile logFile;
    // The editor cards in the right pane; one is open at a time.
    private readonly ForceEditorCard forceEditor;
    private readonly CityEditorCard cityEditor;
    private readonly GateEditorCard gateEditor;
    private readonly FacilityEditorCard facilityEditor;
    private readonly OfficerEditorCard officerEditor;
    private readonly UnitEditorCard unitEditor;
    private readonly PersonalityEditorCard personalityEditor;
    private readonly ItemEditorCard itemEditor;
    private readonly FormationEditorCard formationEditor;
    private readonly TacticEditorCard tacticEditor;
    private readonly PolicyEditorCard policyEditor, policyEffectEditor;
    private readonly OfficeEditorCard officeEditor;
    private readonly PlainRuleEditorCard rankEditor;
    private readonly PlainRuleEditorCard doctrineEditor;
    private readonly WT.DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly WT.DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<(int Index, string Name)> countryNames = [];

    private CancellationTokenSource? scanCancellation;
    private GameIdentity? game;
    private GameLayout? layout;
    private GameSnapshot? snapshot;
    private bool busy;
    private bool refreshing;
    private bool layoutRecovering;
    private (int Force, int Ruler)? lastPlayer;
    private bool demoNoticed;

    internal EditorWindow(Func<bool>? registeredGameRunning = null, string? seasonStorageRoot = null, string? aiStorageRoot = null, string? ruleStorageRoot = null, RuleGameAccess? ruleAccess = null, RuntimeRuleGameAccess? runtimeRuleAccess = null, GamePreparationAccess? gamePreparationAccess = null, AutomaticGameAccess? automaticGameAccess = null)
    {
        string aiFolder = aiStorageRoot ?? Program.DataFolder;
        aiKept = new(Path.Combine(aiFolder, "ai_values.txt"));
        aiExtensionKept = new(Path.Combine(aiFolder, "ai_extensions.json"));
        ruleFile = new(Path.Combine(ruleStorageRoot ?? Program.DataFolder, "rule_profiles.json"));
        runtimeRuleFile = new(Path.Combine(ruleStorageRoot ?? Program.DataFolder, "runtime-rules"));
        logFile = new(Path.Combine(ruleStorageRoot ?? Program.DataFolder, "logs"));
        this.runtimeRuleAccess = runtimeRuleAccess ?? RuntimeRuleGameAccess.Live;
        this.gamePreparationAccess = gamePreparationAccess ?? GamePreparationAccess.Live;
        this.automaticGameAccess = automaticGameAccess ?? AutomaticGameAccess.Live;
        automaticGameSettings = new(this.automaticGameAccess);
        this.ruleAccess = ruleAccess ?? RuleGameAccess.Live;
        this.seasonStorageRoot=seasonStorageRoot ?? Path.Combine(Program.DataFolder,"seasons");
        registeredIsRunning = registeredGameRunning ?? RegisteredOfficerFile.GameIsRunning;
        lists = map.Lists;
        forceEditor = map.ForceEditor;
        cityEditor = map.CityEditor;
        gateEditor = map.GateEditor;
        facilityEditor = map.FacilityEditor;
        officerEditor = map.OfficerEditor;
        unitEditor = map.UnitEditor;
        personalityEditor = map.PersonalityEditor;
        itemEditor = map.ItemEditor;
        formationEditor = map.FormationEditor;
        tacticEditor = map.TacticEditor;
        policyEditor = map.PolicyEditor; policyEffectEditor = map.PolicyEffectEditor;
        officeEditor = map.OfficeEditor;
        rankEditor = map.RankEditor;
        doctrineEditor = map.DoctrineEditor;
        Title = AppTitle;
        // The exe's icon (app.ico), for the title bar and the taskbar.
        using (Stream? icon = typeof(EditorWindow).Assembly.GetManifestResourceStream("app.ico"))
            if (icon is not null) Icon = System.Windows.Media.Imaging.BitmapFrame.Create(icon, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Background = CardSkin.Parchment;
        WD.TextElement.SetFontFamily(this, CardSkin.Font);
        WD.TextElement.SetFontSize(this, CardSkin.FontSize);
        WD.TextElement.SetForeground(this, CardSkin.Ink);
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
        UseLayoutRounding = true;
        // Kept inside the working area.
        W.Rect area = W.SystemParameters.WorkArea;
        MinWidth = Math.Min(1000, area.Width);
        MinHeight = Math.Min(680, area.Height);
        Width = Math.Min(1500, area.Width * 0.95);
        Height = Math.Min(950, area.Height * 0.95);
        WindowStartupLocation = W.WindowStartupLocation.CenterScreen;

        // Connection tools stay left, editing/settings tools align right; status fills the space between them.
        var toolbar = new WC.DockPanel { LastChildFill = true };
        WC.StackPanel connection = Group(connectButton, reloadButton, calendarButton, cancelButton),
            tools = Group(playButton, undoButton, panelButton, BuildSettingsMenu(), registeredSeparator, registeredButton);
#if LOCAL_DEVELOPER
        log.SetDeveloperButton(BuildDeveloperMenu());
#endif
        registeredSeparator.Margin = new W.Thickness(10, 0, 10, 0);
        registeredButton.Margin = new W.Thickness(0);
        WC.DockPanel.SetDock(connection, WC.Dock.Left);
        WC.DockPanel.SetDock(tools, WC.Dock.Right);
        var middle = new WC.DockPanel { Margin = new W.Thickness(8, 0, 8, 0) };
        var messages = new WC.StackPanel { VerticalAlignment = W.VerticalAlignment.Center };
        messages.Children.Add(status);
        messages.Children.Add(seasonStatus);
        messages.Children.Add(progress);
        status.SetBinding(W.FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(WC.TextBlock.Text)) { Source = status });
        middle.Children.Add(messages);
        toolbar.Children.Add(connection);
        toolbar.Children.Add(tools);
        toolbar.Children.Add(middle);
        var bar = new WC.Border { Background = CardSkin.DarkWood, BorderBrush = CardSkin.Gold, BorderThickness = new W.Thickness(0, 0, 0, 2), Padding = new W.Thickness(8), Child = toolbar };
        WC.ToolTipService.SetShowOnDisabled(undoButton, true);
        WC.ToolTipService.SetShowOnDisabled(playButton, true);
        connectButton.Click += async () => { automaticGameFailure = null; await ConnectAsync(); };
        reloadButton.Click += async () => await ReloadAsync();
        calendarButton.Click += async () => await ChangeCalendarAsync();
        cancelButton.Click += () => scanCancellation?.Cancel();
        playButton.Click += async () => await ResumePlayAsync();
        panelButton.Click += TogglePanel;
        registeredButton.Click += OpenRegisteredEditor;
        InitializeRegisteredAvailability();
        map.PaneChanged += shown => panelButton.Selected = shown;
        map.ListsSelected += () => { RememberLists(); FillLists(); FocusSearch(); };
        undoButton.Click += async () => await UndoAsync();

        BuildLists();
        WireEditors();
        map.ForceClicked += force => _ = ToggleForceAsync(force);
        map.CitySelected += id => _ = OpenAsync(new CityTarget(id));
        map.GateSelected += id => _ = OpenAsync(new GateTarget(id));
        map.FacilitySelected += target => _ = OpenAsync(target);
        map.UnitSelected += (slot, commander) => _ = OpenUnitFromMapAsync(slot, commander);
        // The log folds under the map, as wide as the window.
        var page = new WC.DockPanel();
        WC.DockPanel.SetDock(bar, WC.Dock.Top);
        WC.DockPanel.SetDock(log, WC.Dock.Bottom);
        page.Children.Add(bar);
        page.Children.Add(log);
        page.Children.Add(map);
        Content = page;
        // Esc that nothing on the map took (a toolbar button has the keyboard): the card, then the list.
        KeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Escape && Escape()) e.Handled = true;
        };
        map.EscapePressed += () => Escape();

        map.FaceIcons = face => faceIcons.GetValueOrDefault(face);
        cityEditor.Faces = gateEditor.Faces = forceEditor.Faces = map.FaceBrush;
        WireFills();
        WireSurrender();
        WireGive();
        WireIndependence();
        WireGoTo();
        WireAi();
        WireRules();
        // Skipped while the map is zooming or panning: updating the lists would drop map frames.
        liveTimer.Tick += async (_, _) => { if (!map.IsMoving) await LiveRefreshAsync(); };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshLists(currentOnly: true); };
        iconTimer.Tick += (_, _) => ApplyDecodedIcons();
        automaticGameTimer.Tick += async (_, _) => await PollAutomaticGameAsync();
        ContentRendered += async (_, _) =>
        {
            StartFaceLibrary(null);
            automaticGameTimer.Start();
            await PollAutomaticGameAsync();
        };
        Closed += (_, _) => { automaticGameTimer.Stop(); automaticGameCancellation.Cancel(); };
        Closing += OnEditorClosing;

        FillLists();
        UpdateControls();
        AddLog(L.Window.WelcomeCards);
        AddLog(L.Window.WelcomeWrites);
    }

    // A tool on the toolbar: its icon alone, its name and what it does in the tooltip.
    private static PlateButton Tool(string name, string icon, string tip) => new(name, icon: icon, iconOnly: true) { ToolTip = tip.Length > 0 ? tip : null };

    private static WC.StackPanel Group(params W.FrameworkElement[] buttons)
    {
        var group = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, VerticalAlignment = W.VerticalAlignment.Center };
        foreach (W.FrameworkElement button in buttons)
        {
            button.Margin = new W.Thickness(0, 0, 6, 0);
            group.Children.Add(button);
        }
        return group;
    }

    // The toolbar folds the whole pane; its current view and pending inputs stay in place.
    private void TogglePanel()
    {
        if (map.PaneShown)
        {
            map.HidePane();
            map.FocusMap();
            return;
        }
        map.ShowPane();
        if (lists.Shown) { FillLists(); FocusSearch(); }
    }

    private void HideLists()
    {
        if (!lists.Shown) return;
        map.HidePane();
        map.FocusMap();
    }

    // Device pixels for 96-DPI units on this window's monitor (the portrait icons are decoded at this size).
    private int Px(int value) => (int)Math.Round(value * WM.VisualTreeHelper.GetDpi(this).DpiScaleX);

    private void SetBusy(bool value, bool scanning)
    {
        busy = value || seasonClosing || seasonDisconnecting;
        progress.Visibility = value && scanning ? W.Visibility.Visible : W.Visibility.Collapsed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        UpdateSeasonControls();
        bool connected = snapshot is not null && !layoutRecovering;
        UpdateCalendarControls(connected);
        connectButton.IsEnabled = !busy && !automaticGameChecking;
        UpdateRegisteredButton();
        reloadButton.IsEnabled = !busy && connected;
        runtimeRulesButton.IsEnabled = !busy && !automaticGameChecking;
        if(rulesImportButton is {} import)import.IsEnabled=!busy&&connected&&!RulesPending&&ruleKept?.Profile is not null&&game is {Verified:true};
        map.AiEnabled = !busy;
        // [검색 취소] only matters while the slow memory search runs.
        cancelButton.Visibility = busy && scanCancellation is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
        playButton.IsEnabled = !busy && snapshot?.IsDemoPlay == true;
        undoButton.IsEnabled = !busy && connected && undoHistory.Count > 0;
        undoButton.ToolTip = undoHistory.Count == 0 ? L.Undo.NothingTip
            : L.Undo.LastTip(undoHistory.Count, undoHistory[^1].Summary.Replace(", ", "\n"));
        foreach (EditorCard card in Editors) card.ApplyEnabled = !busy && connected;
        lists.ApplyEnabled = !busy && connected;
        formationEditor.ApplyEnabled = !busy && connected && ruleSnapshot is not null && ruleKept?.Profile is not null;
        formationEditor.SetAvailable(!busy && connected && ruleSnapshot is not null && ruleKept?.Profile is not null);
        tacticEditor.ApplyEnabled = !busy && connected && tacticRuleSnapshot is not null && ruleKept?.Profile?.Table("tactic") is not null;
        tacticEditor.SetAvailable(!busy && connected && tacticRuleSnapshot is not null && ruleKept?.Profile?.Table("tactic") is not null);
        foreach (PolicyEditorCard card in new[] { policyEditor, policyEffectEditor })
        {
            bool available = !busy && connected && policyRuleSnapshot is not null && ruleKept?.Profile?.Table(card.Table) is not null;
            card.ApplyEnabled = available; card.SetAvailable(available);
        }
        foreach (GameTable table in ListTables) table.EditingEnabled = !busy && connected;
        bool officeAvailable = !busy && connected && officeRuleSnapshot is not null && ruleKept?.Profile?.Table("office") is not null;
        officeEditor.ApplyEnabled = officeAvailable; officeEditor.SetAvailable(officeAvailable);
        bool rankAvailable=!busy && connected && rankRuleSnapshot is not null && ruleKept?.Profile?.Table("rank") is not null;
        rankEditor.ApplyEnabled=rankAvailable; rankEditor.SetAvailable(rankAvailable);
        bool doctrineAvailable=!busy && connected && doctrineRuleSnapshot is not null && ruleKept?.Profile?.Table("doctrine") is not null;
        doctrineEditor.ApplyEnabled=doctrineAvailable; doctrineEditor.SetAvailable(doctrineAvailable);
        cityEditor.FillsEnabled = gateEditor.FillsEnabled = forceEditor.FillsEnabled = !busy && connected;
        officerEditor.RevivalEnabled = !busy && connected;
        officerEditor.StatusChangeEnabled = !busy && connected;
        unlockButton.IsEnabled = !busy && snapshot?.Play is not null;
    }

    // Esc leaves the visible view first. A detail editor hidden behind the list keeps its pending inputs.
    private bool Escape()
    {
        if (!map.PaneShown) return false;
        if (map.EscapeAi()) return true;
        if (lists.Shown) HideLists();
        else if (OpenEditor is not null) _ = CloseEditorAsync();
        else return false;
        return true;
    }

    private void AddLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        log.Append(line); logFile.Append(line);
    }

    // The system's message boxes, owned by the window: a question answered yes or no, or a note.
    private bool Confirm(string text, W.MessageBoxImage icon = W.MessageBoxImage.Question, bool defaultNo = false, string? title = null) =>
        W.MessageBox.Show(this, text, title ?? AppTitle, W.MessageBoxButton.YesNo, icon, defaultNo ? W.MessageBoxResult.No : W.MessageBoxResult.Yes) == W.MessageBoxResult.Yes;

    // The question before a write that was shown first: what it does, then whether to write it now.
    private bool ConfirmWrite(string what) => Confirm(L.Window.WriteNow(what));

    private void Tell(string text, W.MessageBoxImage icon = W.MessageBoxImage.Information) =>
        W.MessageBox.Show(this, text, AppTitle, W.MessageBoxButton.OK, icon);

    // Yes, no or cancel (null).
    private bool? Choose(string text) => W.MessageBox.Show(this, text, AppTitle, W.MessageBoxButton.YesNoCancel, W.MessageBoxImage.Question) switch
    {
        W.MessageBoxResult.Yes => true,
        W.MessageBoxResult.No => false,
        _ => null
    };

    private void ReportError(Exception ex)
    {
        string message = ex is Win32Exception { NativeErrorCode: 5 }
            ? L.Window.AccessDenied
            : ex.Message;
        status.Text = message;
        AddLog(L.Window.Error(message));
        Tell(message, W.MessageBoxImage.Warning);
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
