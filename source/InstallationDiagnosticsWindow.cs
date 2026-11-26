using System.Diagnostics;
using System.Windows.Data;
using Microsoft.Win32;
using W = System.Windows;
using WC = System.Windows.Controls;
using T = San14Tenkazu.L.InstallationAudit;

namespace San14Tenkazu;

internal sealed class InstallationDiagnosticsWindow : W.Window
{
    private readonly WC.TextBlock paths = Label(), summary = Label();
    private readonly WC.TextBox details = new() { IsReadOnly = true, TextWrapping = W.TextWrapping.Wrap,
        VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, MinHeight = 80, MaxHeight = 155 };
    private readonly WC.CheckBox all = new() { Content = T.ShowMatches };
    private readonly WC.WrapPanel choices = new();
    private readonly PlateButton scan = new(T.Scan, primary: true), cancel = new(T.Cancel);
    private readonly WC.TabControl tabs = new();
    private readonly WC.DataGrid installed, packages, sources;
    private string? directory;
    private string[] packageRoots = [];
    private CancellationTokenSource? running;
    private InstallationAudit? report;

    internal InstallationDiagnosticsWindow(string? gameDirectory)
    {
        directory = gameDirectory ?? SteamInstallation.FindGameDirectory(null, requireResources: false);
        Title = T.Title;
        Width = 1000; Height = 690; MinWidth = 660; MinHeight = 450;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        Background = CardSkin.Parchment; Foreground = CardSkin.Ink; FontFamily = CardSkin.Font;
        var panel = new WC.DockPanel { Margin = new W.Thickness(14) };
        Content = panel;
        var head = new WC.StackPanel();
        WC.DockPanel.SetDock(head, WC.Dock.Top);
        panel.Children.Add(head);
        head.Children.Add(Label(T.Description));
        var buttons = new WC.WrapPanel { Margin = new W.Thickness(0, 8, 0, 5) };
        head.Children.Add(buttons);
        buttons.Children.Add(choices);
        Choice(T.GameFolder, () =>
        {
            var pick = new OpenFolderDialog { Title = T.GameFolder, AddToRecent = false };
            if (pick.ShowDialog(this) == true) { directory = pick.FolderName; Reset(); }
        });
        Choice(T.PackageFolders, () =>
        {
            var pick = new OpenFolderDialog { Title = T.PackageFolders, Multiselect = true, AddToRecent = false };
            if (pick.ShowDialog(this) == true) AddPackages(pick.FolderNames);
        });
        Choice(T.ClearPackages, () => { packageRoots = []; Reset(); });
        buttons.Children.Add(scan);
        buttons.Children.Add(cancel);
        scan.Click += async () => await ScanAsync();
        cancel.Click += () => running?.Cancel();
        cancel.IsEnabled = false;
        head.Children.Add(paths);
        head.Children.Add(summary);
        head.Children.Add(all);
        all.Checked += (_, _) => ShowInstalled();
        all.Unchecked += (_, _) => ShowInstalled();
        var foot = new WC.StackPanel();
        foot.Children.Add(Label(T.Evidence));
        foot.Children.Add(details);
        WC.DockPanel.SetDock(foot, WC.Dock.Bottom);
        panel.Children.Add(foot);
        panel.Children.Add(tabs);
        installed = Grid(T.Installed, (T.Result, "State"), (T.File, "Path"), (T.DepotClaims, "Info"), (T.Runtime, "Runtime"));
        packages = Grid(T.Packages, (T.Content, "State"), (T.PackageFile, "Path"), (T.DiskMatch, "Info"),
            (T.PackageOverlap, "Overlap"), (T.Runtime, "Runtime"));
        sources = Grid(T.Sources, (T.Result, "State"), (T.Source, "Path"), (T.Evidence, "Info"));
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == tabs && tabs.SelectedContent is WC.DataGrid grid)
                details.Text = (grid.SelectedItem as Row)?.Detail ?? "";
        };
        Closed += (_, _) => running?.Cancel();
        Reset();
    }

    private sealed record Row(string State, string Path, string Info, string Detail, string Runtime = "", string Overlap = "");

    internal void AddPackages(IEnumerable<string> folders)
    {
        packageRoots = packageRoots.Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Reset();
    }

    private static WC.TextBlock Label(string text = "") => new() { Text = text, TextWrapping = W.TextWrapping.Wrap };

    private void Choice(string text, Action click)
    {
        var button = new PlateButton(text) { Margin = new W.Thickness(0, 0, 6, 0) };
        button.Click += click;
        choices.Children.Add(button);
    }

    private WC.DataGrid Grid(string title, params (string Name, string Binding)[] columns)
    {
        var grid = new WC.DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            SelectionMode = WC.DataGridSelectionMode.Single, HeadersVisibility = WC.DataGridHeadersVisibility.Column };
        foreach (var col in columns)
            grid.Columns.Add(new WC.DataGridTextColumn { Header = col.Name, Binding = new Binding(col.Binding), MaxWidth = 550 });
        grid.SelectionChanged += (_, _) => details.Text = (grid.SelectedItem as Row)?.Detail ?? "";
        tabs.Items.Add(new WC.TabItem { Header = title, Content = grid });
        return grid;
    }

    private void Reset()
    {
        report = null;
        installed.ItemsSource = packages.ItemsSource = sources.ItemsSource = null;
        details.Clear();
        scan.IsEnabled = directory != null;
        paths.Text = $"{T.GameFolder} · {directory ?? T.ChooseGame}\n{T.Packages} · " +
            (packageRoots.Length == 0 ? T.NoneSelected : string.Join(" | ", packageRoots));
        summary.Text = T.Ready;
    }

    internal async Task ScanAsync()
    {
        if (directory == null || running != null) return;
        Reset();
        using var cancelSource = new CancellationTokenSource();
        running = cancelSource;
        choices.IsEnabled = scan.IsEnabled = false;
        cancel.IsEnabled = true;
        summary.Text = T.Reading;
        string game = directory;
        string[] folders = packageRoots;
        var throttle = Stopwatch.StartNew();
        IProgress<string> progress = new Progress<string>(path =>
        {
            if (running == cancelSource) summary.Text = $"{T.Reading} · {path}";
        });
        try
        {
            report = await Task.Run(() => InstallationAudit.Scan(game, folders, cancelSource.Token, path =>
            {
                if (throttle.ElapsedMilliseconds < 200) return;
                throttle.Restart();
                progress.Report(path);
            }));
            ShowReport(report);
        }
        catch (OperationCanceledException) { summary.Text = T.Cancelled; }
        catch (Exception ex) when (InstallationAudit.IsReadFailure(ex) || ex is ArgumentException)
        {
            summary.Text = ex.Message;
        }
        finally
        {
            running = null;
            choices.IsEnabled = scan.IsEnabled = true;
            cancel.IsEnabled = false;
        }
    }

    private static string Status(InstalledFileStatus status) => status switch
    {
        InstalledFileStatus.Matches => T.Matches, InstalledFileStatus.Changed => T.Changed, InstalledFileStatus.Added => T.Added,
        InstalledFileStatus.Missing => T.Missing, _ => T.Unknown
    };

    private void ShowInstalled()
    {
        if (report == null) return;
        installed.ItemsSource = report.Installed.Where(f => all.IsChecked == true || f.Status != InstalledFileStatus.Matches || f.Owners.Count > 1)
            .Select(f =>
            {
                string disk = f.Disk == null ? T.NoDiskFile : $"{f.Disk.Size:N0} B · SHA-1 {f.Disk.Sha1 ?? "—"}";
                string claims = string.Join("\n", f.Owners.Select(e => $"{e.Pin} · {e.Size:N0} B · SHA-1 {e.Sha1 ?? "—"}"));
                return new Row(Status(f.Status), f.Path, string.Join(", ", f.Owners.Select(e => e.Pin.Depot)),
                    string.Join("\n", new[] { f.Path, f.Reason, disk, claims }.Where(s => !string.IsNullOrEmpty(s))),
                    f.Runtime.HasValue ? T.Unobserved : "—");
            }).ToArray();
    }

    private void ShowReport(InstallationAudit audit)
    {
        string counts = string.Join(" · ", Enum.GetValues<InstalledFileStatus>().Select(s => $"{Status(s)} {audit.Installed.Count(f => f.Status == s):N0}"));
        summary.Text = T.Coverage(audit.Depots.Build ?? "—", audit.Depots.Sources.Count(s => s.Error == null), audit.Depots.Sources.Count) +
            (audit.Depots.Complete ? "" : $" ({T.Unknown})") +
            $" · {T.Packages} {audit.Packages.Length:N0}\n{counts} · {T.DepotOverlap} {audit.Installed.Count(f => f.Owners.Count > 1):N0}";
        ShowInstalled();
        packages.ItemsSource = audit.Packages.Select(p =>
        {
            var file = p.Member.File;
            string source = Path.Combine(p.Member.Root, file.Path);
            string matches = string.Join(", ", p.ContentPaths);
            string state = p.Status switch { PackageContentStatus.Found => T.ContentFound, PackageContentStatus.NoMatch => T.NoMatch, _ => T.Unknown };
            string peers = string.Join("\n", p.OtherPackages.Select(m =>
                $"{Path.Combine(m.Root, m.File.Path)} · SHA-256 {m.File.Sha256 ?? "—"}"));
            string evidence = $"{source}\n{file.Size:N0} B · SHA-256 {file.Sha256 ?? "—"}\n{T.DiskMatch} · {(matches.Length == 0 ? "—" : matches)}\n" +
                $"{T.DiskNames} · {(p.SameNamePaths.Length == 0 ? "—" : string.Join(", ", p.SameNamePaths))}\n{T.PackageNames} · {(peers.Length == 0 ? "—" : peers)}";
            if (file.Error != null) evidence += "\n" + file.Error;
            else if (p.Status == PackageContentStatus.Unknown) evidence += "\n" + T.IncompleteDisk;
            string overlap = p.OtherPackages.Length == 0 ? "—" : p.OtherPackages.Any(m =>
                m.File.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)) ? T.RelativeOverlap : T.NameOverlap;
            return new Row(state, Path.GetFileName(p.Member.Root) + "/" + file.Path, matches, evidence,
                p.Runtime.HasValue ? T.Unobserved : "—", overlap);
        }).ToArray();
        var index = audit.Depots;
        var rows = new List<Row> { new(T.DepotScope, index.AppManifest, index.Build ?? "—", index.AppManifest + "\n" + T.ScopeDetail) };
        rows.AddRange(index.SharedDepots.Select(s => new Row(T.Shared, $"{s.Depot} → {s.Owner}", s.Directory ?? T.Unknown,
            $"{s.Source}\n{s.Directory}\n{T.ScopeDetail}")));
        rows.AddRange(index.Sources.Select(s => new Row(s.Error == null ? T.Read : T.Unknown, s.Pin.ToString(),
            s.Error ?? $"{s.Files:N0}", $"{s.Path}\n{s.Error}")));
        rows.AddRange(audit.Issues.Select(i => new Row(T.Unknown, i.Source, i.Detail, $"{i.Source}\n{i.Detail}")));
        sources.ItemsSource = rows;
    }
}
