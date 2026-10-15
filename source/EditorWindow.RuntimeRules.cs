namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private readonly PlateButton runtimeRulesButton = new("개성·능력치") { ToolTip = "9개성 발동 · 능력치 상한과 성장 규칙" };
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
            if (busy || game is not { Verified: true } identity) return;
            try
            {
                var state = await Task.Run(() => runtimeRuleAccess.Read(identity));
                if (game != identity || busy) return;
                var window = new RuntimeRulesWindow(state) { Owner = this };
                var preference = runtimeRuleFile.Read();
                if (preference is not null) { preference.CheckBuild(identity.ExeHash); window.ShowSaved(preference, select: true); }
                runtimeRulesWindow = window;
                runtimeRulesIdentity = identity;
                window.Apply.Click += async () => await ChangeRuntimeRules(window, window.Selection);
                window.Restore.Click += async () => await ChangeRuntimeRules(window, RuntimeRuleSettings.Original);
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
        if (busy || refreshing || runtimeRulesChecking || game is not { Verified: true } identity) return;
        if (runtimeRulesIdentity != identity) { window.Status.Text = "게임 연결이 바뀌었습니다. 창을 닫고 다시 여세요."; return; }
        if (Editors.Any(card => card.Pending > 0)) { window.Status.Text = "편집 중인 내용을 먼저 적용하거나 취소하세요."; return; }
        bool convert = window.ConvertProgress.IsChecked == true;
        runtimeRulesChanging = true; ++ruleRevision; SetBusy(true, scanning: false); window.SetBusy(true);
        try
        {
            var preference = new RuntimeRulePreference(1, identity.ExeHash, settings, convert);
            runtimeRuleFile.Save(preference); window.ShowSaved(preference);
            runtimeRuleFailure = null; runtimeRuleNotice = "";
            string receipt = runtimeRuleFile.ReceiptPath(identity);
            if (await Task.Run(() => runtimeRuleAccess.Pace(identity, layout!)) != GamePace.Paused)
            { window.Status.Text = "설정 저장 완료 · 일시정지 후 다음 갱신에서 자동 적용합니다."; AddLog(window.Status.Text); return; }
            var result = await Task.Run(() => runtimeRuleAccess.Apply(identity, settings, receipt, convert, false));
            FollowRuntimeRuleResult(result);
            window.ShowSettings(result.Snapshot.RuntimeRules);
            window.Status.Text = $"설정 저장·적용 완료 · 경험치 {result.Converted}개 변환. 변경 기록: {receipt}";
            AddLog($"개성·능력치 규칙 저장·적용: 개성 {settings.Slots}개 / 목록 {(settings.TraitList9 ? 9 : 5)}개 / {settings.Abilities} · 경험치 {result.Converted}개 변환");
        }
        catch (Exception error) { runtimeRuleFailure = identity; window.Status.Text = error.Message; ReportError(error); }
        finally { runtimeRulesChanging = false; SetBusy(false, scanning: false); window.SetBusy(false); }
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
        bool Pending() => aiView?.Pending == true || Editors.Any(c => c.Pending > 0) || RulesPending || runtimeRulesWindow?.HasPendingChanges == true;
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
                Notice("개성·능력치: 현재 적용한 기록을 자동 재적용 설정으로 저장했습니다.");
            }
            preference.CheckBuild(identity.ExeHash);
            if (current is null) { Notice("개성·능력치 자동 적용 보류 · 현재 규칙을 확인할 수 없습니다."); return; }
            if (current == preference.Settings)
            {
                if (runtimeRulesWindow is { } same && runtimeRulesIdentity == identity)
                { same.ShowSettings(current); same.ShowSaved(preference); }
                runtimeRuleNotice = "";
                return;
            }
            var pace = await Task.Run(() => runtimeRuleAccess.Pace(identity, currentLayout));
            if (!Current() || Pending()) return;
            if (runtimeRuleFile.Read() != preference) return;
            if (pace != GamePace.Paused) { Notice("개성·능력치 저장 설정 대기 · 일시정지 후 자동 적용합니다."); return; }
            writing = true; runtimeRulesChanging = true; ++ruleRevision; SetBusy(true, scanning: false); runtimeRulesWindow?.SetBusy(true);
            var result = await Task.Run(() => runtimeRuleAccess.Apply(identity, preference.Settings, runtimeRuleFile.ReceiptPath(identity), preference.ConvertExperience, true));
            if (game != identity) return;
            FollowRuntimeRuleResult(result);
            if (runtimeRulesWindow is { } applied && runtimeRulesIdentity == identity)
            { applied.ShowSettings(result.Snapshot.RuntimeRules); applied.ShowSaved(preference); }
            Notice($"개성·능력치 저장 설정 자동 적용 완료 · 경험치 {result.Converted}개 변환");
        }
        catch (Exception error)
        {
            runtimeRuleFailure = identity;
            Notice("개성·능력치 자동 적용 중단 · " + error.Message + " · 원인을 확인한 뒤 새로고침으로 재시도하세요.");
        }
        finally
        {
            runtimeRulesChecking = false;
            if (writing) { runtimeRulesChanging = false; SetBusy(false, scanning: false); runtimeRulesWindow?.SetBusy(false); }
        }
    }
}
