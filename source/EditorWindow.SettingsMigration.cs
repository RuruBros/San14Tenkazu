namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private PlateButton? rulesImportButton;
    private void ImportRuntimeSettings(RuntimeRulesWindow window, GameIdentity identity)
    {
        if (busy || game != identity) return;
        var preferences=runtimeRuleFile.For(identity);
        string targetStamp=preferences.Stamp();
        var dialog=new Microsoft.Win32.OpenFileDialog { Filter="JSON|*.json", Title=L.Compatibility.Import };
        if (dialog.ShowDialog(window)!=true) return;
        try
        {
            var source=SettingsImportSource.Read(dialog.FileName); using var guard=source.Guard();
            if(source.PathName.Equals(Path.GetFullPath(preferences.PathName),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(L.Compatibility.MigrationUnsupported);
            var migrated=SettingsMigration.Runtime(source.Parse<RuntimeRulePreference>(), identity.ExeHash);
            if(preferences.Stamp()!=targetStamp)throw new IOException(L.Rules.ProfileMoved);
            window.ImportTargetStamp=targetStamp;
            window.LoadImported(migrated,source);
        }
        catch(Exception error){window.Status.Text=error.Message;ReportError(error);}
    }
    private PlateButton BuildRulesImportMenu()
    {
        var button=new PlateButton(L.Compatibility.ImportRules);
        rulesImportButton=button;button.IsEnabled=false;
        button.Click+=()=>
        {
            if(busy||RulesPending||game is not { } identity||!SupportsRuleTables(identity)||ruleKept is not {Profile:{} baseline} kept) return;
            var dialog=new Microsoft.Win32.OpenFileDialog { Filter=L.FileComparison.ProfileFilter, Title=L.Compatibility.ImportRules,
                InitialDirectory=RuleFileFor(identity).StorageDirectory };
            if(dialog.ShowDialog(this)!=true)return;
            try
            {
                GameCapabilities.Require(identity,GameFeature.RuleTables);
                var source=SettingsImportSource.Read(dialog.FileName);
                var plan = SettingsMigration.PlanRules(source.Parse<RuleProfile>(), baseline);
                HashSet<RuleKey>? selected = null;
                if (plan.Conflicts.Length > 0 || plan.Unavailable.Length > 0)
                {
                    if (ruleSnapshot is null) return;
                    var state = new RuleReadState(ruleSnapshot, tacticRuleSnapshot, policyRuleSnapshot,
                        officeRuleSnapshot, rankRuleSnapshot, doctrineRuleSnapshot);
                    var choices = new RuleImportDialog(plan, state.Name) { Owner = this };
                    if (choices.ShowDialog() != true) return;
                    selected = choices.TakeIncoming;
                }
                else if (!Confirm(L.Compatibility.ImportConfirm, System.Windows.MessageBoxImage.Question, defaultNo: true)) return;
                SaveImportedRules(source, identity, baseline, kept.Stamp, selected);
            }
            catch(Exception error){ReportError(error);}
        };
        return button;
    }
    private void SaveImportedRules(SettingsImportSource source,GameIdentity identity,RuleProfile baseline,string stamp, IReadOnlySet<RuleKey>? selected)
    {
        if(busy||RulesPending||game!=identity||ruleKept?.Stamp!=stamp)throw new IOException(L.Rules.ProfileMoved);
        GameCapabilities.Require(identity,GameFeature.RuleTables);
        ++ruleRevision;
        ruleKept=WithRuleDataset(identity, () => RuleFileFor(identity).Import(source,baseline,stamp,takeIncoming: selected));
        FillLists();RefreshEditor();UpdateControls();AddLog(L.Compatibility.ImportSaved);
    }
}
