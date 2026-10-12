using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;

internal static partial class RenderChecks
{
    // Keep both verified product sessions alive while the user observes the original report.
    // No gameplay effect is enabled. Closing the report or stop.request ends this bounded check.
    private static void CheckEnvironmentNotificationArt(string output,bool originalOnly=false,bool waitReady=false)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("New live evidence required");Directory.CreateDirectory(output);
        using var writer=new StreamWriter(Path.Combine(output,"native-notice.jsonl")){AutoFlush=true};var sync=new object();
        void Log(string value){lock(sync){writer.WriteLine(DateTime.UtcNow.ToString("O")+" "+value);Console.WriteLine(value);}}
        var game=GameMemory.FindGame();Require(game.Verified,"Unverified game");var layout=GameMemory.Resolve(game,Log,CancellationToken.None);
        Require(GameMemory.ReadPace(game,layout)==GamePace.Paused,"Notification art check requires already-paused game");
        var before=GameMemory.ReadSnapshot(game,layout);var calendar=GameMemory.ReadCalendar(game,layout);var baseline=GameMemory.ReadSeasonSnapshot(game);
        Require(GameMemory.ReadEnvironmentNotification(game).Count==0,"An original report is already open");
        Log(JsonSerializer.Serialize(new{game.ProcessId,game.StartTimeUtc,calendar,paused=true,gameplayEffects=0,diagnosticKind="wildfire"}));
        byte[] materials=originalOnly?[]:GameSeasonSources.Read(game),scene=originalOnly?[]:GameSeasonScene.Read(game);
        if(!originalOnly)scene=SeasonSceneData.WithWeatherCapture(scene,Path.Combine(output,"weather-capture.bin"));
        string build=originalOnly?"":SeasonNativeBundle.ResolveForGame(game,Path.Combine(output,"season-native"),Log);
        SeasonNativeSession? renderer=null;IEnvironmentSession? environment=null;SeasonBridgeProtocol.Reply? final=null;
        uint artBefore=0;bool dialogSeen=false,artSeen=false;
        T WaitWithRenderer<T>(Task<T> task)
        {
            while(!task.IsCompleted){renderer?.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();Thread.Sleep(200);}
            return task.GetAwaiter().GetResult();
        }
        T WaitWithEnvironment<T>(Task<T> task)
        {
            while(!task.IsCompleted){environment!.QueryAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();Thread.Sleep(200);}
            return task.GetAwaiter().GetResult();
        }
        const string text="산불 알림 그림·소리 검증\n생성한 재해 그림과 원본 재난 효과음을 확인합니다.\n표시 검사이며 날짜·병력·사기·병량 피해는 발생시키지 않습니다.";
        try
        {
            environment=new EnvironmentNativeBackend(game,EnvironmentClimate.Pack(before),Path.Combine(output,"environment-native"),Log)
                .StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var applied=environment.ApplyAsync(EnvironmentSettings.New() with{Effects=EnvironmentEffects.None},CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Require(applied.Flags==0&&applied.LastEvent==0,"Diagnostic must not enable gameplay effects");
            if(!originalOnly){renderer=(SeasonNativeSession)WaitWithEnvironment(new SeasonNativeBackend(game,build,Log,()=>materials,()=>scene)
                .StartAsync(baseline,1,CancellationToken.None).AsTask());
            int pending=0;
            while(true)
            {
                try{WaitWithEnvironment(renderer.ApplyAsync(SeasonParameters.At(baseline.Date) with{Seasonal=false},CancellationToken.None).AsTask());break;}
                catch(SeasonNativeDrawPendingException){if(++pending>=3)throw;}
            }
            artBefore=renderer.ReadSceneStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult().Selection[11];}
            if(waitReady)
            {
                Log("READY_TO_SHOW: create show.request when the user is watching the game");
                var readiness=Stopwatch.StartNew();
                while(!File.Exists(Path.Combine(output,"show.request")))
                {
                    Require(readiness.ElapsedMilliseconds<120000,"Notification readiness timed out without showing a report");
                    Require(!File.Exists(Path.Combine(output,"stop.request")),"Notification readiness cancelled without showing a report");
                    var ready=environment.QueryAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    renderer?.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    Require(GameMemory.ReadCalendar(game,layout)==calendar&&GameMemory.ReadPace(game,layout)==GamePace.Paused,"Game date or pause changed while waiting to show the report");
                    File.WriteAllText(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new{utc=DateTime.UtcNow,game.ProcessId,ready.Thread,ready.Frames}));
                    Thread.Sleep(200);
                }
                Log("SHOW_REQUESTED");
            }
            WaitWithRenderer(((IEnvironmentNotificationSession)environment).CheckNotificationAsync(text,CancellationToken.None,1).AsTask()
                .ContinueWith(t=>{t.GetAwaiter().GetResult();return true;},TaskScheduler.Default));
            var timer=Stopwatch.StartNew();
            do
            {
                var receipt=environment.QueryAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                renderer?.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if(renderer is not null)final=renderer.ReadSceneStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                var observed=GameMemory.ReadEnvironmentNotification(game);dialogSeen|=observed is{Count:1,DialogClass:0x146e2c0};artSeen|=final is{} draw&&draw.Selection[11]>artBefore;
                Log(JsonSerializer.Serialize(new{elapsedMs=timer.ElapsedMilliseconds,receipt.Thread,receipt.Frames,receipt.Reserved,receipt.DiagnosticSoundAttempted,receipt.DiagnosticAudioMissing,receipt.DiagnosticSoundId,receipt.DiagnosticSoundChannel,receipt.DiagnosticSoundState,receipt.Flags,receipt.LastEvent,
                    observed.Count,observed.DialogClass,artDraws=final?.Selection[11],selection=final?.Selection,swaps=final?.Swaps,restores=final?.Restores,gpuBytes=final?.GpuBytes,originalOnly}));
                Require(GameMemory.ReadCalendar(game,layout)==calendar,"Calendar changed during notification check");
                Require(receipt.Flags==0&&receipt.LastEvent==0,"Diagnostic recorded gameplay damage");
                if(File.Exists(Path.Combine(output,"stop.request"))||dialogSeen&&observed.Count==0&&timer.ElapsedMilliseconds>2000)break;
                Thread.Sleep(250);
            }while(timer.ElapsedMilliseconds<45000);
            Require(dialogSeen,"Original disaster report dialog was not observed");
            Require(originalOnly||artSeen,"Generated disaster art has no authenticated actual Draw receipt");
            Log(JsonSerializer.Serialize(new{dialogSeen,artSeen,submittedText=text,visibleTextVerified=false,soundAudibleVerified=false}));
        }
        catch(Exception error){if(error is EnvironmentRecoveryException retained)environment=retained.Session;File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());throw;}
        finally
        {
            // Keep the renderer alive through the environment's original still closing animation.
            try
            {
                if(environment is not null)
                {
                    var stop=environment.StopAsync().AsTask();
                    while(!stop.IsCompleted){renderer?.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();Thread.Sleep(100);}
                    stop.GetAwaiter().GetResult();Log("ENVIRONMENT_RESTORED");
                }
            }
            finally
            {
                if(renderer is not null)
                {
                    try{final=renderer.ReadSceneStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();}catch(Exception error){Log("Last scene status: "+error.Message);}
                    var recovery=renderer.StopAsync().AsTask().GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(output,"recovery.json"),JsonSerializer.Serialize(new{recovery,final,dialogSeen,artSeen}));
                    Require(recovery.Confirmed,"Notification renderer recovery remains unconfirmed");renderer.DisposeAsync().AsTask().GetAwaiter().GetResult();Log("SEASON_RENDERER_RESTORED");
                }
                var after=GameMemory.ReadSnapshot(game,layout);
                bool sameDate=GameMemory.ReadCalendar(game,layout)==calendar;
                bool sameUnits=before.Units.Select(u=>(u.Slot,u[UnitFields.Troops],u[UnitFields.Morale])).SequenceEqual(after.Units.Select(u=>(u.Slot,u[UnitFields.Troops],u[UnitFields.Morale])));
                bool sameFood=before.Cities.Select(c=>c[CityFields.Food]).SequenceEqual(after.Cities.Select(c=>c[CityFields.Food]));
                Log(JsonSerializer.Serialize(new{sameDate,sameUnits,sameFood,dialogSeen,artSeen,swapsBalanced=final is{} value&&value.Swaps==value.Restores}));
                Require(sameDate&&sameUnits&&sameFood,"Live date/resources changed during notification art check");
            }
        }
        Log(originalOnly?"PASS original report observer session; environment hook restored; date and resources unchanged; generated art not tested":
            "PASS original report and authenticated generated art Draw; hooks restored; date and resources unchanged; audible sound requires user confirmation");
    }
}
