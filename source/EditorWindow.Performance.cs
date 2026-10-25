#if LOCAL_DEVELOPER
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu.Seasons;
namespace San14Tenkazu;
internal sealed partial class EditorWindow
{
    private CancellationTokenSource? performanceCancellation;
    private async Task RunPerformanceDiagnostic()
    {
        if(developerChanging||environmentChanging||busy||refreshing||HasPendingEdits||seasonStarting||seasonStopping||
           game is not{Verified:true} identity||layout is not{} currentLayout||seasonController is not{} controller||developerMenu is not{} window)return;
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(120));performanceCancellation=cancel;developerChanging=true;UpdateDeveloperControls();
        window.PerformanceStop.IsEnabled=true;bool started=false,startAttempted=false;string? directory=null,error=null,native=null;GameSnapshot? before=null,after=null;
        var watch=Stopwatch.StartNew();var envBefore=environmentController?.Status;var rendererBefore=controller.Status;
        string settingsPath=Path.Combine(seasonStorageRoot,"environment","settings.json");string? settingsBefore=null;
        try{
            if(await Task.Run(()=>GameMemory.ReadPace(identity,currentLayout),cancel.Token)!=GamePace.Paused)
                throw new InvalidOperationException("일시정지 상태에서 시작해 주세요. 진단이 게임 속도를 변경하지는 않습니다.");
            if(envBefore is null||(envBefore.Actual&~(EnvironmentRules.EnvironmentEffects.RainVisual|EnvironmentRules.EnvironmentEffects.SnowVisual))!=0)
                throw new InvalidOperationException("무피해 진단에는 이동·사기·재해 효과가 꺼진 상태가 필요합니다. 현재 설정은 변경하지 않았습니다.");
            settingsBefore=SettingsHash();
            before=await Task.Run(()=>GameMemory.ReadSnapshot(identity,currentLayout),cancel.Token);
            directory=Path.Combine(seasonStorageRoot,"diagnostics",$"performance-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{identity.ProcessId}-{Guid.NewGuid():N}");Directory.CreateDirectory(directory);
            Write("initial.json",new{identity,environmentDll=EnvironmentRules.EnvironmentNativeBundle.DllHash,seasonDll=SeasonNativeBundle.DllHash,
                envBefore,rendererBefore,settingsBefore,gameplay=Gameplay(before),collectionSeconds=20,maximumSeconds=120,settingsChanged=false,patchState="not authenticated by this action"});
            startAttempted=true;await controller.PerformanceAsync(8,cancel.Token);started=true;
            window.PerformanceLog.AppendText("현재 효과를 유지한 채 CPU·GPU 시간을 수집합니다. 자동 설정 변경·날짜 진행은 없습니다.\n");
            while((await controller.PerformanceAsync(10,cancel.Token)).Collecting){await Task.Delay(250,cancel.Token);}
            await Task.Delay(5000,cancel.Token);
        }catch(Exception e){error=e.Message;if(directory is null)window.PerformanceLog.AppendText(error+"\n");}
        finally{
            if(startAttempted)try{
                using var finish=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await controller.PerformanceAsync(9,finish.Token);
                // No spin/Flush: pending GPU queries remain explicitly incomplete after cancellation.
                native=(await controller.PerformanceAsync(11,finish.Token)).Result;
            }catch(Exception e){error=(error is null?"":error+"; ")+"계측 복구 미확인: "+e.Message;}
            try{after=await Task.Run(()=>GameMemory.ReadSnapshot(identity,currentLayout));}catch(Exception e){error=(error is null?"":error+"; ")+e.Message;}
            try{if(directory is not null){
                if(native is not null){File.WriteAllText(Path.Combine(directory,"native.json"),native);Write("summary.json",DeveloperPerformanceSummary.Read(native));File.WriteAllText(Path.Combine(directory,"gpu.csv"),DeveloperPerformanceSummary.GpuCsv(native));}
                Write("result.json",new{elapsedSeconds=watch.Elapsed.TotalSeconds,started,error,diagnosticDetached=native is not null,
                    gameplayUnchanged=before is not null&&after is not null&&Gameplay(before)==Gameplay(after),
                    noNewDamageEvent=envBefore?.Receipt is {} a&&environmentController?.Status.Receipt is {} b&&a.LastEvent==b.LastEvent,
                    settingsBefore,settingsAfter=SettingsHash(),settingsUnchanged=settingsBefore==SettingsHash(),
                    envBefore,envAfter=environmentController?.Status,rendererBefore,rendererAfter=controller.Status,
                    effectsStopped=false,filesRestored=false,screenVerified=false});
                window.PerformanceLog.AppendText($"{(error is null?"수집 종료":error)}\n{directory}\n기존 효과는 유지됩니다. 성능 원인 판정은 저장된 유효 표본을 검토해야 합니다.\n");
            }}catch(Exception e){window.PerformanceLog.AppendText("진단 저장 실패: "+e.Message+"\n");}
            performanceCancellation=null;developerChanging=false;window.PerformanceStop.IsEnabled=false;UpdateDeveloperControls();
        }
        void Write(string name,object value){using var file=new FileStream(Path.Combine(directory!,name),FileMode.CreateNew);JsonSerializer.Serialize(file,value,new JsonSerializerOptions{WriteIndented=true});}
        string? SettingsHash()=>File.Exists(settingsPath)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath))):null;
        static string Gameplay(GameSnapshot data)=>JsonSerializer.Serialize(new{data.Date,data.Hour,cities=data.CityIds.Select(id=>new{id,gold=data.Cities[id][CityFields.Gold],food=data.Cities[id][CityFields.Food]}),
            units=data.Units.OrderBy(u=>u.Slot).Select(u=>new{u.Slot,troops=u[UnitFields.Troops],morale=u[UnitFields.Morale]})});
    }
}
#endif
