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
    internal readonly WC.CheckBox PictureDiagnostic=new(){Content=L.DeveloperUi.PictureDiagnostic,IsChecked=true,ToolTip=L.DeveloperUi.PictureDiagnosticTip,Margin=new W.Thickness(0,0,0,10)};
    internal readonly WC.TextBlock Status=new(){TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,12,0,12)};
    internal readonly WC.TextBox Log=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,MinHeight=150};
    internal readonly PlateButton ReadTraits=new(L.DeveloperUi.ReadTraits);
    internal readonly PlateButton PerformanceStart=new(L.DeveloperUi.PerformanceStart),PerformanceStop=new(L.DeveloperUi.PerformanceStop){IsEnabled=false};
    internal readonly WC.TextBox PerformanceLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
    internal readonly WC.TextBox TraitLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
    internal readonly WC.TabControl Tabs=new(){Background=CardSkin.Parchment,BorderBrush=CardSkin.Gold,Padding=new W.Thickness(0)};
    internal readonly PlateButton ReadCompatibility=new(L.DeveloperUi.ReadCompatibility),CancelCompatibility=new(L.DeveloperUi.CancelDiagnostic);
    internal readonly PlateButton ProfiledRulesEdit = new(L.DeveloperUi.ProfiledRuleRoundTrip) { IsEnabled = false };
    internal readonly PlateButton ProfiledAiEdit = new(L.DeveloperUi.ProfiledAiRoundTrip) { IsEnabled = false };
    internal readonly PlateButton ProfiledAiExtension = new(L.DeveloperUi.ProfiledAiExtensionRoundTrip) { IsEnabled = false };
    internal readonly PlateButton ProfiledRuntimeRules = new(L.DeveloperUi.ProfiledRuntimeRulesRoundTrip) { IsEnabled = false };
    internal readonly PlateButton NativeCommandSuite = new(L.DeveloperUi.NativeSuite) { IsEnabled = false };
    internal readonly PlateButton ProfiledRender = new(L.DeveloperUi.ProfiledRenderCheck) { IsEnabled = false };
    internal readonly WC.TextBox CompatibilityLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
    internal City? Target=>Cities.SelectedItem as City;
    internal readonly PlateButton ReadGeography=new(L.DeveloperUi.ReadGeography),EditGeography=new(L.DeveloperUi.EditGeography);
    internal readonly PlateButton ProfiledCityEdit=new(L.DeveloperUi.ProfiledEditRoundTrip){IsEnabled=false};
    internal readonly PlateButton ProfiledFieldsEdit=new(L.DeveloperUi.ProfiledFieldsRoundTrip){IsEnabled=false};
    internal readonly PlateButton CompleteEditsPreview = new(L.DeveloperUi.CompleteEditsPreview) { IsEnabled = false };
    internal readonly PlateButton CompleteEditsRun = new(L.DeveloperUi.CompleteEditsRun) { IsEnabled = false };
    internal readonly PlateButton CompleteEditsStop = new(L.DeveloperUi.CompleteEditsStop) { IsEnabled = false };
    internal readonly PlateButton HeldProvocationLoad = new(L.DeveloperUi.ProvocationLoad);
    internal readonly PlateButton HeldProvocationApply = new(L.DeveloperUi.ProvocationApply) { IsEnabled = false };
    internal readonly PlateButton HeldProvocationFinish = new(L.DeveloperUi.ProvocationFinish) { IsEnabled = false };
    internal readonly PlateButton HeldProvocationRestore = new(L.DeveloperUi.ProvocationRestore) { IsEnabled = false };
    internal readonly WC.TextBox HeldProvocationLog = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = W.TextWrapping.Wrap, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    private sealed record CompleteUnitChoice(int Commander, string Name);
    internal readonly WC.ComboBox CompleteProvokeUnits = new() { DisplayMemberPath = "Name", Margin = new W.Thickness(0, 0, 0, 12) };
    internal int? CompleteProvokeCommander => CompleteProvokeUnits.SelectedItem is CompleteUnitChoice { Commander: > 0 } choice ? choice.Commander : null;
    internal readonly WC.TextBox CompleteEditsLog = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = W.TextWrapping.Wrap, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    internal readonly WC.TextBox GeographyLog=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};
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
        form.Children.Add(PictureDiagnostic);
        var actions=new WC.WrapPanel();foreach(var button in new[]{Preview,Damage,Stop,ReadText,ProfiledRender}){button.Margin=new W.Thickness(0,0,8,8);actions.Children.Add(button);}form.Children.Add(actions);form.Children.Add(Status);
        Cities.SelectionChanged+=(_,_)=>SelectionChanged();Kind.SelectionChanged+=(_,_)=>SelectionChanged();
        var diagnostic = new WC.DockPanel { Margin = new W.Thickness(20) };
        var instructions = new WC.StackPanel();
        instructions.Children.Add(new WC.TextBlock { Text = L.DeveloperUi.TraitInstructions, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 14) });
        ReadTraits.Margin = new W.Thickness(0, 0, 0, 14); instructions.Children.Add(ReadTraits);
        WC.DockPanel.SetDock(instructions, WC.Dock.Top); diagnostic.Children.Add(instructions); diagnostic.Children.Add(TraitLog);
        Tabs.Items.Add(new WC.TabItem { Header = L.DeveloperUi.TraitTab, Content = diagnostic, Background = CardSkin.Parchment, Foreground = CardSkin.Ink, Padding = new W.Thickness(12, 6, 12, 6) });
        var performance=new WC.DockPanel{Margin=new W.Thickness(20)};var performanceActions=new WC.StackPanel();
        performanceActions.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.PerformanceInstructions,TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,0,0,12)});
        performanceActions.Children.Add(PerformanceStart);performanceActions.Children.Add(PerformanceStop);
        WC.DockPanel.SetDock(performanceActions,WC.Dock.Top);performance.Children.Add(performanceActions);performance.Children.Add(PerformanceLog);

        var compatibility=new WC.DockPanel{Margin=new W.Thickness(20)};
        var heading=new WC.StackPanel();
        heading.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.CompatibilityInstructions,TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,0,0,14)});
        var reads=new WC.WrapPanel();foreach(var b in new[]{ReadCompatibility,CancelCompatibility}){b.Margin=new W.Thickness(0,0,8,14);reads.Children.Add(b);}heading.Children.Add(reads);
        var edits=new WC.WrapPanel();
        foreach(var button in new[]{ProfiledRulesEdit,ProfiledAiEdit,ProfiledAiExtension,ProfiledRuntimeRules,NativeCommandSuite}){button.Margin=new W.Thickness(0,0,8,14);edits.Children.Add(button);}
        heading.Children.Add(edits);
        WC.DockPanel.SetDock(heading,WC.Dock.Top);compatibility.Children.Add(heading);compatibility.Children.Add(CompatibilityLog);
        Tabs.Items.Add(new WC.TabItem{Header=L.DeveloperUi.CompatibilityTab,Content=compatibility,Background=CardSkin.Parchment,Foreground=CardSkin.Ink,Padding=new W.Thickness(12,6,12,6)});
        var geography=new WC.DockPanel{Margin=new W.Thickness(20)};var geographyHeading=new WC.StackPanel();
        geographyHeading.Children.Add(new WC.TextBlock{Text=L.DeveloperUi.GeographyInstructions,TextWrapping=W.TextWrapping.Wrap,Margin=new W.Thickness(0,0,0,14)});
        foreach(var b in new[]{ReadGeography,EditGeography,ProfiledCityEdit,ProfiledFieldsEdit}){b.Margin=new W.Thickness(0,0,0,14);geographyHeading.Children.Add(b);}
        WC.DockPanel.SetDock(geographyHeading,WC.Dock.Top);geography.Children.Add(geographyHeading);geography.Children.Add(GeographyLog);
        Tabs.Items.Add(new WC.TabItem{Header=L.DeveloperUi.GeographyTab,Content=geography,Background=CardSkin.Parchment,Foreground=CardSkin.Ink,Padding=new W.Thickness(12,6,12,6)});
        Tabs.Items.Add(new WC.TabItem{Header=L.DeveloperUi.PerformanceTab,Content=performance,Background=CardSkin.Parchment,Foreground=CardSkin.Ink});
        var complete = new WC.DockPanel { Margin = new W.Thickness(20) };
        var completeHeading = new WC.StackPanel();
        completeHeading.Children.Add(new WC.TextBlock { Text = L.DeveloperUi.CompleteEditsInstructions,
            TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 14) });
        completeHeading.Children.Add(new WC.TextBlock { Text = L.DeveloperUi.CompleteSeededProvocationHint,
            TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 8) });
        completeHeading.Children.Add(CompleteProvokeUnits);
        var completeActions = new WC.WrapPanel();
        foreach (var button in new[] { CompleteEditsPreview, CompleteEditsRun, CompleteEditsStop })
        { button.Margin = new W.Thickness(0, 0, 8, 14); completeActions.Children.Add(button); }
        completeHeading.Children.Add(completeActions);
        WC.DockPanel.SetDock(completeHeading, WC.Dock.Top); complete.Children.Add(completeHeading); complete.Children.Add(CompleteEditsLog);
        Tabs.Items.Add(new WC.TabItem { Header = L.DeveloperUi.CompleteEditsTab, Content = complete,
            Background = CardSkin.Parchment, Foreground = CardSkin.Ink, Padding = new W.Thickness(12, 6, 12, 6) });
        var held = new WC.DockPanel { Margin = new W.Thickness(20) };
        var heldHeading = new WC.StackPanel();
        heldHeading.Children.Add(new WC.TextBlock { Text = L.DeveloperUi.ProvocationInstructions, TextWrapping = W.TextWrapping.Wrap });
        foreach (var button in new[] { HeldProvocationLoad, HeldProvocationApply, HeldProvocationFinish, HeldProvocationRestore })
        { button.Margin = new W.Thickness(0, 8, 0, 0); heldHeading.Children.Add(button); }
        WC.DockPanel.SetDock(heldHeading, WC.Dock.Top); held.Children.Add(heldHeading); held.Children.Add(HeldProvocationLog);
        Tabs.Items.Add(new WC.TabItem { Header = L.DeveloperUi.ProvocationTab, Content = held,
            Background = CardSkin.Parchment, Foreground = CardSkin.Ink, Padding = new W.Thickness(12, 6, 12, 6) });
    }
    private bool available,previewOnly;
    private void SelectionChanged(){Preview.IsEnabled=available&&Target is not null&&Kind.SelectedIndex>=0;Damage.IsEnabled=Preview.IsEnabled&&!previewOnly;}
    internal void SetAvailable(bool ready,bool stop,bool previewOnly=false)
    {
        if(this.previewOnly!=previewOnly)PictureDiagnostic.IsChecked=!previewOnly;
        available=ready;this.previewOnly=previewOnly;
        PictureDiagnostic.IsEnabled=ready;
        Cities.IsEnabled=Kind.IsEnabled=Refresh.IsEnabled=ready;
        ReadText.IsEnabled=ReadTraits.IsEnabled=ready&&!previewOnly;
        Stop.IsEnabled=stop;SelectionChanged();
    }
    internal void AppendTraitDiagnostic(string text){TraitLog.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+text+System.Environment.NewLine);TraitLog.ScrollToEnd();}
    internal void SetCompatibilityAvailable(bool ready,bool reading){ReadCompatibility.IsEnabled=ready;CancelCompatibility.IsEnabled=reading;}
    internal void AppendCompatibility(string text){CompatibilityLog.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+text+System.Environment.NewLine);CompatibilityLog.ScrollToEnd();}
    internal void SetCities(GameSnapshot data)
    {
        int selected=Target?.Id??1;Cities.ItemsSource=data.CityIds.Where(EnvironmentPolicy.EffectCity).Select(id=>new City(id,data.ForceOfCity(id),data.CityName(id)+" · "+data.ForceName(data.ForceOfCity(id)))).ToArray();Cities.SelectedItem=Cities.Items.Cast<City>().FirstOrDefault(c=>c.Id==selected);
        int commander = CompleteProvokeCommander ?? 0;
        CompleteProvokeUnits.ItemsSource = new[] { new CompleteUnitChoice(0, L.DeveloperUi.CompleteNoSeededProvocation) }
            .Concat(data.Units.Where(u => data.ForceOfUnit(u) == data.PlayerForce && u[UnitFields.Ailments[UnitFields.Provoked]] == 0)
                .OrderBy(u => u.Slot).Select(u => new CompleteUnitChoice(u.Commander, data.CommanderName(u.Commander)))).ToArray();
        CompleteProvokeUnits.SelectedItem = CompleteProvokeUnits.Items.Cast<CompleteUnitChoice>().FirstOrDefault(c => c.Commander == commander)
            ?? CompleteProvokeUnits.Items[0];
    }
    internal void SetGeographyAvailable(bool ready){ReadGeography.IsEnabled=EditGeography.IsEnabled=ready;}
    internal void AppendGeography(string text){GeographyLog.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+text+System.Environment.NewLine);GeographyLog.ScrollToEnd();}
    internal void AppendCompleteEdits(string text) { CompleteEditsLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + text + System.Environment.NewLine); CompleteEditsLog.ScrollToEnd(); }
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
