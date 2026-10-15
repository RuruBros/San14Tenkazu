using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Seasons;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private sealed class EditorSeasonSource(SeasonSnapshot snapshot):ISeasonDateSource
    {internal SeasonSnapshot Current=snapshot;public ValueTask<SeasonSnapshot> ReadAsync(CancellationToken token)=>ValueTask.FromResult(Current);}
    private sealed class EditorSeasonBackend:ISeasonNativeBackend
    {
        internal readonly List<EditorSeasonSession> Sessions=[];
        internal TaskCompletionSource? ContinueNext;
        public ValueTask<ISeasonNativeSession> StartAsync(SeasonSnapshot snapshot,ulong generation,CancellationToken token)
        {var session=new EditorSeasonSession(snapshot.Identity,generation){ContinueApply=ContinueNext};ContinueNext=null;Sessions.Add(session);return ValueTask.FromResult<ISeasonNativeSession>(session);}
    }
    private sealed class EditorSeasonSession(SeasonRenderIdentity identity,ulong generation):ISeasonNativeSession
    {
        public SeasonRenderIdentity Identity=>identity;
        public ulong Generation=>generation;
        internal bool Restored=true,Disposed;
        internal TaskCompletionSource? ContinueApply;
        internal TaskCompletionSource? ContinueStop;
        internal bool StopEntered;
        internal readonly List<SeasonParameters> Applied=[];
        private ulong applies;
        public async ValueTask<SeasonApplyReceipt> ApplyAsync(SeasonParameters parameters,CancellationToken token)
        {if(ContinueApply is {} pending)await pending.Task.WaitAsync(token);Applied.Add(parameters);return new(generation,parameters.Date,++applies,true);}
        public async ValueTask<SeasonTreeColorReceipt> ApplyTreeColorAsync(SeasonParameters parameters,CancellationToken token)
        {if(ContinueApply is {} pending)await pending.Task.WaitAsync(token);ulong n=++applies;return new(generation,parameters.Date,n,n,n*3,n*3,n*3,1,174752,1048512,true,true,false);}
        public ValueTask HeartbeatAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public async ValueTask<SeasonRestoreReceipt> StopAsync()
        {StopEntered=true;if(ContinueStop is {} pending)await pending.Task;return new(generation,Restored,Restored,Restored,Restored,Restored);}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }

    private static void CheckSeasonEditor(string output,bool live,int calendarSeconds=0)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("Season editor output already exists");
        Directory.CreateDirectory(output);using var journal=new StreamWriter(Path.Combine(output,"checks.jsonl")){AutoFlush=true};
        var previousContext=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        void Record(string scenario,bool passed,object? details=null)
        {
            string row=JsonSerializer.Serialize(new{utc=DateTime.UtcNow,scenario,passed,details});journal.WriteLine(row);Console.WriteLine(row);
            Require(passed,"Season editor check failed: "+scenario);
        }
        static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Hidden)!.GetValue(owner)!;
        static void Set(object owner,string name,object? value)=>owner.GetType().GetField(name,Hidden)!.SetValue(owner,value);
        static object? Call(object owner,string name,params object?[] args)=>owner.GetType().GetMethod(name,Hidden)!.Invoke(owner,args);
        static void PumpUntil(Func<bool> done,int timeoutMs=12000)
        {
            var watch=Stopwatch.StartNew();
            while(!done())
            {
                if(watch.ElapsedMilliseconds>timeoutMs)throw new TimeoutException("Season WPF operation did not complete");
                var frame=new WT.DispatcherFrame();WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background,new Action(()=>frame.Continue=false));
                WT.Dispatcher.PushFrame(frame);Thread.Sleep(5);
            }
            var drain=new WT.DispatcherFrame();WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background,new Action(()=>drain.Continue=false));WT.Dispatcher.PushFrame(drain);
        }
        static void Await(Task task){PumpUntil(()=>task.IsCompleted,35000);task.GetAwaiter().GetResult();}
        var window=new EditorWindow(()=>false,Path.Combine(output,"product-storage"));
        var root=(W.FrameworkElement)window.Content;window.Content=null;
        var host=new W.Window{Content=root,Width=1100,Height=760,ShowActivated=false,ShowInTaskbar=false,
            FontFamily=window.FontFamily,FontSize=window.FontSize,UseLayoutRounding=true,Title="Season native WPF check"};
        var on=Field<WC.RadioButton>(window,"seasonOn");var off=Field<WC.RadioButton>(window,"seasonOff");
        var text=Field<WC.TextBlock>(window,"seasonStatus");
        var map=Field<MapView>(window,"map");var surface=Field<MapSurface>(map,"surface");
        void Request(WC.RadioButton radio)
        {
            // Exercise controller lifecycle requests directly. The environment menu check covers
            // actual draft radio clicks and the shared Save and Apply button.
            _=(Task)Call(window,"SetSeasonsAsync",ReferenceEquals(radio,on))!;
        }
        void Capture(string name,int width=1100,int dpi=96)
        {
            host.Width=width;host.UpdateLayout();root.Measure(new W.Size(width,700));root.Arrange(new W.Rect(0,0,width,700));root.UpdateLayout();
            double height=map.TranslatePoint(new W.Point(),root).Y;
            var bitmap=new WMI.RenderTargetBitmap((int)Math.Ceiling(width*dpi/96.0),(int)Math.Ceiling(height*dpi/96.0),dpi,dpi,WM.PixelFormats.Pbgra32);bitmap.Render(root);
            var encoder=new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
            W.FrameworkElement[] actions=[Field<PlateButton>(window,"playButton"),
                Field<PlateButton>(window,"undoButton"),Field<PlateButton>(window,"panelButton"),
                Field<PlateButton>(window,"settingsButton"),
                Field<WC.Border>(window,"registeredSeparator"),Field<PlateButton>(window,"registeredButton")];
            var bounds=actions.Select(a=>a.TransformToAncestor(root).TransformBounds(new W.Rect(0,0,a.ActualWidth,a.ActualHeight))).ToArray();
            bool fit=actions.Zip(bounds).All(p=>p.First.ActualWidth+1>=p.First.DesiredSize.Width-p.First.Margin.Left-p.First.Margin.Right
                &&p.Second.Left>=0&&p.Second.Right<=root.ActualWidth+1&&p.Second.Bottom<=height);
            bool ordered=bounds.Zip(bounds.Skip(1)).All(p=>p.First.Right<=p.Second.Left);
            var receipt=text.TransformToAncestor(root).TransformBounds(new W.Rect(0,0,text.ActualWidth,text.ActualHeight));
            var connection=Field<PlateButton>(window,"calendarButton");
            var connectionBounds=connection.TransformToAncestor(root).TransformBounds(new W.Rect(0,0,connection.ActualWidth,connection.ActualHeight));
            Record("toolbar-layout-"+name,fit&&ordered&&text.ActualWidth>=100&&receipt.Left>=connectionBounds.Right&&receipt.Right<=bounds[0].Left
                &&receipt.Top<bounds.Max(b=>b.Bottom)&&receipt.Bottom>bounds.Min(b=>b.Top)
                &&on.IsChecked!=off.IsChecked&&on.GroupName==off.GroupName,
                new{width,dpi,status=text.Text,statusWidth=text.ActualWidth,actions=bounds.Select(b=>new{b.Left,b.Right,b.Top,b.Bottom}),on.IsChecked,offChecked=off.IsChecked});
        }
        SeasonController? controller=null;EditorSeasonBackend? fake=null;
        EditorSeasonSource? fakeSource=null;
        var calendarDates=new List<SeasonDate>();
        try
        {
            Record("wpf-dispatcher-context",SynchronizationContext.Current is WT.DispatcherSynchronizationContext,new{previous=previousContext?.GetType().FullName});
            host.Show();PumpUntil(()=>host.IsLoaded);Call(window,"UpdateControls");
            Record("initial-off",!on.IsEnabled&&!off.IsEnabled&&on.IsChecked==false&&off.IsChecked==true);
            Capture("disconnected-96");
            GameIdentity identity;
            GameSnapshot data;
            if(live)
            {
                identity=GameMemory.FindGame();var baseline=GameMemory.ReadSeasonSnapshot(identity);
                Record("live-read-only-baseline",identity.Verified,new{identity.ProcessId,date=new{baseline.Date.Year,baseline.Date.Month,baseline.Date.Day},baseline.Identity});
                var layout=GameMemory.Resolve(identity,Console.WriteLine,CancellationToken.None,[]);data=GameMemory.ReadSnapshot(identity,layout);
                Set(window,"layout",layout);Set(window,"game",identity);Set(window,"snapshot",data);map.SetSnapshot(data);Call(window,"AttachSeasons",identity);
            }
            else
            {
                SeasonDate.TryCreate(195,5,1,out var date);var render=new SeasonRenderIdentity(42,new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc),1,2,3,4,5,6,7,8);
                identity=new(42,render.ProcessStartUtc,1,"fixture",true,null!);data=new TestGame().Build();fake=new();
                fakeSource=new(new(render,date));controller=new(fakeSource,fake,scope:SeasonScope.Full);
                Set(window,"game",identity);Set(window,"snapshot",data);Set(window,"seasonController",controller);
                var changed=(Action<SeasonStatus>)typeof(EditorWindow).GetMethod("OnSeasonStatusChanged",Hidden)!.CreateDelegate(typeof(Action<SeasonStatus>),window);
                controller.StatusChanged+=changed;
            }
            controller=Field<SeasonController>(window,"seasonController");Call(window,"UpdateControls");
            Record("connected-still-off",on.IsEnabled&&off.IsEnabled&&on.IsChecked==false&&off.IsChecked==true&&controller.Status.Phase==SeasonPhase.Off);
            Record("separate-on-off-tooltips",on.ToolTip as string==L.Seasons.OnTip&&off.ToolTip as string==L.Seasons.OffTip);
            if(!live)
            {
                string detail=new string('E',180);Set(window,"seasonUiError",detail);Call(window,"UpdateSeasonControls");
                Record("ui-error-details-kept-in-status-tooltip",text.ToolTip is string tip&&tip.Contains(detail,StringComparison.Ordinal));
                Set(window,"seasonUiError",null);Call(window,"UpdateSeasonControls");
            }
            on.IsChecked=true;Call(window,"UpdateControls");
            Record("programmatic-radio-update-does-not-start-native",controller.Status.Phase==SeasonPhase.Off&&on.IsChecked==false&&off.IsChecked==true
                &&(fake is null||fake.Sessions.Count==0));
            Request(off);Record("selected-off-is-idempotent",controller.Status.Phase==SeasonPhase.Off&&controller.Status.OriginalRestored);
            Record("no-native-cache-before-on",!Directory.Exists(Path.Combine(output,"product-storage","native")));Capture("connected-off-144",1000,144);
            Request(on);PumpUntil(()=>!Field<bool>(window,"seasonStarting"));
            Record("controller-request-scene-on",controller.Status.Phase==SeasonPhase.On&&on.IsChecked==true&&off.IsChecked==false,
                new{status=text.Text,controller.Status,treeDate=new{controller.Status.Date?.Year,controller.Status.Date?.Month,controller.Status.Date?.Day}});
            ulong generation=controller.Status.Generation;int sessions=fake?.Sessions.Count??0;
            Request(on);Record("selected-on-is-idempotent",controller.Status.Phase==SeasonPhase.On&&controller.Status.Requested
                &&controller.Status.Generation==generation&&(fake?.Sessions.Count??0)==sessions);
            Capture("tree-applied-96");
            Capture("tree-applied-wide-96",1500);
            if(calendarSeconds>0)
            {
                var watch=Stopwatch.StartNew();string? previous=null;
                File.WriteAllText(Path.Combine(output,"ready.txt"),"Native WPF toggle is ON; user-controlled date progress only.");
                Console.WriteLine("SEASON_CALENDAR_READY: user-controlled date progress; stop.request ends the check");
                PumpUntil(()=>
                {
                    var value=controller.Status;
                    if(value.Phase==SeasonPhase.Faulted)throw new IOException("Calendar update failed: "+value.Error);
                    Require(value.Requested&&on.IsChecked==true&&off.IsChecked==false,"Calendar update changed the ON request/radios");
                    string key=$"{value.Phase}/{value.Date?.Year}/{value.Date?.Month}/{value.Date?.Day}";
                    if(key!=previous)
                    {
                        previous=key;Record("live-calendar-toggle-on",true,new{phase=value.Phase.ToString(),value.Requested,
                            date=new{value.Date?.Year,value.Date?.Month,value.Date?.Day},onChecked=on.IsChecked,offChecked=off.IsChecked,
                            nativePassReceipt=value.Phase==SeasonPhase.On,screenVerified=false,value.Error});
                        if(value.Phase==SeasonPhase.On&&value.Date is {} date&&!calendarDates.Contains(date))calendarDates.Add(date);
                    }
                    return watch.Elapsed.TotalSeconds>=calendarSeconds||File.Exists(Path.Combine(output,"stop.request"));
                },calendarSeconds*1000+5000);
                bool monthBoundary=calendarDates.Zip(calendarDates.Skip(1)).Any(p=>p.First.Month!=p.Second.Month||p.First.Year!=p.Second.Year);
                Record("live-calendar-actual-progress",calendarDates.Count>1&&monthBoundary,new{dates=calendarDates.Select(d=>new{d.Year,d.Month,d.Day})});
                Capture("calendar-final-96");
            }
            if(fake is not null)
            {
                string confirmedText=text.Text;
                var shortSession=fake.Sessions[^1];shortSession.ContinueApply=new(TaskCreationOptions.RunContinuationsAsynchronously);
                SeasonDate.TryCreate(195,5,2,out var nextDay);fakeSource!.Current=fakeSource.Current with{Date=nextDay};
                Task brief=controller.UpdateDate(fakeSource.Current);PumpUntil(()=>controller.Status.Phase==SeasonPhase.Applying);
                Record("brief-date-update-keeps-last-confirmed-dated-label",text.Text==confirmedText&&controller.Status.TreeColor is null&&
                    text.ToolTip.ToString()!.StartsWith((string)Call(window,"SeasonStatusText",controller.Status)!,StringComparison.Ordinal),new{status=text.Text,actual=controller.Status});
                shortSession.ContinueApply.SetResult();Await(brief);shortSession.ContinueApply=null;
                Record("brief-date-update-completion-shows-new-date",controller.Status.Date==nextDay&&controller.Status.Phase==SeasonPhase.On&&text.Text!=confirmedText);
                var session=fake.Sessions[^1];session.ContinueApply=new(TaskCreationOptions.RunContinuationsAsynchronously);
                SeasonDate.TryCreate(195,6,1,out var summer);fakeSource!.Current=fakeSource.Current with{Date=summer};
                Task update=controller.UpdateDate(fakeSource.Current);PumpUntil(()=>controller.Status.Phase==SeasonPhase.Applying);
                Record("date-update-keeps-radio-on-without-completion",on.IsChecked==true&&off.IsChecked==false&&
                    controller.Status.Requested&&controller.Status.TreeColor is null,new{status=text.Text,controller.Status});
                var pendingWatch=Stopwatch.StartNew();PumpUntil(()=>text.Text==L.Seasons.Applying(L.Seasons.Dated(L.Seasons.Summer,195,6,1)));
                Record("long-date-update-displays-real-pending-state",pendingWatch.ElapsedMilliseconds>=150&&controller.Status.TreeColor is null,
                    new{status=text.Text,elapsedMs=pendingWatch.ElapsedMilliseconds});
                Capture("date-update-pending-96");session.ContinueApply.SetResult();Await(update);session.ContinueApply=null;
                Record("date-update-publishes-only-actual-receipt",on.IsChecked==true&&off.IsChecked==false&&controller.Status.Phase==SeasonPhase.On&&controller.Status.Date==summer);
            }
            Call(window,"SetBusy",true,false);Record("controller-stays-active-during-editor-busy",controller.Status is {Phase:SeasonPhase.On,Requested:true});
            using(var scanning=new CancellationTokenSource())
            {
                Set(window,"scanCancellation",scanning);Call(window,"SetBusy",true,true);
                Capture("scanning-minimum-96",1000);
                Set(window,"scanCancellation",null);Call(window,"SetBusy",true,false);
            }
            if(live)
            {
                var hold=Stopwatch.StartNew();PumpUntil(()=>hold.ElapsedMilliseconds>=2600);
                Record("independent-date-watch-during-busy",controller.Status.Phase==SeasonPhase.On);
            }
            Request(off);PumpUntil(()=>!Field<bool>(window,"seasonStopping"));
            Record("controller-request-off-after-recovery",controller.Status.Phase==SeasonPhase.Off&&controller.Status.OriginalRestored&&on.IsChecked==false&&off.IsChecked==true);
            Call(window,"SetBusy",false,false);Capture("restored-144",1000,144);
            Request(on);PumpUntil(()=>!Field<bool>(window,"seasonStarting"));Record("on-after-off",controller.Status.Phase==SeasonPhase.On);
            if(fake is not null)
            {
                fake.Sessions[^1].Restored=false;Request(off);PumpUntil(()=>!Field<bool>(window,"seasonStopping"));
                Record("failed-recovery-keeps-controller",ReferenceEquals(controller,Field<SeasonController>(window,"seasonController"))&&
                    !controller.Status.OriginalRestored&&!fake.Sessions[^1].Disposed
                    &&off.IsChecked==true&&off.ToolTip.ToString()!.StartsWith(L.Seasons.RecoverButton,StringComparison.Ordinal));
                Capture("recovery-unconfirmed-96");
                Task<bool> disconnect=(Task<bool>)Call(window,"DisconnectAsync",new object?[]{null})!;Await(disconnect);
                Record("disconnect-blocked-keeps-game-and-controller",!disconnect.Result&&Field<GameIdentity?>(window,"game")==identity&&!fake.Sessions[^1].Disposed);
                var cancel=new System.ComponentModel.CancelEventArgs();Call(window,"OnEditorClosing",window,cancel);
                PumpUntil(()=>!Field<bool>(window,"seasonClosing"));
                Record("close-blocked-keeps-controller",cancel.Cancel&&!fake.Sessions[^1].Disposed&&Field<SeasonController>(window,"seasonController")==controller);
                fake.Sessions[^1].Restored=true;Request(off);PumpUntil(()=>!Field<bool>(window,"seasonStopping"));
                Record("recovery-retry-off",controller.Status.Phase==SeasonPhase.Off&&fake.Sessions[^1].Disposed);
                fake.ContinueNext=new(TaskCreationOptions.RunContinuationsAsynchronously);Request(on);PumpUntil(()=>controller.Status.Phase==SeasonPhase.Applying);
                Record("controller-awaits-draw-before-stop-request",controller.Status is {Phase:SeasonPhase.Applying,Requested:true,TreeColor:null});
                Request(off);PumpUntil(()=>!Field<bool>(window,"seasonStopping")&&!Field<bool>(window,"seasonStarting"));
                Record("pending-draw-cancelled-and-restored",controller.Status.Phase==SeasonPhase.Off&&controller.Status.OriginalRestored&&fake.Sessions[^1].Disposed);
                Request(on);PumpUntil(()=>!Field<bool>(window,"seasonStarting"));
                var closingSession=fake.Sessions[^1];closingSession.ContinueStop=new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancel=new System.ComponentModel.CancelEventArgs();Call(window,"OnEditorClosing",window,cancel);PumpUntil(()=>closingSession.StopEntered);
                Call(window,"SetBusy",false,false);
                Record("close-waits-for-native-recovery",cancel.Cancel&&Field<bool>(window,"seasonClosing")&&Field<bool>(window,"busy")&&
                    !closingSession.Disposed&&ReferenceEquals(controller,Field<SeasonController>(window,"seasonController")));
                closingSession.ContinueStop.SetResult();PumpUntil(()=>!Field<bool>(window,"seasonClosing"));
                Record("close-releases-only-after-recovery",closingSession.Disposed&&Field<SeasonController?>(window,"seasonController") is null&&Field<bool>(window,"seasonCloseApproved"));
            }
            Task<bool> disconnected=(Task<bool>)Call(window,"DisconnectAsync",new object?[]{null})!;Await(disconnected);
            Record("disconnect-restored-and-released",disconnected.Result&&Field<SeasonController?>(window,"seasonController") is null&&Field<GameIdentity?>(window,"game") is null);
            if(live)
            {
                Set(window,"game",identity);Set(window,"snapshot",data);Call(window,"AttachSeasons",identity);controller=Field<SeasonController>(window,"seasonController");
                Record("reconnected-default-off",controller.Status.Phase==SeasonPhase.Off&&on.IsChecked==false&&off.IsChecked==true);
                Request(on);PumpUntil(()=>!Field<bool>(window,"seasonStarting"));Record("reconnected-actual-scene-passes",controller.Status.Phase==SeasonPhase.On,new{controller.Status});
                disconnected=(Task<bool>)Call(window,"DisconnectAsync",new object?[]{null})!;Await(disconnected);Record("reconnected-final-restoration",disconnected.Result);
                string[] lines=Directory.GetFiles(Path.Combine(output,"product-storage","logs"),"*.log").SelectMany(File.ReadAllLines).ToArray();
                Record("recorded-independent-date-reads",lines.Count(l=>l.Contains("Date read:"))>=5,new{dateReads=lines.Count(l=>l.Contains("Date read:"))});
            }
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{nativeWpf=true,actualGame=live,actualScenePasses=live,
                calendarDates=calendarDates.Select(d=>new{d.Year,d.Month,d.Day}),
                screenVerified=false,browser=false,gameplayInput=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());throw;}
        finally
        {
            if(fake is not null)foreach(var session in fake.Sessions)session.Restored=true;
            if(controller is not null){Await(controller.StopAsync());Record("final-native-recovery",controller.Status.OriginalRestored);}
            Await((Task<bool>)Call(window,"StopSeasonsAsync",true)!);
            Call(surface,"StopLoop");Field<GpuMap?>(surface,"gpu")?.Dispose();host.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
}
