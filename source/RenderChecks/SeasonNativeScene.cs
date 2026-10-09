using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Seasons;

internal static partial class RenderChecks
{
    // One bounded product session; this is not an observer retry or gameplay
    // automation. OFF is requested by timeout or a local stop.request file.
    private static void CheckSeasonNativeScene(string[] args)
    {
        string Arg(string name){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException($"Missing {name}");}
        string build=Path.GetFullPath(Arg("--native-build")),output=Path.GetFullPath(Arg("--native-output"));
        int seconds=int.Parse(Arg("--seconds"));if(seconds is <5 or >180)throw new ArgumentOutOfRangeException(nameof(seconds));
        if(Directory.Exists(output))throw new IOException("Scene evidence directory already exists");Directory.CreateDirectory(output);
        void Log(string message){string line=$"{DateTime.UtcNow:O} {message}";File.AppendAllText(Path.Combine(output,"journal.log"),line+Environment.NewLine);Console.WriteLine(line);}
        var options=new JsonSerializerOptions{WriteIndented=true};
        var game=GameMemory.FindGame();var baseline=GameMemory.ReadSeasonSnapshot(game);
        object Dated(SeasonSnapshot value)=>new{value.Identity,date=new{value.Date.Year,value.Date.Month,value.Date.Day}};
        File.WriteAllText(Path.Combine(output,"before.json"),JsonSerializer.Serialize(Dated(baseline),options));
        Log($"SCENE_BASELINE pid={game.ProcessId}, date={baseline.Date.Year}/{baseline.Date.Month}/{baseline.Date.Day}");
        bool weatherCapture=args.Contains("--weather-capture");
        byte[] ReadScene()
        {
            byte[] scene=GameSeasonScene.Read(game);
            if(weatherCapture)
            {
                File.WriteAllBytes(Path.Combine(output,"scene-upload.bin"),scene);
                scene=SeasonSceneData.WithWeatherCapture(scene,Path.Combine(output,"weather-capture.bin"));
            }
            return scene;
        }
        var backend=new SeasonNativeBackend(game,build,Log,()=>GameSeasonSources.Read(game),ReadScene);
        SeasonNativeSession? active=null;var updates=new List<object>();SeasonBridgeProtocol.Reply? final=null;
        try
        {
            active=(SeasonNativeSession)backend.StartAsync(baseline,1,CancellationToken.None).AsTask().GetAwaiter().GetResult();
            SeasonDate? applied=null;int pending=0;
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(750));
            for(int tick=0;tick<seconds*4/3;tick++)
            {
                var snapshot=GameMemory.ReadSeasonSnapshot(game);
                if(snapshot.Identity!=baseline.Identity)throw new InvalidDataException("Scene identity changed; stop and restore before reconnecting");
                try
                {
                    if(applied!=snapshot.Date)
                    {
                        var receipt=active.ApplyAsync(SeasonParameters.At(snapshot.Date),CancellationToken.None).AsTask().GetAwaiter().GetResult();
                        applied=snapshot.Date;pending=0;updates.Add(new{receipt,date=new{snapshot.Date.Year,snapshot.Date.Month,snapshot.Date.Day}});
                        Log($"SCENE_PASSES_ACTIVE date={applied.Value.Year}/{applied.Value.Month}/{applied.Value.Day}; screen evidence is separate");
                    }
                    else active.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                catch(SeasonNativeDrawPendingException)
                {
                    if(++pending>=3)throw new IOException("Three bounded status windows had no complete pass receipt; restoring without another load");
                }
                catch(SeasonNativeDateChangedException change)
                {
                    Log(change.Message);if(change.ReconnectRequired||++pending>=3)throw;
                }
                final=active.ReadSceneStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if(File.Exists(Path.Combine(output,"stop.request")))break;
                timer.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult();
            }
            var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();
            if(!recovery.Confirmed)throw new InvalidDataException("Scene restoration unconfirmed");
            active.DisposeAsync().AsTask().GetAwaiter().GetResult();active=null;
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{baseline=Dated(baseline),updates,final,
                finalDate=final is {} last?new{last.Date.Year,last.Date.Month,last.Date.Day}:null,recovery,
                after=Dated(GameMemory.ReadSeasonSnapshot(game)),screenVerified=false,gameplayInput=false},options));Log("SCENE_RESTORED");
            if(weatherCapture&&!File.Exists(Path.Combine(output,"weather-capture.bin")))throw new IOException("Weather capture was not written after restoration");
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());throw;}
        finally
        {
            if(active is not null)
            {
                try{final=active.ReadSceneStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();}catch(Exception error){Log(error.Message);}
                File.WriteAllText(Path.Combine(output,"last-status.json"),JsonSerializer.Serialize(final,options));
                var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(output,"recovery.json"),JsonSerializer.Serialize(recovery,options));Log($"SCENE_FINAL_RECOVERY confirmed={recovery.Confirmed}");
                if(recovery.Confirmed)active.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
