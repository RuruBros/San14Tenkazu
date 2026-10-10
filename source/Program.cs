using System.Reflection;

namespace San14Tenkazu;

internal static class Program
{
    internal static string AppVersion => Version.Parse(
        typeof(Program).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version).ToString(3);

    // What the editor keeps from one run to the next (caches, the last player force).
    internal static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "San14Tenkazu");

    [STAThread]
    private static void Main()
    {
        if (IntPtr.Size != 8)
        {
            System.Windows.MessageBox.Show(L.Window.Needs64Bit, EditorWindow.AppTitle);
            return;
        }
        KeepEarlierData();
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        TooltipPresentation.Install(app.Resources);
        // A mistake in a click's handler is told and the editor goes on, as the Windows Forms editor did; left
        // alone, WPF would close it and drop what was typed.
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            System.Windows.MessageBox.Show(app.MainWindow, L.Window.Unhandled(e.Exception.GetType().Name, e.Exception.Message),
                EditorWindow.AppTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        };
        app.Run(new EditorWindow());
    }

    // The folder under the editor's earlier name is renamed once, so that the caches need not be made again.
    private static void KeepEarlierData()
    {
        string earlier = Path.Combine(Path.GetDirectoryName(DataFolder)!, "San14MemoryEditor");
        try
        {
            if (!Directory.Exists(DataFolder) && Directory.Exists(earlier)) Directory.Move(earlier, DataFolder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
