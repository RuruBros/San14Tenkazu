namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private readonly PlateButton runtimeRulesButton = new(L.GameSettingsUi.RuntimeMenu) { ToolTip = L.GameSettingsUi.RuntimeMenuTip };
    private RuntimeRulesWindow? runtimeRulesWindow;
    private bool runtimeRulesChanging;
    private bool runtimeRulesChecking;
    private readonly RuntimeRulePreferenceFile runtimeRuleFile;
    private readonly RuntimeRuleGameAccess runtimeRuleAccess;
    private GameIdentity? runtimeRuleFailure;
    private string runtimeRuleNotice = "";
    private GameIdentity? runtimeRulesIdentity;
    private PlateButton BuildRuntimeRulesMenu()
    {
        runtimeRulesButton.Click += async () =>
        {
            if (runtimeRulesWindow is { } existing) { existing.Activate(); return; }
            if (busy || automaticGameChecking) return;
            try
            {
                var identity = game;
                var state = identity is { Verified: true } connected ? await Task.Run(() => runtimeRuleAccess.Read(connected)) : null;
                if (game != identity || busy) return;
                var window = new RuntimeRulesWindow(state, offline: identity is null) { Owner = this };
                var preference = runtimeRuleFile.Read();
                if (preference is not null) { preference.CheckBuild(identity?.ExeHash ?? GameBuildProfiles.Current.ExeSha256); window.ShowSaved(preference, select: true); }
                runtimeRulesWindow = window;
                runtimeRulesIdentity = identity;
                window.Apply.Click += async () => await ChangeRuntimeRules(window, window.Selection);
                window.Restore.Click += async () => await ChangeRuntimeRules(window, RuntimeRuleSettings.Original);
                window.Import.Click += () => { if (game is { Verified: true } current) ImportRuntimeSettings(window, current); };
                window.Closing += (_, e) => { if (busy) e.Cancel = true; };
                window.Closed += (_, _) => runtimeRulesWindow = null;
                window.Show();
            }
            catch (Exception error) { ReportError(error); }
        };
        return runtimeRulesButton;
    }

    private async Task ChangeRuntimeRules(RuntimeRulesWindow window, RuntimeRuleSettings settings)
    {
        if (busy || refreshing || runtimeRulesChecking || automaticGameChecking) return;
        if (game is null && runtimeRulesIdentity is null)
        {
            try
            {
                var saved = new RuntimeRulePreference(1, GameBuildProfiles.Current.ExeSha256, settings, window.ConvertProgress.IsChecked == true);
                runtimeRuleFile.Save(saved); window.ShowSaved(saved, select: true);
                automaticGameFailure = null;
                window.Status.Text = L.AutomaticSettings.Saved; AddLog(window.Status.Text);
            }
            catch (Exception error) { window.Status.Text = error.Message; ReportError(error); }
            return;
        }
        if (game is not { Verified: true } identity) return;
        if (runtimeRulesIdentity != identity) { window.Status.Text = L.GameSettingsUi.ConnectionChanged; return; }
        if (lists.Pending > 0 || Editors.Any(card => card.Pending > 0)) { window.Status.Text = L.GameSettingsUi.PendingEdits; return; }
        bool convert = window.ConvertProgress.IsChecked == true;
        runtimeRulesChanging = true; ++ruleRevision; SetBusy(true, scanning: false); window.SetBusy(true);
        try
        {
            using var importedGuard = window.ImportedSource?.Guard();
            window.ImportedSource?.Archive(Path.Combine(Path.GetDirectoryName(runtimeRuleFile.PathName)!, "settings-history"));
            var preference = new RuntimeRulePreference(1, identity.ExeHash, settings, convert);
            runtimeRuleFile.Save(preference,preservePrevious:window.ImportedSource is not null,expectedStamp:window.ImportedSource is not null?window.ImportTargetStamp:null);
            window.ImportedSource=null; window.ImportTargetStamp=null; window.ShowSaved(preference);
            runtimeRuleFailure = null; runtimeRuleNotice = "";
            string receipt = runtimeRuleFile.ReceiptPath(identity);
            if (await Task.Run(() => runtimeRuleAccess.Pace(identity, layout!)) != GamePace.Paused)
            { window.Status.Text = L.GameSettingsUi.SavedWaitingForPause; AddLog(window.Status.Text); return; }
            var result = await Task.Run(() => runtimeRuleAccess.Apply(identity, settings, receipt, convert, false));
            FollowRuntimeRuleResult(result);
            window.ShowSettings(result.Snapshot.RuntimeRules);
            window.Status.Text = L.GameSettingsUi.SavedAndApplied($"{result.Converted}", $"{receipt}");
            AddLog(L.GameSettingsUi.AppliedLog($"{settings.Slots}", $"{(settings.TraitList9 ? 9 : 5)}", $"{settings.Abilities}", $"{result.Converted}"));
            foreach (string line in RestoreNotices(result)) { window.Status.Text += " " + line; AddLog(line); }
        }
        catch (Exception error) { runtimeRuleFailure = identity; window.Status.Text = error.Message; ReportError(error); }
        finally { runtimeRulesChanging = false; SetBusy(false, scanning: false); window.SetBusy(false); }
    }

    private static IEnumerable<string> RestoreNotices(RuntimeRuleApplyResult result)
    {
        if (result.Restored > 0) yield return L.GameSettingsUi.AbilitiesRestored($"{result.Restored}");
        if (result.TraitsRestored > 0) yield return L.GameSettingsUi.TraitsRestored($"{result.SaveName}", $"{result.TraitsRestored}");
    }

    private void FollowRuntimeRuleResult(RuntimeRuleApplyResult result)
    {
        layout = result.Layout; snapshot = result.Snapshot;
        // Earlier ability/experience Undo belongs to a different growth curve.
        undoHistory.RemoveAll(h => h.Edits.Any(e => e.Changes.Any(c => OfficerFields.Abilities.Contains(c.Field) || OfficerFields.IsExperience(c.Field))));
        map.SetSnapshot(snapshot); FillLists(); ReopenEditor();
    }

    private async Task ReapplyRuntimeRulesAsync(bool retry = false)
    {
        if (busy || refreshing || runtimeRulesChecking || runtimeRulesChanging || layoutRecovering
            || seasonClosing || seasonDisconnecting || game is not { Verified: true } identity || layout is null) return;
        if (retry) runtimeRuleFailure = null;
        if (runtimeRuleFailure == identity) return;
        bool Pending() => lists.Pending > 0 || aiView?.Pending == true || Editors.Any(c => c.Pending > 0) || RulesPending || runtimeRulesWindow?.HasPendingChanges == true;
        if (Pending()) return;
        var observed = snapshot; var currentLayout = layout;
        bool Current() => !busy && !refreshing && game == identity && ReferenceEquals(snapshot, observed) && !seasonClosing && !seasonDisconnecting;
        void Notice(string text)
        {
            if (runtimeRuleNotice != text) { runtimeRuleNotice = text; AddLog(text); }
            if (runtimeRulesWindow is { } window && runtimeRulesIdentity == identity) window.Status.Text = text;
        }
        bool writing = false;
        runtimeRulesChecking = true;
        try
        {
            var current = await Task.Run(() => runtimeRuleAccess.Read(identity));
            if (!Current() || Pending()) return;
            var preference = runtimeRuleFile.Read();
            if (preference is null)
            {
                preference = runtimeRuleFile.AdoptCommitted(identity, current);
                if (preference is null) return;
                Notice(L.GameSettingsUi.AdoptedCurrentRules);
            }
            preference.CheckBuild(identity.ExeHash);
            if (current is null) { Notice(L.GameSettingsUi.UnknownRulesDeferred); return; }
            // The steady state (rules already applied, officer data already right) is decided without watching the
            // clock: ReadPace waits up to 0.8 s while the game is paused, and this runs after every live refresh.
            if (current == preference.Settings)
            {
                bool experienceChanged = false;
                if (preference.Settings.Growth || preference.Settings.Traits9 || preference.Settings.Expanded)
                {
                    // Matching patch bytes and table addresses do not prove that officer data still matches.
                    // A load can reuse storage; save discovery can also find recovery data on a later refresh.
                    experienceChanged = await Task.Run(() => runtimeRuleAccess.NeedsExperienceReapply?.Invoke(identity, currentLayout, preference.Settings, runtimeRuleFile.ReceiptPath(identity)) ?? false);
                    if (!Current() || Pending() || runtimeRuleFile.Read() != preference) return;
                }
                if (!experienceChanged)
                {
                    if (runtimeRulesWindow is { } same && runtimeRulesIdentity == identity)
                    { same.ShowSettings(current); same.ShowSaved(preference); }
                    runtimeRuleNotice = "";
                    return;
                }
            }
            // Something is left to write: the paused transaction needs the clock confirmed still.
            var pace = await Task.Run(() => runtimeRuleAccess.Pace(identity, currentLayout));
            if (!Current() || Pending()) return;
            if (runtimeRuleFile.Read() != preference) return;
            if (pace != GamePace.Paused) { Notice(L.GameSettingsUi.WaitingForPause); return; }
            writing = true; runtimeRulesChanging = true; ++ruleRevision; SetBusy(true, scanning: false); runtimeRulesWindow?.SetBusy(true);
            var result = await Task.Run(() => runtimeRuleAccess.Apply(identity, preference.Settings, runtimeRuleFile.ReceiptPath(identity), preference.ConvertExperience, true));
            if (game != identity) return;
            FollowRuntimeRuleResult(result);
            if (runtimeRulesWindow is { } applied && runtimeRulesIdentity == identity)
            { applied.ShowSettings(result.Snapshot.RuntimeRules); applied.ShowSaved(preference); }
            Notice(string.Join(" ", new[] { L.GameSettingsUi.Reapplied($"{result.Converted}") }.Concat(RestoreNotices(result))));
        }
        catch (Exception error)
        {
            runtimeRuleFailure = identity;
            Notice(L.GameSettingsUi.AutomaticApplyFailed + error.Message + L.GameSettingsUi.RetryAfterRefresh);
        }
        finally
        {
            runtimeRulesChecking = false;
            if (writing) { runtimeRulesChanging = false; SetBusy(false, scanning: false); runtimeRulesWindow?.SetBusy(false); }
        }
    }
}
