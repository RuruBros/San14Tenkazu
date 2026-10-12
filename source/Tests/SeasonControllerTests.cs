using System.Threading.Channels;
using San14Tenkazu.Seasons;
using Xunit.Abstractions;

namespace San14Tenkazu.Tests;

public class SeasonControllerTests(ITestOutputHelper output)
{
    private static SeasonDate Date(int year=194,int month=10,int day=1)
    {
        Assert.True(SeasonDate.TryCreate(year,month,day,out var date));return date;
    }
    private static readonly SeasonRenderIdentity Identity=new(42,new DateTime(2026,10,6,0,0,0,DateTimeKind.Utc),
        0x100000,0x200000,0x210000,0x220000,0x230000,0x240000,0x250000,0x260000);
    [Fact]
    public async Task WeatherOnlyAndSeasonalModesShareOneRecoverableSession()
    {
        await using var fixture=new Fixture();var controller=fixture.Controller;
        await controller.SetSeasonalAsync(false);await controller.StartAsync();
        var session=Assert.Single(fixture.Native.Sessions);Assert.False(Assert.Single(session.Applied).Seasonal);
        Assert.Equal(SeasonScope.Weather,controller.Status.Scope);Assert.Equal(SeasonPhase.On,controller.Status.Phase);
        await controller.SetSeasonalAsync(true);Assert.True(session.Applied[^1].Seasonal);Assert.Equal(SeasonScope.Full,controller.Status.Scope);
        await controller.SetSeasonalAsync(false);Assert.False(session.Applied[^1].Seasonal);Assert.Single(fixture.Native.Sessions);
        await controller.StopAsync();Assert.True(controller.Status.OriginalRestored);Assert.Equal(SeasonPhase.Off,controller.Status.Phase);
    }
    private sealed class Source : ISeasonDateSource
    {
        internal SeasonSnapshot Current=new(Identity,Date());
        internal int Reads;
        internal bool Fail;
        public ValueTask<SeasonSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Interlocked.Increment(ref Reads);
            if (Fail) throw new IOException("Date source disconnected");return ValueTask.FromResult(Current);
        }
    }
    private sealed class Pulse : ISeasonPulse
    {
        private readonly Channel<bool> channel=Channel.CreateUnbounded<bool>();
        internal void Tick() => channel.Writer.TryWrite(true);
        public async ValueTask<bool> WaitAsync(CancellationToken token) => await channel.Reader.ReadAsync(token);
        public ValueTask DisposeAsync() { channel.Writer.TryComplete();return ValueTask.CompletedTask; }
    }
    private sealed class Native : ISeasonNativeBackend
    {
        internal readonly List<Session> Sessions=[];
        internal readonly List<string> Calls=[];
        internal Action<Session>? Configure;
        internal bool FailStart;
        public ValueTask<ISeasonNativeSession> StartAsync(SeasonSnapshot snapshot,ulong generation,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add("start:"+generation);
            if (FailStart) throw new IOException("No native renderer connection");
            var session=new Session(this,snapshot.Identity,generation);Configure?.Invoke(session);Sessions.Add(session);
            return ValueTask.FromResult<ISeasonNativeSession>(session);
        }
    }
    private sealed class Session(Native owner,SeasonRenderIdentity identity,ulong generation) : ISeasonNativeSession
    {
        public SeasonRenderIdentity Identity { get; internal set; }=identity;
        public ulong Generation { get; internal set; }=generation;
        internal readonly List<SeasonParameters> Applied=[];
        internal bool Rendered=true,Restored=true,FailApply,FailHeartbeat,Disposed,Pending;
        internal Action? DateRace;
        internal Func<SeasonApplyReceipt,SeasonApplyReceipt>? ModifyReceipt;
        internal Func<SeasonRestoreReceipt,SeasonRestoreReceipt>? ModifyRestoreReceipt;
        internal Action? OnStop;
        internal TaskCompletionSource? ApplyEntered,ContinueApply;
        internal int Heartbeats;
        public async ValueTask<SeasonApplyReceipt> ApplyAsync(SeasonParameters parameters,CancellationToken token)
        {
            owner.Calls.Add("apply:"+Generation);Applied.Add(parameters);
            ApplyEntered?.TrySetResult();
            if (ContinueApply is not null) await ContinueApply.Task.WaitAsync(token);
            if (FailApply) throw new IOException("Native shader rejected");
            if(Pending)throw new SeasonNativeDrawPendingException();
            if(DateRace is {} race){DateRace=null;race();throw new SeasonNativeDateChangedException(parameters.Date,Date(month:7));}
            var receipt=new SeasonApplyReceipt(Generation,parameters.Date,(ulong)Applied.Count,Rendered);
            return ModifyReceipt?.Invoke(receipt) ?? receipt;
        }
        public ValueTask HeartbeatAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if (FailHeartbeat) throw new IOException("Native lease expired");
            Interlocked.Increment(ref Heartbeats);return ValueTask.CompletedTask;
        }
        public ValueTask<SeasonRestoreReceipt> StopAsync()
        {
            owner.Calls.Add("stop:"+Generation);OnStop?.Invoke();
            var receipt=new SeasonRestoreReceipt(Generation,Restored,Restored,Restored,Restored,Restored);
            return ValueTask.FromResult(ModifyRestoreReceipt?.Invoke(receipt) ?? receipt);
        }
        public ValueTask DisposeAsync() { Disposed=true;owner.Calls.Add("dispose:"+Generation);return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Source Source=new();
        internal readonly Native Native;
        internal readonly List<Pulse> Pulses=[];
        internal readonly SeasonController Controller;
        internal Fixture()
        {
            Native=new();Controller=new(Source,Native,()=>{var pulse=new Pulse();lock(Pulses)Pulses.Add(pulse);return pulse;});
        }
        internal async Task<Pulse> PulseAsync()
        {
            for(int i=0;i<100;i++) { lock(Pulses)if(Pulses.Count>0)return Pulses[^1];await Task.Delay(1); }
            throw new TimeoutException("Watcher did not start");
        }
        public async ValueTask DisposeAsync()
        {
            foreach(var session in Native.Sessions){session.Restored=true;session.ModifyRestoreReceipt=null;}
            await Controller.DisposeAsync();
        }
    }
    private static Task<SeasonStatus> Next(SeasonController controller,Func<SeasonStatus,bool> predicate)
    {
        var completed=new TaskCompletionSource<SeasonStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<SeasonStatus>? observer=null;
        observer=status=>{if(predicate(status)){controller.StatusChanged-=observer;completed.TrySetResult(status);}};
        controller.StatusChanged+=observer;return completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task FullPassPendingKeepsRequestAndDateRaceRereadsBeforeAcknowledging()
    {
        await using var f=new Fixture();f.Native.Configure=s=>s.Pending=true;
        await f.Controller.StartAsync();var session=Assert.Single(f.Native.Sessions);
        Assert.Equal(SeasonPhase.Applying,f.Controller.Status.Phase);Assert.True(f.Controller.Status.Requested);
        Assert.False(session.Disposed);session.Pending=false;
        session.DateRace=()=>f.Source.Current=new(Identity,Date(month:7));
        await f.Controller.UpdateDate(f.Source.Current);
        Assert.Equal(SeasonPhase.On,f.Controller.Status.Phase);Assert.Equal(Date(month:7),f.Controller.Status.Date);
        Assert.Single(f.Native.Sessions);Assert.Equal(Date(month:7),session.Applied[^1].Date);
        await f.Controller.StopAsync();Assert.True(f.Controller.Status.OriginalRestored);Assert.True(session.Disposed);
    }
    [Fact]
    public async Task StartsOffAppliesCurrentDateImmediatelyAndStopsOnlyAfterConfirmedRecovery()
    {
        await using var f=new Fixture();Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);
        await f.Controller.StartAsync();Assert.Equal(SeasonPhase.On,f.Controller.Status.Phase);Assert.Equal(1,f.Source.Reads);
        Assert.Equal(f.Source.Current.Date,Assert.Single(f.Native.Sessions).Applied.Single().Date);
        await f.Controller.StopAsync();Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);Assert.True(f.Controller.Status.OriginalRestored);
        Assert.True(f.Native.Sessions[0].Disposed);Assert.Equal(new[]{"start:1","apply:1","stop:1","dispose:1"},f.Native.Calls);
    }
    [Fact]
    public async Task DateProgressYearWrapAndBackwardLoadResampleWithoutReconnectingOrAllocatingSessions()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();
        foreach(var date in new[]{Date(194,12,30),Date(195,1,1),Date(193,3,11)})
            await f.Controller.UpdateDate(new(Identity,date));
        var session=Assert.Single(f.Native.Sessions);Assert.Equal(4,session.Applied.Count);
        Assert.Equal(SeasonTimeline.At(Date(193,3,11)),session.Applied[^1].Month);
        Assert.Equal(SeasonPhase.On,f.Controller.Status.Phase);
    }
    [Fact]
    public async Task WatcherReadsDatesWithoutMapMovementAndUnchangedDateOnlyRenewsLease()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();var pulse=await f.PulseAsync();
        f.Source.Current=new(Identity,Date(195,1,11));
        Task<SeasonStatus> changed=Next(f.Controller,s=>s.Phase==SeasonPhase.On && s.Date==f.Source.Current.Date);
        pulse.Tick();await changed;Assert.Equal(2,f.Source.Reads);Assert.Equal(2,f.Native.Sessions[0].Applied.Count);
        pulse.Tick();
        for(int i=0;i<100 && f.Native.Sessions[0].Heartbeats==0;i++)await Task.Delay(1);
        Assert.Equal(1,f.Native.Sessions[0].Heartbeats);Assert.Equal(2,f.Native.Sessions[0].Applied.Count);
    }
    [Fact]
    public async Task SceneChangeRestoresOldSessionAndRereadsAfterRecoveryBeforeConnecting()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();
        var next=Identity with { Scene=0x330000,Context=0x360000,Device=0x350000 };
        f.Source.Current=new(next,Date(195,2,1));
        f.Native.Sessions[0].OnStop=()=>f.Source.Current=new(next with { Scene=0x430000 },Date(195,3,21));
        await f.Controller.UpdateDate(new(next,Date(195,2,1)));
        Assert.True(f.Native.Sessions[0].Disposed);Assert.Equal(2,f.Native.Sessions.Count);
        Assert.Equal(f.Source.Current.Identity,f.Native.Sessions[1].Identity);
        Assert.Equal(Date(195,3,21),f.Controller.Status.Date);Assert.Equal((ulong)2,f.Controller.Status.Generation);
        Assert.Equal(new[]{"start:1","apply:1","stop:1","dispose:1","start:2","apply:2"},f.Native.Calls);
    }
    [Fact]
    public async Task FailedRestorationRetainsReferencesRefusesOffAndPreventsAnotherConnection()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();var session=f.Native.Sessions[0];session.Restored=false;
        await f.Controller.StopAsync();Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);Assert.False(f.Controller.Status.OriginalRestored);
        Assert.False(session.Disposed);await f.Controller.StartAsync();Assert.Single(f.Native.Sessions);
        session.Restored=true;await f.Controller.StopAsync();Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);Assert.True(session.Disposed);
    }
    [Theory]
    [InlineData("callbacks")] [InlineData("gpu")] [InlineData("dispatch")]
    [InlineData("protection")] [InlineData("resources")] [InlineData("generation")]
    public async Task EveryMissingNativeRestorationConfirmationPreventsOff(string field)
    {
        await using var f=new Fixture();await f.Controller.StartAsync();
        var session=f.Native.Sessions[0];session.ModifyRestoreReceipt=receipt=>field switch
        {
            "callbacks"=>receipt with { CallbacksDrained=false },"gpu"=>receipt with { OriginalGpuState=false },
            "dispatch"=>receipt with { DispatchDetached=false },"protection"=>receipt with { ProtectionRestored=false },
            "resources"=>receipt with { ResourcesReleased=false },_=>receipt with { Generation=99 }
        };
        await f.Controller.StopAsync();Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);
        Assert.False(f.Controller.Status.OriginalRestored);Assert.False(session.Disposed);
    }
    [Fact]
    public async Task SceneChangeCannotConnectNewGpuUntilOldRecoveryIsConfirmed()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();f.Native.Sessions[0].Restored=false;
        await f.Controller.UpdateDate(new(Identity with { Context=0x360000 },Date()));
        Assert.Single(f.Native.Sessions);Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);
        Assert.False(f.Native.Sessions[0].Disposed);Assert.False(f.Controller.Status.OriginalRestored);
    }
    [Theory]
    [InlineData("rendered")] [InlineData("generation")] [InlineData("date")] [InlineData("frame")]
    public async Task IncorrectNativeRenderingReceiptNeverBecomesOn(string field)
    {
        await using var f=new Fixture();f.Native.Configure=session=>session.ModifyReceipt=receipt=>field switch
        {
            "rendered"=>receipt with { Rendered=false },"generation"=>receipt with { Generation=99 },
            "date"=>receipt with { Date=Date(193,1,1) },_=>receipt with { Frame=0 }
        };
        var phases=new List<SeasonPhase>();f.Controller.StatusChanged+=s=>phases.Add(s.Phase);
        await f.Controller.StartAsync();Assert.DoesNotContain(SeasonPhase.On,phases);
        Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);Assert.True(f.Controller.Status.OriginalRestored);
        Assert.True(f.Native.Sessions[0].Disposed);
    }
    [Fact]
    public async Task InvalidDatesAndDisconnectedSourceStopAndRestoreInsteadOfFeedingNativeParameters()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();
        await f.Controller.UpdateDate(new(Identity,default));Assert.Single(f.Native.Sessions[0].Applied);
        Assert.True(f.Native.Sessions[0].Disposed);Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);
        f.Source.Fail=true;await f.Controller.StartAsync();Assert.Single(f.Native.Sessions);
        Assert.True(f.Controller.Status.OriginalRestored);
    }
    [Fact]
    public async Task StopCancelsAnInflightApplyAndDrainsBeforeOffWithoutDeadlockingTheWatcher()
    {
        await using var f=new Fixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Native.Configure=s=>{s.ApplyEntered=entered;s.ContinueApply=new(TaskCreationOptions.RunContinuationsAsynchronously);};
        Task start=f.Controller.StartAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));await start;
        Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);Assert.True(f.Native.Sessions[0].Disposed);
        Assert.DoesNotContain(f.Native.Sessions[0].Applied,parameters=>!parameters.Date.IsValid);
    }
    [Fact]
    public async Task ExpiredHeartbeatAndWatcherReadFailureTriggerRecovery()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();f.Native.Sessions[0].FailHeartbeat=true;
        Task<SeasonStatus> failed=Next(f.Controller,s=>s.Phase==SeasonPhase.Faulted);(await f.PulseAsync()).Tick();await failed;
        Assert.True(f.Native.Sessions[0].Disposed);Assert.True(f.Controller.Status.OriginalRestored);
        await f.Controller.StopAsync();await f.Controller.StartAsync();f.Source.Fail=true;
        failed=Next(f.Controller,s=>s.Phase==SeasonPhase.Faulted);
        // Wait for the new watcher, not the previous pulse disposed during OFF.
        for(int i=0;i<100 && f.Pulses.Count<2;i++)await Task.Delay(1);
        f.Pulses[^1].Tick();await failed;Assert.True(f.Native.Sessions[1].Disposed);
    }
    [Fact]
    public async Task RepeatedOnOffDoesNotReuseOldGenerationOrRetainStoppedSessions()
    {
        await using var f=new Fixture();
        for(int i=0;i<20;i++){await f.Controller.StartAsync();await f.Controller.StartAsync();await f.Controller.StopAsync();}
        Assert.Equal(20,f.Native.Sessions.Count);Assert.All(f.Native.Sessions,s=>Assert.True(s.Disposed));
        Assert.Equal(Enumerable.Range(1,20).Select(i=>(ulong)i),f.Native.Sessions.Select(s=>s.Generation));
    }
    [Fact]
    public async Task NativeConnectionMismatchAndShaderFailureAreRestoredAndReported()
    {
        await using var f=new Fixture();f.Native.Configure=s=>s.Identity=Identity with { Context=0xdead };
        await f.Controller.StartAsync();Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);
        Assert.Empty(f.Native.Sessions[0].Applied);Assert.True(f.Native.Sessions[0].Disposed);
        f.Native.Configure=s=>s.FailApply=true;await f.Controller.StartAsync();
        Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);Assert.True(f.Native.Sessions[1].Disposed);
        Assert.Contains("shader",f.Controller.Status.Error!);
    }
    [Fact]
    public async Task StatusObserverFailureDoesNotInterruptNativeRecovery()
    {
        await using var f=new Fixture();f.Controller.StatusChanged+=_=>throw new Exception("UI observer failed");
        await f.Controller.StartAsync();await f.Controller.StopAsync();
        Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);Assert.True(f.Native.Sessions[0].Disposed);
    }
    [Fact]
    public async Task UnavailableNativeBackendDoesNotPretendToRender()
    {
        await using var f=new Fixture();f.Native.FailStart=true;await f.Controller.StartAsync();
        Assert.Empty(f.Native.Sessions);Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);
        Assert.True(f.Controller.Status.OriginalRestored);
    }
    [Fact]
    public async Task ProductionDateSourceReadsTheCurrentVerifiedGameWithoutWriting()
    {
        if (!OperatingSystem.IsWindows() || !GameMemory.IsRunning()) return;
        var identity=GameMemory.FindGame();if(!identity.Verified)return;
        var snapshot=await new GameSeasonDateSource(identity).ReadAsync(CancellationToken.None);snapshot.Validate();
        Assert.Equal(identity.ProcessId,snapshot.Identity.ProcessId);Assert.Equal(identity.StartTimeUtc,snapshot.Identity.ProcessStartUtc);
        Assert.Equal(identity.ModuleBase,snapshot.Identity.ModuleBase);Assert.True(snapshot.Date.IsValid);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            mode="Read-only production date source; no Hook or GPU state changes",pid=identity.ProcessId,
            year=snapshot.Date.Year,month=snapshot.Date.Month,day=snapshot.Date.Day,
            world=$"0x{snapshot.Identity.World:x}",scene=$"0x{snapshot.Identity.Scene:x}",
            device=$"0x{snapshot.Identity.Device:x}",context=$"0x{snapshot.Identity.Context:x}"
        }));
    }
}
