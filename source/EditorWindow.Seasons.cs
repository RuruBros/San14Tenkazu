using System.ComponentModel;
using San14Tenkazu.Seasons;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private readonly WC.RadioButton seasonOn=SeasonChoice(L.Seasons.OnChoice,L.Seasons.OnTip),
        seasonOff=SeasonChoice(L.Seasons.OffChoice,L.Seasons.OffTip);
    private readonly WC.TextBlock seasonStatus=new(){Foreground=CardSkin.InkOnDark,VerticalAlignment=W.VerticalAlignment.Center,
        TextTrimming=W.TextTrimming.CharacterEllipsis};
    private readonly string seasonStorageRoot;
    private SeasonController? seasonController;
    private SeasonJournal? seasonJournal;
    private string? seasonUiError;
    private SeasonStatus? seasonDisplayedStatus;
    private System.Windows.Threading.DispatcherTimer? seasonStatusTimer;
    private bool seasonStopping,seasonStarting,seasonDisconnecting,seasonClosing,seasonCloseApproved;

    private static readonly WC.ControlTemplate SeasonRadioTemplate=(WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='RadioButton'>"+
        "<Border x:Name='edge' Padding='{TemplateBinding Padding}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='3'>"+
        "<StackPanel Orientation='Horizontal'><Grid Width='13' Height='13' VerticalAlignment='Center' Margin='0,0,5,0'>"+
        "<Ellipse Stroke='{TemplateBinding Foreground}' StrokeThickness='1'/><Ellipse x:Name='dot' Margin='3' Fill='{TemplateBinding Foreground}' Visibility='Collapsed'/>"+
        "</Grid><ContentPresenter VerticalAlignment='Center'/></StackPanel></Border>"+
        "<ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='dot' Property='Visibility' Value='Visible'/></Trigger>"+
        "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='edge' Property='BorderBrush' Value='{Binding Foreground, RelativeSource={RelativeSource TemplatedParent}}'/></Trigger>"+
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='edge' Property='Opacity' Value='0.45'/></Trigger>"+
        "</ControlTemplate.Triggers></ControlTemplate>");

    private static WC.RadioButton SeasonChoice(string text,string tip)=>new()
    {
        Content=text,GroupName="SeasonRequest",Foreground=CardSkin.Gold,Template=SeasonRadioTemplate,
        Padding=new W.Thickness(4,5,4,5),VerticalAlignment=W.VerticalAlignment.Center,ToolTip=tip,
        Cursor=System.Windows.Input.Cursors.Hand
    };

    private WC.StackPanel BuildSeasonControls()
    {
        var row=new WC.StackPanel{Orientation=WC.Orientation.Horizontal,VerticalAlignment=W.VerticalAlignment.Center};
        row.Children.Add(new WC.TextBlock{Text=L.Seasons.Label,Foreground=CardSkin.Ink,
            VerticalAlignment=W.VerticalAlignment.Center,Margin=new W.Thickness(0,0,4,0)});
        row.Children.Add(seasonOn);row.Children.Add(seasonOff);
        WC.ToolTipService.SetShowOnDisabled(seasonOn,true);WC.ToolTipService.SetShowOnDisabled(seasonOff,true);
        // These radios edit the window draft. Native requests run only through Save and Apply.
        seasonOn.Click+=(_,_)=>environmentMenu?.SelectionChanged();
        seasonOff.Click+=(_,_)=>environmentMenu?.SelectionChanged();
        UpdateSeasonControls();return row;
    }

    // Saved enabled settings request Native startup only when eligible for application.
    private sealed class RecordedSeasonDateSource(GameIdentity identity,Action<SeasonSnapshot> record):ISeasonDateSource
    {
        private readonly GameSeasonDateSource source=new(identity);
        public async ValueTask<SeasonSnapshot> ReadAsync(CancellationToken token)
        {
            var value=await source.ReadAsync(token).ConfigureAwait(false);record(value);return value;
        }
    }
    private void AttachSeasons(GameIdentity identity)
    {
        if(seasonController is not null)throw new InvalidOperationException("Retain the previous season controller until recovery");
        seasonController=new(new RecordedSeasonDateSource(identity,value=>seasonJournal?.Record(
            $"Date read: pid={value.Identity.ProcessId}, date={value.Date.Year}/{value.Date.Month}/{value.Date.Day}, "+
            $"scene=0x{value.Identity.Scene:x}, device=0x{value.Identity.Device:x}, context=0x{value.Identity.Context:x}")),new SeasonPackagedBackend(identity,
            Path.Combine(seasonStorageRoot,"native"),RecordSeason,()=>GameSeasonSources.Read(identity,RecordSeason),
            sceneFromSources:sources=>GameSeasonScene.Read(identity,sources,RecordSeason)),scope:SeasonScope.Full);
        seasonController.StatusChanged+=OnSeasonStatusChanged;UpdateSeasonControls();
    }

    private void RecordSeason(string message)
    {
        try{seasonJournal?.Record(message);}
        catch(Exception error){message+="; "+L.Seasons.LogFailed(error.Message);}
        string line=message;
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(()=>AddLog(line));
    }

    private void OnSeasonStatusChanged(SeasonStatus value)
    {
        var color=value.TreeColor;
        RecordSeason($"Controller: scope={value.Scope}, requested={value.Requested}, phase={value.Phase}, generation={value.Generation}, "+
            $"date={value.Date?.Year}/{value.Date?.Month}/{value.Date?.Day}, restored={value.OriginalRestored}, "+
            $"epoch={color?.CompletedEpoch}, draws={color?.Draws}, swaps/restores={color?.Swaps}/{color?.Restores}, error={value.Error}");
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(UpdateSeasonControls);
    }

    private static bool NeedsSeasonStop(SeasonStatus value)=>value.Phase is not (SeasonPhase.Off or SeasonPhase.Faulted) ||
        !value.OriginalRestored;

    private void UpdateSeasonControls()
    {
        var value=seasonController?.Status ?? new(SeasonPhase.Off,null,0,true,Scope:SeasonScope.Full);
        bool stop=NeedsSeasonStop(value);
        // Ordinary editor work does not disable OFF or recovery retry.
        bool available=!seasonStopping&&!seasonDisconnecting&&!seasonClosing&&!environmentChanging;
        bool connected=game is {Verified:true}&&snapshot is not null;
        seasonOn.IsEnabled=available&&!seasonStarting&&!busy&&connected&&(!stop||value.Requested);
        seasonOff.IsEnabled=available&&(stop||(!seasonStarting&&!busy&&connected));
        // The selected radio reports the user's request. The adjacent phase and receipt
        // report actual completion, including dates still awaiting a real Draw.
        if(environmentMenu is null){
            seasonOn.IsChecked=value.Requested&&value.Scope!=SeasonScope.Weather;
            seasonOff.IsChecked=seasonOn.IsChecked!=true;
        }
        seasonOff.ToolTip=value.Phase==SeasonPhase.Faulted&&!value.OriginalRestored?
            L.Seasons.RecoverButton+"\n"+L.Seasons.RecoverTip:L.Seasons.OffTip;
        // Keep the last confirmed, explicitly dated label during a short
        // background update. A real wait becomes visible after 250 ms;
        // the controller receipt and tooltip always describe the current state.
        bool briefUpdate=seasonUiError is null&&value.Phase==SeasonPhase.Applying&&value.Requested&&
            seasonDisplayedStatus is {Phase:SeasonPhase.PartialOn or SeasonPhase.On} displayed&&displayed.Generation==value.Generation;
        if(briefUpdate)
        {
            if(seasonStatusTimer is null)
            {
                seasonStatusTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
                seasonStatusTimer.Tick+=(_,_)=>
                {
                    seasonStatusTimer.Stop();seasonDisplayedStatus=seasonController?.Status;UpdateSeasonControls();
                };
            }
            if(!seasonStatusTimer.IsEnabled)seasonStatusTimer.Start();
        }
        else{seasonStatusTimer?.Stop();seasonDisplayedStatus=value;}
        seasonStatus.Text=seasonUiError is {} issue?L.Seasons.Failed(value.OriginalRestored?L.Seasons.Restored:L.Seasons.Unrestored,issue):SeasonStatusText(seasonDisplayedStatus??value);
        string? detail=seasonUiError??value.Error;
        seasonStatus.ToolTip=detail is {} error?SeasonStatusText(value)+"\n"+error:SeasonStatusText(value)+"\n"+L.Seasons.Tip;
        seasonStatus.Foreground=value.Phase==SeasonPhase.Faulted||seasonUiError is not null?CardSkin.Gold:CardSkin.InkOnDark;
        UpdateEnvironmentControls();
    }

    private string SeasonStatusText(SeasonStatus value)
    {
        string dated=value.Date is {} date?L.Seasons.Dated(SeasonName(date),date.Year,date.Month,date.Day):"";
        return value.Phase switch
        {
            SeasonPhase.Off=>game is null?L.Seasons.ConnectFirst:game.Value.Verified?L.Seasons.OffStatus:L.Seasons.VerifiedOnly,
            SeasonPhase.Connecting=>L.Seasons.Connecting,
            SeasonPhase.Applying=>L.Seasons.Applying(dated),
            SeasonPhase.PartialOn=>L.Seasons.TreeApplied(dated),
            SeasonPhase.On=>value.Scope==SeasonScope.Weather?L.Environment.WeatherApplied(dated):L.Seasons.FullApplied(dated),
            SeasonPhase.Restoring=>L.Seasons.Restoring,
            SeasonPhase.Reconnecting=>L.Seasons.Reconnecting,
            SeasonPhase.Faulted=>L.Seasons.Failed(value.OriginalRestored?L.Seasons.Restored:L.Seasons.Unrestored,value.Error),
            _=>throw new InvalidOperationException("Unknown season phase")
        };
    }

    private static string SeasonName(SeasonDate date)
    {
        string Name(int month)=>month switch{>=3 and <=5=>L.Seasons.Spring,>=6 and <=8=>L.Seasons.Summer,
            >=9 and <=11=>L.Seasons.Autumn,_=>L.Seasons.Winter};
        var blend=SeasonTimeline.At(date);string current=Name(blend.Month),next=Name(blend.NextMonth);
        return current!=next&&blend.Fraction>0?L.Seasons.Transition(current,next,(int)Math.Round(blend.Fraction*100)):current;
    }

    private async Task SetSeasonsAsync(bool requested)
    {
        UpdateSeasonControls();
        if(seasonController is not {} controller||seasonStopping||seasonDisconnecting||seasonClosing)return;
        if(!requested){
            if(EnvironmentRenderingActive&&controller.Status.Requested){await controller.SetSeasonalAsync(false);UpdateSeasonControls();}
            else if(NeedsSeasonStop(controller.Status))await StopSeasonsAsync(false);return;
        }
        if(seasonStarting||(busy&&!environmentChanging)||game is not {Verified:true}||snapshot is null)return;
        if(controller.Status.Requested){await controller.SetSeasonalAsync(true);UpdateSeasonControls();return;}
        if(NeedsSeasonStop(controller.Status))return;
        seasonUiError=null;seasonStarting=true;UpdateSeasonControls();
        try
        {
            seasonJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"logs"));
            RecordSeason($"Season ON requested; scope=Full; journal={seasonJournal.Path}");
            await controller.SetSeasonalAsync(true);await controller.StartAsync();
        }
        catch(Exception error){seasonUiError=error.Message;RecordSeason(error.ToString());}
        finally{seasonStarting=false;UpdateSeasonControls();}
    }

    private async Task<bool> StopSeasonsAsync(bool release)
    {
        if(seasonController is not {} controller)return true;
        seasonUiError=null;seasonStopping=true;UpdateSeasonControls();
        try
        {
            await controller.StopAsync();
            if(!controller.Status.OriginalRestored||controller.Status.Phase!=SeasonPhase.Off)return false;
            if(release)
            {
                await controller.DisposeAsync();controller.StatusChanged-=OnSeasonStatusChanged;seasonController=null;
                if(seasonJournal is {} journal)
                {
                    seasonJournal=null;
                    try{await journal.DisposeAsync();}catch(Exception error){AddLog(L.Seasons.LogFailed(error.Message));}
                }
            }
            return true;
        }
        catch(Exception error){seasonUiError=error.Message;RecordSeason(error.ToString());return false;}
        finally{seasonStopping=false;UpdateSeasonControls();}
    }

    private async void OnEditorClosing(object? sender,CancelEventArgs e)
    {
        if (gamePreparationCancellation is { } preparation) { preparation.Cancel(); e.Cancel = true; return; }
#if LOCAL_DEVELOPER
        if(compatibilityCancellation is {} diagnostic){diagnostic.Cancel();e.Cancel=true;return;}
#endif
        if(runtimeRulesChanging){e.Cancel=true;return;}
        if(seasonCloseApproved){registeredEditor?.AllowOwnerClose();return;}
        if(seasonClosing||seasonDisconnecting){e.Cancel=true;return;}
        if((HasPendingEdits||registeredEditor?.HasPending==true)&&!Confirm(L.Window.DiscardAndClose))
        {e.Cancel=true;return;}
        if(environmentChanging){e.Cancel=true;return;}
        if(seasonController is null&&environmentController is null){seasonStatusTimer?.Stop();registeredEditor?.AllowOwnerClose();return;}
        bool wasBusy=busy;
        e.Cancel=true;seasonClosing=true;liveTimer.Stop();scanCancellation?.Cancel();SetBusy(true,scanning:false);
        try
        {
            // Return from WPF's Closing event before a completed stop can close again.
            await System.Windows.Threading.Dispatcher.Yield();
            if(await StopEnvironmentAsync(true)&&await StopSeasonsAsync(true))
            {
                seasonCloseApproved=true;Close();
            }
            else{AddLog(L.Seasons.CloseBlocked);if(game is not null)liveTimer.Start();}
        }
        finally{seasonClosing=false;SetBusy(wasBusy,scanning:false);}
    }
}
