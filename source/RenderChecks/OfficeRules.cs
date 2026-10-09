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
    private static void CheckOfficeRules(string output)
    {
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "office-rules.jsonl")) { AutoFlush = true };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
        static void Set(object o, string name, object? value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
        static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
        static void Await(Task t)
        {
            var watch = Stopwatch.StartNew();
            while (!t.IsCompleted) {
                if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("Office operation did not complete");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background,new Action(() => frame.Continue=false));
                WT.Dispatcher.PushFrame(frame); Thread.Sleep(2);
            }
            t.GetAwaiter().GetResult();
        }
        int cases=0;
        void Check(string name,bool passed,object details) {
            ++cases; journal.WriteLine(JsonSerializer.Serialize(new {name,passed,details,gameAccess=false,browserUsed=false}));
            if (!passed) throw new InvalidOperationException("Office check failed: "+name);
        }
        var fixture=new OfficeFixture(); var policies=fixture.Policies; var tactics=policies.Tactics; var formations=tactics.Formations;
        var access=new RuleGameAccess(g => FormationMemory.Read(fixture,g),(_,_) => GamePace.Paused,(g,s,c) => FormationMemory.Apply(fixture,g,s,c),
            g => TacticMemory.Read(fixture,g),(g,s,c) => TacticMemory.Apply(fixture,g,s,c),g => PolicyMemory.Read(fixture,g),(g,s,c) => PolicyMemory.Apply(fixture,g,s,c),
            g => OfficeMemory.Read(fixture,g),(g,s,c) => OfficeMemory.Apply(fixture,g,s,c));
        string storage=Path.Combine(output,"storage-"+Guid.NewGuid().ToString("N"));
        var window=new EditorWindow(() => false,Path.Combine(storage,"seasons"),Path.Combine(storage,"ai"),storage,access);
        var map=Field<MapView>(window,"map");
        try {
            var file=Field<RuleProfileFile>(window,"ruleFile"); var original=policies.Profile(); var kept=file.Save(original,"");
            Set(window,"game",fixture.Identity); Set(window,"snapshot",new TestGame().Build());
            Set(window,"layout",new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,formations.State));
            Set(window,"ruleSnapshot",formations.Snapshot()); Set(window,"tacticRuleSnapshot",tactics.Snapshot()); Set(window,"policyRuleSnapshot",policies.Snapshot()); Set(window,"ruleKept",kept);
            var history=Field<List<EditorUndo>>(window,"undoHistory"); history.Add(new("fixture-existing-undo",[],new(original,original,kept.Stamp,[])));
            Await((Task)Call(window,"PollRulesAsync")!); kept=file.Read();
            Check("capture-preserves-prior-tables",fixture.Writes==0 && kept.Profile!.Defaults.Length==original.Defaults.Length+18
                && kept.Profile.Defaults.Where(d => d.Key.Table!="office").SequenceEqual(original.Defaults),new {defaults=kept.Profile!.Defaults.Length});
            Check("capture-preserves-undo-and-stamp",history[0].Rule!.Before.Table("office") is not null && history[0].Rule!.After.Table("office") is not null
                && history[0].Rule!.Stamp==kept.Stamp,new {count=history.Count});
            var officerCard=map.OfficerEditor; var officeChoice=Field<NameChoice>(officerCard,"office");
            Set(officerCard,"filling",true); officeChoice.Show(1); officeChoice.Pick(2); Set(officerCard,"filling",false);
            GameName[] updatedOffices=[new(1,"O1","1품 · 20000"),new(2,"O2","3품 · 18000"),new(73,"없음","9품 · 3000")];
            officerCard.FollowOffices(updatedOffices);
            Check("officer-office-label-refresh-preserves-draft",officeChoice.Current==1 && officeChoice.Picked==2
                && ((WC.ComboBoxItem)officeChoice.Box.SelectedItem).Content.ToString()!.Contains("3품"),new {officeChoice.Current,officeChoice.Picked});
            int selections=0; officeChoice.Box.SelectionChanged+=(_,_) => ++selections; officerCard.FollowOffices(updatedOffices);
            Check("unchanged-office-labels-do-not-reset-selection",selections==0,new {selections});
            map.ShowList(ListKind.Offices); Await((Task)Call(window,"OpenAsync",new OfficeTarget(1),true)!);
            var card=map.OfficeEditor; var fields=Field<Dictionary<OfficeField,NumberField>>(card,"fields");
            Check("office-six-fields-and-data-section",fields.Count==6 && map.Section==PaneSection.Data,new {count=fields.Count});
            fields[OfficeRules.Field("capacity")].Set(65535); fields[OfficeRules.Field("grade")].Set(1);
            Await((Task)Call(window,"PollRulesAsync")!); Await((Task)Call(window,"StepAsync",-1)!);
            Check("back-returns-office-list-with-draft",map.Lists.Shown && map.Lists.Tab==(int)ListKind.Offices && card.Pending==2,new {card.Pending});
            map.ResumeEditor(); Check("resume-preserves-office-draft",card.Shown && fields[OfficeRules.Field("capacity")].Value==65535,new {card.Pending});
            foreach (int width in new[] {500,600,850}) {
                Set(map,"paneWidth",(double)width); map.Measure(new W.Size(width+306,800)); map.Arrange(new W.Rect(0,0,width+306,800)); map.UpdateLayout();
                Check("office-layout-"+width,fields.Values.All(f => Math.Abs(f.Box.ActualWidth-80)<0.1),new {width});
            }
            foreach (var bad in new[] {("capacity","65536"),("leadership","256"),("grade","0"),("grade","10")}) {
                var number=fields[OfficeRules.Field(bad.Item1)]; long before=number.Value; number.Box.Text=bad.Item2;
                Check("invalid-input-"+bad.Item1+"-"+bad.Item2,card.Changes(out string error) is null && error.Length>0,new {error}); number.Set(before);
            }
            var apply=(Task<bool>)Call(window,"ApplyOfficeAsync")!; Await(apply); kept=file.Read();
            Check("edit-memory-and-file",apply.Result && fixture.Snapshot().Values[new("office",1,"capacity")]==65535
                && kept.Profile!.Desired(new("office",1,"grade"))==1 && card.Pending==0,new {apply.Result,fixture.Writes});
            var edit=history[^1].Rule!;
            var undo=(Task<bool>)Call(window,"WriteRuleAsync",edit.Changes.Select(c => c with {Before=c.After,After=c.Before}).ToArray(),edit.Before,"fixture-undo",false)!; Await(undo);
            Check("undo-write-restores-memory-and-file",undo.Result && file.Read().Profile!.SameAs(edit.Before) && fixture.Snapshot().Values[new("office",1,"capacity")]==18000,new {undo.Result});
            apply=(Task<bool>)Call(window,"WriteRuleAsync",edit.Changes,edit.After,"fixture-redo",false)!; Await(apply);
            var current=fixture.Snapshot().Record(1); var restored=OfficeRules.Fields.Select(f => new RuleChange(OfficeRules.Key(1,f),current.Value(f),edit.After.Default(OfficeRules.Key(1,f)))).ToArray();
            var restore=(Task<bool>)Call(window,"WriteRuleAsync",restored,edit.After.WithChanges(restored,true),"fixture-restore",true)!; Await(restore);
            Check("restore-removes-office-overrides",restore.Result && file.Read().Profile!.Overrides.All(o => o.Key.Table!="office") && history[^1].Rule!.Before.Desired(new("office",1,"capacity"))==65535,new {restore.Result});
            var restoreUndo=history[^1].Rule!;
            undo=(Task<bool>)Call(window,"WriteRuleAsync",restoreUndo.Changes.Select(c => c with {Before=c.After,After=c.Before}).ToArray(),restoreUndo.Before,"fixture-restore-undo",false)!; Await(undo);
            Check("restore-undo-recovers-settings",undo.Result && fixture.Snapshot().Values[new("office",1,"grade")]==1 && file.Read().Profile!.Desired(new("office",1,"capacity"))==65535,new {undo.Result});
            string stamp=file.Read().Stamp; fixture.Array+=0x100000; fixture.Vectors+=0x100000; fixture.Load(); Await((Task)Call(window,"PollRulesAsync")!);
            Check("reload-new-address-reapplies-settings",fixture.Snapshot().Values[new("office",1,"capacity")]==65535 && file.Read().Stamp==stamp,new {fixture.Writes});
            int transitions=0,writes=fixture.Writes;
            W.UIElement[] controls=[Field<PlateButton>(window,"connectButton"),Field<PlateButton>(card,"restore"),Field<WC.CheckBox>(card,"auto")];
            foreach (var c in controls) c.IsEnabledChanged+=(_,_) => ++transitions;
            for (int i=0;i<8;++i) Await((Task)Call(window,"PollRulesAsync")!);
            Check("idle-poll-no-flicker-or-write",transitions==0 && fixture.Writes==writes,new {transitions,writes});
            fields[OfficeRules.Field("leadership")].Set(100); fixture.Load(); Await((Task)Call(window,"PollRulesAsync")!);
            Check("office-draft-defers-auto-write",fixture.Writes==writes && fields[OfficeRules.Field("leadership")].Value==100,new {fixture.Writes});
            var state=new RuleReadState(formations.Snapshot(),tactics.Snapshot(),policies.Snapshot(),fixture.Snapshot());
            RuleChange[] mixed=[new(new("formation",1,"mobility"),10,16),new(new("tactic",1,"amount1"),10,50),new(new("policyeffect",1,"level1"),2,500),new(new("office",1,"capacity"),18000,20000)];
            // OfficeMemory recovers its own failed first write; ApplyRules must recover the three prior groups.
            fixture.FailWrite=fixture.Writes+7; bool rejected=false;
            try {Call(window,"ApplyRules",fixture.Identity,state,mixed);} catch (System.Reflection.TargetInvocationException ex) {rejected=ex.InnerException is IOException;}
            Check("four-group-failure-restores-all",rejected && formations.Snapshot().Values[mixed[0].Key]==10 && tactics.Snapshot().Values[mixed[1].Key]==10
                && policies.Snapshot().Values[mixed[2].Key]==2 && fixture.Snapshot().Values[mixed[3].Key]==18000,new {rejected});
            Console.WriteLine($"Office Native WPF checks: {cases} passed; output={output}");
        } finally {map.HideEditors(); window.Close(); SynchronizationContext.SetSynchronizationContext(previous);}
    }
}
