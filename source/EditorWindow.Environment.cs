using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;
using W=System.Windows;
using WC=System.Windows.Controls;
namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private readonly PlateButton environmentButton=new(L.Environment.Menu){ToolTip=L.Environment.MenuTip};
    private EnvironmentSettingsWindow? environmentMenu;
    private WC.StackPanel? seasonChoices;
    private EnvironmentSettingsFile environmentFile=null!;
    private EnvironmentSettings environmentSettings=EnvironmentSettings.New();
    private string environmentStamp="",environmentUiStatus="";
    private string? environmentUiError;
    private EnvironmentController? environmentController;
    private SeasonJournal? environmentJournal;
    private bool environmentChanging;
    private bool environmentChecking;
    private GameIdentity? environmentFailure;
    private EnvironmentSettings? environmentApplied;
    private Func<GameIdentity,GameLayout,GamePace> environmentReadPace=GameMemory.ReadPace;
    private readonly List<string> environmentEvents=[];

    private PlateButton BuildEnvironmentMenu()
    {
        environmentFile=new(Path.Combine(seasonStorageRoot,"environment","settings.json"));
        ReadEnvironmentSettings();seasonChoices=BuildSeasonControls();
        seasonOn.Foreground=seasonOff.Foreground=CardSkin.Ink;
        environmentButton.Click+=OpenEnvironmentMenu;return environmentButton;
    }
    private void ReadEnvironmentSettings()
    {
        try
        {
            environmentUiError=null;
            var read=environmentFile.Read();environmentSettings=read.Settings??environmentSettings;environmentStamp=read.Stamp;
            environmentUiStatus=environmentApplied==environmentSettings&&EnvironmentMatches(environmentSettings)?L.Environment.AppliedTogether:L.Environment.NotApplied;
        }
        catch(Exception error){environmentUiError=error.Message;environmentUiStatus=L.Environment.Failed(error.Message);}
    }
    private void OpenEnvironmentMenu()
    {
        if(environmentMenu is {} existing){existing.Activate();return;}
        ReadEnvironmentSettings();
        var menu=new EnvironmentSettingsWindow(seasonChoices!,seasonOn,seasonOff,environmentSettings,environmentEvents){Owner=this};environmentMenu=menu;
        menu.Apply.Click+=async()=>await ApplyEnvironmentAsync(menu.Selection(environmentSettings));
        menu.DisableAll.Click+=async()=>
        {
            menu.ClearSelection();await ApplyEnvironmentAsync(menu.Selection(environmentSettings));
        };
        menu.Closing+=(_,e)=>{if(environmentChanging)e.Cancel=true;};
        menu.Closed+=(_,_)=>environmentMenu=null;
        UpdateEnvironmentControls();menu.Show();
    }
    private void RecordEnvironment(string message)
    {
        environmentJournal?.Record(message);
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(()=>{AddLog(message);
#if LOCAL_DEVELOPER
            developerMenu?.Append(message);
#endif
        });
    }
    private void OnEnvironmentStatus(EnvironmentStatus value)
    {
#if LOCAL_DEVELOPER
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(()=>developerNoticeDiagnostic?.Observe(value));
#endif
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(()=>
        {
            if(value.Phase==EnvironmentPhase.Faulted){environmentFailure=game;environmentUiError=value.Error;}
            UpdateEnvironmentControls();
        });
#if LOCAL_DEVELOPER
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(async()=>{
            if(developerRendering&&!developerChanging&&(value.Phase is EnvironmentPhase.Off or EnvironmentPhase.Faulted||value.Receipt?.TestState is 5 or 6)){
                if(developerNoticeDiagnostic is not null){await CancelDeveloperTest();return;}
                developerRendering=false;try{await SyncWeatherRenderingAsync();}catch(Exception error){RecordEnvironment("Developer renderer recovery: "+error);}
            }
        });
#endif
    }
    private void OnEnvironmentEvent(EnvironmentProtocol.Event item)
    {
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(()=>
        {
            string kind=item.Kind switch{1=>L.Environment.Morale,2 or 3=>L.Environment.Flood,4 or 5=>L.Environment.Wildfire,6=>L.Environment.Drought,8 or 9=>L.Environment.Blizzard,10 or 11=>L.Environment.Heatwave,_=>"#"+item.Kind};
            string target=item.Unit>0?L.Environment.Unit(item.Commander,item.Tile):L.Fields.Food;
            string city=snapshot?.Cities.FirstOrDefault(c=>c?.CityId==(int)item.City)?.Name??"#"+item.City;
            environmentEvents.Add(L.Environment.Event(EnvironmentProtocol.Date(item.Date),kind,city,target,
                L.Environment.Damage(item.TroopsAfter-item.TroopsBefore,item.MoraleAfter-item.MoraleBefore,item.FoodAfter-item.FoodBefore)));
            if(environmentEvents.Count>200)environmentEvents.RemoveAt(0);environmentMenu?.SetHistory(environmentEvents);
        });
    }
    private void UpdateEnvironmentControls()
    {
#if LOCAL_DEVELOPER
        UpdateDeveloperControls();
#endif
        map.SetEnvironment(environmentController is {} active?EnvironmentOverlay.From(active.Status):null);
        environmentButton.IsEnabled=!seasonClosing&&!seasonDisconnecting;
        if(environmentMenu is not {} menu)return;
        bool available=!environmentChanging&&!seasonStarting&&!seasonStopping&&!seasonClosing&&!seasonDisconnecting;
        menu.SetAvailable(available&&!busy&&environmentController?.Status is not {Phase:EnvironmentPhase.Faulted,Restored:false},available&&!busy);
        menu.SeasonStatus.Text=L.Environment.SeasonStatus(seasonStatus.Text);
        var value=environmentController?.Status;
        string actual=value?.Phase switch
        {
            EnvironmentPhase.Connecting or EnvironmentPhase.Applying or EnvironmentPhase.Stopping=>L.Environment.Applying,
            EnvironmentPhase.On=>L.Environment.Active(EnvironmentEffectNames(value.Actual),value.Receipt is {} receipt?EnvironmentProtocol.Date(receipt.Date):""),
            EnvironmentPhase.Faulted=>L.Environment.Failed(value.Error),
            _=>L.Environment.Off
        };
        menu.Status.Text=actual+"\n"+(environmentUiError is {} issue?L.Environment.Failed(issue):environmentUiStatus);
    }
    private static string EnvironmentEffectNames(EnvironmentEffects flags)=>string.Join(", ",new[]{
        (EnvironmentEffects.RainMovement,L.Environment.RainName),(EnvironmentEffects.SnowMovement,L.Environment.SnowName),
        (EnvironmentEffects.WeatherMorale,L.Environment.MoraleName),(EnvironmentEffects.Flood,L.Environment.FloodName),
        (EnvironmentEffects.Wildfire,L.Environment.WildfireName),(EnvironmentEffects.Drought,L.Environment.DroughtName),
        (EnvironmentEffects.RainVisual,L.Environment.RainVisualName),(EnvironmentEffects.SnowVisual,L.Environment.SnowVisualName),
        (EnvironmentEffects.Blizzard,L.Environment.BlizzardName),(EnvironmentEffects.Heatwave,L.Environment.HeatwaveName)}.Where(p=>(flags&p.Item1)!=0).Select(p=>p.Item2));
    private bool EnvironmentRenderingActive=>
#if LOCAL_DEVELOPER
        developerRendering||
#endif
        environmentController?.Status.Actual is {} flags&&flags!=EnvironmentEffects.None;
    private async Task SyncWeatherRenderingAsync()
    {
        if(seasonController is not {} controller||seasonClosing||seasonDisconnecting)return;
        if(EnvironmentRenderingActive&&!controller.Status.Requested){
            seasonJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"logs"));
            await controller.SetSeasonalAsync(false);await controller.StartAsync();
            if(controller.Status.Phase==SeasonPhase.Faulted)throw new IOException(controller.Status.Error);
        }
        else if(!EnvironmentRenderingActive&&controller.Status.Requested&&controller.Status.Scope==SeasonScope.Weather)
            if(!await StopSeasonsAsync(false))throw new IOException(L.Seasons.Unrestored);
        UpdateSeasonControls();
    }
    private void EnvironmentNotice(string text)
    {
        if(environmentUiStatus!=text){environmentUiStatus=text;AddLog(text);}
    }
    private async Task ApplyEnvironmentAsync(EnvironmentSettings selected)
    {
        if(environmentChanging||environmentChecking||runtimeRulesChecking||seasonStarting||seasonStopping||seasonClosing||seasonDisconnecting||busy||refreshing)return;
        environmentChanging=true;SetBusy(true,scanning:false);
        try
        {
            environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));
            using var timing=new SeasonTiming(RecordEnvironment,"environment-save-and-apply");
            environmentUiError=null;environmentFailure=null;
            selected.Validate();environmentStamp=environmentFile.Save(selected,environmentStamp);environmentSettings=selected;
            timing.Step("validate-and-save");
            environmentMenu?.ShowSaved(selected);
            await ApplySavedEnvironmentAsync(selected);
            timing.Complete("request-finished");
        }
        catch(Exception error){environmentFailure=game;environmentUiError=error.Message;AddLog(L.Environment.Failed(error.Message));}
        finally{environmentChanging=false;SetBusy(false,scanning:false);}
    }
    private async Task ApplySavedEnvironmentAsync(EnvironmentSettings selected)
    {
        environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));
        using var timing=new SeasonTiming(RecordEnvironment,"environment-apply");
        if(game is not {Verified:true} identity||layout is not {} found||snapshot is null)
        {EnvironmentNotice(L.Environment.Saved);timing.Complete("saved-without-game");return;}
        // Stopping remains available during play; enabling/changing effects waits for pause.
        if((selected.Seasonal||selected.Effects!=EnvironmentEffects.None)&&await Task.Run(()=>environmentReadPace(identity,found))!=GamePace.Paused)
        {EnvironmentNotice(L.Environment.WaitingForPause);timing.Complete("waiting-for-pause");return;}
        timing.Step("pause-check");
        if(game!=identity){timing.Complete("game-changed");return;}
        if(selected.Effects==EnvironmentEffects.None)
        {
            if(!await StopEnvironmentAsync(false))throw new IOException(environmentUiError??L.Seasons.Unrestored);
        }
        else
        {
            if(environmentController is null)
            {
                var data=await Task.Run(()=>GameMemory.ReadSnapshot(identity,found));
                timing.Step("snapshot-read");
                var climate=EnvironmentClimate.Pack(data);
                timing.Step("environment-climate-pack");
                environmentController=new(new EnvironmentNativeBackend(identity,climate,Path.Combine(seasonStorageRoot,"environment","native"),RecordEnvironment),RecordEnvironment);
                environmentController.StatusChanged+=OnEnvironmentStatus;environmentController.EventRecorded+=OnEnvironmentEvent;
            }
            await environmentController.ApplyAsync(selected);
            timing.Step("environment-native-apply");
            if(environmentController.Status.Actual!=selected.Effects||environmentController.Status.Receipt?.Values!=selected.Values)
                throw new IOException(L.Environment.ReceiptMismatch);
        }
        timing.Step("environment-receipt-verified");
        await SetSeasonsAsync(selected.Seasonal);timing.Step("season-mode");
        await SyncWeatherRenderingAsync();timing.Step("weather-renderer");
        if(seasonUiError is {} issue)throw new IOException(issue);
        if(seasonController?.Status is {Phase:SeasonPhase.Faulted} failed)throw new IOException(failed.Error);
        if(selected.Seasonal&&seasonController?.Status is not {Requested:true,Scope:SeasonScope.Full})
            throw new IOException(L.Environment.SeasonMismatch);
        environmentApplied=selected;
        EnvironmentNotice(L.Environment.AppliedTogether);
        timing.Complete();
    }
    private bool EnvironmentMatches(EnvironmentSettings selected)
    {
        bool sameEnvironment=selected.Effects==EnvironmentEffects.None
            ? environmentController is null||environmentController.Status is {Phase:EnvironmentPhase.Off,Restored:true}
            : environmentController?.Status is {Phase:EnvironmentPhase.On} active&&active.Actual==selected.Effects&&active.Receipt?.Values==selected.Values;
        var season=seasonController?.Status;
        bool sameSeason=selected.Seasonal?season is {Requested:true,Scope:SeasonScope.Full,Phase:SeasonPhase.On or SeasonPhase.PartialOn}
            : season is null||season is {Phase:SeasonPhase.Off,OriginalRestored:true}||season is {Requested:true,Scope:SeasonScope.Weather,Phase:SeasonPhase.On};
        return sameEnvironment&&sameSeason;
    }
    private async Task ReapplyEnvironmentAsync(bool retry=false)
    {
        if(busy||refreshing||environmentChecking||environmentChanging||layoutRecovering||seasonStarting||seasonStopping
            ||seasonClosing||seasonDisconnecting||game is not {Verified:true} identity||layout is null)return;
#if LOCAL_DEVELOPER
        // Automatic saved settings must not interfere with existing developer actions.
        if(developerMenu is not null||developerChanging||developerRendering)return;
#endif
        if(seasonController?.Status.Phase is SeasonPhase.Connecting or SeasonPhase.Applying or SeasonPhase.Restoring or SeasonPhase.Reconnecting)return;
        if(retry)environmentFailure=null;
        if(environmentFailure==identity||environmentMenu?.HasPendingChanges==true||lists.Pending>0||aiView?.Pending==true
            ||Editors.Any(card=>card.Pending>0)||RulesPending)return;
        environmentChecking=true;
        bool writing=false;
        try
        {
            var read=environmentFile.Read();
            if(read.Settings is not {} selected)return;
            if(environmentMenu is not null&&read.Stamp!=environmentStamp)throw new IOException(L.Environment.SettingsMoved);
            environmentSettings=selected;environmentStamp=read.Stamp;environmentMenu?.ShowSaved(selected);
            var season=seasonController?.Status;
            if(!retry&&(environmentController?.Status.Phase==EnvironmentPhase.Faulted||season?.Phase==SeasonPhase.Faulted))
            {
                environmentFailure=identity;EnvironmentNotice(L.Environment.AutoStopped);return;
            }
            if(environmentApplied==selected&&EnvironmentMatches(selected))return;
            environmentUiError=null;writing=true;environmentChanging=true;SetBusy(true,scanning:false);
            await ApplySavedEnvironmentAsync(selected);
        }
        catch(Exception error){environmentFailure=identity;environmentUiError=error.Message;AddLog(L.Environment.AutoFailed(error.Message));}
        finally
        {
            environmentChecking=false;
            if(writing){environmentChanging=false;SetBusy(false,scanning:false);}else UpdateEnvironmentControls();
        }
    }
    private async Task<bool> StopEnvironmentAsync(bool release)
    {
        try
        {
            if(environmentController is not {} controller)
            {
                if(release)
                {
                    environmentApplied=null;
                    if(environmentJournal is {} savedJournal){environmentJournal=null;await savedJournal.DisposeAsync();}
                }
                return true;
            }
            await controller.StopAsync();if(!controller.Status.Restored)return false;
#if LOCAL_DEVELOPER
            developerRendering=false;
#endif
            if(!release)await SyncWeatherRenderingAsync();
            environmentUiError=null;
            environmentUiStatus=environmentSettings.Effects==EnvironmentEffects.None?L.Environment.Off:L.Environment.NotApplied;
            if(release)
            {
                environmentApplied=null;
                await controller.DisposeAsync();controller.StatusChanged-=OnEnvironmentStatus;controller.EventRecorded-=OnEnvironmentEvent;environmentController=null;
                if(environmentJournal is {} journal){environmentJournal=null;await journal.DisposeAsync();}
                environmentEvents.Clear();environmentMenu?.SetHistory(environmentEvents);
            }
            return true;
        }
        catch(Exception error){environmentUiError=error.Message;environmentUiStatus=L.Environment.Failed(error.Message);AddLog(environmentUiStatus);return false;}
        finally{UpdateEnvironmentControls();}
    }
}
