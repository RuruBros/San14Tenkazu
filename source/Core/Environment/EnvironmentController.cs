using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.EnvironmentRules;

internal enum EnvironmentPhase { Off,Connecting,Applying,On,Stopping,Faulted }
internal sealed record EnvironmentStatus(EnvironmentPhase Phase,EnvironmentEffects Requested,EnvironmentEffects Actual,
    bool Restored,EnvironmentProtocol.Reply? Receipt=null,string? Error=null);
internal interface IEnvironmentSession
{
    ValueTask<EnvironmentProtocol.Reply> ApplyAsync(EnvironmentSettings settings,CancellationToken token);
    ValueTask<EnvironmentProtocol.Reply> QueryAsync(CancellationToken token);
    ValueTask StopAsync();
}
internal interface IEnvironmentBackend
{
    ValueTask<IEnvironmentSession> StartAsync(CancellationToken token);
}
internal interface IEnvironmentNotificationSession{ValueTask CheckNotificationAsync(string text,CancellationToken token,int kind=0);}
#if LOCAL_DEVELOPER
internal interface IEnvironmentDeveloperSession {ValueTask<EnvironmentProtocol.Reply> TestAsync(int mode,int city,int kind,CancellationToken token);}
#endif
internal sealed class EnvironmentNativeBackend(GameIdentity identity,byte[] climate,string root,Action<string> log):IEnvironmentBackend
{
    public async ValueTask<IEnvironmentSession> StartAsync(CancellationToken token)
    {
        string directory=EnvironmentNativeBundle.Resolve(identity,root,log);
        using var owner=Process.GetCurrentProcess();
        ulong generation=BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8))|1;
        string name="san14-environment-"+Guid.NewGuid().ToString("N");
        var process=new SeasonNativeProcess(identity,directory,log,environment:true);
        var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        var session=new NativeSession(process,pipe,generation,climate,log);
        try
        {
            var input=new EnvironmentProtocol.Bootstrap{Magic=EnvironmentProtocol.Magic,Version=EnvironmentProtocol.Version,
                Size=(uint)Marshal.SizeOf<EnvironmentProtocol.Bootstrap>(),ControllerPid=(uint)owner.Id,
                ControllerStartFileTime=(ulong)owner.StartTime.ToUniversalTime().ToFileTimeUtc(),Module=identity.ModuleBase,Generation=generation,
                WorldClass=identity.Classes.World,UnitClass=identity.Classes.Unit,TileClass=identity.Classes.Tile,
                CountyClass=identity.Classes.County,CityClass=identity.Classes.City,Pipe="\\\\.\\pipe\\"+name};
            await GameMemory.FrameDispatchGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Run(()=>process.StartEnvironment(input,token),token).ConfigureAwait(false);
                await pipe.ConnectAsync(5000,token).ConfigureAwait(false);
            }
            finally{GameMemory.FrameDispatchGate.Release();}
            return session;
        }
        catch(Exception failure)
        {
            try{await session.StopAsync().ConfigureAwait(false);}
            catch(Exception recovery){throw new EnvironmentRecoveryException(session,failure,recovery);}
            throw;
        }
    }
    private sealed class NativeSession(SeasonNativeProcess process,NamedPipeClientStream pipe,ulong generation,byte[] climate,Action<string> log):IEnvironmentSession,IEnvironmentNotificationSession
#if LOCAL_DEVELOPER
        ,IEnvironmentDeveloperSession
#endif
    {
        private ulong sequence;private bool closed;
        public async ValueTask CheckNotificationAsync(string text,CancellationToken token,int kind=0)
        {if(text.Length is <1 or >200||kind is <0 or >4)throw new ArgumentOutOfRangeException(nameof(text));await Send(4,null,token,text,(uint)kind).ConfigureAwait(false);}
#if LOCAL_DEVELOPER
        public async ValueTask<EnvironmentProtocol.Reply> TestAsync(int mode,int city,int kind,CancellationToken token)
        {
            if(mode is <0 or >2||city is <1 or >46||kind is <0 or >4)throw new ArgumentOutOfRangeException(nameof(mode));
            var reply=await Send(5,null,token,reserved:(uint)(mode|(city<<8)|(kind<<16))).ConfigureAwait(false);ulong requested=sequence;
            for(int i=0;i<60&&(reply.TestId!=requested||mode==0&&reply.TestState==7);i++){await Task.Delay(50,token).ConfigureAwait(false);reply=await QueryAsync(token).ConfigureAwait(false);}
            if(reply.TestId!=requested||mode==0&&reply.TestState==7)throw new IOException("Developer request/recovery was not acknowledged by the game frame");return reply;
        }
#endif
        private async ValueTask<EnvironmentProtocol.Reply> Send(uint command,EnvironmentSettings? settings,CancellationToken token,string? notice=null,uint kind=0,uint reserved=0)
        {
            if(closed)throw new IOException("Environment session is closed");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(2500);
            var message=new EnvironmentProtocol.Message{Magic=EnvironmentProtocol.Magic,Version=EnvironmentProtocol.Version,
                Size=(uint)Marshal.SizeOf<EnvironmentProtocol.Message>(),Command=command,Generation=generation,Sequence=++sequence,
                Seed=settings?.Seed??1,Flags=(uint)(settings?.Effects??EnvironmentEffects.None),Values=settings?.Values??EnvironmentValues.Default,Climate=climate,Reserved=reserved};
            if(notice is not null){message.Reserved=kind;message.Climate=new byte[EnvironmentProtocol.ClimateBytes];System.Text.Encoding.Unicode.GetBytes(notice+'\0').CopyTo(message.Climate,0);}
            await pipe.WriteAsync(EnvironmentProtocol.Bytes(message),timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            byte[] bytes=new byte[Marshal.SizeOf<EnvironmentProtocol.Reply>()];
            await pipe.ReadExactlyAsync(bytes,timeout.Token).ConfigureAwait(false);
            var reply=EnvironmentProtocol.Read<EnvironmentProtocol.Reply>(bytes);
            if(reply.Magic!=EnvironmentProtocol.Magic||reply.Version!=EnvironmentProtocol.Version||reply.Size!=bytes.Length||
                reply.Generation!=generation||reply.Sequence!=sequence)throw new InvalidDataException("Environment reply identity differs");
            if(reply.Error!=0)throw new IOException($"Environment native error={reply.Error}");
            return reply;
        }
        public async ValueTask<EnvironmentProtocol.Reply> ApplyAsync(EnvironmentSettings settings,CancellationToken token)
        {
            settings.Validate();var reply=await Send(1,settings,token).ConfigureAwait(false);ulong requested=sequence;
            for(int i=0;i<60&&reply.Applied!=requested;i++)
            {await Task.Delay(50,token).ConfigureAwait(false);reply=await Send(2,null,token).ConfigureAwait(false);}
            if(reply.Applied!=requested||reply.Flags!=(uint)settings.Effects||reply.Values!=settings.Values)
                throw new IOException("Environment configuration was not acknowledged by the game frame");
            return reply;
        }
        public ValueTask<EnvironmentProtocol.Reply> QueryAsync(CancellationToken token)=>Send(2,null,token);
        public async ValueTask StopAsync()
        {
            await GameMemory.FrameDispatchGate.WaitAsync().ConfigureAwait(false);
            try{await StopLocked().ConfigureAwait(false);}finally{GameMemory.FrameDispatchGate.Release();}
        }
        private async ValueTask StopLocked()
        {
            if(!closed)
            {
                try{if(pipe.IsConnected)await Send(3,null,CancellationToken.None).ConfigureAwait(false);}
                catch(Exception error){log("Environment stop transport: "+error.Message);}
                finally{pipe.Dispose();closed=true;}
            }
            var result=await Task.Run(()=>process.RecoverEnvironment(generation)).ConfigureAwait(false);
            if(!result.Confirmed(generation))throw new IOException("Environment recovery remains unconfirmed");
            process.Dispose();
        }
    }
}
internal sealed class EnvironmentRecoveryException(IEnvironmentSession session,Exception start,Exception recovery):AggregateException("Retain environment ownership until recovery",start,recovery)
{
    internal IEnvironmentSession Session=>session;
}
internal sealed class EnvironmentController(IEnvironmentBackend backend,Action<string> log):IAsyncDisposable
{
    private readonly SemaphoreSlim gate=new(1);
    private readonly CancellationTokenSource lifetime=new();
    private IEnvironmentSession? session;
    private Task? monitor;
    private ulong lastEvent;private byte[]? weather;private bool disposed;
    private ulong noticesQueued;
    private (ulong Id,uint State,uint Error,uint Reserved) lastTest;
    internal EnvironmentStatus Status {get;private set;}=new(EnvironmentPhase.Off,EnvironmentEffects.None,EnvironmentEffects.None,true);
    internal event Action<EnvironmentStatus>? StatusChanged;
    internal event Action<EnvironmentProtocol.Event>? EventRecorded;
    private void Set(EnvironmentStatus value){Status=value;StatusChanged?.Invoke(value);}
    private void Record(EnvironmentProtocol.Reply reply)
    {
        var test=(reply.TestId,reply.TestState,reply.TestError,reply.Reserved);
        if(test!=lastTest){lastTest=test;log("Environment developer test: "+JsonSerializer.Serialize(new{reply.TestId,reply.TestMode,reply.TestState,reply.TestCity,reply.TestKind,reply.TestDate,reply.TestError,reply.LastEvent,reply.Reserved,reply.DiagnosticSoundId,reply.DiagnosticSoundChannel,reply.DiagnosticSoundState}));}
        if(reply.LastEvent<lastEvent)throw new InvalidDataException("Environment event sequence moved backwards");
        if(reply.LastEvent-lastEvent>EnvironmentProtocol.EventCount)throw new IOException("Environment event journal overflow; native history was not fully observed");
        foreach(var item in reply.Events.Where(v=>v.Id>lastEvent&&v.Id<=reply.LastEvent).OrderBy(v=>v.Id))
        {
            if(item.Id!=lastEvent+1)throw new InvalidDataException("Environment event journal has a gap");
            log("Environment event: "+JsonSerializer.Serialize(new{item.Id,item.Date,item.Kind,item.City,item.Tile,item.Unit,item.Commander,
                item.TroopsBefore,item.TroopsAfter,item.MoraleBefore,item.MoraleAfter,item.FoodBefore,item.FoodAfter}));
            EventRecorded?.Invoke(item);lastEvent=item.Id;
        }
        if(lastEvent!=reply.LastEvent)throw new InvalidDataException("Environment event receipt is incomplete");
        if(reply.NoticesQueued!=noticesQueued){noticesQueued=reply.NoticesQueued;log("Environment native notice: "+JsonSerializer.Serialize(new{reply.NoticesQueued,reply.NoticesPending,reply.NoticeDate,reply.NoticeCity,reply.NoticeKind}));}
        if(weather is null||!weather.SequenceEqual(reply.Weather))
        {weather=reply.Weather.ToArray();log($"Environment weather: date={EnvironmentProtocol.Date(reply.Date)}, regions={Convert.ToHexString(weather)}");}
    }
    internal async Task ApplyAsync(EnvironmentSettings settings,CancellationToken token=default,bool keepEmptySession=false)
    {
        settings.Validate();ObjectDisposedException.ThrowIf(disposed,this);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(settings.Effects==EnvironmentEffects.None&&!keepEmptySession){await StopLocked().ConfigureAwait(false);return;}
            Set(Status with{Phase=session is null?EnvironmentPhase.Connecting:EnvironmentPhase.Applying,Requested=settings.Effects,Error=null});
            if(session is null){session=await backend.StartAsync(token).ConfigureAwait(false);lastEvent=0;weather=null;noticesQueued=0;}
            Set(Status with{Phase=EnvironmentPhase.Applying,Restored=false});
            var reply=await session.ApplyAsync(settings,token).ConfigureAwait(false);Record(reply);
            log($"Environment applied: generation={reply.Generation}, applied={reply.Applied}, flags={reply.Flags}, values={JsonSerializer.Serialize(reply.Values)}, thread={reply.Thread}, frames={reply.Frames}");
            Set(new(EnvironmentPhase.On,settings.Effects,(EnvironmentEffects)reply.Flags,false,reply));
            monitor??=MonitorAsync();
        }
        catch(Exception error)
        {
            if(error is EnvironmentRecoveryException recovery)session=recovery.Session;
            await FailLocked(error).ConfigureAwait(false);throw;
        }
        finally{gate.Release();}
    }
#if LOCAL_DEVELOPER
    internal async Task TestAsync(int mode,int city,int kind,CancellationToken token=default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            if(session is not IEnvironmentDeveloperSession developer)throw new InvalidOperationException("Developer session is not ready");
            var reply=await developer.TestAsync(mode,city,kind,token).ConfigureAwait(false);Record(reply);Set(Status with{Receipt=reply});
        }
        catch(Exception error){await FailLocked(error).ConfigureAwait(false);throw;}
        finally{gate.Release();}
    }
#endif
    private async Task MonitorAsync()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while(await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if(session is null)continue;
                    var reply=await session.QueryAsync(lifetime.Token).ConfigureAwait(false);Record(reply);
                    Set(Status with{Actual=(EnvironmentEffects)reply.Flags,Receipt=reply});
                }
                catch(OperationCanceledException) when(lifetime.IsCancellationRequested){break;}
                catch(Exception error){await FailLocked(error).ConfigureAwait(false);}
                finally{gate.Release();}
            }
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    }
    private async Task FailLocked(Exception error)
    {
        log("Environment failure: "+error);
        bool restored=session is null;
        if(session is not null)try{await session.StopAsync().ConfigureAwait(false);session=null;restored=true;}
        catch(Exception recovery){error=new AggregateException(error,recovery);log("Environment recovery failure: "+recovery);}
        Set(new(EnvironmentPhase.Faulted,Status.Requested,restored?EnvironmentEffects.None:Status.Actual,restored,Status.Receipt,error.Message));
    }
    private async Task StopLocked()
    {
        Set(Status with{Phase=EnvironmentPhase.Stopping,Requested=EnvironmentEffects.None});
        if(session is not null){await session.StopAsync().ConfigureAwait(false);session=null;}
        Set(new(EnvironmentPhase.Off,EnvironmentEffects.None,EnvironmentEffects.None,true));
    }
    internal async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try{await StopLocked().ConfigureAwait(false);}
        catch(Exception error){Set(Status with{Phase=EnvironmentPhase.Faulted,Restored=false,Error=error.Message});throw;}
        finally{gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;await StopAsync().ConfigureAwait(false);lifetime.Cancel();
        if(monitor is not null)await monitor.ConfigureAwait(false);disposed=true;lifetime.Dispose();gate.Dispose();
    }
}
