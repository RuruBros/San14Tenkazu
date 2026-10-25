#if LOCAL_DEVELOPER
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;
namespace San14Tenkazu;

// Evidence for an explicitly requested preview; it never writes game memory.
internal sealed class DeveloperNoticeDiagnostic
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    internal GameIdentity Identity {get;}
    internal GameLayout? Layout {get;}
    internal string DirectoryPath {get;}
    internal string CapturePath=>Path.Combine(DirectoryPath,"weather-capture.bin");
    internal string TracePath=>CapturePath+".notice.json";
    internal string ResultPath=>Path.Combine(DirectoryPath,"result.json");
    private readonly string before;
    private readonly int city,kind;
    private readonly List<object> receipts=[];
    private string? lastState;
    private ulong? initialEvent,lastEvent;
    private readonly long started;
    private double? controllerDeadlineElapsedSeconds;
    internal bool DeadlineExpired=>System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds>=120;
    internal void RecordControllerDeadline()
    {
        double elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        if(elapsed<120)throw new InvalidOperationException("Controller deadline has not expired");
        controllerDeadlineElapsedSeconds??=elapsed;
    }
    internal DeveloperNoticeDiagnostic(string root,GameIdentity identity,GameSnapshot data,int city,int kind,GameLayout? layout=null,long started=0)
    {
        if(!EnvironmentPolicy.EffectCity(city)||!data.CityIds.Contains(city)||kind is <0 or >4)throw new InvalidOperationException("Invalid loaded preview target");
        Identity=identity;Layout=layout;this.city=city;this.kind=kind;before=Gameplay(data);this.started=started==0?System.Diagnostics.Stopwatch.GetTimestamp():started;
        DirectoryPath=Path.Combine(root,$"notice-art-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{identity.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        Write("initial.json",new{formatVersion=1,utc=DateTime.UtcNow,identity.ProcessId,identity.StartTimeUtc,identity.ExeHash,
            city,name=data.CityName(city),kind,mode=1,gameplayEffectsEnabled=0,maximumSeconds=120,
            environmentDll=EnvironmentNativeBundle.DllHash,seasonDll=SeasonNativeBundle.DllHash,
            before=JsonSerializer.Deserialize<JsonElement>(before),CapturePath,TracePath});
    }
    internal byte[] PrepareScene(byte[] scene)=>SeasonSceneData.WithWeatherCapture(scene,CapturePath);
    internal void Observe(EnvironmentStatus status)
    {
        if(status.Receipt is not {} r)return;
        initialEvent??=r.LastEvent;lastEvent=r.LastEvent;
        string state=$"{r.TestId}/{r.TestMode}/{r.TestState}/{r.TestCity}/{r.TestKind}/{r.TestError}/{r.LastEvent}";
        if(state==lastState||receipts.Count>=32)return;lastState=state;
        receipts.Add(new{utc=DateTime.UtcNow,r.Generation,r.Sequence,r.Frames,r.Thread,r.Date,r.Flags,r.Error,
            r.TestId,r.TestMode,r.TestState,r.TestCity,r.TestKind,r.TestDate,r.TestError,r.LastEvent});
    }
    private static string Gameplay(GameSnapshot data)=>JsonSerializer.Serialize(new{
        date=data.Date is {} d?new[]{d.Year,d.Month,d.Day}:null,data.Hour,
        cities=data.CityIds.Select(id=>new{id,gold=data.Cities[id][CityFields.Gold],food=data.Cities[id][CityFields.Food]}),
        units=data.Units.OrderBy(u=>u.Slot).Select(u=>new{u.Slot,u.Commander,troops=u[UnitFields.Troops],morale=u[UnitFields.Morale]})});
    internal void Complete(GameSnapshot? after,EnvironmentStatus? environment,SeasonStatus? renderer,string? readError=null,bool ownerRestored=false)
    {
        JsonElement? trace=null;string? traceError=null;bool imageSwapped=false,bindingRestored=false;
        try{
            // v2 adds at most 64 gate groups and 128 correlated binding groups.
            if(new FileInfo(TracePath).Length>256*1024)throw new InvalidDataException("Notice trace exceeds its bounded format");
            using var document=JsonDocument.Parse(File.ReadAllText(TracePath));trace=document.RootElement.Clone();
            var calls=trace.Value.GetProperty("kindCalls");var swaps=trace.Value.GetProperty("kindSwaps");var restores=trace.Value.GetProperty("kindRestores");
            if(trace.Value.GetProperty("stages").GetArrayLength()!=16||calls.GetArrayLength()!=6||swaps.GetArrayLength()!=6||restores.GetArrayLength()!=6)
                throw new InvalidDataException("Notice trace layout differs");
            int visual=kind+1;imageSwapped=visual>=2&&calls[visual].GetUInt64()>0&&swaps[visual].GetUInt64()>0;
            bindingRestored=imageSwapped&&swaps[visual].GetUInt64()==restores[visual].GetUInt64();
        }catch(Exception error){traceError=error.Message;}
        string? current=after is null?null:Gameplay(after);
        object? causal=null;string? causalError=null;
        try{causal=DeveloperNoticeCausality.Read(TracePath+".causal.bin");}catch(Exception error){causalError=error.Message;}
        Write("result.json",new{formatVersion=1,utc=DateTime.UtcNow,Identity.ProcessId,Identity.StartTimeUtc,city,kind,expectedVisualKind=kind+1,
            mode=1,receipts,initialEvent,lastEvent,gameplayUnchanged=current is not null&&current==before,
            controllerStopReason=controllerDeadlineElapsedSeconds.HasValue?"OverallDeadline":null,controllerDeadlineElapsedSeconds,
            noNewDamageEvent=initialEvent.HasValue&&lastEvent==initialEvent,readError,
            after=current is null?(JsonElement?)null:JsonSerializer.Deserialize<JsonElement>(current),
            environmentRestored=ownerRestored||environment?.Restored==true,rendererRestored=ownerRestored||renderer is{OriginalRestored:true,Requested:false},
            originalFloodExpected=kind==0,imageSwapped,bindingRestored,screenVerified=false,traceError,trace,causal,causalError,
            traceSha256=File.Exists(TracePath)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(TracePath))):null});
    }
    private void Write(string name,object value)
    {
        using var output=new FileStream(Path.Combine(DirectoryPath,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(output,value,Json);
    }
}
#endif
