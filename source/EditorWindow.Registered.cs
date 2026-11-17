using W = System.Windows;
using WC = System.Windows.Controls;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private RegisteredOfficerWindow? registeredEditor;
    private readonly Func<bool> registeredIsRunning;
    private readonly WT.DispatcherTimer registeredTimer = new(WT.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private bool registeredGameRunning;
    private bool registeredPolling, registeredClosed;
    private long registeredRevision;

    private void InitializeRegisteredAvailability()
    {
        WC.ToolTipService.SetShowOnDisabled(registeredButton, true);
        registeredTimer.Tick += async (_, _) => await PollRegisteredAvailabilityAsync();
        Loaded += async (_, _) => { registeredTimer.Start(); await PollRegisteredAvailabilityAsync(); };
        Activated += async (_, _) => await PollRegisteredAvailabilityAsync();
        Closed += (_, _) => { registeredClosed = true; registeredTimer.Stop(); };
        RefreshRegisteredAvailability();
    }

    private async Task PollRegisteredAvailabilityAsync()
    {
        if (registeredPolling || registeredClosed) return;
        registeredPolling = true;
        long revision = registeredRevision;
        try
        {
            bool running = await Task.Run(registeredIsRunning);
            if (registeredClosed || revision != registeredRevision) return;
            registeredGameRunning = running;
            UpdateRegisteredButton();
            registeredEditor?.SetGameRunning(running);
        }
        catch (Exception) when (registeredClosed) { }
        finally { registeredPolling = false; }
    }

    private void RefreshRegisteredAvailability()
    {
        ++registeredRevision;
        registeredGameRunning = registeredIsRunning();
        UpdateRegisteredButton();
        registeredEditor?.SetGameRunning(registeredGameRunning);
    }

    private void UpdateRegisteredButton()
    {
        registeredButton.IsEnabled = !busy && !registeredGameRunning;
        registeredButton.Text = L.Registered.Button;
        registeredButton.ToolTip = registeredGameRunning ? L.Registered.RunningTip : L.Registered.Tip;
    }

    private void OpenRegisteredEditor()
    {
        RefreshRegisteredAvailability();
        if (busy || registeredGameRunning) return;
        if (registeredEditor is not null) { registeredEditor.Activate(); return; }
        var window = new RegisteredOfficerWindow(catalog, AddLog, registeredIsRunning) { Owner = this };
        registeredEditor = window;
        window.Closed += (_, _) => registeredEditor = null;
        window.Show();
        try
        {
            string? directory = FaceLibrary.FindGameDirectory(game is { } identity ? GameMemory.ExecutablePath(identity) : null);
            string[] files = RegisteredOfficerFile.FindFiles(directory);
            if (files.Length == 1) window.Load(files[0]);
            else AddLog(L.Registered.NoFile);
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { ReportError(ex); }
    }
}
