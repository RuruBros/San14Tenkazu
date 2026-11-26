using Microsoft.Win32;
using System.Windows.Data;
using W = System.Windows;
using WC = System.Windows.Controls;
using T = San14Tenkazu.L.FileExport;

namespace San14Tenkazu;

internal sealed class RuleFileExportWindow : W.Window
{
    private readonly string? gameDirectory;
    private string input = "", profile = "";
    private RuleFileExport? plan;
    private readonly WC.StackPanel controls = new();
    private readonly WC.TextBlock summary = new() { TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.Expander choices = new() { Header = T.ConflictChoices };
    private readonly WC.DataGrid grid = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false };
    private readonly PlateButton preview = new(T.Preview), save = new(T.Save, primary: true);

    internal RuleFileExportWindow(string? gameDirectory)
    {
        this.gameDirectory = gameDirectory;
        string? installed = gameDirectory == null ? null : Path.Combine(gameDirectory, FormationFileData.RelativePath);
        if (File.Exists(installed)) input = installed;
        Title = T.Title;
        Width = 860; Height = 580; MinWidth = 540; MinHeight = 360;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        Background = CardSkin.Parchment; Foreground = CardSkin.Ink; FontFamily = CardSkin.Font;
        var panel = new WC.DockPanel { Margin = new W.Thickness(14) };
        Content = panel;
        WC.DockPanel.SetDock(controls, WC.Dock.Top); panel.Children.Add(controls);
        controls.Children.Add(new WC.TextBlock { Text = T.Instructions, TextWrapping = W.TextWrapping.Wrap });
        AddPicker(T.Input, T.FileFilter, input, gameDirectory, selected => input = selected);
        AddPicker(T.Profile, L.FileComparison.ProfileFilter, profile, Program.DataFolder, selected => profile = selected);
        var buttons = new WC.WrapPanel { Margin = new W.Thickness(0, 8, 0, 6) };
        buttons.Children.Add(preview);
        save.Margin = new W.Thickness(6, 0, 0, 0);
        buttons.Children.Add(save);
        controls.Children.Add(buttons); controls.Children.Add(summary); controls.Children.Add(choices);
        preview.Click += async () => await Preview();
        save.Click += async () => await Save();
        foreach (var (label, property) in new[] {
            (L.FileComparison.Table, "Table"), (L.FileComparison.Name, "Name"), (L.FileComparison.Field, "Field"),
            (L.Rules.Baseline, "Baseline"), (L.FileComparison.Input, "Current"), (L.Rules.Setting, "Desired"), (L.Rules.State, "Status") })
            grid.Columns.Add(new WC.DataGridTextColumn { Header = label, Binding = new Binding(property) });
        panel.Children.Add(grid);
        Clear();
    }

    private void AddPicker(string title, string filter, string initialFile, string? initialDirectory, Action<string> select)
    {
        var row = new WC.DockPanel { Margin = new W.Thickness(0, 6, 0, 0) };
        var button = new PlateButton(title);
        var path = new WC.TextBlock { Text = initialFile, VerticalAlignment = W.VerticalAlignment.Center,
            TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(8, 0, 0, 0) };
        WC.DockPanel.SetDock(button, WC.Dock.Left); row.Children.Add(button); row.Children.Add(path);
        controls.Children.Add(row);
        button.Click += () =>
        {
            var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            string? directory = Path.GetDirectoryName(path.Text);
            if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
            else if (Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            if (dialog.ShowDialog(this) != true) return;
            path.Text = dialog.FileName;
            select(dialog.FileName);
            Clear();
        };
    }

    private void Clear()
    {
        plan = null; grid.ItemsSource = null; summary.Text = "";
        choices.Content = null; choices.IsExpanded = false; choices.Visibility = W.Visibility.Collapsed;
        preview.IsEnabled = input.Length > 0 && profile.Length > 0;
        save.IsEnabled = false;
    }

    private async Task Preview()
    {
        Clear();
        summary.Text = L.FileComparison.Loading;
        controls.IsEnabled = false;
        try
        {
            var prepared = await Task.Run(() => RuleFileExport.Preview(input, profile));
            ShowPlan(prepared);
            var conflicts = prepared.Edits.Where(e => e.Status == FileEditStatus.Conflict).ToArray();
            if (conflicts.Length > 0)
            {
                var selected = new HashSet<RuleKey>();
                var list = new WC.StackPanel();
                foreach (var e in conflicts)
                {
                    var check = new WC.CheckBox { Content = $"{Table(e.Key)} #{e.Key.Record} {e.Cell!.Row} · {e.Cell.Label}  {e.Cell.Value} → {e.Desired}" };
                    check.Checked += (_, _) => { selected.Add(e.Key); ShowPlan(prepared.WithConflictChoices(selected)); };
                    check.Unchecked += (_, _) => { selected.Remove(e.Key); ShowPlan(prepared.WithConflictChoices(selected)); };
                    list.Children.Add(check);
                }
                choices.Content = new WC.ScrollViewer { Content = list, MaxHeight = 120, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
                choices.Visibility = W.Visibility.Visible;
            }
        }
        catch (Exception ex) when (FileError(ex)) { summary.Text = ex.Message; }
        finally { controls.IsEnabled = true; }
    }

    private static string Table(RuleKey key) => key.Table switch { "formation" => L.Rules.Formation, "tactic" => L.Rules.TacticPage,
        "policy" => L.Rules.PolicyPage, "policyeffect" => L.Rules.PolicyEffects, "office" => L.Rules.OfficePage,
        "rank" => L.Rules.RankPage, _ => L.Rules.DoctrinePage };

    private void ShowPlan(RuleFileExport prepared)
    {
        plan = prepared;
        var rows = plan.Edits;
        grid.ItemsSource = rows.Select(e => new {
                Table = Table(e.Key),
                Name = $"#{e.Key.Record} {e.Cell?.Row}", Field = e.Cell?.Label ?? e.Key.Field, e.Baseline, Current = e.Cell?.Value, e.Desired,
                Status = e.Status switch { FileEditStatus.Apply => T.Ready, FileEditStatus.Already => T.Already,
                    FileEditStatus.Conflict => L.Rules.Conflict, FileEditStatus.Identity => T.Identity,
                    FileEditStatus.Dependency => T.Dependency, FileEditStatus.RangeHeld => L.EditRanges.FormationHold, _ => T.Unsupported }
            }).ToArray();
        int apply = rows.Count(e => e.Status == FileEditStatus.Apply), already = rows.Count(e => e.Status == FileEditStatus.Already);
        summary.Text = T.Summary(apply, already, rows.Count - apply - already) + $"\nSHA-256  {plan.InputHash}";
        save.IsEnabled = plan.CanExport;
    }

    private async Task Save()
    {
        if (plan is not { } prepared) return;
        var dialog = new OpenFolderDialog { Title = T.ChooseFolder };
        if (dialog.ShowDialog(this) != true) return;
        string directory = dialog.FolderName;
        controls.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => prepared.Save(directory, gameDirectory));
            summary.Text = T.Saved(result.File, result.Sha256, result.Original);
        }
        catch (Exception ex) when (FileError(ex)) { Clear(); summary.Text = ex.Message; }
        finally { controls.IsEnabled = true; }
    }

    private static bool FileError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException
        or OverflowException or InvalidOperationException or FormatException;
}
