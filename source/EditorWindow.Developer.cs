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
        var button=new PlateButton("개발자"){ToolTip="개발자 테스트 창 열기 · 로컬 빌드"};
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
        window.ReadTraits.Click+=async()=>await ReadDeveloperTraitList();
        bool closeAllowed=false,closePending=false;
        window.Closing+=async(_,e)=>{
            if(closeAllowed)return;e.Cancel=true;if(developerChanging||closePending)return;closePending=true;
            try {
                // Leave WPF's Closing event even when there is no active test to stop.
                await System.Windows.Threading.Dispatcher.Yield();
                if(await CancelDeveloperTest()){closeAllowed=true;window.Close();}
            }finally{closePending=false;}
        };
        window.Closed+=(_,_)=>developerMenu=null;UpdateDeveloperControls();window.Show();
    }
    private void UpdateDeveloperControls()
    {
        if(developerMenu is not {} window)return;
        bool available=!developerChanging&&!environmentChanging&&!busy&&!seasonClosing&&!seasonDisconnecting;
        window.SetAvailable(available&&game is{Verified:true}&&layout is not null&&snapshot is not null,available);
        window.SetReceipt(environmentController?.Status);
    }
    private async Task RefreshDeveloperCities()
    {
        try{if(game is not{Verified:true} identity||layout is not {} found)throw new InvalidOperationException("게임 연결이 필요합니다.");
            developerChanging=true;UpdateDeveloperControls();var data=await Task.Run(()=>GameMemory.ReadSnapshot(identity,found));developerMenu?.SetCities(data);}
        catch(Exception error){developerMenu?.Append(error.Message);}
        finally{developerChanging=false;UpdateDeveloperControls();}
    }
    private async Task ReadDeveloperNoticeText()
    {
        if(developerChanging||developerMenu is not {} window)return;
        developerChanging=true;UpdateDeveloperControls();
        try {
            if(game is not{Verified:true} identity||busy||seasonClosing||seasonDisconnecting)throw new InvalidOperationException("게임 연결과 작업 완료가 필요합니다.");
            var evidence=await Task.Run(()=>GameMemory.ReadNoticeTypography(identity));
            string directory=Path.Combine(seasonStorageRoot,"environment","diagnostics");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,$"notice-text-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{identity.ProcessId}-{Guid.NewGuid():N}.json");
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                await System.Text.Json.JsonSerializer.SerializeAsync(file,new{formatVersion=3,utc=DateTime.UtcNow,identity.ProcessId,identity.StartTimeUtc,identity.ExeHash,readOnly=true,consistent=true,evidence},new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
            environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));
            string scale=evidence.FontScaleX is {} x&&evidence.FontScaleY is {} y?$"글꼴 배율 X={x:G5}, Y={y:G5}":"글꼴 getter의 원시 데이터를 저장했습니다. 배율 해석은 추가 확인이 필요합니다.";
            RecordEnvironment($"알림 글자 상태 읽기: PID={identity.ProcessId}, 텍스트 영역={evidence.TextAreaWidth}×{evidence.TextAreaHeight}, {scale}, file={path}");
            if(evidence.Details is {} details){
                if(details.RenderText is {ScaleX:{} renderX,ScaleY:{} renderY} rendered)
                    window.Append($"렌더 객체 글꼴 배율 X={renderX:G5}, Y={renderY:G5} · 영역 {rendered.Width}×{rendered.Height} · 측정 {details.MeasuredWidth}×{details.MeasuredHeight} · {details.MeasuredLines}줄");
                if(details.ReadError is {} detailError)window.Append(detailError);
                if(details.RenderText?.ReadError is {} renderError)window.Append(renderError);
            }
            if(evidence.FontReadError is {} fontError)window.Append(fontError);
            if(evidence.TextReadError is {} textError)window.Append(textError);
            window.Append("읽기 전용 진단 저장: "+path);
        }
        catch(Exception error){
            window.Append("글자 상태 읽기 실패: "+error.Message);
            if(error is NoticeReadRaceException race)try {
                string directory=Path.Combine(seasonStorageRoot,"environment","diagnostics");Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,$"notice-text-rejected-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{game?.ProcessId}-{Guid.NewGuid():N}.json");
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                    await System.Text.Json.JsonSerializer.SerializeAsync(file,new{formatVersion=3,utc=DateTime.UtcNow,ProcessId=game?.ProcessId,readOnly=true,consistent=false,error=race.Message,race.Stage,race.Address,race.BeforeHex,race.AfterHex,race.UnconfirmedEvidence},new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
                window.Append("불일치 원시 기록 저장 (확정 결과 아님): "+path);
                environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));RecordEnvironment("알림 글자 불일치 원시 기록: stage="+race.Stage+", file="+path);
            }catch(Exception saveError){window.Append("불일치 원시 기록 저장 실패: "+saveError.Message);}
            try{environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));RecordEnvironment("알림 글자 상태 읽기 실패: "+error.Message);}
            catch(Exception logError){AddLog("알림 글자 상태 읽기 실패: "+error.Message+" · 실패 로그 저장 오류: "+logError.Message);}
        }
        finally{developerChanging=false;UpdateDeveloperControls();}
    }
    private async Task RunDeveloperTest(int mode)
    {
        if(developerChanging||developerMenu is not {} window)return;
        try {
            if(game is not{Verified:true} identity||layout is not {} found||busy||seasonClosing||seasonDisconnecting)throw new InvalidOperationException("게임 연결과 작업 완료가 필요합니다.");
            var target=window.Target??throw new InvalidOperationException("대상 도시를 선택하세요.");int kind=window.Kind.SelectedIndex;
            developerChanging=true;UpdateDeveloperControls();
            var fresh=await Task.Run(()=>GameMemory.ReadSnapshot(identity,found));
            if(fresh.ForceOfCity(target.Id)!=target.Force)throw new InvalidOperationException("도시 소유 세력이 바뀌었습니다. 도시 목록을 새로 읽으세요.");
            if(await Task.Run(()=>GameMemory.ReadPace(identity,found))!=GamePace.Paused)throw new InvalidOperationException("게임을 일시정지한 뒤 실행하세요.");
            var values=environmentController?.Status.Receipt?.Values??environmentSettings.Values;
            string details=DisasterTestWindow.Losses(kind,values);
            if(mode==2&&W.MessageBox.Show(window,$"{target.Label} · {DisasterTestWindow.Names[kind]}\n{details}\n\n다음 정상 1/11/21일 경계에 한 번 적용합니다.\n실제 병력·사기·병량이 변경되며 자동 복구하지 않습니다.\n지형·대상 조건에 따라 피해가 없을 수 있습니다. 예약할까요?","실제 피해 테스트",W.MessageBoxButton.YesNo,W.MessageBoxImage.Warning)!=W.MessageBoxResult.Yes)return;
            environmentJournal??=new SeasonJournal(Path.Combine(seasonStorageRoot,"environment","logs"));
            if(environmentController is null){
                environmentController=new(new EnvironmentNativeBackend(identity,EnvironmentClimate.Pack(fresh),Path.Combine(seasonStorageRoot,"environment","native"),RecordEnvironment),RecordEnvironment);
                environmentController.StatusChanged+=OnEnvironmentStatus;environmentController.EventRecorded+=OnEnvironmentEvent;
            }
            if(environmentController.Status.Phase!=EnvironmentPhase.On)
                await environmentController.ApplyAsync(environmentSettings with{Effects=EnvironmentEffects.None},keepEmptySession:true);
            developerRendering=true;await SyncWeatherRenderingAsync();
            await environmentController.TestAsync(mode,target.Id,kind);
            if(environmentController.Status.Receipt is not {} receipt||receipt.TestState is 5 or 6)
                throw new InvalidOperationException("게임 프레임에서 요청을 취소하거나 거부했습니다. 개발자 상태 로그를 확인하세요.");
            RecordEnvironment($"개발자 재난 테스트: PID={identity.ProcessId}, 도시={target.Label}, 재난={DisasterTestWindow.Names[kind]}, 모드={mode}, {details}");
            window.Append(mode==1?"게임 창을 선택하고 일시정지를 유지하세요. 전경·시간 정지를 확인한 뒤 한 번 표시합니다. 2분 후 자동 종료합니다.":"예약되었습니다. 게임에서 정상적으로 날짜를 진행하세요. 기존 환경 설정의 다른 효과는 그대로 적용됩니다.");
        }
        catch(Exception error){window.Append("실행 실패: "+error.Message);RecordEnvironment("Developer test failure: "+error);await CancelDeveloperCore();}
        finally{developerChanging=false;UpdateEnvironmentControls();}
    }
    private async Task<bool> CancelDeveloperCore()
    {
        try{
            if(environmentController is {} controller){
                if(controller.Status.Phase==EnvironmentPhase.On)await controller.TestAsync(0,1,0);
                if(controller.Status.Requested==EnvironmentEffects.None)await controller.StopAsync();
            }
            developerRendering=false;await SyncWeatherRenderingAsync();return true;
        }catch(Exception error){developerMenu?.Append("종료/복구 확인 실패: "+error.Message);RecordEnvironment("Developer recovery failure: "+error);return false;}
    }
    private async Task<bool> CancelDeveloperTest()
    {
        if(developerChanging)return false;developerChanging=true;UpdateDeveloperControls();
        try{return await CancelDeveloperCore();}finally{developerChanging=false;UpdateEnvironmentControls();}
    }
}
#endif
