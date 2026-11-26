using Microsoft.Win32;
using W = System.Windows;
using WC = System.Windows.Controls;
using System.Windows.Data;
using T = San14Tenkazu.L.FileComparison;

namespace San14Tenkazu;

internal sealed class FormationComparisonWindow : W.Window
{
    private readonly Dictionary<ulong, OfficialFormation> references = OfficialFormation.Known.ToDictionary(r => r.Manifest);
    private readonly Dictionary<ulong, string> manifests;
    private readonly string? installed;
    private readonly WC.ComboBox start = new(), target = new();
    private readonly WC.TextBlock path = new() { TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.TextBlock summary = new() { TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.CheckBox all = new() { Content = T.All };
    private readonly WC.StackPanel controls = new();
    private readonly WC.DataGrid grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false };
    private readonly WC.ComboBox table = new();
    private readonly Dictionary<string, FileRuleDifference[]?> comparisons = [];
    private string provenance = "";

    internal FormationComparisonWindow(string? directory)
    {
        Title = T.Title;
        Width = 800; Height = 600; MinWidth = 580; MinHeight = 380;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        Background = CardSkin.Parchment; Foreground = CardSkin.Ink; FontFamily = CardSkin.Font;
        installed = directory is null ? null : Path.Combine(directory, FormationFileData.RelativePath);
        ulong? detected = SteamInstallation.InstalledDepotManifest(directory, OfficialFormation.Depot);
        manifests = FormationDepotManifest.Find(directory);
        var versions = references.Keys.Concat(manifests.Keys).Distinct().ToList();
        if (detected is ulong id && !versions.Contains(id)) versions.Add(id);
        start.ItemsSource = target.ItemsSource = versions;
        start.SelectedItem = OfficialFormation.Known[0].Manifest;
        target.SelectedItem = detected;
        path.Text = installed ?? "";
        var panel = new WC.DockPanel { Margin = new W.Thickness(14) };
        Content = panel;
        WC.DockPanel.SetDock(controls, WC.Dock.Top);
        panel.Children.Add(controls);
        controls.Children.Add(new WC.TextBlock { Text = T.ReadOnly, TextWrapping = W.TextWrapping.Wrap });
        controls.Children.Add(new WC.TextBlock { Text = detected.HasValue ? T.Detected(detected.Value) : T.UnknownTarget,
            TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 4, 0, 6) });
        var choices = new WC.WrapPanel();
        controls.Children.Add(choices);
        AddVersion(T.Start, start);
        AddVersion(T.Target, target);
        table.ItemsSource = new[] { L.Rules.Formation }.Concat(FileRuleLayout.All.Select(s => s.Label)).ToArray();
        table.SelectedIndex = 0;
        AddVersion(T.Table, table);
        table.SelectionChanged += (_, _) => ShowFields();
        var buttons = new WC.WrapPanel { Margin = new W.Thickness(0, 8, 0, 4) };
        controls.Children.Add(buttons);
        var useInstall = new PlateButton(T.Installed) { IsEnabled = installed != null };
        useInstall.Click += () => { path.Text = installed; Clear(); };
        var select = new PlateButton(T.Choose) { Margin = new W.Thickness(6, 0, 6, 0) };
        select.Click += () =>
        {
            if (Pick(T.Choose) is string selected) { path.Text = selected; Clear(); }
        };
        var compare = new PlateButton(T.Compare, primary: true);
        compare.Click += async () => await Compare();
        buttons.Children.Add(useInstall); buttons.Children.Add(select); buttons.Children.Add(compare);
        controls.Children.Add(path);
        controls.Children.Add(summary);
        controls.Children.Add(all);
        all.Margin = new W.Thickness(0, 6, 0, 6);
        all.Checked += (_, _) => ShowFields();
        all.Unchecked += (_, _) => ShowFields();
        start.SelectionChanged += (_, _) => Clear();
        target.SelectionChanged += (_, _) => Clear();
        AddColumn(T.Name, "Name");
        AddColumn(T.Field, "Field");
        AddColumn(T.Start, "Base");
        AddColumn(T.Target, "Target");
        AddColumn(T.Input, "File");
        AddColumn(T.Result, "Result");
        panel.Children.Add(grid);
        Clear();

        void AddVersion(string label, WC.ComboBox combo)
        {
            var column = new WC.StackPanel { Margin = new W.Thickness(0, 0, 12, 0) };
            choices.Children.Add(column);
            column.Children.Add(new WC.TextBlock { Text = label });
            column.Children.Add(combo);
        }
        void AddColumn(string label, string property) => grid.Columns.Add(new WC.DataGridTextColumn
            { Header = label, Binding = new Binding(property) });
    }

    private void Clear()
    {
        comparisons.Clear();
        provenance = "";
        grid.ItemsSource = null;
        summary.Text = T.Waiting;
    }

    private string? Pick(string title)
    {
        var picker = new OpenFileDialog { Title = title, Filter = T.FileFilter, CheckFileExists = true };
        if (installed != null && Directory.Exists(Path.GetDirectoryName(installed))) picker.InitialDirectory = Path.GetDirectoryName(installed);
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private async Task<OfficialFormation?> Resolve(ulong id, FormationFileData file)
    {
        if (references.TryGetValue(id, out var known)) return known;
        if (!manifests.TryGetValue(id, out string? manifest)) throw new InvalidDataException(T.MissingCache(id));
        var entry = await Task.Run(() => FormationDepotManifest.Read(manifest, id));
        FormationFileData? original = entry.Matches(file) ? file : null;
        if (original is null && installed != null && !path.Text.Equals(installed, StringComparison.OrdinalIgnoreCase) && File.Exists(installed))
        {
            // A patched or unreadable installation does not prevent using an archived original.
            try
            {
                var candidate = await Task.Run(() => FormationFileData.Read(installed));
                if (entry.Matches(candidate)) original = candidate;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException) { }
        }
        if (original is null)
        {
            if (!IsVisible || Pick(T.NeedOriginal(id)) is not string selected) return null;
            original = await Task.Run(() => FormationFileData.Read(selected));
            if (!entry.Matches(original)) throw new InvalidDataException(T.WrongOriginal(id));
        }
        return references[id] = new(id, original.Sha256, original.Rows, original.Tables);
    }

    internal async Task Compare()
    {
        Clear();
        if (start.SelectedItem is not ulong from || target.SelectedItem is not ulong to || path.Text.Length == 0) return;
        controls.IsEnabled = false;
        summary.Text = T.Loading;
        try
        {
            string selected = path.Text;
            var file = await Task.Run(() => FormationFileData.Read(selected));
            var original = await Resolve(from, file);
            if (original is null) { Clear(); return; }
            var destination = await Resolve(to, file);
            if (destination is null) { Clear(); return; }
            var report = FormationFileComparison.Compare(original, destination, file, references.Values);
            comparisons[L.Rules.Formation] = report.Fields.Length == 0 ? null : report.Fields.Select(f =>
                new FileRuleDifference($"#{f.Id} {file.Rows[f.Id - 1].Name}", FormationRules.Field(f.Field).Label,
                    f.Base.ToString(), f.Target.ToString(), f.File.ToString(), f.IdentityChanged)).ToArray();
            foreach (var layout in FileRuleLayout.All)
                comparisons[layout.Label] = FileRuleDifference.Compare(layout, original, destination, file);
            string source = file.Decoded ? T.DecodedFile : report.OfficialFile is ulong id ? T.KnownFile(id) : T.OtherFile;
            provenance = $"{source}\nSHA-256 {file.Sha256}\n";
            ShowFields();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            summary.Text = ex.Message;
        }
        finally { controls.IsEnabled = true; }
    }

    private void ShowFields()
    {
        if (table.SelectedItem is not string name || !comparisons.TryGetValue(name, out var fields)) return;
        summary.Text = provenance + (fields is null ? T.TableError : T.Summary(fields.Length,
            fields.Count(f => f.Base != f.Target), fields.Count(f => f.Base != f.File), fields.Count(f => f.Target != f.File)));
        grid.ItemsSource = all.IsChecked == true ? fields : fields?.Where(f => f.Changed).ToArray();
    }
}
