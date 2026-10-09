using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckRankRules(string output)
    {
        Directory.CreateDirectory(output); using var journal=new StreamWriter(Path.Combine(output,"rank-rules.jsonl")) {AutoFlush=true};
        var previous=SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        static T Field<T>(object o,string n) => (T)o.GetType().GetField(n,Hidden)!.GetValue(o)!;
        static void Set(object o,string n,object? v) => o.GetType().GetField(n,Hidden)!.SetValue(o,v);
        static object? Call(object o,string n,params object?[] a) => o.GetType().GetMethod(n,Hidden)!.Invoke(o,a);
        static void Await(Task t) {
            var watch=Stopwatch.StartNew(); while (!t.IsCompleted) {
                if (watch.ElapsedMilliseconds>15000) throw new TimeoutException("Rank check timeout");
                var frame=new WT.DispatcherFrame(); WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background,new Action(() => frame.Continue=false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(2);
            } t.GetAwaiter().GetResult();
        }
        int cases=0; void Check(string name,bool passed,object details) {
            ++cases; journal.WriteLine(JsonSerializer.Serialize(new {name,passed,details,gameAccess=false,browserUsed=false}));
            if (!passed) throw new InvalidOperationException("Rank check failed: "+name);
        }
        var m=new PlainRuleFixture(); var o=m.Offices; var p=o.Policies; var t=p.Tactics; var f=t.Formations;
        var access=new RuleGameAccess(g => FormationMemory.Read(m,g),(_,_) => GamePace.Paused,(g,s,c) => FormationMemory.Apply(m,g,s,c),
            g => TacticMemory.Read(m,g),(g,s,c) => TacticMemory.Apply(m,g,s,c),g => PolicyMemory.Read(m,g),(g,s,c) => PolicyMemory.Apply(m,g,s,c),
            g => OfficeMemory.Read(m,g),(g,s,c) => OfficeMemory.Apply(m,g,s,c),g => PlainRuleMemory.Read(m,g,RankRules.Spec),(g,s,c) => PlainRuleMemory.Apply(m,g,s,c));
        string storage=Path.Combine(output,"storage-"+Guid.NewGuid().ToString("N"));
        var window=new EditorWindow(() => false,Path.Combine(storage,"seasons"),Path.Combine(storage,"ai"),storage,access); var map=Field<MapView>(window,"map");
        try {
            var file=Field<RuleProfileFile>(window,"ruleFile"); var original=o.Profile(); var kept=file.Save(original,"");
            Set(window,"game",m.Identity); Set(window,"snapshot",new TestGame().Build());
            Set(window,"layout",new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,f.State));
            Set(window,"ruleSnapshot",f.Snapshot()); Set(window,"tacticRuleSnapshot",t.Snapshot()); Set(window,"policyRuleSnapshot",p.Snapshot());
            Set(window,"officeRuleSnapshot",o.Snapshot()); Set(window,"ruleKept",kept);
            var history=Field<List<EditorUndo>>(window,"undoHistory"); history.Add(new("fixture-existing-undo",[],new(original,original,kept.Stamp,[])));
            Await((Task)Call(window,"PollRulesAsync")!); kept=file.Read();
            Check("capture-preserves-all-prior-baselines",m.Writes==0 && kept.Profile!.Defaults.Length==original.Defaults.Length+84
                && kept.Profile.Defaults.Where(d => d.Key.Table!="rank").SequenceEqual(original.Defaults),new {defaults=kept.Profile!.Defaults.Length});
            Check("new-rank-baseline-preserves-undo",history[0].Rule!.Before.Table("rank") is not null && history[0].Rule!.Stamp==kept.Stamp,new {history.Count});
            map.ShowList(ListKind.Ranks); Await((Task)Call(window,"OpenAsync",new PlainRuleTarget("rank",1),true)!);
            var card=map.RankEditor; var fields=Field<Dictionary<PlainRuleField,NumberField>>(card,"fields");
            Check("rank-seven-fields-and-data-section",fields.Count==7 && map.Section==PaneSection.Data,new {fields.Count});
            fields[RankRules.Spec.Field("capacity")].Set(65535); fields[RankRules.Spec.Field("charm")].Set(255); fields[RankRules.Spec.Field("grade")].Set(1);
            Await((Task)Call(window,"PollRulesAsync")!); Await((Task)Call(window,"StepAsync",-1)!);
            Check("rank-back-retains-list-and-draft",map.Lists.Shown && map.Lists.Tab==(int)ListKind.Ranks && card.Pending==3,new {card.Pending});
            map.ResumeEditor(); Check("rank-resume-retains-input",card.Shown && fields[RankRules.Spec.Field("charm")].Value==255,new {card.Pending});
            foreach (int width in new[] {500,600,850}) {
                Set(map,"paneWidth",(double)width); map.Measure(new W.Size(width+306,800)); map.Arrange(new W.Rect(0,0,width+306,800)); map.UpdateLayout();
                Check("rank-layout-"+width,fields.Values.All(n => Math.Abs(n.Box.ActualWidth-80)<.1),new {width});
            }
            foreach (var bad in new[] {("capacity","65536"),("charm","256"),("grade","0"),("grade","10")}) {
                var n=fields[RankRules.Spec.Field(bad.Item1)]; long before=n.Value; n.Box.Text=bad.Item2;
                Check("rank-invalid-"+bad.Item1+bad.Item2,card.Changes(out string error) is null && error.Length>0,new {error}); n.Set(before);
            }
            var apply=(Task<bool>)Call(window,"ApplyPlainRuleAsync",card)!; Await(apply);
            Check("rank-edit-saves-memory-and-profile",apply.Result && m.Snapshot().Values[new("rank",1,"charm")]==255 && card.Pending==0,new {m.Writes});
            var edit=history[^1].Rule!; var inverse=edit.Changes.Select(c => c with {Before=c.After,After=c.Before}).ToArray();
            var undo=(Task<bool>)Call(window,"WriteRuleAsync",inverse,edit.Before,"fixture-rank-undo",false)!; Await(undo);
            Check("rank-undo-restores-memory-and-profile",undo.Result && file.Read().Profile!.SameAs(edit.Before) && m.Snapshot().Values[new("rank",1,"charm")]==5,new {undo.Result});
            apply=(Task<bool>)Call(window,"WriteRuleAsync",edit.Changes,edit.After,"fixture-redo",false)!; Await(apply);
            var current=m.Snapshot().Record(1); var restored=RankRules.Spec.Fields.Select(x => new RuleChange(new("rank",1,x.Id),current.Value(x),edit.After.Default(new("rank",1,x.Id)))).ToArray();
            var restore=(Task<bool>)Call(window,"WriteRuleAsync",restored,edit.After.WithChanges(restored,true),"fixture-restore",true)!; Await(restore);
            Check("rank-restore-removes-overrides",restore.Result && file.Read().Profile!.Overrides.All(x => x.Key.Table!="rank"),new {restore.Result});
            var restoreUndo=history[^1].Rule!; undo=(Task<bool>)Call(window,"WriteRuleAsync",restoreUndo.Changes.Select(c => c with {Before=c.After,After=c.Before}).ToArray(),restoreUndo.Before,"fixture-restore-undo",false)!; Await(undo);
            Check("rank-restore-undo-recovers-overrides",undo.Result && file.Read().Profile!.Desired(new("rank",1,"charm"))==255,new {undo.Result});
            string stamp=file.Read().Stamp; m.Array+=0x100000; m.Load(); Await((Task)Call(window,"PollRulesAsync")!);
            Check("rank-new-address-reapplies-settings",m.Snapshot().Values[new("rank",1,"capacity")]==65535 && file.Read().Stamp==stamp,new {m.Writes});
            int transitions=0,writes=m.Writes; W.UIElement[] controls=[Field<PlateButton>(window,"connectButton"),Field<PlateButton>(card,"restore"),Field<WC.CheckBox>(card,"auto")];
            foreach (var c in controls) c.IsEnabledChanged+=(_,_) => ++transitions;
            for (int i=0;i<8;++i) Await((Task)Call(window,"PollRulesAsync")!);
            Check("rank-idle-poll-has-no-flicker",transitions==0 && writes==m.Writes,new {transitions,writes});
            fields[RankRules.Spec.Field("charm")].Set(100); m.Load(); Await((Task)Call(window,"PollRulesAsync")!);
            Check("rank-draft-defers-auto-write",writes==m.Writes && fields[RankRules.Spec.Field("charm")].Value==100,new {m.Writes});
            var state=new RuleReadState(f.Snapshot(),t.Snapshot(),p.Snapshot(),o.Snapshot(),m.Snapshot());
            RuleChange[] mixed=[new(new("formation",1,"mobility"),10,16),new(new("tactic",1,"amount1"),10,50),new(new("policyeffect",1,"level1"),2,500),new(new("office",1,"capacity"),18000,20000),new(new("rank",1,"capacity"),18000,20000)];
            m.FailWrite=m.Writes+9; bool rejected=false;
            try {Call(window,"ApplyRules",m.Identity,state,mixed);} catch (System.Reflection.TargetInvocationException ex) {rejected=ex.InnerException is IOException;}
            Check("five-group-failure-restores-all",rejected && new RuleReadState(f.Snapshot(),t.Snapshot(),p.Snapshot(),o.Snapshot(),m.Snapshot()).Values.All(x => !mixed.Any(c => c.Key==x.Key) || x.Value==mixed.Single(c => c.Key==x.Key).Before),new {rejected});
            Console.WriteLine($"Rank Native WPF checks: {cases} passed; output={output}");
        } finally {map.HideEditors(); window.Close(); SynchronizationContext.SetSynchronizationContext(previous);}
    }
}
