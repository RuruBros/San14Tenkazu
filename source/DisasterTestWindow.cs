#if LOCAL_DEVELOPER
using San14Tenkazu.EnvironmentRules;
using W=System.Windows;
using WC=System.Windows.Controls;
namespace San14Tenkazu;

internal sealed class DisasterTestWindow:W.Window
{
    internal static readonly string[] Names=[L.DeveloperUi.Flood,L.DeveloperUi.Wildfire,L.DeveloperUi.Drought,L.DeveloperUi.Blizzard,L.DeveloperUi.Heatwave];
    internal static string SoundName(int id)=>id switch{0x65=>L.DeveloperUi.FloodSound,0x6d=>L.DeveloperUi.FireSound,0x5f=>L.DeveloperUi.WindSound,0x60=>L.DeveloperUi.ColdWindSound,0x77=>L.DeveloperUi.InsectSound,_=>L.DeveloperUi.UnknownSound};
    internal sealed record City(int Id,int Force,string Label){public override string ToString()=>Label;}
    internal readonly WC.ComboBox Kind=new(){ItemsSource=Names,SelectedIndex=0,Margin=new W.Thickness(0,4,0,12)};
    internal readonly WC.ComboBox Cities=new(){Margin=new W.Thickness(0,4,0,12)};
    internal readonly PlateButton Preview=new(L.DeveloperUi.Preview){ToolTip=L.DeveloperUi.PreviewTip},Damage=new(L.DeveloperUi.ScheduleDamage),Stop=new(L.DeveloperUi.CancelTest),Refresh=new(L.DeveloperUi.RefreshCities);
    internal readonly PlateButton ReadText=new(L.DeveloperUi.ReadNotice){ToolTip=L.DeveloperUi.ReadNoticeTip};
    internal readonly WC.TextBlock Status=new(){TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,12,0,12)};
    internal readonly WC.TextBox Log=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,MinHeight=150};
    internal readonly PlateButton ReadTraits=new(L.DeveloperUi.ReadTraits);
    internal readonly WC.TextBox TraitLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
    internal readonly WC.TabControl Tabs=new(){Background=CardSkin.Parchment,BorderBrush=CardSkin.Gold,Padding=new W.Thickness(0)};
    internal readonly PlateButton ReadCompatibility=new(L.DeveloperUi.ReadCompatibility),CancelCompatibility=new(L.DeveloperUi.CancelDiagnostic);
    internal readonly WC.TextBox CompatibilityLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
    internal City? Target=>Cities.SelectedItem as City;
    internal DisasterTestWindow()
    {
        Title=L.DeveloperUi.WindowTitle;Width=650;Height=740;MinWidth=580;MinHeight=640;Background=CardSkin.Parchment;Foreground=CardSkin.Ink;
        WindowStartupLocation=W.WindowStartupLocation.CenterOwner;
        var root=new WC.DockPanel{Margin=new W.Thickness(20)};var form=new WC.StackPanel();WC.DockPanel.SetDock(form,WC.Dock.Top);root.Children.Add(form);root.Children.Add(Log);
        Tabs.Items.Add(new WC.TabItem{Header=L.DeveloperUi.DisasterTab,Content=root,Background=CardSkin.Parchment,Foreground=CardSkin.Ink,Padding=new W.Thickness(12,6,12,6)});Tabs.SelectedIndex=0;Content=Tabs;
        form.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.DisasterTab,FontSize=21,Margin=new W.Thickness(0,0,0,14)});
        form.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.CityHeading});form.Children.Add(Cities);form.Children.Add(Refresh);
        form.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.DisasterKind,Margin=new W.Thickness(0,12,0,0)});form.Children.Add(Kind);
        form.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.DisasterInstructions,TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,0,0,12)});
        var actions=new WC.WrapPanel();foreach(var button in new[]{Preview,Damage,Stop,ReadText}){button.Margin=new W.Thickness(0,0,8,8);actions.Children.Add(button);}form.Children.Add(actions);form.Children.Add(Status);
        Cities.SelectionChanged+=(_,_)=>SelectionChanged();Kind.SelectionChanged+=(_,_)=>SelectionChanged();
        var diagnostic = new WC.DockPanel { Margin = new W.Thickness(20) };
        var instructions = new WC.StackPanel();
        instructions.Children.Add(new WC.TextBlock { Text = L.DeveloperUi.TraitInstructions, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 14) });
        ReadTraits.Margin = new W.Thickness(0, 0, 0, 14); instructions.Children.Add(ReadTraits);
        WC.DockPanel.SetDock(instructions, WC.Dock.Top); diagnostic.Children.Add(instructions); diagnostic.Children.Add(TraitLog);
        Tabs.Items.Add(new WC.TabItem { Header = L.DeveloperUi.TraitTab, Content = diagnostic, Background = CardSkin.Parchment, Foreground = CardSkin.Ink, Padding = new W.Thickness(12, 6, 12, 6) });
        var compatibility=new WC.DockPanel{Margin=new W.Thickness(20)};
        var heading=new WC.StackPanel();
        heading.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.CompatibilityInstructions,TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,0,0,14)});
        var reads=new WC.WrapPanel();foreach(var b in new[]{ReadCompatibility,CancelCompatibility}){b.Margin=new W.Thickness(0,0,8,14);reads.Children.Add(b);}heading.Children.Add(reads);
        WC.DockPanel.SetDock(heading,WC.Dock.Top);compatibility.Children.Add(heading);compatibility.Children.Add(CompatibilityLog);
        Tabs.Items.Add(new WC.TabItem{Header=L.DeveloperUi.CompatibilityTab,Content=compatibility,Background=CardSkin.Parchment,Foreground=CardSkin.Ink,Padding=new W.Thickness(12,6,12,6)});
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
        if(status is{Phase:EnvironmentPhase.Faulted}){Status.Text=L.DeveloperUi.ExecutionRecoveryFailed+status.Error;return;}
        if(status?.Receipt is not {} r){Status.Text=L.DeveloperUi.ChooseCityAndDisaster;return;}
        string stage=r.TestState switch{1=>L.DeveloperUi.WaitingForPausedForeground,2=>L.DeveloperUi.PreviewNoDamage,3=>L.DeveloperUi.WaitingForDateBoundary,4=>L.DeveloperUi.DamageCompleted,5=>L.DeveloperUi.PreviewStopped,6=>L.DeveloperUi.TestRejected,_=>L.DeveloperUi.TestWaiting};
        Status.Text=L.DeveloperUi.TestReceipt($"{stage}", $"{EnvironmentProtocol.Date(r.Date)}", $"{r.LastEvent}", $"{r.NoticesPending}")+
            (r.TestError!=0?L.DeveloperUi.TestErrorCode($"{r.TestError}"):"")+
            (r.DiagnosticSoundAttempted?L.DeveloperUi.TestSoundReceipt($"{SoundName(r.DiagnosticSoundId)}", $"{r.DiagnosticSoundId}", $"{r.DiagnosticSoundChannel}", $"{SoundState(r.DiagnosticSoundState)}"):r.DiagnosticAudioMissing?L.DeveloperUi.SoundNotPlayed:"");
    }
    private static string SoundState(int state)=>state switch{1=>L.DeveloperUi.SoundOwned,2=>L.DeveloperUi.SoundStopped,3=>L.DeveloperUi.SoundOwnerChanged,4=>L.DeveloperUi.SoundRejected,5=>L.DeveloperUi.SoundMissing,6=>L.DeveloperUi.SoundHandleUnconfirmed,7=>L.DeveloperUi.SoundStopPending,_=>L.DeveloperUi.SoundUnknown};
    internal static string Losses(int kind,EnvironmentValues v)=>kind switch{
        0=>L.DeveloperUi.FloodLosses($"{v.FloodTroopsPercent}", $"{v.FloodMoraleLoss}", $"{v.FloodFoodPercent}"),
        1=>L.DeveloperUi.FloodLosses($"{v.WildfireTroopsPercent}", $"{v.WildfireMoraleLoss}", $"{v.WildfireFoodPercent}"),
        2=>L.DeveloperUi.DroughtLosses($"{v.DroughtFoodPercent}"),3=>L.DeveloperUi.FloodLosses($"{v.BlizzardTroopsPercent}", $"{v.BlizzardMoraleLoss}", $"{v.BlizzardFoodPercent}"),
        _=>L.DeveloperUi.FloodLosses($"{v.HeatwaveTroopsPercent}", $"{v.HeatwaveMoraleLoss}", $"{v.HeatwaveFoodPercent}")};
}
#endif
