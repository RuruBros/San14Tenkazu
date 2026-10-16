using WT = System.Windows.Threading;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private readonly WT.DispatcherTimer automaticGameTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource automaticGameCancellation = new();
    private readonly AutomaticGameAccess automaticGameAccess;
    private readonly AutomaticGameSettings automaticGameSettings;
    private bool automaticGameChecking, automaticGameConnecting;
    private GameIdentity? automaticGameFailure;
    private RuntimeRulePreference? automaticFailedPreference;
    private string automaticNotice = "";

    private void AutomaticNotice(string message)
    {
        status.Text = message;
        if (automaticNotice != message) { automaticNotice = message; AddLog(message); }
    }

    private async Task PollAutomaticGameAsync()
    {
        if (automaticGameChecking || busy || refreshing || runtimeRulesChecking || runtimeRulesChanging
            || environmentChecking || environmentChanging || seasonClosing || seasonDisconnecting
            || gamePreparationCancellation is not null || automaticGameCancellation.IsCancellationRequested) return;
        automaticGameChecking = true;
        GameIdentity? candidate = null;
        RuntimeRulePreference? preference = null;
        try
        {
            if (game is { } connected && await Task.Run(() => automaticGameAccess.Same(connected))) return;
            if (game is not null && (HasPendingEdits || registeredEditor?.HasPending == true))
            { AutomaticNotice(L.AutomaticSettings.PendingEdits); return; }
            if (game is not null && !await DisconnectAsync(L.AutomaticSettings.Waiting)) return;
            preference = runtimeRuleFile.Read();
            if (automaticGameFailure is { } failed && preference == automaticFailedPreference
                && await Task.Run(() => automaticGameAccess.Same(failed))) return;
            var observation = await Task.Run(() => automaticGameSettings.Poll(preference,
                runtimeRuleFile.ReceiptPath, () => runtimeRuleFile.Read() == preference, automaticGameCancellation.Token));
            if (automaticGameCancellation.IsCancellationRequested) return;
            candidate = observation.Identity;
            switch (observation.Phase)
            {
                case AutomaticGamePhase.Absent:
                    automaticGameFailure = null; AutomaticNotice(L.AutomaticSettings.Waiting); break;
                case AutomaticGamePhase.Starting:
                    AutomaticNotice(L.AutomaticSettings.Starting); break;
                case AutomaticGamePhase.RulesApplied:
                    AutomaticNotice(L.AutomaticSettings.RulesApplied); break;
                case AutomaticGamePhase.Unsupported:
                    AutomaticNotice(L.Preparation.Unsupported); automaticGameFailure = candidate; automaticFailedPreference = preference; break;
                case AutomaticGamePhase.Connect:
                    if (HasPendingEdits || registeredEditor?.HasPending == true) { AutomaticNotice(L.AutomaticSettings.PendingEdits); break; }
                    automaticGameConnecting = true;
                    try { await ConnectAsync(); }
                    finally { automaticGameConnecting = false; }
                    if (game != candidate) { automaticGameFailure = candidate; automaticFailedPreference = preference; }
                    else { automaticGameFailure = null; automaticNotice = ""; }
                    break;
            }
        }
        catch (OperationCanceledException) when (automaticGameCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Find can fail before an identity is available; deduplicate the notice then retry.
            automaticGameFailure = candidate ?? automaticGameSettings.Observed;
            automaticFailedPreference = preference;
            AutomaticNotice(L.AutomaticSettings.Failed + " " + error.Message);
        }
        finally { automaticGameChecking = false; UpdateControls(); }
    }
}
