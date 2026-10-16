using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;
using San14Tenkazu.Tests;
using W=System.Windows;
using WC=System.Windows.Controls;
using WM=System.Windows.Media;
using WMI=System.Windows.Media.Imaging;
using WT=System.Windows.Threading;

internal static partial class RenderChecks
{
    private sealed class MenuEnvironmentSession:IEnvironmentSession
    {
        internal bool FailApply;
        internal int Applies;
        internal EnvironmentProtocol.Reply Receipt=new(){Magic=EnvironmentProtocol.Magic,Version=EnvironmentProtocol.Version,Size=58552,Generation=5,
            Date=192|(4u<<16)|(21u<<24),Weather=new byte[46],Events=new EnvironmentProtocol.Event[1024],RecentDates=new uint[230]};
        public ValueTask<EnvironmentProtocol.Reply> ApplyAsync(EnvironmentSettings value,CancellationToken token)
        {Applies++;if(FailApply)throw new IOException("fixture environment apply failure");Receipt.Flags=(uint)value.Effects;Receipt.Values=value.Values;Receipt.Sequence++;Receipt.Applied=Receipt.Sequence;return ValueTask.FromResult(Receipt);}
        public ValueTask<EnvironmentProtocol.Reply> QueryAsync(CancellationToken token)=>ValueTask.FromResult(Receipt);
        public ValueTask StopAsync()=>ValueTask.CompletedTask;
    }
    private sealed class MenuEnvironmentBackend(MenuEnvironmentSession value):IEnvironmentBackend
    {public ValueTask<IEnvironmentSession> StartAsync(CancellationToken token)=>ValueTask.FromResult<IEnvironmentSession>(value);}
    private static void CheckEnvironmentMenu(string output)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("New evidence directory required");Directory.CreateDirectory(output);
        using var log=new StreamWriter(Path.Combine(output,"checks.jsonl")){AutoFlush=true};
        void Check(string name,bool passed){log.WriteLine(JsonSerializer.Serialize(new{name,passed}));Require(passed,name);}
        static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Hidden)!.GetValue(owner)!;
        static void Set(object owner,string name,object? value)=>owner.GetType().GetField(name,Hidden)!.SetValue(owner,value);
        static object? Call(object owner,string name,params object?[] values)=>owner.GetType().GetMethod(name,Hidden)!.Invoke(owner,values);
        static void Pump(Func<bool>? done=null)
        {
            var watch=Stopwatch.StartNew();do
            {
                var frame=new WT.DispatcherFrame();WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background,new Action(()=>frame.Continue=false));WT.Dispatcher.PushFrame(frame);
                if(watch.ElapsedMilliseconds>15000)throw new TimeoutException("Environment menu did not complete");if(done is not null)Thread.Sleep(5);
            }while(done is not null&&!done());
        }
        static void Click(PlateButton button){Require(button.IsEnabled,"Button disabled");((Action)typeof(PlateButton).GetField("Click",Hidden)!.GetValue(button)!).Invoke();}
        static bool HasCombo(W.DependencyObject node){if(node is WC.ComboBox)return true;foreach(object child in W.LogicalTreeHelper.GetChildren(node))if(child is W.DependencyObject value&&HasCombo(value))return true;return false;}
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new WT.DispatcherSynchronizationContext(WT.Dispatcher.CurrentDispatcher));
        var editor=OfflineEditor(()=>false,Path.Combine(output,"storage"));
        Set(editor,"environmentReadPace",new Func<GameIdentity,GameLayout,GamePace>((_,_)=>GamePace.Paused));new System.Windows.Interop.WindowInteropHelper(editor).EnsureHandle();
        var root=(W.FrameworkElement)editor.Content;editor.Content=null;var host=new W.Window{Content=root,Width=1100,Height=750,ShowActivated=false,ShowInTaskbar=false};
        EnvironmentSettingsWindow? menu=null;var session=new MenuEnvironmentSession();var controller=new EnvironmentController(new MenuEnvironmentBackend(session),_=>{});
        try
        {
            host.Show();Pump();var on=Field<WC.RadioButton>(editor,"seasonOn");var off=Field<WC.RadioButton>(editor,"seasonOff");
            Check("toolbar-has-menu-without-radio",!IsDescendant(root,on)&&!IsDescendant(root,off));
            Click(Field<PlateButton>(editor,"environmentButton"));Pump();menu=Field<EnvironmentSettingsWindow>(editor,"environmentMenu");
            Check("season-choices-moved-inside-menu",IsDescendant(menu,on)&&IsDescendant(menu,off)&&menu.Choices.Count==10);
            Check("direct-numeric-fields-without-presets",menu.Numbers.Count==23&&!HasCombo(menu));
            Check("shared-apply-outside-three-tabs-with-collapsed-history",menu.Tabs.Items.Count==3&&!IsDescendant(menu.Tabs,menu.Apply)&&!menu.RecentHistory.IsExpanded&&!IsDescendant(menu.Tabs,menu.RecentHistory));
            for(uint flags=0;flags<1024;flags++)
            {
                foreach(var pair in menu.Choices)pair.Value.IsChecked=(flags&(uint)pair.Key)!=0;
                Check("combination-"+flags,(uint)menu.Selection(EnvironmentSettings.New()).Effects==flags);
            }
            menu.ClearSelection();menu.Choices[EnvironmentEffects.RainMovement].IsChecked=true;menu.Choices[EnvironmentEffects.Flood].IsChecked=true;
            menu.Numbers[nameof(EnvironmentValues.RainReduction)].Text="99.1";
            Check("invalid-numeric-input-blocks-apply-with-off-available",!menu.Apply.IsEnabled&&menu.DisableAll.IsEnabled);
            menu.Numbers[nameof(EnvironmentValues.RainReduction)].Text="17";menu.Numbers[nameof(EnvironmentValues.HeavyRainReduction)].Text="41";
            menu.Numbers[nameof(EnvironmentValues.FloodChance)].Text="1.7";menu.Numbers[nameof(EnvironmentValues.WildfireFoodPercent)].Text="29";
            menu.Numbers[nameof(EnvironmentValues.BlizzardChance)].Text="4.2";menu.Numbers[nameof(EnvironmentValues.BlizzardTroopsPercent)].Text="9";
            menu.Numbers[nameof(EnvironmentValues.HeatwaveMoraleLoss)].Text="7";menu.Numbers[nameof(EnvironmentValues.HeatwaveFoodPercent)].Text="11";
            Check("new-disasters-have-independent-numeric-input",menu.Selection(EnvironmentSettings.New()).Values is{BlizzardChance:42,BlizzardTroopsPercent:9,HeatwaveMoraleLoss:7,HeatwaveFoodPercent:11});
            Check("valid-numeric-input-enables-apply",menu.Apply.IsEnabled);Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            var saved=new EnvironmentSettingsFile(Path.Combine(output,"storage","environment","settings.json")).Read().Settings!;
            Check("disconnected-saves-without-starting-native",saved.Effects==(EnvironmentEffects.RainMovement|EnvironmentEffects.Flood)&&
                saved.Values.RainReduction==17&&saved.Values.HeavyRainReduction==41&&saved.Values.FloodChance==17&&saved.Values.WildfireFoodPercent==29&&Field<EnvironmentController?>(editor,"environmentController") is null);
            var savedJournal=Field<SeasonJournal>(editor,"environmentJournal");
            var releasedJournal=(Task<bool>)Call(editor,"StopEnvironmentAsync",true)!;Pump(()=>releasedJournal.IsCompleted);
            Check("disconnected-save-releases-timing-journal",releasedJournal.GetAwaiter().GetResult()&&Field<SeasonJournal?>(editor,"environmentJournal") is null);
            using(var exclusive=File.Open(savedJournal.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Check("disconnected-timing-journal-is-flushed-and-closed",exclusive.Length>0);
            menu.Close();Pump();Click(Field<PlateButton>(editor,"environmentButton"));Pump();menu=Field<EnvironmentSettingsWindow>(editor,"environmentMenu");
            Check("reopened-menu-retains-saved-combination",menu.Selection(saved)==saved);
            Set(editor,"game",new GameIdentity(1,DateTime.UnixEpoch,0,"fixture",true,TestGame.Classes));Set(editor,"snapshot",new TestGame().Build());
            Set(editor,"layout",new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,0));
            Set(editor,"environmentController",controller);Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Call(editor,"UpdateEnvironmentControls");Check("menu-reports-actual-applied-combination",controller.Status.Actual==saved.Effects&&menu.Status.Text.Contains("비 이동"));
            SeasonDate.TryCreate(192,4,21,out var date);
            var render=new SeasonRenderIdentity(1,DateTime.UnixEpoch,1,2,3,4,5,6,7,8);var seasonBackend=new EditorSeasonBackend();
            var seasons=new SeasonController(new EditorSeasonSource(new(render,date)),seasonBackend,scope:SeasonScope.Full);
            Set(editor,"seasonController",seasons);
            seasons.StatusChanged+=(Action<SeasonStatus>)typeof(EditorWindow).GetMethod("OnSeasonStatusChanged",Hidden)!.CreateDelegate(typeof(Action<SeasonStatus>),editor);
            menu.Choices[EnvironmentEffects.RainVisual].IsChecked=true;Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Check("rain-graphics-start-with-seasonal-off",seasons.Status is{Phase:SeasonPhase.On,Scope:SeasonScope.Weather,Requested:true}
                &&off.IsChecked==true&&seasonBackend.Sessions.Single().Applied[^1].Seasonal==false);
            typeof(WC.RadioButton).GetMethod("OnClick",Hidden)!.Invoke(on,null);Pump();
            Call(editor,"UpdateSeasonControls");
            Check("season-on-is-draft-until-shared-save",seasons.Status.Scope==SeasonScope.Weather&&menu.HasPendingChanges&&on.IsChecked==true);
            Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Check("seasonal-on-reuses-weather-session",seasons.Status.Scope==SeasonScope.Full&&on.IsChecked==true
                &&seasonBackend.Sessions.Count==1&&seasonBackend.Sessions[0].Applied[^1].Seasonal);
            typeof(WC.RadioButton).GetMethod("OnClick",Hidden)!.Invoke(off,null);Pump();
            Check("season-off-is-draft-until-shared-save",seasons.Status.Scope==SeasonScope.Full&&menu.HasPendingChanges);
            Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Check("seasonal-off-keeps-weather-graphics",seasons.Status is{Phase:SeasonPhase.On,Scope:SeasonScope.Weather,Requested:true}
                &&off.IsChecked==true&&controller.Status.Actual.HasFlag(EnvironmentEffects.RainVisual)&&!seasonBackend.Sessions[0].Applied[^1].Seasonal);
            menu.Choices[EnvironmentEffects.RainVisual].IsChecked=false;menu.Choices[EnvironmentEffects.SnowVisual].IsChecked=true;
            Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Check("rain-and-snow-graphics-select-independently",controller.Status.Actual.HasFlag(EnvironmentEffects.SnowVisual)
                &&!controller.Status.Actual.HasFlag(EnvironmentEffects.RainVisual)&&seasons.Status.Scope==SeasonScope.Weather);
            seasonBackend.Sessions[0].Restored=false;var failedStop=seasons.StopAsync();Pump(()=>failedStop.IsCompleted);failedStop.GetAwaiter().GetResult();
            Call(editor,"UpdateSeasonControls");Check("weather-recovery-error-keeps-shared-stop-available",seasons.Status is{Phase:SeasonPhase.Faulted,OriginalRestored:false}&&menu.DisableAll.IsEnabled);
            seasonBackend.Sessions[0].Restored=true;Click(menu.DisableAll);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Check("off-retries-failed-weather-recovery",seasons.Status is{Phase:SeasonPhase.Off,OriginalRestored:true}&&seasonBackend.Sessions[0].Disposed);
            menu.Choices[EnvironmentEffects.SnowVisual].IsChecked=true;Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Check("graphics-restart-after-confirmed-recovery",seasons.Status is{Phase:SeasonPhase.On,Scope:SeasonScope.Weather}&&seasonBackend.Sessions.Count==2);
            for(int tab=0;tab<menu.Tabs.Items.Count;tab++)foreach(int dpi in new[]{96,144})
            {
                menu.Tabs.SelectedIndex=tab;menu.Width=dpi==96?760:580;menu.Height=650;Pump();menu.UpdateLayout();
                var content=(W.FrameworkElement)menu.Content;var bitmap=new WMI.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth*dpi/96),(int)Math.Ceiling(content.ActualHeight*dpi/96),dpi,dpi,WM.PixelFormats.Pbgra32);var drawing=new WM.DrawingVisual();using(var dc=drawing.RenderOpen())dc.DrawRectangle(new WM.VisualBrush(content),null,new W.Rect(0,0,content.ActualWidth,content.ActualHeight));bitmap.Render(drawing);
                var encoder=new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,$"menu-{tab}-{dpi}.png"));encoder.Save(file);
                var applyBounds=menu.Apply.TransformToAncestor(content).TransformBounds(new W.Rect(0,0,menu.Apply.ActualWidth,menu.Apply.ActualHeight));
                Check($"native-wpf-shared-footer-tab-{tab}-{dpi}",applyBounds.Left>=0&&applyBounds.Right<=content.ActualWidth+1&&applyBounds.Top>=0&&applyBounds.Bottom<=content.ActualHeight+1&&menu.Tabs.ActualHeight>150);
            }
            menu.RecentHistory.IsExpanded=true;menu.UpdateLayout();
            var footerBounds=menu.Apply.TransformToAncestor((W.FrameworkElement)menu.Content).TransformBounds(new W.Rect(0,0,menu.Apply.ActualWidth,menu.Apply.ActualHeight));
            Check("expanded-history-keeps-shared-footer-visible",footerBounds.Bottom<=((W.FrameworkElement)menu.Content).ActualHeight+1&&menu.Tabs.ActualHeight>100);
            menu.RecentHistory.IsExpanded=false;menu.Tabs.SelectedIndex=0;
            Set(editor,"environmentReadPace",new Func<GameIdentity,GameLayout,GamePace>((_,_)=>GamePace.Running));
            on.IsChecked=true;off.IsChecked=false;menu.Numbers[nameof(EnvironmentValues.RainReduction)].Text="23";
            Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            var deferred=new EnvironmentSettingsFile(Path.Combine(output,"storage","environment","settings.json")).Read().Settings!;
            Check("running-saves-entire-draft-without-applying",deferred.Seasonal&&deferred.Values.RainReduction==23&&seasons.Status.Scope==SeasonScope.Weather&&!menu.HasPendingChanges&&menu.Status.Text.Contains("일시정지"));
            menu.Choices[EnvironmentEffects.Flood].IsChecked=true;
            var pendingApply=(Task)Call(editor,"ReapplyEnvironmentAsync",true)!;Pump(()=>pendingApply.IsCompleted);pendingApply.GetAwaiter().GetResult();
            Check("automatic-apply-preserves-unsaved-draft",seasons.Status.Scope==SeasonScope.Weather&&menu.HasPendingChanges&&menu.Choices[EnvironmentEffects.Flood].IsChecked==true);
            menu.Choices[EnvironmentEffects.Flood].IsChecked=false;
            Set(editor,"environmentReadPace",new Func<GameIdentity,GameLayout,GamePace>((_,_)=>GamePace.Paused));
#if LOCAL_DEVELOPER
            Set(editor,"developerRendering",true);int beforeDeveloper=session.Applies;
            var heldForDeveloper=(Task)Call(editor,"ReapplyEnvironmentAsync",true)!;Pump(()=>heldForDeveloper.IsCompleted);heldForDeveloper.GetAwaiter().GetResult();
            Check("automatic-apply-does-not-interfere-with-developer-session",session.Applies==beforeDeveloper&&seasons.Status.Scope==SeasonScope.Weather);
            Set(editor,"developerRendering",false);
#endif
            var automatic=(Task)Call(editor,"ReapplyEnvironmentAsync",true)!;Pump(()=>automatic.IsCompleted);automatic.GetAwaiter().GetResult();
            Check("paused-refresh-applies-saved-season-and-effects-together",seasons.Status.Scope==SeasonScope.Full&&session.Receipt.Values.RainReduction==23&&!menu.HasPendingChanges);
            ulong appliedSequence=session.Receipt.Sequence;
            var same=(Task)Call(editor,"ReapplyEnvironmentAsync",false)!;Pump(()=>same.IsCompleted);same.GetAwaiter().GetResult();
            Check("same-settings-not-reapplied",session.Receipt.Sequence==appliedSequence);
            Click(menu.DisableAll);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            Check("all-off-clears-selection-and-restores",controller.Status.Restored&&controller.Status.Phase==EnvironmentPhase.Off&&menu.Selection(saved).Effects==EnvironmentEffects.None);
            Check("all-off-restores-weather-renderer",seasons.Status is{Phase:SeasonPhase.Off,OriginalRestored:true,Requested:false}&&seasonBackend.Sessions.All(s=>s.Disposed));
            menu.Close();Pump();Click(Field<PlateButton>(editor,"environmentButton"));Pump();menu=Field<EnvironmentSettingsWindow>(editor,"environmentMenu");
            Check("all-off-persisted",menu.Selection(saved).Effects==EnvironmentEffects.None&&!menu.Selection(saved).Seasonal);
            session.FailApply=true;menu.Choices[EnvironmentEffects.RainVisual].IsChecked=true;
            Click(menu.Apply);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            int failedApplies=session.Applies;
            var noRetry=(Task)Call(editor,"ReapplyEnvironmentAsync",false)!;Pump(()=>noRetry.IsCompleted);noRetry.GetAwaiter().GetResult();
            Check("apply-failure-preserves-save-and-blocks-automatic-retry",session.Applies==failedApplies&&menu.Status.Text.Contains("fixture environment apply failure")&&new EnvironmentSettingsFile(Path.Combine(output,"storage","environment","settings.json")).Read().Settings!.Effects==EnvironmentEffects.RainVisual);
            session.FailApply=false;
            var retry=(Task)Call(editor,"ReapplyEnvironmentAsync",true)!;Pump(()=>retry.IsCompleted);retry.GetAwaiter().GetResult();
            Check("explicit-refresh-retries-failed-saved-settings",session.Applies==failedApplies+1&&controller.Status.Phase==EnvironmentPhase.On);
            menu.Close();Pump();Click(Field<PlateButton>(editor,"environmentButton"));Pump();menu=Field<EnvironmentSettingsWindow>(editor,"environmentMenu");
            Check("reopen-retains-confirmed-applied-status",menu.Status.Text.Contains(L.Environment.AppliedTogether)&&!menu.Status.Text.Contains(L.Environment.NotApplied)&&!menu.HasPendingChanges);
            Click(menu.DisableAll);Pump(()=>!Field<bool>(editor,"environmentChanging"));
            var journal=Field<SeasonJournal>(editor,"environmentJournal");
            var flushed=journal.DisposeAsync().AsTask();Pump(()=>flushed.IsCompleted);flushed.GetAwaiter().GetResult();Set(editor,"environmentJournal",null);
            string[] timings=File.ReadAllLines(journal.Path).Where(line=>line.Contains("Timing:")).ToArray();
            foreach(string stage in new[]{"validate-and-save","pause-check","environment-native-apply","season-mode","weather-renderer"})
                Check("timing-records-"+stage,timings.Any(line=>line.Contains("stage="+stage+",")));
            Check("timing-distinguishes-pause-wait",timings.Any(line=>line.Contains("operation=environment-apply,")&&line.Contains("outcome=waiting-for-pause")));
            Check("timing-distinguishes-failure",timings.Any(line=>line.Contains("operation=environment-apply,")&&line.Contains("outcome=incomplete")));
            Console.WriteLine("PASS environment native WPF menu, 1024 combinations, direct numeric validation/persistence, actual status, OFF");
        }
        finally
        {
            menu?.Close();var task=controller.DisposeAsync().AsTask();Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            if(Field<SeasonController?>(editor,"seasonController") is not null){var stopped=(Task<bool>)Call(editor,"StopSeasonsAsync",true)!;Pump(()=>stopped.IsCompleted);Require(stopped.GetAwaiter().GetResult(),"Weather renderer cleanup failed");}
            Set(editor,"environmentController",null);host.Close();editor.Close();SynchronizationContext.SetSynchronizationContext(context);
        }
    }
    private static bool IsDescendant(System.Windows.DependencyObject root,System.Windows.DependencyObject target)
    {
        if(ReferenceEquals(root,target))return true;
        foreach(object child in W.LogicalTreeHelper.GetChildren(root))if(child is W.DependencyObject value&&IsDescendant(value,target))return true;return false;
    }
    private static void CheckEnvironmentVectors(string file)
    {
        using var json=JsonDocument.Parse(File.ReadAllBytes(file));int count=0;
        foreach(var row in json.RootElement.EnumerateArray())
        {
            uint weather=EnvironmentPolicy.Weather(ulong.Parse(row.GetProperty("seed").GetString()!),row.GetProperty("date").GetUInt32(),row.GetProperty("city").GetUInt32(),row.GetProperty("t").GetInt32(),row.GetProperty("p").GetInt32());
            Require(weather==row.GetProperty("weather").GetUInt32(),"Native weather differs");
            Require(EnvironmentPolicy.MovementPercent(weather,(EnvironmentEffects)row.GetProperty("flags").GetUInt32(),EnvironmentValues.Default with{RainReduction=row.GetProperty("rates")[0].GetInt32(),HeavyRainReduction=row.GetProperty("rates")[1].GetInt32(),SnowReduction=row.GetProperty("rates")[2].GetInt32(),DeepSnowReduction=row.GetProperty("rates")[3].GetInt32()})==row.GetProperty("movement").GetInt32(),"Native movement differs");count++;
        }
        Require(count==8640,"Incomplete native vectors");Console.WriteLine("PASS "+count+" native/managed weather and movement vectors");
    }
    private static void CheckEnvironmentLive(string output)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("New live evidence required");Directory.CreateDirectory(output);
        using var writer=new StreamWriter(Path.Combine(output,"live.jsonl")){AutoFlush=true};var sync=new object();
        void Log(string value){lock(sync){writer.WriteLine(DateTime.UtcNow.ToString("O")+" "+value);Console.WriteLine(value);}}
        GameIdentity identity=GameMemory.FindGame();Require(identity.Verified,"Unverified game");var layout=GameMemory.Resolve(identity,Log,CancellationToken.None);
        Require(GameMemory.ReadPace(identity,layout)==GamePace.Paused,"Live movement check requires already-paused game");
        var baseline=GameMemory.ReadSnapshot(identity,layout);var calendar=GameMemory.ReadCalendar(identity,layout);byte[] climate=EnvironmentClimate.Pack(baseline);
        var unit=baseline.Units.First(u=>u.Kind is 1 or 2&&!environmentWater(MapTerrain.Terrain[u.Tile]));
        int original=GameMemory.ReadEnvironmentMovement(identity,layout,unit,false);Require(original>0,"Native movement baseline must be positive");
        var county=baseline.Counties.First(c=>c.Index==baseline.TileCounties[unit.Tile]);int city=county.HomeCity;
        int month=calendar.Date.Month-1,nextMonth=(month+1)%12,day=calendar.Date.Day-1,offset=(city-1)*48+month*4,next=(city-1)*48+nextMonth*4;
        int t=BitConverter.ToInt16(climate,offset)+(BitConverter.ToInt16(climate,next)-BitConverter.ToInt16(climate,offset))*day/30;
        int p=BitConverter.ToUInt16(climate,offset+2)+(BitConverter.ToUInt16(climate,next+2)-BitConverter.ToUInt16(climate,offset+2))*day/30;
        ulong seed=1;while(EnvironmentPolicy.Weather(seed,EnvironmentPolicy.Serial(calendar.Date),(uint)city,t,p)==0)seed++;
        uint weather=EnvironmentPolicy.Weather(seed,EnvironmentPolicy.Serial(calendar.Date),(uint)city,t,p);
        Log(JsonSerializer.Serialize(new{identity.ProcessId,calendar,unit=unit.Slot,unit.Commander,unit.Tile,city,t,p,seed,weather,original}));
        var controller=new EnvironmentController(new EnvironmentNativeBackend(identity,climate,Path.Combine(output,"native"),Log),Log);
        try
        {
            foreach(var flags in new[]{EnvironmentEffects.RainMovement|EnvironmentEffects.SnowMovement,EnvironmentEffects.RainMovement,EnvironmentEffects.SnowMovement})
            {
                controller.ApplyAsync(EnvironmentSettings.New() with{Seed=seed,Effects=flags}).GetAwaiter().GetResult();
                var fresh=GameMemory.ReadSnapshot(identity,layout);var currentUnit=fresh.Units.Single(u=>u.Slot==unit.Slot);
                int actual=GameMemory.ReadEnvironmentMovement(identity,layout,currentUnit,true);
                int percent=EnvironmentPolicy.MovementPercent(weather,flags,EnvironmentValues.Default),expected=Math.Max(1,original*percent/100);
                Log(JsonSerializer.Serialize(new{flags,expected,actual,receipt=controller.Status.Receipt?.Applied,thread=controller.Status.Receipt?.Thread}));Require(actual==expected,"Actual game movement differs from selected rule");
            }
            controller.StopAsync().GetAwaiter().GetResult();var after=GameMemory.ReadSnapshot(identity,layout);
            int restored=GameMemory.ReadEnvironmentMovement(identity,layout,after.Units.Single(u=>u.Slot==unit.Slot),false);
            Require(restored==original&&controller.Status.Restored,"Native movement restoration differs");
            Require(GameMemory.ReadCalendar(identity,layout)==calendar,"Calendar changed during check");
            Require(baseline.Units.Select(u=>(u.Slot,u[UnitFields.Troops],u[UnitFields.Morale])).SequenceEqual(after.Units.Select(u=>(u.Slot,u[UnitFields.Troops],u[UnitFields.Morale]))),"Live troops or morale changed");
            Require(baseline.Cities.Select(c=>c[CityFields.Food]).SequenceEqual(after.Cities.Select(c=>c[CityFields.Food])),"Live city food changed");
            Log("PASS actual game frame movement, independent rain/snow choices, original code/slot/protection recovery; date and resources unchanged; damage not invoked");
        }
        finally{controller.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        static bool environmentWater(byte terrain)=>terrain is 13 or 14 or 22 or 23 or 24;
    }
    private static void CheckEnvironmentNotification(string output)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("New live evidence required");Directory.CreateDirectory(output);
        using var writer=new StreamWriter(Path.Combine(output,"native-notice.jsonl")){AutoFlush=true};var sync=new object();
        void Log(string value){lock(sync){writer.WriteLine(DateTime.UtcNow.ToString("O")+" "+value);Console.WriteLine(value);}}
        var identity=GameMemory.FindGame();Require(identity.Verified,"Unverified game");var layout=GameMemory.Resolve(identity,Log,CancellationToken.None);
        Require(GameMemory.ReadPace(identity,layout)==GamePace.Paused,"Original notification check requires already-paused game");
        var before=GameMemory.ReadSnapshot(identity,layout);var calendar=GameMemory.ReadCalendar(identity,layout);
        Require(GameMemory.ReadEnvironmentNotification(identity).Count==0,"An original report is already open");
        const string text="환경·재해 알림창 검증\n기존 재난 보고창 호출을 확인했습니다.\n이 메시지는 표시 검사이며 실제 재해 피해 기록이 아닙니다.";
        var session=new EnvironmentNativeBackend(identity,EnvironmentClimate.Pack(before),Path.Combine(output,"native"),Log).StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try{
            var applied=session.ApplyAsync(EnvironmentSettings.New() with{Effects=EnvironmentEffects.RainVisual},CancellationToken.None).AsTask().GetAwaiter().GetResult();Log(JsonSerializer.Serialize(new{identity.ProcessId,applied.Thread,applied.Frames,calendar}));
            ((IEnvironmentNotificationSession)session).CheckNotificationAsync(text,CancellationToken.None).AsTask().GetAwaiter().GetResult();
            (int Count,ulong DialogClass,string Text) observed=default;var timer=Stopwatch.StartNew();
            do{var receipt=session.QueryAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();observed=GameMemory.ReadEnvironmentNotification(identity);Log(JsonSerializer.Serialize(new{receipt.Thread,receipt.Frames,receipt.Reserved,observed.Count,observed.DialogClass,observed.Text}));if(observed.Count==1&&observed.DialogClass==0x146e2c0)break;Thread.Sleep(30);}while(timer.ElapsedMilliseconds<5000);
            // The original constructor consumes its input list. An empty post-construction list
            // cannot prove the visible widget's text; record that limitation instead of failing a valid dialog.
            Log(JsonSerializer.Serialize(new{observed.Count,observed.DialogClass,observed.Text,submittedText=text,visibleTextVerified=false}));Require(observed is{Count:1,DialogClass:0x146e2c0},"Original disaster dialog class was not observed");
            session.StopAsync().AsTask().GetAwaiter().GetResult();var after=GameMemory.ReadSnapshot(identity,layout);
            Require(GameMemory.ReadCalendar(identity,layout)==calendar,"Original recovery/calendar changed");
            bool sameUnits=before.Units.Select(u=>(u.Slot,u[UnitFields.Troops],u[UnitFields.Morale])).SequenceEqual(after.Units.Select(u=>(u.Slot,u[UnitFields.Troops],u[UnitFields.Morale])));
            bool sameFood=before.Cities.Select(c=>c[CityFields.Food]).SequenceEqual(after.Cities.Select(c=>c[CityFields.Food]));Require(sameUnits&&sameFood,"Live resources changed during report check");
            Log("PASS original CCmnDlgMsgWithRInputDialog active instance; original hooks restored; calendar, troops, morale and food unchanged; visible text, illustration and sound require direct verification");
        }finally{session.StopAsync().AsTask().GetAwaiter().GetResult();}
    }
}
