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
                var state = identity is { } connected && RuntimeRuleBuildProfile.Supports(connected)
                    ? await Task.Run(() => runtimeRuleAccess.Read(connected)) : null;
                if (game != identity || busy) return;
                string build = identity?.ExeHash ?? gamePreparationAccess.Build();
                var window = new RuntimeRulesWindow(state, offline: identity is null) { Owner = this, TargetBuild = build };
                var preference = runtimeRuleFile.ForBuild(build).Read();
                if (preference is not null) { preference.CheckBuild(build); window.ShowSaved(preference, select: true); }
                runtimeRulesWindow = window;
                runtimeRulesIdentity = identity;
                window.Apply.Click += async () => await ChangeRuntimeRules(window, window.Selection);
                window.Restore.Click += async () => await ChangeRuntimeRules(window, RuntimeRuleSettings.Original);
                window.Import.Click += () => { if (game is { } current && RuntimeRuleBuildProfile.Supports(current)) ImportRuntimeSettings(window, current); };
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
                var saved = new RuntimeRulePreference(1, window.TargetBuild, settings, window.ConvertProgress.IsChecked == true);
                saved.CheckBuild(gamePreparationAccess.Build());
                runtimeRuleFile.ForBuild(saved.Build).Save(saved); window.ShowSaved(saved, select: true);
                automaticGameFailure = null;
                window.Status.Text = L.AutomaticSettings.Saved; AddLog(window.Status.Text);
            }
            catch (Exception error) { window.Status.Text = error.Message; ReportError(error); }
            return;
        }
        if (game is not { } identity || !RuntimeRuleBuildProfile.Supports(identity) || layout is null) return;
        if (runtimeRulesIdentity != identity) { window.Status.Text = L.GameSettingsUi.ConnectionChanged; return; }
        if (lists.Pending > 0 || Editors.Any(card => card.Pending > 0)) { window.Status.Text = L.GameSettingsUi.PendingEdits; return; }
        bool convert = window.ConvertProgress.IsChecked == true;
        var preferences = runtimeRuleFile.For(identity);
        runtimeRulesChanging = true; ++ruleRevision; SetBusy(true, scanning: false); window.SetBusy(true);
        try
        {
            using var importedGuard = window.ImportedSource?.Guard();
            window.ImportedSource?.Archive(Path.Combine(Path.GetDirectoryName(preferences.PathName)!, "settings-history"));
            var preference = new RuntimeRulePreference(1, identity.ExeHash, settings, convert);
            preferences.Save(preference,preservePrevious:window.ImportedSource is not null,expectedStamp:window.ImportedSource is not null?window.ImportTargetStamp:null);
            window.ImportedSource=null; window.ImportTargetStamp=null; window.ShowSaved(preference);
            runtimeRuleFailure = null; runtimeRuleNotice = "";
            string receipt = preferences.ReceiptPath(identity);
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
        // Earlier officer edits may belong to a different growth curve or trait slot count.
        undoHistory.RemoveAll(h => h.Edits.Any(e => e.Changes.Any(c => OfficerFields.Abilities.Contains(c.Field) ||
            OfficerFields.IsExperience(c.Field) || OfficerFields.Personalities.Contains(c.Field))));
        map.SetSnapshot(snapshot); FillLists(); ReopenEditor();
    }

    private async Task ReapplyRuntimeRulesAsync(bool retry = false)
    {
        if (busy || refreshing || runtimeRulesChecking || runtimeRulesChanging || layoutRecovering
            || seasonClosing || seasonDisconnecting || game is not { } identity || !RuntimeRuleBuildProfile.Supports(identity) || layout is null) return;
        var preferences = runtimeRuleFile.For(identity);
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
            var preference = preferences.Read();
            if (preference is null)
            {
                preference = preferences.AdoptCommitted(identity, current);
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
                    experienceChanged = await Task.Run(() => runtimeRuleAccess.NeedsExperienceReapply?.Invoke(identity, currentLayout, preference.Settings, preferences.ReceiptPath(identity)) ?? false);
                    if (!Current() || Pending() || preferences.Read() != preference) return;
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
            if (preferences.Read() != preference) return;
            if (pace != GamePace.Paused) { Notice(L.GameSettingsUi.WaitingForPause); return; }
            writing = true; runtimeRulesChanging = true; ++ruleRevision; SetBusy(true, scanning: false); runtimeRulesWindow?.SetBusy(true);
            var result = await Task.Run(() => runtimeRuleAccess.Apply(identity, preference.Settings, preferences.ReceiptPath(identity), preference.ConvertExperience, true));
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
#if LOCAL_DEVELOPER
            UpdateDeveloperControls();
#endif
        }
    }
}
