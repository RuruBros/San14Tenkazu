using San14Tenkazu;
using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Tests;
using W=System.Windows;
using WC=System.Windows.Controls;
internal static partial class RenderChecks
{
#if LOCAL_DEVELOPER
    private sealed class DeveloperFixture:IEnvironmentSession,IEnvironmentBackend,IEnvironmentDeveloperSession
    {
        internal EnvironmentProtocol.Reply Receipt=new(){Weather=new byte[EnvironmentProtocol.CitySlots],RecentDates=new uint[EnvironmentProtocol.CitySlots*5],Events=new EnvironmentProtocol.Event[1024]};
        internal bool Stopped;
        public ValueTask<IEnvironmentSession> StartAsync(CancellationToken token)=>ValueTask.FromResult<IEnvironmentSession>(this);
        public ValueTask<EnvironmentProtocol.Reply> ApplyAsync(EnvironmentSettings settings,CancellationToken token){Receipt.Flags=(uint)settings.Effects;return ValueTask.FromResult(Receipt);}
        public ValueTask<EnvironmentProtocol.Reply> QueryAsync(CancellationToken token)=>ValueTask.FromResult(Receipt);
        public ValueTask StopAsync(){Stopped=true;return ValueTask.CompletedTask;}
        public ValueTask<EnvironmentProtocol.Reply> TestAsync(int mode,int city,int kind,CancellationToken token){Receipt.TestId++;Receipt.TestMode=(uint)mode;Receipt.TestCity=(uint)city;Receipt.TestKind=(uint)kind;Receipt.TestState=mode==0?5u:mode==1?1u:3u;return ValueTask.FromResult(Receipt);}
    }
#endif
    private static void CheckDeveloperMenu(bool expected)
    {
        bool compiled=typeof(EditorWindow).GetMethod("BuildDeveloperMenu",Hidden)!=null;
        Require(compiled==expected,"Developer entry point differs from requested package flavor");
        Require((typeof(EditorWindow).Assembly.GetType("San14Tenkazu.DisasterTestWindow")!=null)==expected,"Developer window leaked into public assembly");
        Require((typeof(EnvironmentController).GetMethod("TestAsync",Hidden)!=null)==expected,"Developer controller entry point differs from package flavor");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="ReadNoticeTypography")==expected,"Read-only typography entry leaked into public build");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="ReadTraitListDiagnostic")==expected,"Trait diagnostic entry leaked into public build");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="ReadCompatibilityDiagnostic")==expected,"Compatibility diagnostic entry leaked into public build");
        Require((typeof(EditorWindow).GetMethod("ReadDeveloperCompatibility",Hidden)!=null)==expected,"Compatibility menu entry leaked into public build");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="RoundTripDeveloperCities")==expected,"City roundtrip entry leaked into public build");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="ReadDeveloperGeographyTopology")==expected,"Raw geography diagnostic leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunDeveloperGeography",Hidden)!=null)==expected,"City diagnostics leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunProfiledCityEdit",Hidden)!=null)==expected,"Profiled city round trip leaked into public build");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="RoundTripProfiledCityGold")==expected,"Profiled city memory test leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunProfiledFieldsEdit",Hidden)!=null)==expected,"Profiled field round trip leaked into public build");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="RoundTripProfiledFields")==expected,"Profiled field memory test leaked into public build");
        foreach (string method in new[] { "PrepareCompleteEditorTrials", "PlanCompleteEditorTrials", "RoundTripCompleteEditorFields", "RunCompleteEditTrial", "PrepareSeededProvocationTrial" })
            Require(typeof(GameMemory).GetMethods(Hidden | BindingFlags.Static).Any(m => m.Name == method) == expected, "Complete editor trial leaked: " + method);
        Require((typeof(EditorWindow).GetMethod("PrepareCompleteEdits", Hidden) != null) == expected, "Complete editor preview leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunCompleteEdits", Hidden) != null) == expected, "Complete editor UI action leaked into public build");
        Require((typeof(GameMemory).GetNestedType("CompleteEditTrial", BindingFlags.NonPublic) != null) == expected, "Complete editor trial types leaked into public build");
        foreach (string method in new[] { "BeginDeveloperProvocation", "FinishDeveloperProvocation", "ApplyProvocationBytes", "RestoreProvocationBytes" })
            Require(typeof(GameMemory).GetMethods(Hidden | BindingFlags.Static).Any(m => m.Name == method) == expected, "Held provocation leaked: " + method);
        Require((typeof(EditorWindow).GetMethod("RunHeldProvocation", Hidden) != null) == expected, "Held provocation UI leaked");
        Require((typeof(EditorWindow).GetMethod("RunProfiledRuleEdit",Hidden)!=null)==expected,"Profiled rule round trip leaked into public build");
        Require(typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name is "RoundTripProfiledRules" or "PlanProfiledRuleTrials")==expected,"Profiled rule diagnostic leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunProfiledAiEdit",Hidden)!=null)==expected&&
            typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name is "RoundTripProfiledAi" or "RunProfiledAiTrial")==expected,
            "Profiled AI diagnostic leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunProfiledAiExtension",Hidden)!=null)==expected&&
            typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="RoundTripNovemberAiExtension")==expected,
            "AI supply protection diagnostic leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunProfiledRuntimeRules",Hidden)!=null)==expected&&
            typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="RoundTripProfiledRuntimeRules")==expected,
            "Runtime rule diagnostic leaked into public build");
        Require((typeof(EditorWindow).GetMethod("StartNativeCommandSuiteStepAsync",Hidden)!=null)==expected&&
            (typeof(EditorWindow).GetMethod("FindNativeCommandContinuation",Hidden|BindingFlags.Static)!=null)==expected&&
            typeof(GameMemory).GetMethods(Hidden|BindingFlags.Static).Any(m=>m.Name=="ReadNativeTrialCamera")==expected,
            "Native command suite leaked into public build");
        Require((typeof(EditorWindow).GetMethod("RunProfiledRenderCheck",Hidden)!=null)==expected,"Profiled render diagnostic leaked into public build");
        Require((typeof(EditorWindow).GetMethod("DeveloperEnvironmentEvidence",Hidden|BindingFlags.Static)!=null)==expected,"Native receipt diagnostic leaked into public build");
        Require((typeof(EditorWindow).GetMethod("PrepareDeveloperNoticeScene",Hidden)!=null)==expected&&
            (typeof(EditorWindow).Assembly.GetType("San14Tenkazu.DeveloperNoticeDiagnostic")!=null)==expected,"Picture diagnostics leaked into public build");
        Require((typeof(EditorWindow).Assembly.GetType("San14Tenkazu.DeveloperNoticeCausality")!=null)==expected,"Causal diagnostic reader leaked into public build");
        Require((typeof(EditorWindow).Assembly.GetType("San14Tenkazu.DeveloperPerformanceSummary")!=null)==expected&&
            (typeof(EditorWindow).GetMethod("RunPerformanceDiagnostic",Hidden)!=null)==expected&&
            (typeof(San14Tenkazu.Seasons.SeasonController).GetMethod("PerformanceAsync",Hidden)!=null)==expected,"Performance diagnostics leaked into public build");
        Require((typeof(LogPanel).GetMethod("SetDeveloperButton",Hidden)!=null)==expected,"Developer log-header entry leaked into public build");
#if LOCAL_DEVELOPER
        CheckNoticeTypographyReader();
        CheckTraitListDiagnostic();
        CheckCompatibilityDiagnostic();
        CheckDeveloperEntry();
        CheckNativeCommandContinuation();
        CheckDeveloperGeography();
        CheckProfiledRuleRoundTrip();
        CheckProfiledAiRoundTrip();
        CheckCompleteEditorTrials();
        CheckHeldProvocation();
        CheckDeveloperNoticeDiagnostic();
        CheckPerformanceSummary();
        var fixture=new DeveloperFixture();var controller=new EnvironmentController(fixture,Console.WriteLine);
        try {
            controller.ApplyAsync(EnvironmentSettings.New(),keepEmptySession:true).GetAwaiter().GetResult();
            Require(controller.Status is{Phase:EnvironmentPhase.On,Actual:EnvironmentEffects.None},"Developer session must not enable gameplay effects on startup");
            bool excluded=false;try{controller.TestAsync(2,51,2).GetAwaiter().GetResult();}catch(ArgumentOutOfRangeException){excluded=true;}
            Require(excluded,"Controller must reject excluded city before dispatch");
            controller.TestAsync(2,46,2).GetAwaiter().GetResult();Require(controller.Status.Receipt is{TestCity:46,TestKind:2,TestState:3},"Controller must preserve original city target and scheduled receipt");
            controller.TestAsync(0,1,0).GetAwaiter().GetResult();Require(controller.Status.Receipt is{TestState:5},"Controller must acknowledge cancellation");
        }finally{controller.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        Require(fixture.Stopped,"Developer controller must restore native ownership on disposal");
        var window=new DisasterTestWindow();window.SetCities(new TestGame().Build());window.SetAvailable(true,true);
        Require(ReferenceEquals(window.Content,window.Tabs)&&window.Tabs.Items.Count==7&&window.Tabs.Items[0] is WC.TabItem{Header:"재난 테스트"}
            && window.Tabs.Items[1] is WC.TabItem{Header:"개성 목록 진단"}&&window.Tabs.Items[2] is WC.TabItem{Header:"호환성 진단"},"Developer tabs must separate approved actions");
        try {
            window.Show();window.UpdateLayout();
            Require(window.Kind.Items.Count==5&&window.Cities.Items.Count>0,"Disaster/city choices missing");
            Require(window.PictureDiagnostic.IsChecked==true&&window.PictureDiagnostic.IsEnabled,"Approved preview picture diagnostics must be visible and enabled");
            Require(window.Preview.IsEnabled&&window.Damage.IsEnabled&&window.Stop.IsEnabled&&window.ReadText.IsEnabled,"Separate test actions missing");
            window.Cities.SelectedItem=null;Require(window.ReadText.IsEnabled&&window.ReadTraits.IsEnabled&&!window.Preview.IsEnabled,"Read-only diagnostics must not require a damage target");window.Cities.SelectedIndex=0;
            window.SetAvailable(false,true);Require(!window.Preview.IsEnabled&&!window.Damage.IsEnabled&&!window.ReadTraits.IsEnabled&&window.Stop.IsEnabled,"Busy state must retain cancellation");
            window.SetAvailable(true,true,previewOnly:true);
            Require(window.Preview.IsEnabled&&window.Stop.IsEnabled&&!window.Damage.IsEnabled&&!window.ReadText.IsEnabled&&
                window.PictureDiagnostic.IsChecked==false&&window.PictureDiagnostic.IsEnabled,"New profile must default to the repeatable no-damage preview");
            window.PictureDiagnostic.IsChecked=true;window.SetAvailable(false,true,previewOnly:true);window.SetAvailable(true,true,previewOnly:true);
            Require(window.PictureDiagnostic.IsChecked==true,"Refreshing availability must preserve the user's trace selection");
            var receipt=new EnvironmentProtocol.Reply{Date=192|(7u<<16)|(11u<<24),Weather=new byte[EnvironmentProtocol.CitySlots],RecentDates=new uint[EnvironmentProtocol.CitySlots*5],TestState=2,TestCity=1,TestKind=1};
            var overlay=EnvironmentOverlay.From(new(EnvironmentPhase.On,EnvironmentEffects.None,EnvironmentEffects.None,false,receipt));
            Require(overlay is not null&&overlay.IsPreview(1,4)&&overlay.Recent(1,4)==receipt.Date&&receipt.RecentDates.All(d=>d==0),"Preview must be distinct from real history");
            window.SetReceipt(new(EnvironmentPhase.On,EnvironmentEffects.None,EnvironmentEffects.None,false,receipt));Require(window.Status.Text.Contains("피해 없음"),"Preview status must identify no damage");
            window.SetReceipt(new(EnvironmentPhase.On,EnvironmentEffects.None,EnvironmentEffects.None,false,receipt with{TestState=3}));Require(window.Status.Text.Contains("경계 대기"),"Reservation status missing");
            uint soundReceipt=1u|4u|(1u<<8)|(110u<<16)|(2u<<24);
            window.SetReceipt(new(EnvironmentPhase.On,EnvironmentEffects.None,EnvironmentEffects.None,false,receipt with{Reserved=soundReceipt}));
            Require(window.Status.Text.Contains("FIRE")&&window.Status.Text.Contains("ID=109")&&window.Status.Text.Contains("channel=0")&&window.Status.Text.Contains("중단 확인")&&window.Status.Text.Contains("직접 확인"),"Developer sound status must identify original source, channel zero, stop confirmation and audibility limit");
            Require(DisasterTestWindow.SoundName(0x65).Contains("FLOOD_WATER")&&DisasterTestWindow.SoundName(0x5f).Contains("WIND_STRONG")&&DisasterTestWindow.SoundName(0x60).Contains("WIND_COLD")&&DisasterTestWindow.SoundName(0x77).Contains("INSECT"),"Approved disaster sound names missing");
            window.SetAvailable(true,true);window.UpdateLayout();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            string artifact=Path.Combine(Path.GetTempPath(),"San14Tenkazu-developer-menu.png");using(var output=File.Create(artifact))encoder.Save(output);Console.WriteLine("WPF preview: "+artifact);
            Console.WriteLine($"Typography button layout: width={window.ReadText.ActualWidth}, height={window.ReadText.ActualHeight}, desired={window.ReadText.DesiredSize}");
            Require(window.ReadText.ActualWidth>0&&window.ReadText.ActualHeight>0&&
                window.ReadText.ActualWidth+window.ReadText.Margin.Left+window.ReadText.Margin.Right+1>=window.ReadText.DesiredSize.Width,
                "Read-only typography action must fit its measured text");
            window.Tabs.SelectedIndex=1;window.UpdateLayout();
            Require(window.ReadTraits.ActualWidth>0&&window.ReadTraits.ActualHeight>0&&window.TraitLog.ActualHeight>100,
                "Trait diagnostic action and result log must fit the second tab");
            window.Tabs.SelectedIndex=2;window.SetCompatibilityAvailable(true,false);window.UpdateLayout();
            Require(window.ReadCompatibility.IsEnabled&&!window.CancelCompatibility.IsEnabled&&window.CompatibilityLog.ActualHeight>100,"Compatibility tab layout missing");
            Require(window.NativeCommandSuite.IsVisible&&!window.NativeCommandSuite.IsEnabled,
                "One native command suite entry should be visible and disabled offline");
            window.SetCompatibilityAvailable(false,true);
            Require(!window.ReadCompatibility.IsEnabled&&window.CancelCompatibility.IsEnabled,"Read-only cancellation must remain enabled while reading");
            Console.WriteLine("PASS local developer WPF choices, distinct actions, cancellation, receipt, typography and preview provenance");
        }finally{window.Close();}
#endif
        Console.WriteLine("PASS developer build flavor expected="+expected+" compiled="+compiled);
    }
#if LOCAL_DEVELOPER
    private static void CheckProfiledAiRoundTrip()
    {
        var data=new TestGame().City(1,1).Build();
        var city=data.Cities[1];
        var memory=new FakeMemory(data);
        var edit=new RecordEdit(RecordKind.City,city.Address,city.Data,"AI recovery fixture",
            [new(CityFields.Gold,city[CityFields.Gold]+1)]);
        int writes=0;
        void Apply(IReadOnlyList<RecordEdit> edits){writes++;GameMemory.Apply(memory,TestGame.Classes,edits);}
        var receipt=GameMemory.RunProfiledAiTrial(memory,[edit],Apply);
        Require(writes==2&&receipt.Changed.Single().Sha256!=receipt.Restored.Single().Sha256&&
            memory.Read(city.Address,city.Data.Length).SequenceEqual(city.Data),"AI diagnostic must compare readback and restore whole records");
        memory.Set(city.Address,CityFields.Gold,city[CityFields.Gold]+2);
        try{GameMemory.RunProfiledAiTrial(memory,[edit],Apply);throw new IOException("AI diagnostic admitted a stale target");}
        catch(InvalidOperationException){Require(writes==2,"Stale AI target must receive no writes");}
        Console.WriteLine("PASS AI diagnostic exact restored bytes and stale target refusal; gameAccess=false");
    }

    private static void CheckNativeCommandContinuation()
    {
        var identity = new GameIdentity(1234, DateTime.UnixEpoch, 0, GameNativeFactsNovember2026.ExeSha256, false, TestGame.Classes);
        string directory = Directory.CreateTempSubdirectory("San14Tenkazu-command-journal-").FullName;
        string path = Path.Combine(directory, "native-commands-fixture.jsonl");
        string[] commands = ["Camera", "Calendar", "Item", "Alliance", "Give", "Capture", "Transfer", "Revival", "Independence", "Surrender", "Demo"];
        var find = typeof(EditorWindow).GetMethod("FindNativeCommandContinuation", Hidden | BindingFlags.Static)!;
        object? Find(GameIdentity game, DateTime title) => find.Invoke(null, [directory, game, title]);
        string Row(string stage, string command) => JsonSerializer.Serialize(new { stage, command });
        try
        {
            File.WriteAllLines(path, [JsonSerializer.Serialize(new { stage = "started", identity.ProcessId, identity.ExeHash, identity.StartTimeUtc, commands }),
                Row("armed", "Camera"), Row("before", "Camera"), Row("after", "Camera"),
                Row("armed", "Calendar"), Row("before", "Calendar"), Row("after", "Calendar")]);
            DateTime latest = File.GetLastWriteTimeUtc(path);
            DateTime title = latest.AddMinutes(1);
            object? result = Find(identity, title);
            Require(result is not null && Equals(result.GetType().GetField("Completed", Hidden)!.GetValue(result), 2),
                "Native command continuation must preserve completed steps");
            Require(Find(identity, latest) is null, "Native command continuation must require a later title observation");
            Require(Find(identity with { StartTimeUtc = identity.StartTimeUtc.AddSeconds(1) }, title) is null,
                "Native command continuation must reject a reused PID");
            File.AppendAllLines(path, [Row("armed", "Item"), Row("before", "Item")]);
            Require(Find(identity, title) is null, "Native command continuation must reject an unfinished write");
            File.AppendAllLines(path, [Row("failed", "Item")]);
            Require(Find(identity, title) is not null, "Native command continuation must keep failed steps pending after title");
            File.AppendAllLines(path, [Row("after", "Alliance")]);
            Require(Find(identity, title) is null, "Native command continuation must reject an out-of-order result");
            Console.WriteLine("PASS native command continuation: 6 checks; gameAccess=false");
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private static void CheckDeveloperEntry()
    {
        static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Hidden)!.GetValue(owner)!;
        static void Click(PlateButton button)=>((Action)typeof(PlateButton).GetField("Click",Hidden)!.GetValue(button)!).Invoke();
        static void Pump(){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);}
        string directory=Path.Combine(Path.GetTempPath(),"San14Tenkazu-developer-header-"+Guid.NewGuid().ToString("N"));
        var editor=OfflineEditor(()=>false,directory,directory,directory);
        new System.Windows.Interop.WindowInteropHelper(editor).EnsureHandle();
        var root=(W.FrameworkElement)editor.Content;editor.Content=null;
        var host=new W.Window{Content=root,Width=1100,Height=750,ShowActivated=false,ShowInTaskbar=false,Title="Developer header WPF check"};
        var log=Field<LogPanel>(editor,"log");var button=Field<PlateButton>(editor,"developerButton");
        string journal = Path.GetTempFileName();
        try {
            host.Show();Pump();host.UpdateLayout();
            Require(IsDescendant(log,button)&&button.ContextMenu is null&&!log.Open,"Developer button must belong to the folded log header and open directly");
            var bounds=button.TransformToAncestor(log).TransformBounds(new W.Rect(0,0,button.ActualWidth,button.ActualHeight));
            Require(bounds.Right>log.ActualWidth-24&&bounds.Top>=0&&bounds.Bottom<=log.ActualHeight,"Developer entry must sit at the right edge of the log header");
            Click(button);Pump();var first=Field<DisasterTestWindow>(editor,"developerMenu");
            Require(first.IsVisible&&first.Tabs.Items.Count==7&&!log.Open,"One click must open the tabbed window without expanding the log");
            Require(first.Tabs.Items[6] is WC.TabItem held && Equals(held.Header, L.DeveloperUi.ProvocationTab), "Held provocation tab is missing");
            first.Tabs.SelectedIndex = 2; Pump(); first.UpdateLayout();
            Require(first.ProfiledRulesEdit.IsVisible && !first.ProfiledRulesEdit.IsEnabled,
                "The rule test button must be visible in the compatibility tab and unavailable offline");
            Require(first.ProfiledAiEdit.IsVisible&&!first.ProfiledAiEdit.IsEnabled,"The AI test button must be visible and unavailable offline");
            Click(button);Pump();Require(ReferenceEquals(first,Field<DisasterTestWindow>(editor,"developerMenu")),"Repeated click must reuse the same tools window");
            var suiteField = typeof(EditorWindow).GetField("nativeCommandSuite", Hidden)!;
            var fixture = new GameIdentity(1234, DateTime.UnixEpoch, 0, GameNativeFactsNovember2026.ExeSha256, false, TestGame.Classes);
            object? suite = Activator.CreateInstance(suiteField.FieldType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [journal, fixture], null);
            suiteField.SetValue(editor, suite);
            first.Close();Pump();
            Require(suite is not null && ReferenceEquals(suite, suiteField.GetValue(editor)),
                "Closing the tools window must keep the active native command suite");
            Console.WriteLine("PASS native command suite survives closing the tools window; gameAccess=false");
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(log.ActualWidth),(int)Math.Ceiling(log.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
            var visual=new System.Windows.Media.DrawingVisual();using(var draw=visual.RenderOpen())draw.DrawRectangle(new System.Windows.Media.VisualBrush(log),null,new W.Rect(0,0,log.ActualWidth,log.ActualHeight));bitmap.Render(visual);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            string path=Path.Combine(Path.GetTempPath(),"San14Tenkazu-developer-header-24.png");using(var output=File.Create(path))encoder.Save(output);
            Console.WriteLine("WPF developer header: "+path);
            Console.WriteLine("PASS developer entry at log header right edge, direct opening, visible disaster tab, window reuse and unchanged log fold; no game connection");
        }finally {
            Field<DisasterTestWindow?>(editor,"developerMenu")?.Close();Pump();host.Close();editor.Close();File.Delete(journal);
        }
    }
#endif
}
