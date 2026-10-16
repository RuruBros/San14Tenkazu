#if LOCAL_DEVELOPER
using San14Tenkazu;
using San14Tenkazu.Tests;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using W=System.Windows;
using WT=System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckCompatibilityDiagnostic()
    {
        var identity=new GameIdentity(1,DateTime.UnixEpoch,0x140000000,GameBuildFacts.ExeSha256,true,TestGame.Classes);
        var layout=new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,0x1000);
        var engine=GameBuildProfiles.Current.SeasonTargets.Concat(GameBuildProfiles.Current.EnvironmentTargets).Distinct()
            .Select(t=>new CompatibilityEngineRead(t.Rva,t.Size,t.Hash,t.Hash,"fixture",0x1000,0x20,true,true)).ToArray();
        var sample=new CompatibilityRead(identity,layout,"snapshot","catalog","203/3/2",0,GamePace.Paused,true,engine,[],[]);
        var stable=CompatibilityDiagnostic.Read(()=>sample,()=>true,CancellationToken.None);
        Require(stable.Consistent&&stable.Capabilities.Single(c=>c.Feature==GameFeature.BasicRead).State==CapabilityState.Passed,"Stable base read must be recorded");
        Require(stable.Capabilities.Single(c=>c.Feature==GameFeature.Seasons).State==CapabilityState.Eligible,"Read-only capture cannot certify native execution");
        int rejected=0;
        foreach(var changed in new[]{sample with{Identity=identity with{ProcessId=2}},sample with{Layout=layout with{State=0x2000}},sample with{SnapshotSha256="changed"},
            sample with{CatalogSha256="changed"},sample with{Date="203/3/3"},sample with{Hour=2},sample with{Pace=GamePace.Running},sample with{GrowthKnown=false},
            sample with{Engine=engine.Select((e,i)=>i==0?e with{Protection=0x40}:e).ToArray()},sample with{Tables=[new(GameFeature.RuleTables,false,"moved")]},
            sample with{Checks=[new("fixture","changed",0,10,1)]}})
        {
            int reads=0;var report=CompatibilityDiagnostic.Read(()=>++reads==1?sample:changed,()=>true,CancellationToken.None);
            Require(!report.Consistent&&report.Capabilities.Length==0&&report.Second is not null,"Changing reads must not become confirmed capability evidence");++rejected;
        }
        Require(!CompatibilityDiagnostic.Read(()=>sample,()=>false,CancellationToken.None).Consistent,"Stale connection must stop");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();int attempts=0;
        Require(!CompatibilityDiagnostic.Read(()=>{++attempts;return sample;},()=>true,cancellation.Token).Consistent&&attempts==0,"Pre-cancel must read nothing");
        using var mid=new CancellationTokenSource();
        var cancelled=CompatibilityDiagnostic.Read(()=>{mid.Cancel();return sample;},()=>true,mid.Token);
        Require(!cancelled.Consistent&&cancelled.First is not null&&cancelled.Second is null,"Mid-read cancel retains only unconfirmed evidence");
        Require(!CompatibilityDiagnostic.Read(()=>throw new IOException("fixture read failure"),()=>true,CancellationToken.None).Consistent,"Read failure must remain inconclusive");
        string directory=Path.Combine(Path.GetTempPath(),"compatibility-close-"+Guid.NewGuid().ToString("N"));
        var editor=OfflineEditor(()=>false,directory,directory,directory);
        using var closing=new CancellationTokenSource();
        try
        {
            editor.GetType().GetField("compatibilityCancellation",Hidden)!.SetValue(editor,closing);
            var args=new System.ComponentModel.CancelEventArgs();
            editor.GetType().GetMethod("OnEditorClosing",Hidden)!.Invoke(editor,[editor,args]);
            Require(args.Cancel&&closing.IsCancellationRequested,"Owner close must cancel a read and wait for its evidence log without native cleanup");
        }
        finally{editor.GetType().GetField("compatibilityCancellation",Hidden)!.SetValue(editor,null);editor.Close();}
        Console.WriteLine($"PASS compatibility read-only fixture: {rejected} concurrent changes, stale connection, pre/mid cancellation, read failure; gameAccess=false");
    }

    // Explicit live command: exercises the actual approved menu event, never EditorWindow.ContentRendered.
    private static void ReadDeveloperCompatibilityFromMenu(string output)
    {
        string directory=Path.GetFullPath(output);if(Directory.Exists(directory))throw new IOException("A new evidence directory is required");Directory.CreateDirectory(directory);
        var old=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        _=W.Application.Current??new W.Application{ShutdownMode=W.ShutdownMode.OnExplicitShutdown};
        static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,Hidden)!.GetValue(o)!;
        static void Click(PlateButton b)=>((Action)typeof(PlateButton).GetField("Click",Hidden)!.GetValue(b)!).Invoke();
        static void Pump(){var frame=new WT.DispatcherFrame();WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background,new Action(()=>frame.Continue=false));WT.Dispatcher.PushFrame(frame);}
        var editor=OfflineEditor(()=>false,directory,directory,directory);new System.Windows.Interop.WindowInteropHelper(editor).EnsureHandle();
        var root=(W.FrameworkElement)editor.Content;editor.Content=null;
        var host=new W.Window{Content=root,Width=1100,Height=750,ShowActivated=false,ShowInTaskbar=false,Title="Approved read-only compatibility diagnostic"};
        try
        {
            host.Show();Pump();Click(Field<PlateButton>(editor,"developerButton"));Pump();
            var menu=Field<DisasterTestWindow>(editor,"developerMenu");menu.Tabs.SelectedIndex=2;
            Require(menu.ReadCompatibility.IsEnabled,"Approved compatibility action unavailable");Click(menu.ReadCompatibility);
            var task=Field<Task>(editor,"compatibilityTask");var watch=Stopwatch.StartNew();bool cancelled=false;
            while(!task.IsCompleted)
            {
                if(watch.Elapsed>TimeSpan.FromSeconds(90)&&!cancelled){Click(menu.CancelCompatibility);cancelled=true;}
                Pump();Thread.Sleep(5);
            }
            task.GetAwaiter().GetResult();
            var result=Field<CompatibilityReport>(editor,"compatibilityResult");string path=Field<string>(editor,"compatibilityLogPath");
            Require(File.Exists(path),"Live diagnostic JSON was not saved");
            Console.WriteLine(JsonSerializer.Serialize(new{gameAccess=true,readOnly=true,nativeExecutionTested=false,result.Consistent,result.Error,path}));
            Require(result.Consistent,"Live diagnostic is inconclusive; see raw JSON");
        }
        finally
        {
            Field<DisasterTestWindow?>(editor,"developerMenu")?.Close();Pump();host.Close();editor.Close();SynchronizationContext.SetSynchronizationContext(old);
        }
    }
}
#endif
