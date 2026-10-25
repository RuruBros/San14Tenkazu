#if LOCAL_DEVELOPER
using San14Tenkazu;
using San14Tenkazu.Tests;
internal static partial class RenderChecks
{
    private static void CheckDeveloperGeography()
    {
        CheckDeveloperQueryCompletion();
        var data=GeographyFixture.Create(51);var memory=new FakeMemory(data);var evidence=new List<DeveloperCityRoundTrip>();
        GameMemory.RoundTripDeveloperCities(data,memory,TestGame.Classes,evidence.Add);
        Require(evidence.Count(e=>e.Stage=="changed-readback")==5&&evidence.Count(e=>e.Stage=="restored-byte-exact")==5,"Every extra city must have changed and restored evidence");
        foreach(int id in data.CityIds)
        {
            Require(memory.Read(data.Cities[id].Address,data.Cities[id].Data.Length).SequenceEqual(data.Cities[id].Data),"City bytes must be restored exactly");
            var castle=data.CastleByCity[id];Require(memory.Read(castle.Address,castle.Data.Length).SequenceEqual(castle.Data),"Castle bytes must be restored exactly");
        }
        evidence.Clear();data.CastleByCity.Remove(49);
        GameMemory.RoundTripDeveloperCities(data,new FakeMemory(data),TestGame.Classes,evidence.Add);
        Require(evidence.Any(e=>e.City==49&&e.Stage=="skipped")&&!evidence.Any(e=>e.City==49&&e.Stage=="changed-readback"),"Missing castle must never receive a partial test edit");
        var original=GeographyFixture.Create();evidence.Clear();GameMemory.RoundTripDeveloperCities(original,new FakeMemory(original),TestGame.Classes,evidence.Add);
        Require(evidence.Count==0,"Original tribal records must never be edited by extra-city test");
        var failing=GeographyFixture.Create(51);var failureMemory=new FakeMemory(failing);bool failed=false;
        try{GameMemory.RoundTripDeveloperCities(failing,failureMemory,TestGame.Classes,e=>{if(e.Stage=="changed-readback")throw new System.IO.IOException("Owned log failure");});}
        catch(System.IO.IOException){failed=true;}
        Require(failed&&failureMemory.Read(failing.Cities[47].Address,failing.Cities[47].Data.Length).SequenceEqual(failing.Cities[47].Data)&&failureMemory.Read(failing.CastleByCity[47].Address,failing.CastleByCity[47].Data.Length).SequenceEqual(failing.CastleByCity[47].Data),"Log failure after write must still restore city and castle");
        var window=new DisasterTestWindow();window.SetCities(GeographyFixture.Create(51));
        Require(window.Cities.Items.Count==46&&window.Cities.Items.Cast<DisasterTestWindow.City>().All(c=>c.Id<=46),"Extra cities must be excluded from effect test selection while remaining editable");
        window.SetCities(original);Require(window.Cities.Items.Count==46,"Original tribal targets must remain excluded");
        var receipt=new San14Tenkazu.EnvironmentRules.EnvironmentProtocol.Reply{Date=192|(7u<<16)|(11u<<24),Weather=new byte[51],RecentDates=new uint[255],TestState=2,TestCity=51,TestKind=2};
        var overlay=San14Tenkazu.EnvironmentRules.EnvironmentOverlay.From(new(San14Tenkazu.EnvironmentRules.EnvironmentPhase.On,0,0,false,receipt));
        Require(overlay is null,"Stale added city preview must not create a visible marker");
        receipt.AvailableCities=(1UL<<51)-1;receipt.Weather[50]=3;receipt.TestId=7;
        receipt.Events=[new(){Id=1,City=49,Kind=2,FoodBefore=30000,FoodAfter=28200}];
        var status=new San14Tenkazu.EnvironmentRules.EnvironmentStatus(San14Tenkazu.EnvironmentRules.EnvironmentPhase.On,0,0,false,receipt);
        var projected=typeof(EditorWindow).GetMethod("DeveloperEnvironmentEvidence",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,[status]);
        using var json=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(projected));
        var logged=json.RootElement.GetProperty("Receipt");
        Require(logged.GetProperty("AvailableCities").GetUInt64()==receipt.AvailableCities&&logged.GetProperty("UnavailableCities").GetUInt64()==0&&
            logged.GetProperty("Weather").GetArrayLength()==51&&logged.GetProperty("Weather")[50].GetInt32()==3&&logged.GetProperty("TestId").GetUInt64()==7&&
            logged.GetProperty("Events")[0].GetProperty("City").GetInt32()==49&&logged.GetProperty("Events")[0].GetProperty("FoodAfter").GetInt32()==28200,
            "Serialized developer evidence must retain internal Native availability, city 51 weather, test and actual event values");
        System.Console.WriteLine("PASS developer city 47-51 normal edit/readback/byte-exact restore, missing property, original, log failure, loaded target and preview checks; gameAccess=false");
    }
    private static void CheckDeveloperQueryCompletion()
    {
        static void Set(object owner,string name,object? value)=>owner.GetType().GetField(name,Hidden)!.SetValue(owner,value);
        static object? Call(object owner,string name,params object?[] values)=>owner.GetType().GetMethod(name,Hidden)!.Invoke(owner,values);
        var previous=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        string root=System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(),".cache","developer-completion-"+Guid.NewGuid().ToString("N"));
        var identity=new GameIdentity(111,DateTime.UtcNow,0x140000000,"fixture",true,TestGame.Classes);
        var layout=new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,0x1000);
        new RuntimeRulePreferenceFile(System.IO.Path.Combine(root,"runtime-rules")).Save(new(1,identity.ExeHash,RuntimeRuleSettings.Original,false));
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();bool fail=false;
        var access=new RuntimeRuleGameAccess(_=>{entered.Set();if(!release.Wait(5000))throw new TimeoutException("Owned read was not released");if(fail)throw new System.IO.IOException("Owned read failure");return RuntimeRuleSettings.Original;},
            (_,_)=>throw new InvalidOperationException("UI completion must not read live pace"),(_,_,_,_,_)=>throw new InvalidOperationException("UI completion must not write"));
        var editor=OfflineEditor(()=>false,root,root,root,runtimeRuleAccess:access);var menu=new DisasterTestWindow();
        try
        {
            Set(editor,"game",identity);Set(editor,"layout",layout);Set(editor,"snapshot",new TestGame().Build());Set(editor,"developerMenu",menu);
            foreach(bool failing in new[]{false,true})
            {
                fail=failing;entered.Reset();release.Reset();
                var task=(Task)Call(editor,"ReapplyRuntimeRulesAsync",true)!;
                Require(entered.Wait(5000),"Owned async runtime query did not start");Call(editor,"UpdateDeveloperControls");
                Require(!menu.ReadGeography.IsEnabled&&!menu.EditGeography.IsEnabled,"Developer action must remain blocked during a real async query");
                release.Set();var watch=System.Diagnostics.Stopwatch.StartNew();
                while(!task.IsCompleted)
                {
                    if(watch.ElapsedMilliseconds>5000)throw new TimeoutException("Owned query completion timed out");
                    var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                task.GetAwaiter().GetResult();
                Require(menu.ReadGeography.IsEnabled&&menu.EditGeography.IsEnabled,"Developer actions must become available after async success or failure without another click");
            }
            System.Console.WriteLine("PASS developer menu real async query completion/failure restores availability, guarded while pending, no live pace or writes; gameAccess=false");
        }
        finally{release.Set();Set(editor,"developerMenu",null);menu.Close();editor.Close();SynchronizationContext.SetSynchronizationContext(previous);}
    }
}
#endif
