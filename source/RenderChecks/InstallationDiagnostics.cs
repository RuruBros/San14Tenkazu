using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using System.Windows.Threading;
using San14Tenkazu;

// --installation-diagnostics <game directory> [extracted package directory ...]
// Writes the observed evidence to stdout. Runs the WPF scan without a game process or a visible window.
internal static partial class RenderChecks
{
    private static void CheckInstallationDiagnostics(string[] paths)
    {
        if (paths.Length == 0) throw new ArgumentException("Expected game directory, then optional extracted packages");
        var window = new InstallationDiagnosticsWindow(paths[0]);
        T Field<T>(string name) => (T)window.GetType().GetField(name, Hidden)!.GetValue(window)!;
        var context = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            window.AddPackages(paths.Skip(1));
            Task scan = window.ScanAsync();
            var frame = new DispatcherFrame();
            scan.GetAwaiter().OnCompleted(() => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            scan.GetAwaiter().GetResult();
            var report = Field<InstallationAudit?>("report") ?? throw new InvalidOperationException(Field<TextBlock>("summary").Text);
            Require(Field<DataGrid>("installed").Items.Count == report.Installed.Count(f => f.Status != InstalledFileStatus.Matches || f.Owners.Count > 1),
                "visible installation differences and overlapping claims");
            Field<CheckBox>("all").IsChecked = true;
            Require(Field<DataGrid>("installed").Items.Count == report.Installed.Length, "all installation rows");
            var packageGrid = Field<DataGrid>("packages");
            Require(packageGrid.Items.Count == report.Packages.Length, "all package rows");
            int dll = Array.FindIndex(report.Packages, p => p.Runtime.HasValue);
            if (dll >= 0)
            {
                packageGrid.SelectedIndex = dll;
                object row = packageGrid.Items[dll];
                Require(row.GetType().GetProperty("Runtime")?.GetValue(row) as string == L.InstallationAudit.Unobserved,
                    "DLL runtime state remains unobserved");
                Require(Field<TextBox>("details").Text.Contains(report.Packages[dll].Member.File.Path), "package evidence selection");
            }
            var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Depots.AppManifest, report.Depots.Build, report.Depots.Complete, report.Depots.SharedDepots,
                report.Depots.Sources, report.Issues, report.Installed, report.Packages
            }, json));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            window.Close();
        }
    }
}
