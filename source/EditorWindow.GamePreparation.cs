namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private GamePreparationWindow? gamePreparationWindow;
    private CancellationTokenSource? gamePreparationCancellation;
    private readonly GamePreparationAccess gamePreparationAccess;

    private PlateButton BuildGamePreparationMenu()
    {
        var button = new PlateButton(L.Preparation.Menu);
        button.Click += () =>
        {
            if (gamePreparationWindow is { } open) { open.Activate(); return; }
            if (busy) return;
            try
            {
                string build = gamePreparationAccess.Build();
                var saved = runtimeRuleFile.ForBuild(build).Read();
                saved?.CheckBuild(build);
                var window = gamePreparationWindow = new GamePreparationWindow(saved) { Owner = this, Build = build };
                window.Launch.Click += async () => await PrepareGameAsync(window);
                window.Cancel.Click += () => gamePreparationCancellation?.Cancel();
                window.Closing += (_, e) =>
                {
                    if (gamePreparationCancellation is not null) { gamePreparationCancellation.Cancel(); e.Cancel = true; }
                };
                window.Closed += (_, _) => gamePreparationWindow = null;
                window.Show();
            }
            catch (Exception error) { ReportError(error); }
        };
        return button;
    }

    private async Task PrepareGameAsync(GamePreparationWindow window)
    {
        if (busy || gamePreparationCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        gamePreparationCancellation = cancellation;
        SetBusy(true, scanning: false); window.SetWorking(true);
        try
        {
            string build = window.Build;
            // Refuse existing games before saving any changed selection.
            if (gamePreparationAccess.IsRunning()) throw new InvalidOperationException(L.Preparation.AlreadyRunning);
            var preferences = runtimeRuleFile.ForBuild(build);
            var preference = new RuntimeRulePreference(1, build, window.Selection, window.Convert.IsChecked == true);
            preference.CheckBuild(gamePreparationAccess.Build());
            preferences.Save(preference);
            window.Status.Text = L.Preparation.Waiting; AddLog(window.Status.Text);
            var identity = await Task.Run(() => new GamePreparation(gamePreparationAccess).RunAsync(preference,
                preferences.ReceiptPath, () => preferences.Read() == preference, cancellation.Token));
            runtimeRuleFailure = null;
            window.Status.Text = L.Preparation.Ready;
            AddLog(window.Status.Text + $" (PID {identity.ProcessId})");
        }
        catch (OperationCanceledException) { window.Status.Text = L.Preparation.Cancelled; AddLog(window.Status.Text); }
        catch (Exception error) { window.Status.Text = L.Preparation.Failed + " " + error.Message; AddLog(window.Status.Text); }
        finally
        {
            gamePreparationCancellation = null; window.SetWorking(false); SetBusy(false, scanning: false);
        }
    }
}
