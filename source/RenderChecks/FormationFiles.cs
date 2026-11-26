using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using San14Tenkazu;

internal static partial class RenderChecks
{
    // Read-only acceptance check using the user's two archived originals and optional comparison samples.
    private static void CheckFormationFiles(string[] paths)
    {
        if (paths.Length < 2) throw new ArgumentException("Expected previous and current official CE fixed data, then optional samples");
        var directory = SteamInstallation.FindGameDirectory(null);
        var manifests = FormationDepotManifest.Find(directory);
        var known = OfficialFormation.Known;
        var window = new FormationComparisonWindow(directory);
        T Control<T>(string name) => (T)window.GetType().GetField(name, Hidden)!.GetValue(window)!;
        try
        {
            window.Show();
            Control<ComboBox>("start").SelectedItem = known[0].Manifest;
            Control<ComboBox>("target").SelectedItem = known[1].Manifest;
            for (int index = 0; index < paths.Length; index++)
            {
                Control<ComboBox>("table").SelectedIndex = 0;
                var file = FormationFileData.Read(paths[index]);
                var report = FormationFileComparison.Compare(known[0], known[1], file, known);
                if (index < 2)
                {
                    var expected = known[index];
                    var manifest = FormationDepotManifest.Read(manifests[expected.Manifest], expected.Manifest);
                    Require(manifest.Matches(file) && report.OfficialFile == expected.Manifest && report.Fields.All(f =>
                        f.Base == f.Target && f.Target == f.File && !f.IdentityChanged), "official manifest and embedded records");
                }
                Control<TextBlock>("path").Text = paths[index];
                var context = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                    Task comparison = window.Compare();
                    var frame = new DispatcherFrame();
                    comparison.GetAwaiter().OnCompleted(() => frame.Continue = false);
                    Dispatcher.PushFrame(frame);
                    comparison.GetAwaiter().GetResult();
                }
                finally { SynchronizationContext.SetSynchronizationContext(context); }
                var grid = Control<DataGrid>("grid");
                var changed = report.Fields.Where(f => f.Base != f.Target || f.Target != f.File || f.IdentityChanged).ToArray();
                Require(grid.ItemsSource != null && grid.Items.Count == changed.Length, "changed fields in product grid");
                Control<CheckBox>("all").IsChecked = true;
                Require(grid.Items.Count == 100, "all fields in product grid");
                Control<CheckBox>("all").IsChecked = false;
                var related = new Dictionary<string, object>();
                foreach (var layout in FileRuleLayout.All)
                {
                    Control<ComboBox>("table").SelectedItem = layout.Label;
                    var entries = FileRuleDifference.Compare(layout, known[0], known[1], file)
                        ?? throw new InvalidDataException("Expected a complete table in the acceptance samples");
                    var differences = entries.Where(r => r.Changed).ToArray();
                    Require(grid.Items.Count == differences.Length, "related rule differences in WPF");
                    Control<CheckBox>("all").IsChecked = true;
                    Require(grid.Items.Count == layout.Count * (layout.Columns.Length + 1), "all related rule fields in WPF");
                    Control<CheckBox>("all").IsChecked = false;
                    if (index < 2) Require(differences.All(r => (index == 0 ? r.Base : r.Target) == r.File), "verified official reference rows");
                    related.Add(layout.Table, new { total = entries.Length, differences });
                }
                Console.WriteLine(JsonSerializer.Serialize(new { file = paths[index], file.Sha256, report.OfficialFile, changes = changed,
                    related, summary = Control<TextBlock>("summary").Text }));
            }
        }
        finally { window.Close(); }
    }
}
