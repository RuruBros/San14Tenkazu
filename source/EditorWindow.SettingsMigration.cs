namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private PlateButton? rulesImportButton;
    private void ImportRuntimeSettings(RuntimeRulesWindow window, GameIdentity identity)
    {
        if (busy || game != identity) return;
        string targetStamp=runtimeRuleFile.Stamp();
        var dialog=new Microsoft.Win32.OpenFileDialog { Filter="JSON|*.json", Title=L.Compatibility.Import };
        if (dialog.ShowDialog(window)!=true) return;
        try
        {
            var source=SettingsImportSource.Read(dialog.FileName); using var guard=source.Guard();
            if(source.PathName.Equals(Path.GetFullPath(runtimeRuleFile.PathName),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(L.Compatibility.MigrationUnsupported);
            var migrated=SettingsMigration.Runtime(source.Parse<RuntimeRulePreference>(), identity.ExeHash);
            if(runtimeRuleFile.Stamp()!=targetStamp)throw new IOException(L.Rules.ProfileMoved);
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
            if(busy||RulesPending||game is not {Verified:true} identity||ruleKept is not {Profile:{} baseline} kept) return;
            var dialog=new Microsoft.Win32.OpenFileDialog { Filter="JSON|*.json",Title=L.Compatibility.ImportRules };
            if(dialog.ShowDialog(this)!=true)return;
            try
            {
                GameCapabilities.Require(identity,GameFeature.RuleTables);
                var source=SettingsImportSource.Read(dialog.FileName);
                _=SettingsMigration.Rules(source.Parse<RuleProfile>(),baseline); // Validate the concrete plan before confirmation.
                if(!Confirm(L.Compatibility.ImportConfirm,System.Windows.MessageBoxImage.Question,defaultNo:true))return;
                SaveImportedRules(source,identity,baseline,kept.Stamp);
            }
            catch(Exception error){ReportError(error);}
        };
        return button;
    }
    private void SaveImportedRules(SettingsImportSource source,GameIdentity identity,RuleProfile baseline,string stamp)
    {
        if(busy||RulesPending||game!=identity||ruleKept?.Stamp!=stamp)throw new IOException(L.Rules.ProfileMoved);
        GameCapabilities.Require(identity,GameFeature.RuleTables);
        ++ruleRevision;
        ruleKept=ruleFile.Import(source,baseline,stamp);
        FillLists();RefreshEditor();UpdateControls();AddLog(L.Compatibility.ImportSaved);
    }
}
