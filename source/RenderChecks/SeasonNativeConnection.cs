using System.Text.Json;
using System.IO;
using San14Tenkazu.Seasons;
using San14Tenkazu;

internal static partial class RenderChecks
{
    // Explicit native connection check. No browser, gameplay input, UI overlay,
    // date writes or fabricated rendering success. All outputs are journaled.
    private static void CheckSeasonNativeConnection(string[] args)
    {
        string Arg(string name){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException($"Missing {name}");}
        string build=Path.GetFullPath(Arg("--native-build")),output=Path.GetFullPath(Arg("--native-output"));
        if(Directory.Exists(output))throw new IOException("Native evidence output already exists");
        Directory.CreateDirectory(output);string journal=Path.Combine(output,"journal.log");
        void Log(string text){string line=$"{DateTime.UtcNow:O} {text}";File.AppendAllText(journal,line+Environment.NewLine);Console.WriteLine(line);}
        var game=GameMemory.FindGame();var baseline=GameMemory.ReadSeasonSnapshot(game);
        Log($"Baseline: PID={baseline.Identity.ProcessId}, date={baseline.Date.Year}/{baseline.Date.Month}/{baseline.Date.Day}, context=0x{baseline.Identity.Context:x}");
        var backend=new SeasonNativeBackend(game,build,Log,()=>GameSeasonSources.Read(game));var receipts=new List<object>();
        ISeasonNativeSession? active=null;
        try
        {
            for(ulong generation=1;generation<=3;generation++)
            {
                var snapshot=GameMemory.ReadSeasonSnapshot(game);
                active=backend.StartAsync(snapshot,generation,CancellationToken.None).AsTask().GetAwaiter().GetResult();
                active.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                bool rejected=false;
                try{active.ApplyAsync(SeasonParameters.At(snapshot.Date),CancellationToken.None).AsTask().GetAwaiter().GetResult();}
                catch(NotSupportedException e){rejected=true;Log($"Unimplemented rendering correctly rejected: {e.Message}");}
                if(!rejected)throw new InvalidDataException("Observation must not produce a season rendering receipt");
                var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();
                if(!recovery.Confirmed)throw new InvalidDataException("Native original recovery is incomplete");
                active.DisposeAsync().AsTask().GetAwaiter().GetResult();active=null;
                var after=GameMemory.ReadSeasonSnapshot(game);
                if(after!=snapshot)throw new InvalidDataException("Paused game snapshot changed during native connection check");
                receipts.Add(new{generation,recovery,renderingRejected=rejected});
            }
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{baseline,baselineDate=new{baseline.Date.Year,baseline.Date.Month,baseline.Date.Day},
                after=GameMemory.ReadSeasonSnapshot(game),sessions=receipts,actualNativeConnection=true,
                seasonApplied=false,fullSeason=false,productionReady=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());throw;}
        finally
        {
            if(active is not null){var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();Log($"Final recovery confirmed={recovery.Confirmed}");
                if(recovery.Confirmed)active.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        }
    }
    private static void CheckSeasonNativeTreeColor(string[] args)
    {
        string Arg(string name){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException($"Missing {name}");}
        string build=Path.GetFullPath(Arg("--native-build")),output=Path.GetFullPath(Arg("--native-output"));
        if(Directory.Exists(output))throw new IOException("Native evidence output already exists");Directory.CreateDirectory(output);
        void Log(string text){string line=$"{DateTime.UtcNow:O} {text}";File.AppendAllText(Path.Combine(output,"journal.log"),line+Environment.NewLine);Console.WriteLine(line);}
        var game=GameMemory.FindGame();var baseline=GameMemory.ReadSeasonSnapshot(game);var backend=new SeasonNativeBackend(game,build,Log,()=>GameSeasonSources.Read(game));
        Log($"Tree color baseline: PID={baseline.Identity.ProcessId}, date={baseline.Date.Year}/{baseline.Date.Month}/{baseline.Date.Day}");
        SeasonNativeSession? active=null;var receipts=new List<object>();
        try
        {
            for(ulong generation=1;generation<=3;generation++)
            {
                var snapshot=GameMemory.ReadSeasonSnapshot(game);
                active=(SeasonNativeSession)backend.StartAsync(snapshot,generation,CancellationToken.None).AsTask().GetAwaiter().GetResult();
                var color=active.ApplyTreeColorAsync(SeasonParameters.At(snapshot.Date),CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Log($"TREE_COLOR_ACTIVE generation={generation}");
                for(int second=0;second<10;second++)
                {
                    Thread.Sleep(1000);var date=GameMemory.ReadSeasonSnapshot(game);
                    if(date.Identity!=snapshot.Identity)throw new InvalidDataException("Game rendering identity changed during bounded material check");
                    if(date.Date!=snapshot.Date){color=active.ApplyTreeColorAsync(SeasonParameters.At(date.Date),CancellationToken.None).AsTask().GetAwaiter().GetResult();snapshot=date;}
                    else active.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    Log($"TREE_COLOR_HEARTBEAT generation={generation}, second={second+1}");
                }
                color=active.ReadTreeColorStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if(color.Error!=0||color.Swaps!=color.Restores||color.CompletedEpoch!=color.MaterialEpoch)
                    throw new InvalidDataException("The active material receipt is incomplete before restoration");
                Log($"TREE_COLOR_FINAL generation={generation}, draws={color.TreeDraws}, swaps/restores={color.Swaps}/{color.Restores}, generated={color.Generated}");
                var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();
                if(!recovery.Confirmed)throw new InvalidDataException("Native tree color recovery is incomplete");
                active.DisposeAsync().AsTask().GetAwaiter().GetResult();active=null;
                Log($"TREE_COLOR_RESTORED generation={generation}");receipts.Add(new{generation,color,date=new{snapshot.Date.Year,snapshot.Date.Month,snapshot.Date.Day},recovery});Thread.Sleep(2000);
            }
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{baseline.Identity,
                baselineDate=new{baseline.Date.Year,baseline.Date.Month,baseline.Date.Day},sessions=receipts,
                actualConnectedTreeColor=true,fullSeason=false,regionalClimate=false,leafRemoval=false,weather=false,productionReady=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());throw;}
        finally
        {
            if(active is not null){var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();Log($"Final tree recovery confirmed={recovery.Confirmed}");
                if(recovery.Confirmed)active.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        }
    }
    private static void CheckSeasonNativeTreeRecovery(string[] args)
    {
        string Arg(string name){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException($"Missing {name}");}
        string build=Path.GetFullPath(Arg("--native-build")),output=Path.GetFullPath(Arg("--native-output"));
        if(Directory.Exists(output))throw new IOException("Native evidence output already exists");Directory.CreateDirectory(output);
        void Log(string text){string line=$"{DateTime.UtcNow:O} {text}";File.AppendAllText(Path.Combine(output,"journal.log"),line+Environment.NewLine);Console.WriteLine(line);}
        var game=GameMemory.FindGame();var baseline=GameMemory.ReadSeasonSnapshot(game);var backend=new SeasonNativeBackend(game,build,Log,()=>GameSeasonSources.Read(game));
        SeasonNativeSession? active=null;var receipts=new List<object>();
        string[] causes=["pipe-disconnect","heartbeat-expiration","normal-stop-after-reconnect"];
        try
        {
            for(int index=0;index<causes.Length;index++)
            {
                ulong generation=(ulong)index+1;string cause=causes[index];var snapshot=GameMemory.ReadSeasonSnapshot(game);
                if(snapshot!=baseline)throw new InvalidDataException("Paused game snapshot changed before recovery check");
                active=(SeasonNativeSession)backend.StartAsync(snapshot,generation,CancellationToken.None).AsTask().GetAwaiter().GetResult();
                active.ApplyTreeColorAsync(SeasonParameters.At(snapshot.Date),CancellationToken.None).AsTask().GetAwaiter().GetResult();
                for(int second=0;second<2;second++){Thread.Sleep(1000);active.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();}
                var color=active.ReadTreeColorStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if(color.Error!=0||color.TreeDraws==0||color.Swaps!=color.Restores||color.CompletedEpoch!=color.MaterialEpoch)
                    throw new InvalidDataException("Recovery check has no active tree Draw receipt");
                Log($"TREE_RECOVERY_TRIGGER cause={cause}, draws={color.TreeDraws}, swaps/restores={color.Swaps}/{color.Restores}");
                var started=System.Diagnostics.Stopwatch.StartNew();
                var recovery=(index<2?active.CheckAutomaticRecoveryAsync(index==0):active.StopAsync()).AsTask().GetAwaiter().GetResult();
                started.Stop();if(!recovery.Confirmed)throw new InvalidDataException("Original restoration after recovery trigger is incomplete");
                active.DisposeAsync().AsTask().GetAwaiter().GetResult();active=null;
                if(GameMemory.ReadSeasonSnapshot(game)!=snapshot)throw new InvalidDataException("Paused game snapshot changed during recovery check");
                receipts.Add(new{generation,cause,date=new{snapshot.Date.Year,snapshot.Date.Month,snapshot.Date.Day},color,recovery,
                    stopMessageSent=index==2,recoveryWaitMilliseconds=started.Elapsed.TotalMilliseconds});
                Log($"TREE_RECOVERY_CONFIRMED cause={cause}, elapsedMs={started.Elapsed.TotalMilliseconds:F3}");Thread.Sleep(2000);
            }
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{baseline.Identity,
                baselineDate=new{baseline.Date.Year,baseline.Date.Month,baseline.Date.Day},sessions=receipts,
                actualTreeAutomaticRecovery=true,controllerTermination=false,fullSeason=false,productionReady=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());throw;}
        finally
        {
            if(active is not null){var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();Log($"Final tree recovery confirmed={recovery.Confirmed}");
                if(recovery.Confirmed)active.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        }
    }
    private static void CheckSeasonNativeTreeDate(string[] args)
    {
        string Arg(string name){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException($"Missing {name}");}
        string build=Path.GetFullPath(Arg("--native-build")),output=Path.GetFullPath(Arg("--native-output"));
        int seconds=int.Parse(Arg("--seconds"));if(seconds is <5 or >180)throw new ArgumentOutOfRangeException(nameof(seconds));
        if(Directory.Exists(output))throw new IOException("Native evidence output already exists");Directory.CreateDirectory(output);
        void Log(string text){string line=$"{DateTime.UtcNow:O} {text}";File.AppendAllText(Path.Combine(output,"journal.log"),line+Environment.NewLine);Console.WriteLine(line);}
        var game=GameMemory.FindGame();var baseline=GameMemory.ReadSeasonSnapshot(game);var backend=new SeasonNativeBackend(game,build,Log,()=>GameSeasonSources.Read(game));
        SeasonNativeSession? active=null;var receipts=new List<object>();var updates=new List<SeasonDate>();
        try
        {
            active=(SeasonNativeSession)backend.StartAsync(baseline,1,CancellationToken.None).AsTask().GetAwaiter().GetResult();
            SeasonBridgeProtocol.Reply? color=null;
            void Sample()
            {
                for(int retry=0;retry<8;retry++)
                {
                    var snapshot=GameMemory.ReadSeasonSnapshot(game);
                    if(snapshot.Identity!=baseline.Identity)throw new InvalidDataException("Rendering identity changed during the date check");
                    try
                    {
                        if(color is null||snapshot.Date!=color.Value.Date)
                        {
                            var parameters=SeasonParameters.At(snapshot.Date);
                            color=active.ApplyTreeColorAsync(parameters,CancellationToken.None).AsTask().GetAwaiter().GetResult();
                            updates.Add(snapshot.Date);receipts.Add(new{date=new{snapshot.Date.Year,snapshot.Date.Month,snapshot.Date.Day},
                                weights=new{parameters.Weights.Spring,parameters.Weights.Summer,parameters.Weights.Autumn,parameters.Weights.Winter},
                                color.Value.MaterialEpoch,color.Value.CompletedEpoch,color.Value.TreeDraws,color.Value.Swaps,color.Value.Restores,
                                color.Value.Generated,color.Value.GpuBytes,color.Value.AlphaPixels});
                            Log($"TREE_DATE_APPLIED date={snapshot.Date.Year}/{snapshot.Date.Month}/{snapshot.Date.Day}, epoch={color.Value.CompletedEpoch}, generated={color.Value.Generated}");
                        }
                        else active.HeartbeatAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                        Log($"TREE_DATE_TICK date={snapshot.Date.Year}/{snapshot.Date.Month}/{snapshot.Date.Day}");return;
                    }
                    catch(SeasonNativeDateChangedException changed){Log($"TREE_DATE_RETRY retry={retry}, {changed.Message}");}
                }
                throw new IOException("The current date could not be paired with an actual tree Draw after eight reads");
            }
            Sample();Log("TREE_DATE_READY stopFile=stop.request");
            using var pulse=new PeriodicTimer(TimeSpan.FromSeconds(1));
            for(int tick=0;tick<seconds&&pulse.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult();tick++)
            {
                Sample();if(File.Exists(Path.Combine(output,"stop.request")))break;
            }
            var final=active.ReadTreeColorStatusAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();
            if(!recovery.Confirmed)throw new InvalidDataException("Date check restoration is incomplete");
            active.DisposeAsync().AsTask().GetAwaiter().GetResult();active=null;
            bool monthBoundary=updates.Zip(updates.Skip(1)).Any(pair=>pair.First.Month!=pair.Second.Month||pair.First.Year!=pair.Second.Year);
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{baseline.Identity,
                baselineDate=new{baseline.Date.Year,baseline.Date.Month,baseline.Date.Day},updates=receipts,
                final=new{date=new{final.Date.Year,final.Date.Month,final.Date.Day},final.TreeDraws,final.Swaps,final.Restores,final.Generated,final.GpuBytes},
                recovery,readIntervalMilliseconds=1000,dateWrites=0,gameInputSent=false,actualDateProgress=updates.Count>1,
                actualMonthBoundary=monthBoundary,fullSeason=false,productionReady=false},new JsonSerializerOptions{WriteIndented=true}));
            Log($"TREE_DATE_RESTORED updates={updates.Count}, monthBoundary={monthBoundary}");
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());throw;}
        finally
        {
            if(active is not null){var recovery=active.StopAsync().AsTask().GetAwaiter().GetResult();Log($"Final tree recovery confirmed={recovery.Confirmed}");
                if(recovery.Confirmed)active.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        }
    }
}
