using System.ComponentModel;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WM = System.Windows.Media;
using WI = System.Windows.Input;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// An offline editor of the registered originals. Each officer keeps his pending record when another is selected;
// the explicit Save writes all changed officers together, independent of the live game's editor and undo history.
internal sealed partial class RegisteredOfficerWindow : W.Window
{
    private readonly WC.ListBox list = new() { MinWidth = 235, Background = CardSkin.Paper };
    private readonly WC.TextBox search = CardSkin.NumberBox(235);
    private readonly WC.TextBlock note = new() { TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted };
    private readonly WC.TextBlock total = new() { Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 6, 0, 6) };
    private readonly WC.TextBlock count = new() { Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.TextBlock availability = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 12, 0), VerticalAlignment = W.VerticalAlignment.Center };
    private readonly PlateButton save = new(L.Registered.Save, primary: true, icon: Icons.Apply);
    private readonly WC.TabControl tabs = new() { Background = CardSkin.Parchment };
    private readonly Dictionary<FieldDef, NumberField> numbers = [];
    private readonly Dictionary<FieldDef, NameChoice> choices = [];
    private readonly Dictionary<RegisteredOfficerFields.Text, WC.TextBox> texts = [];
    private readonly Dictionary<int, WC.CheckBox> formations = [];
    private readonly Dictionary<int, RegisteredOfficer> working = [];
    private readonly GameCatalog catalog;
    private readonly Action<string> report;
    private readonly Func<bool> isGameRunning;
    private readonly WT.DispatcherTimer gameTimer = new(WT.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private RegisteredOfficerFile? file;
    private RegisteredOfficer? selected;
    private bool filling, discardApproved, gameRunning;
    internal bool HasPending => file is not null && (ChangedCount > 0 || CurrentChanged);
    private bool CurrentChanged => selected is not null && (numbers.Values.Any(n => n.IsChanged) || choices.Values.Any(c => c.Picked != c.Current)
        || texts.Any(t => t.Value.Text != RegisteredOfficerFields.ReadText(selected.Data, t.Key)) || FormationValue() != RegisteredOfficerFields.Formations.Read(selected.Data));
    private int ChangedCount => file?.Officers.Count(o => !o.Data.AsSpan().SequenceEqual(working[o.Id].Data)) ?? 0;

    internal RegisteredOfficerWindow(GameCatalog catalog, Action<string> report, Func<bool>? isGameRunning = null)
    {
        this.catalog = catalog;
        this.report = report;
        this.isGameRunning = isGameRunning ?? RegisteredOfficerFile.GameIsRunning;
        Title = L.Registered.Title;
        Background = CardSkin.Parchment;
        WD.TextElement.SetFontFamily(this, CardSkin.Font);
        WD.TextElement.SetFontSize(this, CardSkin.FontSize);
        WD.TextElement.SetForeground(this, CardSkin.Ink);
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
        UseLayoutRounding = true;
        Width = Math.Min(1150, W.SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(850, W.SystemParameters.WorkArea.Height * 0.9);
        MinWidth = Math.Min(800, Width);
        MinHeight = Math.Min(600, Height);
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        search.Width = double.NaN;
        search.ToolTip = L.Registered.Search;
        WI.InputMethod.SetIsInputMethodEnabled(search, true);
        var open = new PlateButton(L.Registered.Open, icon: Icons.Lists);
        open.Click += OpenFile;
        var top = new WC.DockPanel { Margin = new W.Thickness(12) };
        WC.DockPanel.SetDock(open, WC.Dock.Right);
        top.Children.Add(open);
        top.Children.Add(availability);
        var left = new WC.DockPanel { Margin = new W.Thickness(0, 0, 12, 0) };
        WC.DockPanel.SetDock(search, WC.Dock.Top);
        left.Children.Add(search);
        left.Children.Add(list);
        var body = new WC.Grid { Margin = new W.Thickness(12, 0, 12, 12) };
        body.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(250) });
        body.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(1, W.GridUnitType.Star) });
        body.Children.Add(left);
        WC.Grid.SetColumn(tabs, 1);
        body.Children.Add(tabs);
        var bottom = new WC.DockPanel { Margin = new W.Thickness(12) };
        WC.DockPanel.SetDock(save, WC.Dock.Right);
        bottom.Children.Add(save);
        var messages = new WC.StackPanel();
        messages.Children.Add(count);
        messages.Children.Add(note);
        bottom.Children.Add(messages);
        var page = new WC.DockPanel { Background = CardSkin.Parchment };
        WC.DockPanel.SetDock(top, WC.Dock.Top);
        WC.DockPanel.SetDock(bottom, WC.Dock.Bottom);
        page.Children.Add(top);
        page.Children.Add(bottom);
        page.Children.Add(body);
        Content = page;
        AddPage(L.Registered.Basic, Basic());
        AddPage(L.Registered.Traits, Traits());
        AddPage(L.Registered.Relations, Relations());
        var biography = Text(RegisteredOfficerFields.Biography, multiline: true);
        biography.Height = 330;
        AddPage(L.Registered.Biography, biography);
        tabs.IsEnabled = false;
        save.IsEnabled = false;
        save.Click += Save;
        WC.ToolTipService.SetShowOnDisabled(save, true);
        search.TextChanged += (_, _) => { if (!filling) FillList(); };
        list.SelectionChanged += (_, _) =>
        {
            if (filling) return;
            if (!CaptureCurrent())
            {
                filling = true;
                list.SelectedItem = list.Items.OfType<WC.ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected?.Id));
                filling = false;
                return;
            }
            if (list.SelectedItem is WC.ListBoxItem { Tag: int id }) ShowOfficer(working[id]);
        };
        Closing += (_, e) => { if (!discardApproved && HasPending && !ConfirmDiscard()) e.Cancel = true; };
        gameTimer.Tick += (_, _) => RefreshGameState();
        Loaded += (_, _) => { RefreshGameState(); gameTimer.Start(); };
        Activated += (_, _) => RefreshGameState();
        Closed += (_, _) => gameTimer.Stop();
        RefreshGameState();
    }

    private void RefreshGameState() => SetGameRunning(isGameRunning());

    internal void SetGameRunning(bool running)
    {
        if (gameRunning == running && availability.Text.Length > 0)
        {
            save.IsEnabled = !running && HasPending;
            return;
        }
        gameRunning = running;
        Title = running ? L.Registered.ReadOnlyTitle : L.Registered.Title;
        availability.Text = running ? L.Registered.RunningNote : L.Registered.Note;
        availability.Foreground = running ? CardSkin.Highlight : CardSkin.InkMuted;
        foreach (var number in numbers.Values) number.Box.IsReadOnly = running;
        foreach (var text in texts.Values) text.IsReadOnly = running;
        foreach (var choice in choices.Values) choice.Box.IsEnabled = !running;
        foreach (var formation in formations.Values) formation.IsEnabled = !running;
        foreach (var relation in relationLists.Values) relation.Panel.IsEnabled = !running;
        save.Text = running ? L.Registered.SaveBlocked : L.Registered.Save;
        save.ToolTip = running ? L.Registered.RunningTip : null;
        save.IsEnabled = !running && HasPending;
    }

    internal void AllowOwnerClose() => discardApproved = true;

    internal void Load(string path)
    {
        RegisteredOfficerFile loaded = RegisteredOfficerFile.Read(path);
        file = loaded;
        selected = null;
        working.Clear();
        foreach (RegisteredOfficer officer in loaded.Officers) working.Add(officer.Id, officer with { Data = (byte[])officer.Data.Clone() });
        tabs.IsEnabled = loaded.Officers.Length > 0;
        filling = true;
        search.Clear();
        filling = false;
        FillList();
        report(L.Registered.Loaded(loaded.Officers.Length, path));
        note.Text = System.IO.Path.GetFileName(path);
        note.ToolTip = path;
    }

    private void OpenFile()
    {
        if (HasPending && !ConfirmDiscard()) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = L.Registered.FileFilter, FileName = RegisteredOfficerFile.FileName, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try { Load(picker.FileName); }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { Error(ex); }
    }

    private WC.StackPanel Basic()
    {
        var panel = new WC.StackPanel();
        var names = new WC.WrapPanel();
        foreach (var field in new[] { RegisteredOfficerFields.Surname, RegisteredOfficerFields.GivenName, RegisteredOfficerFields.StyleName }) names.Children.Add(Cell(field.Label, Text(field)));
        panel.Children.Add(names);
        var abilities = new WC.WrapPanel();
        foreach (var field in RegisteredOfficerFields.Abilities) abilities.Children.Add(Cell(field.Label, Number(field)));
        panel.Children.Add(abilities);
        panel.Children.Add(total);
        var basics = new WC.WrapPanel();
        basics.Children.Add(Cell(RegisteredOfficerFields.Face.Label, Number(RegisteredOfficerFields.Face)));
        basics.Children.Add(Cell(RegisteredOfficerFields.Gender.Label, Choice(RegisteredOfficerFields.Gender, [new(0, L.Registered.Male, ""), new(1, L.Registered.Female, "")], none: false)));
        basics.Children.Add(Cell(RegisteredOfficerFields.Birth.Label, Number(RegisteredOfficerFields.Birth)));
        basics.Children.Add(Cell(RegisteredOfficerFields.Death.Label, Number(RegisteredOfficerFields.Death)));
        basics.Children.Add(Cell(RegisteredOfficerFields.Compatibility.Label, Number(RegisteredOfficerFields.Compatibility)));
        panel.Children.Add(basics);
        var policy = new WC.WrapPanel();
        policy.Children.Add(Cell(RegisteredOfficerFields.Ideology.Label, Choice(RegisteredOfficerFields.Ideology, Names("doctrine", catalog.Ideologies))));
        policy.Children.Add(Cell(RegisteredOfficerFields.Policy.Label, Choice(RegisteredOfficerFields.Policy, Names("policy", catalog.Policies))));
        policy.Children.Add(Cell(RegisteredOfficerFields.PolicyLevel.Label, Number(RegisteredOfficerFields.PolicyLevel)));
        policy.Children.Add(Cell(RegisteredOfficerFields.Voice.Label, Choice(RegisteredOfficerFields.Voice, Enumerable.Range(1, 13).Select(i => new GameName(i, L.Registered.VoiceNumber(i), "")))));
        panel.Children.Add(policy);
        panel.Children.Add(Explanation(L.Registered.CurrentGameNote));
        return panel;
    }

    private WC.StackPanel Traits()
    {
        var panel = new WC.StackPanel();
        panel.Children.Add(Explanation(L.Registered.PersonalityNote));
        AddChoices(panel, L.Officer.Personalities, RegisteredOfficerFields.Personalities, catalog.RegisteredPersonalities ?? RegisteredOfficerNames.Get("registered-personality"));
        panel.Children.Add(Explanation(L.Registered.TacticNote));
        AddChoices(panel, L.Officer.Tactics, RegisteredOfficerFields.Tactics, catalog.RegisteredTactics ?? RegisteredOfficerNames.Get("registered-tactic"));
        panel.Children.Add(Heading(L.Officer.Formations));
        var wrap = new WC.WrapPanel();
        foreach (GameName name in RegisteredOfficerNames.Get("formation").Where(n => n.Id is >= 1 and <= 13))
        {
            var check = new WC.CheckBox { Content = name.Name, Margin = new W.Thickness(0, 4, 16, 4) };
            check.Checked += (_, _) => Mark();
            check.Unchecked += (_, _) => Mark();
            formations.Add(name.Id, check);
            wrap.Children.Add(check);
        }
        panel.Children.Add(wrap);
        return panel;
    }


    private WC.TextBox Text(RegisteredOfficerFields.Text field, bool multiline = false)
    {
        var box = new WC.TextBox { Width = multiline ? double.NaN : 130, MaxLength = field.Characters, AcceptsReturn = multiline, TextWrapping = multiline ? W.TextWrapping.Wrap : W.TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? WC.ScrollBarVisibility.Auto : WC.ScrollBarVisibility.Disabled, Background = CardSkin.Paper, Padding = new W.Thickness(6), Margin = new W.Thickness(0, 2, 8, 8) };
        box.TextChanged += (_, _) => Mark();
        texts.Add(field, box);
        return box;
    }
    private WC.TextBox Number(FieldDef field)
    {
        var control = new NumberField(field, 105);
        control.Edited += Mark;
        numbers.Add(field, control);
        return control.Box;
    }
    private WC.ComboBox Choice(FieldDef field, IEnumerable<GameName> names, bool none = true)
    {
        var control = new NameChoice(190, none);
        control.SetNames(names.Where(n => n.Id != 0 || !none));
        control.Box.Margin = new W.Thickness(0, 2, 8, 8);
        control.Box.IsTextSearchEnabled = true;
        control.Box.SelectionChanged += (_, _) => Mark();
        choices.Add(field, control);
        return control.Box;
    }
    private static GameName[] Names(string kind, GameName[] live) => RegisteredOfficerNames.Get(kind).Concat(live).Where(n => n.Id > 0).GroupBy(n => n.Id).Select(g => g.Last()).ToArray();
    private void AddChoices(WC.StackPanel panel, string heading, FieldDef[] fields, IEnumerable<GameName> names)
    {
        panel.Children.Add(Heading(heading));
        var wrap = new WC.WrapPanel();
        foreach (FieldDef field in fields) wrap.Children.Add(Cell(field.Label, Choice(field, names)));
        panel.Children.Add(wrap);
    }
    private void AddPage(string name, W.UIElement content)
    {
        if (content is W.FrameworkElement element) element.VerticalAlignment = W.VerticalAlignment.Top;
        tabs.Items.Add(new WC.TabItem { Header = name, Content = new WC.ScrollViewer { Content = content,
            HorizontalContentAlignment = W.HorizontalAlignment.Stretch, VerticalContentAlignment = W.VerticalAlignment.Top,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, Padding = new W.Thickness(12) } });
    }
    private static WC.TextBlock Heading(string text) => new() { Text = text, FontWeight = W.FontWeights.SemiBold, Margin = new W.Thickness(0, 10, 0, 6) };
    private static WC.TextBlock Explanation(string text) => new() { Text = text, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 4, 0, 6) };
    private static WC.StackPanel Cell(string label, W.UIElement control)
    {
        var cell = new WC.StackPanel { Margin = new W.Thickness(0, 0, 4, 6) };
        cell.Children.Add(new WC.TextBlock { Text = label, Foreground = CardSkin.InkMuted });
        cell.Children.Add(control);
        return cell;
    }

    private void FillList()
    {
        if (file is null || !CaptureCurrent()) return;
        int? keep = selected?.Id;
        filling = true;
        list.Items.Clear();
        foreach (RegisteredOfficer officer in working.Values.OrderBy(o => o.Slot).Where(o => o.Id == keep || o.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            list.Items.Add(new WC.ListBoxItem { Tag = officer.Id, Content = L.Registered.Slot(officer.Slot + 1, officer.Name, officer.Total), Padding = new W.Thickness(5, 8, 5, 8) });
        list.SelectedItem = list.Items.OfType<WC.ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, keep)) ?? list.Items.OfType<WC.ListBoxItem>().FirstOrDefault();
        filling = false;
        if (list.SelectedItem is WC.ListBoxItem { Tag: int id }) ShowOfficer(working[id]);
        Mark();
    }
    private void ShowOfficer(RegisteredOfficer officer)
    {
        filling = true;
        selected = officer;
        GameName[] others = RelationNames().Where(o => o.Id != officer.Id).ToArray();
        foreach (FieldDef field in RegisteredOfficerRelations.Direct) choices[field].SetNames(others);
        byte[] original = file!.Officers.Single(o => o.Id == officer.Id).Data;
        foreach (var entry in numbers)
        {
            entry.Value.Show(entry.Key.Read(original));
            if (entry.Key.Read(officer.Data) != entry.Key.Read(original)) entry.Value.Set(entry.Key.Read(officer.Data));
        }
        foreach (var entry in choices)
        {
            entry.Value.Show((int)entry.Key.Read(original));
            if (entry.Key.Read(officer.Data) != entry.Key.Read(original)) entry.Value.Pick((int)entry.Key.Read(officer.Data));
        }
        foreach (var entry in texts) entry.Value.Text = RegisteredOfficerFields.ReadText(officer.Data, entry.Key);
        long flags = RegisteredOfficerFields.Formations.Read(officer.Data);
        foreach (var entry in formations) entry.Value.IsChecked = (flags >> entry.Key & 1) != 0;
        ShowRelations();
        filling = false;
        Mark();
    }
    private long FormationValue()
    {
        long value = selected is null ? 0 : RegisteredOfficerFields.Formations.Read(selected.Data);
        foreach (var entry in formations) value = entry.Value.IsChecked == true ? value | 1L << entry.Key : value & ~(1L << entry.Key);
        return value;
    }
    private bool CaptureCurrent()
    {
        if (selected is null || file is null) return true;
        byte[] data = (byte[])selected.Data.Clone();
        try
        {
            byte[] original = file.Officers.Single(o => o.Id == selected.Id).Data;
            foreach (var entry in numbers)
            {
                if (!entry.Value.IsChanged)
                {
                    original.AsSpan(entry.Key.Offset, entry.Key.Size).CopyTo(data.AsSpan(entry.Key.Offset));
                    continue;
                }
                if (!entry.Value.TryRead(out long value, out string error)) { Tell(error); return false; }
                entry.Key.Encode(value).CopyTo(data, entry.Key.Offset);
            }
            foreach (var entry in choices)
                if (entry.Value.Picked != entry.Key.Read(data)) entry.Key.Encode(entry.Value.Picked).CopyTo(data, entry.Key.Offset);
            long flags = FormationValue();
            if (flags != RegisteredOfficerFields.Formations.Read(data)) RegisteredOfficerFields.Formations.Encode(flags).CopyTo(data, RegisteredOfficerFields.Formations.Offset);
            foreach (var entry in texts) RegisteredOfficerFields.WriteText(data, entry.Key, entry.Value.Text);
            var updated = selected with { Data = data };
            RegisteredOfficer[] spouses = RegisteredOfficerFields.Spouse.Read(data) != RegisteredOfficerFields.Spouse.Read(selected.Data)
                ? RegisteredOfficerRelations.SpouseChanges(working.Values, selected.Id, (int)RegisteredOfficerFields.Spouse.Read(data)) : [];
            _ = file.Compose([updated, .. spouses]);
            foreach (RegisteredOfficer partner in spouses) working[partner.Id] = partner;
            working[selected.Id] = updated;
            selected = updated;
            WC.ListBoxItem? item = list.Items.OfType<WC.ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, updated.Id));
            if (item is not null) item.Content = L.Registered.Slot(updated.Slot + 1, updated.Name, updated.Total);
            return true;
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { Error(ex); return false; }
    }
    private void Mark()
    {
        if (filling || file is null) return;
        foreach (NameChoice choice in choices.Values) choice.Mark();
        foreach (var fields in new[] { RegisteredOfficerFields.Personalities, RegisteredOfficerFields.Tactics })
            foreach (FieldDef field in fields)
                choices[field].Allow(id => id == 0 ? ""
                    : fields.Any(f => f != field && choices[f].Picked == id) ? L.Registered.DuplicateSlot
                    : fields == RegisteredOfficerFields.Personalities && fields.Count(f => f != field && choices[f].Picked != 0) >= RegisteredOfficerFields.PersonalityActiveMax
                        ? L.Registered.PersonalityLimit : "");
        foreach (var entry in texts) entry.Value.Background = selected is not null && entry.Value.Text != RegisteredOfficerFields.ReadText(file.Officers.Single(o => o.Id == selected.Id).Data, entry.Key) ? CardSkin.Pending : CardSkin.Paper;
        total.Text = L.Registered.Total(RegisteredOfficerFields.Abilities.Sum(f => numbers[f].Value));
        int changed = ChangedCount + (CurrentChanged && selected is not null && file.Officers.Single(o => o.Id == selected.Id).Data.AsSpan().SequenceEqual(working[selected.Id].Data) ? 1 : 0);
        count.Text = L.Registered.Count(file.Officers.Length, changed);
        save.IsEnabled = !gameRunning && changed > 0;
    }
    private void Save()
    {
        RefreshGameState();
        if (file is null || gameRunning || !CaptureCurrent()) return;
        int changed = ChangedCount;
        try
        {
            string backup = file.Save(file.Compose(working.Values), System.IO.Path.Combine(Program.DataFolder, "registered-backups"));
            string path = file.Path;
            Load(path);
            note.Text = L.Registered.Saved(changed, backup);
            report(note.Text);
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex) || ex is InvalidOperationException) { Error(ex); }
    }
    private bool ConfirmDiscard() => W.MessageBox.Show(this, L.Registered.Unsaved, Title, W.MessageBoxButton.YesNo, W.MessageBoxImage.Question) == W.MessageBoxResult.Yes;
    private void Tell(string message) => W.MessageBox.Show(this, message, Title, W.MessageBoxButton.OK, W.MessageBoxImage.Warning);
    private void Error(Exception ex) { report(L.Window.Error(ex.Message)); Tell(ex.Message); }
}
