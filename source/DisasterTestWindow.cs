#if LOCAL_DEVELOPER
using San14Tenkazu.EnvironmentRules;
using W=System.Windows;
using WC=System.Windows.Controls;
namespace San14Tenkazu;

internal sealed class DisasterTestWindow:W.Window
{
    internal static readonly string[] Names=["홍수","산불","가뭄","폭설","폭염"];
    internal static string SoundName(int id)=>id switch{0x65=>"홍수(FLOOD_WATER)",0x6d=>"불(FIRE)",0x5f=>"바람(WIND_STRONG)",0x60=>"찬바람(WIND_COLD)",0x77=>"곤충(INSECT)",_=>"미확인 음원"};
    internal sealed record City(int Id,int Force,string Label){public override string ToString()=>Label;}
    internal readonly WC.ComboBox Kind=new(){ItemsSource=Names,SelectedIndex=0,Margin=new W.Thickness(0,4,0,12)};
    internal readonly WC.ComboBox Cities=new(){Margin=new W.Thickness(0,4,0,12)};
    internal readonly PlateButton Preview=new("표시 전용"){ToolTip="선택한 재난의 원본 효과음을 한 번 재생합니다. 최대 6초이며 알림 닫기·취소·연결 해제 시 중단합니다. 원본 음량과 비활성 창 음소거를 사용합니다."},Damage=new("실제 피해 예약"),Stop=new("예약 취소 · 표시 종료"),Refresh=new("도시 목록 새로 읽기");
    internal readonly PlateButton ReadText=new("현재 알림 글자 상태 읽기"){ToolTip="현재 열린 게임 알림의 텍스트 영역·글꼴 데이터만 읽어 로그에 저장합니다."};
    internal readonly WC.TextBlock Status=new(){TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,12,0,12)};
    internal readonly WC.TextBox Log=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,MinHeight=150};
    internal readonly PlateButton ReadTraits=new("9개성 표시 진단");
    internal readonly WC.TextBox TraitLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
    internal readonly WC.TabControl Tabs=new(){Background=CardSkin.Parchment,BorderBrush=CardSkin.Gold,Padding=new W.Thickness(0)};
    internal readonly PlateButton ReadCompatibility=new("호환성 읽기 전용 진단"),CancelCompatibility=new("진단 취소");
    internal readonly WC.TextBox CompatibilityLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
    internal City? Target=>Cities.SelectedItem as City;
    internal DisasterTestWindow()
    {
        Title="개발자 도구 (로컬 빌드)";Width=650;Height=740;MinWidth=580;MinHeight=640;Background=CardSkin.Parchment;Foreground=CardSkin.Ink;
        WindowStartupLocation=W.WindowStartupLocation.CenterOwner;
        var root=new WC.DockPanel{Margin=new W.Thickness(20)};var form=new WC.StackPanel();WC.DockPanel.SetDock(form,WC.Dock.Top);root.Children.Add(form);root.Children.Add(Log);
        Tabs.Items.Add(new WC.TabItem{Header="재난 테스트",Content=root,Background=CardSkin.Parchment,Foreground=CardSkin.Ink,Padding=new W.Thickness(12,6,12,6)});Tabs.SelectedIndex=0;Content=Tabs;
        form.Children.Add(new WC.TextBlock{Text="재난 테스트",FontSize=21,Margin=new W.Thickness(0,0,0,14)});
        form.Children.Add(new WC.TextBlock{Text="대상 도시 · 소유 세력"});form.Children.Add(Cities);form.Children.Add(Refresh);
        form.Children.Add(new WC.TextBlock{Text="재난",Margin=new W.Thickness(0,12,0,0)});form.Children.Add(Kind);
        form.Children.Add(new WC.TextBlock{Text="표시 전용: 게임을 일시정지하고 게임 창을 선택하면 원본 알림·그림·효과음과 지도 표식을 확인합니다. 피해는 없습니다.\n실제 피해: 다음 정상 1/11/21일에 한 번 적용합니다. 발생 확률·기상만 지정하며 지형 조건과 현재 피해 수치를 사용합니다. 모든 세력을 선택할 수 있습니다.\n날짜 점프·되돌림·불러오기 감지 시 취소합니다. 적용된 피해는 자동으로 되돌리지 않습니다.",TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,0,0,12)});
        var actions=new WC.WrapPanel();foreach(var button in new[]{Preview,Damage,Stop,ReadText}){button.Margin=new W.Thickness(0,0,8,8);actions.Children.Add(button);}form.Children.Add(actions);form.Children.Add(Status);
        Cities.SelectionChanged+=(_,_)=>SelectionChanged();Kind.SelectionChanged+=(_,_)=>SelectionChanged();
        var diagnostic = new WC.DockPanel { Margin = new W.Thickness(20) };
        var instructions = new WC.StackPanel();
        instructions.Children.Add(new WC.TextBlock { Text = "게임을 일시정지한 뒤 실행하세요. 개성 목록의 열 정의를 두 번 읽어 기준 데이터와 비교합니다.\n게임 메모리·저장 파일을 변경하지 않으며, 한 번 읽고 종료합니다. 서로 다르게 읽히면 미확정 결과로 기록합니다.", TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 14) });
        ReadTraits.Margin = new W.Thickness(0, 0, 0, 14); instructions.Children.Add(ReadTraits);
        WC.DockPanel.SetDock(instructions, WC.Dock.Top); diagnostic.Children.Add(instructions); diagnostic.Children.Add(TraitLog);
        Tabs.Items.Add(new WC.TabItem { Header = "개성 목록 진단", Content = diagnostic, Background = CardSkin.Parchment, Foreground = CardSkin.Ink, Padding = new W.Thickness(12, 6, 12, 6) });
        var compatibility=new WC.DockPanel{Margin=new W.Thickness(20)};
        var heading=new WC.StackPanel();
        heading.Children.Add(new WC.TextBlock{Text="게임을 일시정지한 뒤 실행하세요. EXE·클래스·테이블·기능별 검사와 Native 원본 코드·권한을 두 번 읽어 JSON에 기록합니다. 게임 메모리·날짜·설정 원본을 변경하지 않습니다. DLL 주입·Hook·원격 호출은 없습니다. 연결 변경·값 변화·취소 시 미확정으로 중단합니다. Native 실행 가능 여부는 이 진단만으로 확정하지 않습니다.",TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,0,0,14)});
        var reads=new WC.WrapPanel();foreach(var b in new[]{ReadCompatibility,CancelCompatibility}){b.Margin=new W.Thickness(0,0,8,14);reads.Children.Add(b);}heading.Children.Add(reads);
        WC.DockPanel.SetDock(heading,WC.Dock.Top);compatibility.Children.Add(heading);compatibility.Children.Add(CompatibilityLog);
        Tabs.Items.Add(new WC.TabItem{Header="호환성 진단",Content=compatibility,Background=CardSkin.Parchment,Foreground=CardSkin.Ink,Padding=new W.Thickness(12,6,12,6)});
    }
    private bool available;
    private void SelectionChanged(){Preview.IsEnabled=Damage.IsEnabled=available&&Target is not null&&Kind.SelectedIndex>=0;}
    internal void SetAvailable(bool ready,bool stop){available=ready;Cities.IsEnabled=Kind.IsEnabled=Refresh.IsEnabled=ReadText.IsEnabled=ReadTraits.IsEnabled=ready;Stop.IsEnabled=stop;SelectionChanged();}
    internal void AppendTraitDiagnostic(string text){TraitLog.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+text+System.Environment.NewLine);TraitLog.ScrollToEnd();}
    internal void SetCompatibilityAvailable(bool ready,bool reading){ReadCompatibility.IsEnabled=ready;CancelCompatibility.IsEnabled=reading;}
    internal void AppendCompatibility(string text){CompatibilityLog.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+text+System.Environment.NewLine);CompatibilityLog.ScrollToEnd();}
    internal void SetCities(GameSnapshot data){int selected=Target?.Id??1;Cities.ItemsSource=data.Cities.Where(c=>c.CityId is >=1 and <=46).Select(c=>new City(c.CityId,data.ForceOfCity(c.CityId),c.Name+" · "+data.ForceName(data.ForceOfCity(c.CityId)))).ToArray();Cities.SelectedItem=Cities.Items.Cast<City>().FirstOrDefault(c=>c.Id==selected);}
    internal void Append(string text){Log.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+text+System.Environment.NewLine);Log.ScrollToEnd();}
    internal void SetReceipt(EnvironmentStatus? status)
    {
        if(status is{Phase:EnvironmentPhase.Faulted}){Status.Text="실행/복구 실패: "+status.Error;return;}
        if(status?.Receipt is not {} r){Status.Text="게임 연결 후 도시와 재난을 선택하세요.";return;}
        string stage=r.TestState switch{1=>"게임 전경·일시정지 대기",2=>"표시 전용 테스트 중 · 피해 없음",3=>"다음 정상 1/11/21일 경계 대기",4=>"실제 피해 처리 완료 (조건에 따라 피해 0 가능)",5=>"취소·표시 종료",6=>"실행 거부",_=>"테스트 대기"};
        Status.Text=$"{stage}\n게임 날짜: {EnvironmentProtocol.Date(r.Date)} · 피해 기록: {r.LastEvent} · 알림 대기: {r.NoticesPending}"+
            (r.TestError!=0?$"\n종료 사유 코드: {r.TestError}":"")+
            (r.DiagnosticSoundAttempted?$"\n효과음: {SoundName(r.DiagnosticSoundId)} · ID={r.DiagnosticSoundId} · channel={r.DiagnosticSoundChannel}\n{SoundState(r.DiagnosticSoundState)} · 실제 청취는 직접 확인해야 합니다.":r.DiagnosticAudioMissing?"\n원본 음향 객체가 없어 재생하지 않았습니다.":"");
    }
    private static string SoundState(int state)=>state switch{1=>"재생 소유 확인(최대 6초)",2=>"해당 재생 중단 확인",3=>"재생 소유 변경 · 다른 소리는 중단하지 않음",4=>"원본 재생 요청 거부",5=>"원본 음향 객체 없음",6=>"channel 반환 · 재생 handle 소유 미확인",7=>"중단 확인 대기",_=>"재생 상태 미확인"};
    internal static string Losses(int kind,EnvironmentValues v)=>kind switch{
        0=>$"병력 {v.FloodTroopsPercent}% · 사기 {v.FloodMoraleLoss} · 병량 {v.FloodFoodPercent}%",
        1=>$"병력 {v.WildfireTroopsPercent}% · 사기 {v.WildfireMoraleLoss} · 병량 {v.WildfireFoodPercent}%",
        2=>$"병량 {v.DroughtFoodPercent}%",3=>$"병력 {v.BlizzardTroopsPercent}% · 사기 {v.BlizzardMoraleLoss} · 병량 {v.BlizzardFoodPercent}%",
        _=>$"병력 {v.HeatwaveTroopsPercent}% · 사기 {v.HeatwaveMoraleLoss} · 병량 {v.HeatwaveFoodPercent}%"};
}
#endif
