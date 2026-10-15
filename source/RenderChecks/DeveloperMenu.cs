using San14Tenkazu;
using System.IO;
using System.Reflection;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Tests;
using W=System.Windows;
using WC=System.Windows.Controls;
internal static partial class RenderChecks
{
#if LOCAL_DEVELOPER
    private sealed class DeveloperFixture:IEnvironmentSession,IEnvironmentBackend,IEnvironmentDeveloperSession
    {
        internal EnvironmentProtocol.Reply Receipt=new(){Weather=new byte[46],RecentDates=new uint[230],Events=new EnvironmentProtocol.Event[1024]};
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
        Require((typeof(LogPanel).GetMethod("SetDeveloperButton",Hidden)!=null)==expected,"Developer log-header entry leaked into public build");
#if LOCAL_DEVELOPER
        CheckNoticeTypographyReader();
        CheckTraitListDiagnostic();
        CheckCompatibilityDiagnostic();
        CheckDeveloperEntry();
        var fixture=new DeveloperFixture();var controller=new EnvironmentController(fixture,Console.WriteLine);
        try {
            controller.ApplyAsync(EnvironmentSettings.New(),keepEmptySession:true).GetAwaiter().GetResult();
            Require(controller.Status is{Phase:EnvironmentPhase.On,Actual:EnvironmentEffects.None},"Developer session must not enable gameplay effects on startup");
            controller.TestAsync(2,46,2).GetAwaiter().GetResult();Require(controller.Status.Receipt is{TestCity:46,TestKind:2,TestState:3},"Controller must preserve target and scheduled receipt");
            controller.TestAsync(0,1,0).GetAwaiter().GetResult();Require(controller.Status.Receipt is{TestState:5},"Controller must acknowledge cancellation");
        }finally{controller.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        Require(fixture.Stopped,"Developer controller must restore native ownership on disposal");
        var window=new DisasterTestWindow();window.SetCities(new TestGame().Build());window.SetAvailable(true,true);
        Require(ReferenceEquals(window.Content,window.Tabs)&&window.Tabs.Items.Count==3&&window.Tabs.Items[0] is WC.TabItem{Header:"재난 테스트"}
            && window.Tabs.Items[1] is WC.TabItem{Header:"개성 목록 진단"}&&window.Tabs.Items[2] is WC.TabItem{Header:"호환성 진단"},"Developer tabs must separate approved actions");
        try {
            window.Show();window.UpdateLayout();
            Require(window.Kind.Items.Count==5&&window.Cities.Items.Count>0,"Disaster/city choices missing");
            Require(window.Preview.IsEnabled&&window.Damage.IsEnabled&&window.Stop.IsEnabled&&window.ReadText.IsEnabled,"Separate test actions missing");
            window.Cities.SelectedItem=null;Require(window.ReadText.IsEnabled&&window.ReadTraits.IsEnabled&&!window.Preview.IsEnabled,"Read-only diagnostics must not require a damage target");window.Cities.SelectedIndex=0;
            window.SetAvailable(false,true);Require(!window.Preview.IsEnabled&&!window.Damage.IsEnabled&&!window.ReadTraits.IsEnabled&&window.Stop.IsEnabled,"Busy state must retain cancellation");
            var receipt=new EnvironmentProtocol.Reply{Date=192|(7u<<16)|(11u<<24),Weather=new byte[46],RecentDates=new uint[230],TestState=2,TestCity=1,TestKind=1};
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
            window.SetCompatibilityAvailable(false,true);
            Require(!window.ReadCompatibility.IsEnabled&&window.CancelCompatibility.IsEnabled,"Read-only cancellation must remain enabled while reading");
            Console.WriteLine("PASS local developer WPF choices, distinct actions, cancellation, receipt, typography and preview provenance");
        }finally{window.Close();}
#endif
        Console.WriteLine("PASS developer build flavor expected="+expected+" compiled="+compiled);
    }
#if LOCAL_DEVELOPER
    private static void CheckDeveloperEntry()
    {
        static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Hidden)!.GetValue(owner)!;
        static void Click(PlateButton button)=>((Action)typeof(PlateButton).GetField("Click",Hidden)!.GetValue(button)!).Invoke();
        static void Pump(){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);}
        string directory=Path.Combine(Path.GetTempPath(),"San14Tenkazu-developer-header-"+Guid.NewGuid().ToString("N"));
        var editor=new EditorWindow(()=>false,directory,directory,directory);
        new System.Windows.Interop.WindowInteropHelper(editor).EnsureHandle();
        var root=(W.FrameworkElement)editor.Content;editor.Content=null;
        var host=new W.Window{Content=root,Width=1100,Height=750,ShowActivated=false,ShowInTaskbar=false,Title="Developer header WPF check"};
        var log=Field<LogPanel>(editor,"log");var button=Field<PlateButton>(editor,"developerButton");
        try {
            host.Show();Pump();host.UpdateLayout();
            Require(IsDescendant(log,button)&&button.ContextMenu is null&&!log.Open,"Developer button must belong to the folded log header and open directly");
            var bounds=button.TransformToAncestor(log).TransformBounds(new W.Rect(0,0,button.ActualWidth,button.ActualHeight));
            Require(bounds.Right>log.ActualWidth-24&&bounds.Top>=0&&bounds.Bottom<=log.ActualHeight,"Developer entry must sit at the right edge of the log header");
            Click(button);Pump();var first=Field<DisasterTestWindow>(editor,"developerMenu");
            Require(first.IsVisible&&first.Tabs.Items.Count==3&&!log.Open,"One click must open the tabbed window without expanding the log");
            Click(button);Pump();Require(ReferenceEquals(first,Field<DisasterTestWindow>(editor,"developerMenu")),"Repeated click must reuse the same tools window");
            first.Close();Pump();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(log.ActualWidth),(int)Math.Ceiling(log.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
            var visual=new System.Windows.Media.DrawingVisual();using(var draw=visual.RenderOpen())draw.DrawRectangle(new System.Windows.Media.VisualBrush(log),null,new W.Rect(0,0,log.ActualWidth,log.ActualHeight));bitmap.Render(visual);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            string path=Path.Combine(Path.GetTempPath(),"San14Tenkazu-developer-header-24.png");using(var output=File.Create(path))encoder.Save(output);
            Console.WriteLine("WPF developer header: "+path);
            Console.WriteLine("PASS developer entry at log header right edge, direct opening, visible disaster tab, window reuse and unchanged log fold; no game connection");
        }finally {
            Field<DisasterTestWindow?>(editor,"developerMenu")?.Close();Pump();host.Close();editor.Close();
        }
    }
#endif
}
