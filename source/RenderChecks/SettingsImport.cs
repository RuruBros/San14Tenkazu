using San14Tenkazu;
using San14Tenkazu.Tests;
using System.IO;
using System.Text.Json;
using W=System.Windows;
using WC=System.Windows.Controls;

internal static partial class RenderChecks
{
    private static void CheckSettingsImportOffline(string output)
    {
        Directory.CreateDirectory(output);var passed=new List<string>();
        void Check(bool value,string name){Require(value,name);passed.Add(name);}
        static void Set(object o,string n,object? v)=>o.GetType().GetField(n,Hidden)!.SetValue(o,v);
        var identity=new GameIdentity(1,DateTime.UnixEpoch,0x140000000,GameBuildFacts.ExeSha256,true,TestGame.Classes);
        string sourcePath=Path.GetFullPath(Path.Combine(output,"runtime-source.json"));
        var preference=new RuntimeRulePreference(1,identity.ExeHash,new(true,AbilityRuleMode.Growth200,true),false);
        byte[] sourceBytes=JsonSerializer.SerializeToUtf8Bytes(preference);File.WriteAllBytes(sourcePath,sourceBytes);
        var source=SettingsImportSource.Read(sourcePath);var window=new RuntimeRulesWindow(RuntimeRuleSettings.Original);
        var saved=new RuntimeRulePreference(1,identity.ExeHash,RuntimeRuleSettings.Original,true);window.ShowSaved(saved,true);
        try
        {
            var plan=SettingsMigration.Runtime(source.Parse<RuntimeRulePreference>(),identity.ExeHash);window.LoadImported(plan,source);
            Check(window.HasPendingChanges&&window.Selection==preference.Settings&&window.ConvertProgress.IsChecked==false,"import-is-a-pending-draft");
            Check(ReferenceEquals(source,window.ImportedSource)&&File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(sourceBytes),"draft-preserves-original-source");
            var applied=(WC.TextBlock)typeof(RuntimeRulesWindow).GetField("applied",Hidden)!.GetValue(window)!;
            Check(applied.Text.Contains("5개")&&!applied.Text.Contains("9개"),"import-does-not-claim-game-application");
            window.Show();window.Width=500;window.UpdateLayout();
            foreach(var button in new[]{window.Apply,window.Restore,window.Import})
            {
                var at=button.TranslatePoint(new W.Point(),window);
                Check(button.ActualWidth>0&&at.X>=0&&at.X+button.ActualWidth<=window.ActualWidth,"import-buttons-fit-minimum-window-"+button.Text);
            }
            window.SetBusy(true);Check(!window.Import.IsEnabled&&!window.Apply.IsEnabled,"busy-import-cannot-race-apply");
            window.SetBusy(false);Check(window.Import.IsEnabled,"known-state-enables-import");
            window.ShowSettings(null);Check(!window.Import.IsEnabled&&!window.Apply.IsEnabled,"unknown-runtime-state-stays-blocked");
        }
        finally{window.Close();}
        var fixture=new FormationFixture();var formation=fixture.Snapshot();var baseline=formation.Baseline(identity.ExeHash);
        string storage=Path.GetFullPath(Path.Combine(output,"storage"));var profileFile=new RuleProfileFile(Path.Combine(storage,"rule_profiles.json"));
        var kept=profileFile.Save(baseline,"");var key=baseline.Defaults[0].Key;
        string rulesSource=Path.GetFullPath(Path.Combine(output,"rules-source.json"));File.WriteAllText(rulesSource,JsonSerializer.Serialize(baseline.WithChanges([new(key,10,20)])));
        int reads=0,writes=0;
        var access=new RuleGameAccess(_=>{++reads;return formation;},(_,_)=>GamePace.Paused,(_,_,_)=>{++writes;throw new InvalidOperationException("Unexpected game write");});
        var editor=OfflineEditor(()=>false,storage,storage,storage,access);
        try
        {
            Set(editor,"game",identity);Set(editor,"snapshot",new TestGame().Build());Set(editor,"ruleSnapshot",formation);Set(editor,"ruleKept",kept);
            editor.GetType().GetMethod("SaveImportedRules",Hidden)!.Invoke(editor,[SettingsImportSource.Read(rulesSource),identity,baseline,kept.Stamp]);
            var imported=profileFile.Read();Check(imported.Profile!.Desired(key)==20&&!imported.Profile.AutoApply,"rules-import-saves-overrides-with-autoapply-off");
            Check(imported.Profile.Defaults.SequenceEqual(baseline.Defaults),"rules-import-keeps-verified-target-defaults");
            Check(reads==0&&writes==0&&fixture.Writes==0,"rules-import-never-invokes-game-access");
            byte[] before=File.ReadAllBytes(Path.Combine(storage,"rule_profiles.json"));Set(editor,"game",identity with{ProcessId=2});
            try{editor.GetType().GetMethod("SaveImportedRules",Hidden)!.Invoke(editor,[SettingsImportSource.Read(rulesSource),identity,imported.Profile,imported.Stamp]);throw new InvalidOperationException("Stale identity accepted");}
            catch(System.Reflection.TargetInvocationException e)when(e.InnerException is IOException){}
            Check(File.ReadAllBytes(Path.Combine(storage,"rule_profiles.json")).AsSpan().SequenceEqual(before)&&writes==0,"connection-change-refuses-import-without-saving-or-writing");
        }
        finally{editor.Close();}
        File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{gameAccess=false,passed=passed.Count,checks=passed},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS settings import WPF {passed.Count} checks; gameAccess=false");
    }
}
