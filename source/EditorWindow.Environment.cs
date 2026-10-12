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
            environmentUiStatus=environmentSettings.Effects==EnvironmentEffects.None?L.Environment.Off:L.Environment.NotApplied;
        }
        catch(Exception error){environmentUiError=error.Message;environmentUiStatus=L.Environment.Failed(error.Message);}
    }
    private void OpenEnvironmentMenu()
    {
        if(environmentMenu is {} existing){existing.Activate();return;}
        ReadEnvironmentSettings();
        var menu=new EnvironmentSettingsWindow(seasonChoices!,environmentSettings,environmentEvents){Owner=this};environmentMenu=menu;
        menu.Apply.Click+=async()=>await ApplyEnvironmentAsync(menu.Selection(environmentSettings));
        menu.DisableAll.Click+=async()=>
        {
            menu.ClearSelection();await SetSeasonsAsync(false);
            if(await StopEnvironmentAsync(false))await ApplyEnvironmentAsync(menu.Selection(environmentSettings));
        };
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
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(UpdateEnvironmentControls);
#if LOCAL_DEVELOPER
        if(!Dispatcher.HasShutdownStarted)Dispatcher.InvokeAsync(async()=>{
            if(developerRendering&&!developerChanging&&(value.Phase is EnvironmentPhase.Off or EnvironmentPhase.Faulted||value.Receipt?.TestState is 5 or 6)){
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
        bool available=!environmentChanging&&!seasonClosing&&!seasonDisconnecting;
        menu.SetAvailable(available&&!busy&&environmentController?.Status is not {Phase:EnvironmentPhase.Faulted,Restored:false},available);
        menu.SeasonStatus.Text=L.Environment.SeasonStatus(seasonStatus.Text);
        var value=environmentController?.Status;
        menu.Status.Text=environmentUiError is {} issue?L.Environment.Failed(issue):value?.Phase switch
        {
            EnvironmentPhase.Connecting or EnvironmentPhase.Applying or EnvironmentPhase.Stopping=>L.Environment.Applying,
            EnvironmentPhase.On=>L.Environment.Active(EnvironmentEffectNames(value.Actual),value.Receipt is {} receipt?EnvironmentProtocol.Date(receipt.Date):""),
            EnvironmentPhase.Faulted=>L.Environment.Failed(value.Error),
            _=>environmentUiStatus
        };
    }
    private static string EnvironmentEffectNames(EnvironmentEffects flags)=>string.Join(", ",new[]{
        (EnvironmentEffects.RainMovement,L.Environment.Rain),(EnvironmentEffects.SnowMovement,L.Environment.Snow),
        (EnvironmentEffects.WeatherMorale,L.Environment.Morale),(EnvironmentEffects.Flood,L.Environment.Flood),
        (EnvironmentEffects.Wildfire,L.Environment.Wildfire),(EnvironmentEffects.Drought,L.Environment.Drought),
        (EnvironmentEffects.RainVisual,L.Environment.RainVisual),(EnvironmentEffects.SnowVisual,L.Environment.SnowVisual),
        (EnvironmentEffects.Blizzard,L.Environment.Blizzard),(EnvironmentEffects.Heatwave,L.Environment.Heatwave)}.Where(p=>(flags&p.Item1)!=0).Select(p=>p.Item2));
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
    private async Task ApplyEnvironmentAsync(EnvironmentSettings selected)
    {
        if(environmentChanging||seasonClosing||seasonDisconnecting)return;
        environmentChanging=true;UpdateEnvironmentControls();
        try
        {
            environmentUiError=null;
            selected.Validate();environmentStamp=environmentFile.Save(selected,environmentStamp);environmentSettings=selected;
            if(selected.Effects==EnvironmentEffects.None)
            {
                if(!await StopEnvironmentAsync(false))return;environmentUiStatus=L.Environment.Off;
            }
            else if(game is {Verified:true} identity&&layout is {} found&&snapshot is not null)
            {
                if(busy)throw new InvalidOperationException(L.Environment.Busy);
                environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));
                if(environmentController is null)
                {
                    var data=await Task.Run(()=>GameMemory.ReadSnapshot(identity,found));
                    var climate=EnvironmentClimate.Pack(data);
                    environmentController=new(new EnvironmentNativeBackend(identity,climate,Path.Combine(seasonStorageRoot,"environment","native"),RecordEnvironment),RecordEnvironment);
                    environmentController.StatusChanged+=OnEnvironmentStatus;environmentController.EventRecorded+=OnEnvironmentEvent;
                }
                await environmentController.ApplyAsync(selected);await SyncWeatherRenderingAsync();environmentUiStatus=L.Environment.Off;
            }
            else environmentUiStatus=L.Environment.Saved;
        }
        catch(Exception error){environmentUiError=error.Message;environmentUiStatus=L.Environment.Failed(error.Message);AddLog(environmentUiStatus);}
        finally{environmentChanging=false;UpdateSeasonControls();}
    }
    private async Task<bool> StopEnvironmentAsync(bool release)
    {
        if(environmentController is not {} controller)return true;
        try
        {
            await controller.StopAsync();if(!controller.Status.Restored)return false;
#if LOCAL_DEVELOPER
            developerRendering=false;
#endif
            if(!release)await SyncWeatherRenderingAsync();
            environmentUiError=null;
            environmentUiStatus=environmentSettings.Effects==EnvironmentEffects.None?L.Environment.Off:L.Environment.NotApplied;
            if(release)
            {
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
