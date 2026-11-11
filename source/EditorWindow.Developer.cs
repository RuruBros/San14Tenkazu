#if LOCAL_DEVELOPER
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;
using W=System.Windows;
using WC=System.Windows.Controls;
namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private DisasterTestWindow? developerMenu;
    private PlateButton? developerButton;
    private bool developerRendering,developerChanging;
    private PlateButton BuildDeveloperMenu()
    {
        var button=new PlateButton(L.DeveloperUi.Menu){ToolTip=L.DeveloperUi.MenuTip};
        button.Click+=OpenDeveloperTests;developerButton=button;return button;
    }
    private void OpenDeveloperTests()
    {
        if(developerMenu is {} current){current.Activate();return;}
        var window=new DisasterTestWindow(){Owner=this};developerMenu=window;
        if(snapshot is {} data)window.SetCities(data);
        window.Refresh.Click+=async()=>await RefreshDeveloperCities();
        window.Preview.Click+=async()=>await RunDeveloperTest(1);
        window.Damage.Click+=async()=>await RunDeveloperTest(2);
        window.Stop.Click+=async()=>await CancelDeveloperTest();
        window.ReadText.Click+=async()=>await ReadDeveloperNoticeText();
        window.PerformanceStart.Click+=async()=>await RunPerformanceDiagnostic();
        window.PerformanceStop.Click+=()=>performanceCancellation?.Cancel();
        window.ReadTraits.Click+=async()=>await ReadDeveloperTraitList();
        window.ReadCompatibility.Click+=()=>compatibilityTask=ReadDeveloperCompatibility();
        window.CancelCompatibility.Click+=()=>compatibilityCancellation?.Cancel();
        window.ReadGeography.Click+=async()=>await RunDeveloperGeography(false);
        window.EditGeography.Click+=async()=>await RunDeveloperGeography(true);
        window.ProfiledCityEdit.Click+=async()=>await RunProfiledCityEdit();
        window.ProfiledFieldsEdit.Click+=async()=>await RunProfiledFieldsEdit();
        window.CompleteEditsPreview.Click += async () => await PrepareCompleteEdits();
        window.CompleteEditsRun.Click += async () => await RunCompleteEdits();
        window.CompleteEditsStop.Click += () => completeEditsCancellation?.Cancel();
        window.HeldProvocationLoad.Click += LoadHeldProvocation;
        window.HeldProvocationApply.Click += async () => await RunHeldProvocation(true, false);
        window.HeldProvocationFinish.Click += async () => await RunHeldProvocation(false, true);
        window.HeldProvocationRestore.Click += async () => await RunHeldProvocation(false, false);
        window.CompleteProvokeUnits.SelectionChanged += (_, _) => { completeEditsPreparation = null; UpdateDeveloperControls(); };
        window.ProfiledRulesEdit.Click += async () => await RunProfiledRuleEdit();
        window.ProfiledAiEdit.Click += async () => await RunProfiledAiEdit();
        window.ProfiledAiExtension.Click += async () => await RunProfiledAiExtension();
        window.ProfiledRuntimeRules.Click += async () => await RunProfiledRuntimeRules();
        window.NativeCommandSuite.Click += async () => await StartNativeCommandSuiteStepAsync();
        window.ProfiledRender.Click += async () => await RunProfiledRenderCheck();
        bool closeAllowed=false,closePending=false;
        window.Closing+=async(_,e)=>{
            if (heldProvocationReceipt is not null) { e.Cancel = true; return; }
            if (completeEditsCancellation is { } complete) { complete.Cancel(); e.Cancel = true; return; }
            if(performanceCancellation is {} measuring){measuring.Cancel();e.Cancel=true;return;}
            if(compatibilityCancellation is {} reading){reading.Cancel();e.Cancel=true;return;}
            if(closeAllowed)return;e.Cancel=true;if(developerChanging||closePending)return;closePending=true;
            try {
                // Leave WPF's Closing event even when there is no active test to stop.
                await System.Windows.Threading.Dispatcher.Yield();
                if(await CancelDeveloperTest()){closeAllowed=true;window.Close();}
            }finally{closePending=false;}
        };
        window.Closed += (_, _) => developerMenu = null;
        if (nativeCommandSuite is { } suite && game == suite.Game)
        {
            window.AppendCompatibility(suite.Journal);
            if (suite.Armed is not null)
                window.AppendCompatibility(L.DeveloperUi.NativeSuiteStep(suite.Completed + 1, NativeCommandSequence.Length,
                    NativeCommandLabel(suite.Kind)));
            else window.AppendCompatibility(suite.AwaitingReload ? L.DeveloperUi.NativeSuiteReload : L.DeveloperUi.NativeSuiteNext);
        }
        UpdateDeveloperControls();window.Show();
    }
    private void UpdateDeveloperControls()
    {
        if(developerMenu is not {} window)return;
        bool available=!developerChanging&&!environmentChanging&&!busy&&!seasonClosing&&!seasonDisconnecting;
        window.PerformanceStart.IsEnabled=available&&seasonController?.Status.Phase==SeasonPhase.On;
        bool november=game is {} attached&&GameBuildProfiles.FindNative(attached)==GameBuildProfiles.NovemberNative;
        window.SetAvailable(available&&developerRenderTrial is null&&(game is{Verified:true}||november)&&layout is not null&&snapshot is not null,available,november);
        window.SetCompatibilityAvailable(available&&!refreshing&&!rulePolling&&!runtimeRulesChecking&&!environmentChecking&&!seasonStarting&&!seasonStopping,compatibilityCancellation is not null);
        window.SetGeographyAvailable(available&&!HasPendingEdits&&!refreshing&&!rulePolling&&!runtimeRulesChecking&&!environmentChecking&&!seasonStarting&&!seasonStopping&&game is{Verified:true}&&layout is not null&&snapshot is not null);
        window.ProfiledCityEdit.IsEnabled = available && !HasPendingEdits && !refreshing &&
            game?.DataProfile?.Supports(CityFields.Gold) == true && layout is not null && ProfiledCityTarget() is not null;
        window.ProfiledFieldsEdit.IsEnabled = available && CanRunProfiledFieldsEdit;
        window.CompleteEditsPreview.IsEnabled = available && CanPrepareCompleteEdits;
        window.CompleteProvokeUnits.IsEnabled = available && CanPrepareCompleteEdits;
        window.CompleteEditsRun.IsEnabled = available && CanPrepareCompleteEdits && completeEditsPreparation is { } prepared &&
            game == prepared.Identity && layout is { } completeLayout && completeLayout.SameStorage(prepared.Layout);
        window.CompleteEditsStop.IsEnabled = completeEditsCancellation is not null;
        window.HeldProvocationLoad.IsEnabled = available && CanPrepareCompleteEdits && heldProvocationReceipt is null;
        window.HeldProvocationApply.IsEnabled = window.HeldProvocationLoad.IsEnabled && heldProvocationPlan is not null;
        window.HeldProvocationFinish.IsEnabled = window.HeldProvocationRestore.IsEnabled =
            !developerChanging && !seasonClosing && !seasonDisconnecting && heldProvocationReceipt is not null && game == heldProvocationReceipt.Identity;
        window.ProfiledRulesEdit.IsEnabled = available && CanRunProfiledRuleEdit;
        window.ProfiledAiEdit.IsEnabled = available && CanRunProfiledAi;
        window.ProfiledAiExtension.IsEnabled = available && CanRunProfiledAi;
        window.ProfiledRuntimeRules.IsEnabled = available && CanRunProfiledRuntimeRules;
        UpdateNativeCommandSuiteControls(window, available);
        window.ProfiledRender.IsEnabled = available && CanRunProfiledRenderCheck;
        window.SetReceipt(environmentController?.Status);
    }
    private async Task RefreshDeveloperCities()
    {
        try{if(game is not{} identity||GameBuildProfiles.FindNative(identity) is null||layout is not {} found)throw new InvalidOperationException(L.DeveloperUi.ConnectionRequired);
            developerChanging=true;UpdateDeveloperControls();var data=await Task.Run(()=>GameMemory.ReadSnapshot(identity,found));developerMenu?.SetCities(data);}
        catch(Exception error){developerMenu?.Append(error.Message);}
        finally{developerChanging=false;UpdateDeveloperControls();}
    }
    private async Task ReadDeveloperNoticeText()
    {
        if(developerChanging||developerMenu is not {} window)return;
        developerChanging=true;UpdateDeveloperControls();
        try {
            if(game is not{Verified:true} identity||busy||seasonClosing||seasonDisconnecting)throw new InvalidOperationException(L.DeveloperUi.ReadyConnectionRequired);
            var evidence=await Task.Run(()=>GameMemory.ReadNoticeTypography(identity));
            string directory=Path.Combine(seasonStorageRoot,"environment","diagnostics");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,$"notice-text-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{identity.ProcessId}-{Guid.NewGuid():N}.json");
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                await System.Text.Json.JsonSerializer.SerializeAsync(file,new{formatVersion=3,utc=DateTime.UtcNow,identity.ProcessId,identity.StartTimeUtc,identity.ExeHash,readOnly=true,consistent=true,evidence},new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
            environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));
            string scale=evidence.FontScaleX is {} x&&evidence.FontScaleY is {} y?L.DeveloperUi.FontScale($"{x:G5}", $"{y:G5}"):L.DeveloperUi.RawFontEvidence;
            RecordEnvironment(L.DeveloperUi.NoticeEvidence($"{identity.ProcessId}", $"{evidence.TextAreaWidth}", $"{evidence.TextAreaHeight}", $"{scale}", $"{path}"));
            if(evidence.Details is {} details){
                if(details.RenderText is {ScaleX:{} renderX,ScaleY:{} renderY} rendered)
                    window.Append(L.DeveloperUi.RenderTextEvidence($"{renderX:G5}", $"{renderY:G5}", $"{rendered.Width}", $"{rendered.Height}", $"{details.MeasuredWidth}", $"{details.MeasuredHeight}", $"{details.MeasuredLines}"));
                if(details.ReadError is {} detailError)window.Append(detailError);
                if(details.RenderText?.ReadError is {} renderError)window.Append(renderError);
            }
            if(evidence.FontReadError is {} fontError)window.Append(fontError);
            if(evidence.TextReadError is {} textError)window.Append(textError);
            window.Append(L.DeveloperUi.ReadOnlyEvidenceSaved+path);
        }
        catch(Exception error){
            window.Append(L.DeveloperUi.NoticeReadFailed+error.Message);
            if(error is NoticeReadRaceException race)try {
                string directory=Path.Combine(seasonStorageRoot,"environment","diagnostics");Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,$"notice-text-rejected-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{game?.ProcessId}-{Guid.NewGuid():N}.json");
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                    await System.Text.Json.JsonSerializer.SerializeAsync(file,new{formatVersion=3,utc=DateTime.UtcNow,ProcessId=game?.ProcessId,readOnly=true,consistent=false,error=race.Message,race.Stage,race.Address,race.BeforeHex,race.AfterHex,race.UnconfirmedEvidence},new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
                window.Append(L.DeveloperUi.UnconfirmedEvidenceSaved+path);
                environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));RecordEnvironment(L.DeveloperUi.NoticeRaceEvidence+race.Stage+", file="+path);
            }catch(Exception saveError){window.Append(L.DeveloperUi.UnconfirmedSaveFailed+saveError.Message);}
            try{environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));RecordEnvironment(L.DeveloperUi.NoticeReadFailure+error.Message);}
            catch(Exception logError){AddLog(L.DeveloperUi.NoticeReadFailure+error.Message+L.DeveloperUi.FailureLogError+logError.Message);}
        }
        finally{developerChanging=false;UpdateDeveloperControls();}
    }
    private async Task RunDeveloperTest(int mode)
    {
        if(developerChanging||developerMenu is not {} window)return;
        var diagnosticStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        try {
            if(game is not{} identity||layout is not {} found||busy||seasonClosing||seasonDisconnecting||developerRenderTrial is not null||
                (mode==1?GameBuildProfiles.FindNative(identity) is null:!identity.Verified))throw new InvalidOperationException(L.DeveloperUi.ReadyConnectionRequired);
            var target=window.Target??throw new InvalidOperationException(L.DeveloperUi.ChooseTargetCity);int kind=window.Kind.SelectedIndex;
            developerChanging=true;UpdateDeveloperControls();
            if(developerNoticeDiagnostic is not null&&!await CancelDeveloperCore())throw new IOException(L.Seasons.Unrestored);
            var fresh=await Task.Run(()=>GameMemory.ReadSnapshot(identity,found));
            if(fresh.ForceOfCity(target.Id)!=target.Force)throw new InvalidOperationException(L.DeveloperUi.CityOwnerChanged);
            if(await Task.Run(()=>GameMemory.ReadPace(identity,found))!=GamePace.Paused)throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
            bool captureTrace=window.PictureDiagnostic.IsChecked==true;
            bool profiledPreview=mode==1&&GameBuildProfiles.FindNative(identity)==GameBuildProfiles.NovemberNative;
            if(mode==1&&(captureTrace||profiledPreview)){
                var renderer=seasonController?.Status;
                if(renderer is not{OriginalRestored:true,Requested:false}||
                    (renderer.Phase!=SeasonPhase.Off&&!(profiledPreview&&!captureTrace&&renderer.Phase==SeasonPhase.Faulted))||
                    environmentController?.Status.Actual is {} effects&&effects!=EnvironmentEffects.None)throw new InvalidOperationException(L.DeveloperUi.PictureDiagnosticRendererOff);
                developerNoticeDiagnostic=new(Path.Combine(seasonStorageRoot,"environment","diagnostics"),identity,fresh,target.Id,kind,found,diagnosticStarted,captureTrace);
                StartDeveloperNoticeDeadline();
                window.Append(L.DeveloperUi.PictureDiagnosticPrepared+developerNoticeDiagnostic.DirectoryPath);
            }
            var values=environmentController?.Status.Receipt?.Values??environmentSettings.Values;
            string details=DisasterTestWindow.Losses(kind,values);
            if(mode==2&&W.MessageBox.Show(window,L.DeveloperUi.DamageConfirmation($"{target.Label}", $"{DisasterTestWindow.Names[kind]}", $"{details}"),L.DeveloperUi.DamageConfirmationTitle,W.MessageBoxButton.YesNo,W.MessageBoxImage.Warning)!=W.MessageBoxResult.Yes)return;
            environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));
            if(environmentController is null){
                environmentController=new(new EnvironmentNativeBackend(identity,EnvironmentClimate.Pack(fresh),Path.Combine(seasonStorageRoot,"environment","native"),RecordEnvironment),RecordEnvironment);
                environmentController.StatusChanged+=OnEnvironmentStatus;environmentController.EventRecorded+=OnEnvironmentEvent;
            }
            if(environmentController.Status.Phase!=EnvironmentPhase.On)
                await environmentController.ApplyAsync(environmentSettings with{Effects=EnvironmentEffects.None},keepEmptySession:true);
            developerNoticeDiagnostic?.Observe(environmentController.Status);
            developerRendering=true;await SyncWeatherRenderingAsync();
            if(developerNoticeDiagnostic?.DeadlineExpired==true)throw new TimeoutException("Diagnostic preparation exceeded 120 seconds");
            await environmentController.TestAsync(mode,target.Id,kind);
            developerNoticeDiagnostic?.Observe(environmentController.Status);
            if(environmentController.Status.Receipt is not {} receipt||receipt.TestState is 5 or 6)
                throw new InvalidOperationException(L.DeveloperUi.GameFrameRejected);
            RecordEnvironment(L.DeveloperUi.DisasterTestEvidence($"{identity.ProcessId}", $"{target.Label}", $"{DisasterTestWindow.Names[kind]}", $"{mode}", $"{details}"));
            window.Append(mode==1?L.DeveloperUi.PreviewScheduled:L.DeveloperUi.DamageScheduled);
        }
        catch(Exception error){window.Append(L.DeveloperUi.ExecutionFailed+error.Message);RecordEnvironment("Developer test failure: "+error);await CancelDeveloperCore();}
        finally{developerChanging=false;UpdateEnvironmentControls();}
    }
    private async Task<bool> CancelDeveloperCore()
    {
        if(developerRenderTrial is not null)return await StopProfiledRenderCheck("manual-or-deadline");
        try{
            if(environmentController is {} controller){
                if(controller.Status.Phase==EnvironmentPhase.On)await controller.TestAsync(0,1,0);
                if(controller.Status.Requested==EnvironmentEffects.None)await controller.StopAsync();
            }
            developerRendering=false;await SyncWeatherRenderingAsync();await CompleteDeveloperNoticeDiagnostic();return true;
        }catch(Exception error){developerMenu?.Append(L.DeveloperUi.RecoveryUnconfirmed+error.Message);RecordEnvironment("Developer recovery failure: "+error);return false;}
    }
    private async Task<bool> CancelDeveloperTest()
    {
        if(developerChanging)return false;developerChanging=true;UpdateDeveloperControls();
        try{return await CancelDeveloperCore();}finally{developerChanging=false;UpdateEnvironmentControls();}
    }
}
#endif
