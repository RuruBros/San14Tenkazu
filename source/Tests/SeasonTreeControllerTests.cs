using System.Threading.Channels;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonTreeControllerTests
{
    private static readonly SeasonRenderIdentity Identity=new(42,new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc),1,2,3,4,5,6,7,8);
    private static SeasonDate Date(int year=195,int month=5,int day=1)
    {Assert.True(SeasonDate.TryCreate(year,month,day,out var date));return date;}
    private sealed class Source:ISeasonDateSource
    {
        internal SeasonSnapshot Current=new(Identity,Date());
        internal int Reads;
        public ValueTask<SeasonSnapshot> ReadAsync(CancellationToken token)
        {token.ThrowIfCancellationRequested();Reads++;return ValueTask.FromResult(Current);}
    }
    private sealed class Pulse:ISeasonPulse
    {
        private readonly Channel<bool> ticks=Channel.CreateUnbounded<bool>();
        internal void Tick()=>ticks.Writer.TryWrite(true);
        public async ValueTask<bool> WaitAsync(CancellationToken token)=>await ticks.Reader.ReadAsync(token);
        public ValueTask DisposeAsync(){ticks.Writer.TryComplete();return ValueTask.CompletedTask;}
    }
    private sealed class Native:ISeasonNativeBackend
    {
        internal readonly List<Session> Sessions=[];
        internal Action<Session>? Configure;
        public ValueTask<ISeasonNativeSession> StartAsync(SeasonSnapshot snapshot,ulong generation,CancellationToken token)
        {
            var session=new Session(snapshot.Identity,generation);Configure?.Invoke(session);Sessions.Add(session);
            return ValueTask.FromResult<ISeasonNativeSession>(session);
        }
    }
    private sealed class Session(SeasonRenderIdentity identity,ulong generation):ISeasonNativeSession
    {
        public SeasonRenderIdentity Identity=>identity;
        public ulong Generation=>generation;
        internal readonly List<SeasonParameters> Applied=[];
        internal bool Disposed,Restored=true;
        internal int FullCalls,Heartbeats,Races;
        internal Action? OnRace,OnStop;
        internal Func<SeasonTreeColorReceipt,SeasonTreeColorReceipt>? Receipt;
        internal TaskCompletionSource? Entered;
        internal bool Block;
        internal bool RaceStopsSession;
        internal bool Pending;
        public ValueTask<SeasonApplyReceipt> ApplyAsync(SeasonParameters parameters,CancellationToken token)
        {FullCalls++;throw new NotSupportedException("Full renderer remains unsupported");}
        public async ValueTask<SeasonTreeColorReceipt> ApplyTreeColorAsync(SeasonParameters parameters,CancellationToken token)
        {
            Applied.Add(parameters);Entered?.TrySetResult();if(Block)await Task.Delay(Timeout.Infinite,token);
            token.ThrowIfCancellationRequested();
            if(Pending)throw new SeasonNativeDrawPendingException();
            if(Races-->0){OnRace?.Invoke();throw new SeasonNativeDateChangedException(parameters.Date,Date(month:6),RaceStopsSession);}
            ulong n=(ulong)Applied.Count;
            var color=new SeasonTreeColorReceipt(Generation,parameters.Date,n,n,n*3,n*3,n*3,1,174752,1048512,true,true,false);
            return Receipt?.Invoke(color)??color;
        }
        public ValueTask HeartbeatAsync(CancellationToken token){token.ThrowIfCancellationRequested();Heartbeats++;return ValueTask.CompletedTask;}
        public ValueTask<SeasonRestoreReceipt> StopAsync(){OnStop?.Invoke();return ValueTask.FromResult(new SeasonRestoreReceipt(Generation,Restored,Restored,Restored,Restored,Restored));}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly Source Source=new();
        internal readonly Native Native=new();
        internal readonly Pulse Pulse=new();
        internal readonly SeasonController Controller;
        internal Fixture(SeasonScope scope=SeasonScope.TreeColor)=>Controller=new(Source,Native,()=>Pulse,scope);
        public async ValueTask DisposeAsync(){foreach(var s in Native.Sessions)s.Restored=true;await Controller.DisposeAsync();}
    }

    [Fact]
    public async Task TreeReceiptPublishesPartialOnAndNeverFullOnOrFullApply()
    {
        await using var f=new Fixture();Assert.Equal(SeasonScope.TreeColor,f.Controller.Status.Scope);
        Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);
        var phases=new List<SeasonPhase>();f.Controller.StatusChanged+=s=>phases.Add(s.Phase);
        await f.Controller.StartAsync();await f.Controller.StartAsync();
        Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);Assert.DoesNotContain(SeasonPhase.On,phases);
        Assert.True(f.Controller.Status.TreeColor!.Value.Confirmed);Assert.Equal(0,f.Native.Sessions[0].FullCalls);
        Assert.Single(f.Native.Sessions);Assert.Single(f.Native.Sessions[0].Applied);
        await f.Controller.StopAsync();Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);
        Assert.True(f.Controller.Status.OriginalRestored);Assert.Null(f.Controller.Status.TreeColor);
    }

    [Theory]
    [InlineData("generation")][InlineData("date")][InlineData("epoch")][InlineData("completed")]
    [InlineData("draws")][InlineData("swaps")][InlineData("restores")][InlineData("generated")]
    [InlineData("alpha")][InlineData("bytes")][InlineData("scope")][InlineData("gpu")][InlineData("full")]
    public async Task EveryInvalidMaterialReceiptIsRejectedAndRestored(string field)
    {
        await using var f=new Fixture();f.Native.Configure=s=>s.Receipt=r=>field switch
        {
            "generation"=>r with{Generation=99},"date"=>r with{Date=Date(month:6)},"epoch"=>r with{Epoch=0},
            "completed"=>r with{CompletedEpoch=99},"draws"=>r with{Draws=0},"swaps"=>r with{Swaps=0},
            "restores"=>r with{Restores=0},"generated"=>r with{Generated=0},"alpha"=>r with{AlphaPixels=1},
            "bytes"=>r with{GpuBytes=1},"scope"=>r with{ColorScope=false},"gpu"=>r with{GpuStateVerified=false},
            _=>r with{FullRendered=true}
        };
        var phases=new List<SeasonPhase>();f.Controller.StatusChanged+=s=>phases.Add(s.Phase);
        await f.Controller.StartAsync();Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);
        Assert.DoesNotContain(SeasonPhase.PartialOn,phases);Assert.True(f.Controller.Status.OriginalRestored);Assert.True(f.Native.Sessions[0].Disposed);
    }

    [Fact]
    public async Task TreeOnlySessionCannotSupplyFullRenderingSuccess()
    {
        await using var f=new Fixture(SeasonScope.Full);await f.Controller.StartAsync();
        Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);Assert.Empty(f.Native.Sessions[0].Applied);
        Assert.Equal(1,f.Native.Sessions[0].FullCalls);Assert.True(f.Controller.Status.OriginalRestored);
    }

    [Fact]
    public async Task CalendarProgressYearBoundaryAndBackwardLoadUseActualDateWeights()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();
        foreach(var date in new[]{Date(month:5,day:30),Date(month:6),Date(month:12,day:30),Date(196,1),Date(194,3,11)})
        {
            await f.Controller.UpdateDate(new(Identity,date));Assert.Equal(date,f.Controller.Status.Date);
            Assert.Equal(SeasonTimeline.At(date).Weights,f.Native.Sessions[0].Applied[^1].Weights);
            Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);
        }
        Assert.Single(f.Native.Sessions);
    }

    [Fact]
    public async Task DateRaceIsBoundedPerPulseAndRemainsRequestedUntilActualDraw()
    {
        await using var f=new Fixture();f.Native.Configure=s=>{s.Races=1;s.OnRace=()=>f.Source.Current=new(Identity,Date(month:6));};
        await f.Controller.StartAsync();Assert.Equal(2,f.Source.Reads);Assert.Equal(Date(month:6),f.Controller.Status.Date);
        Assert.Equal(2,f.Native.Sessions[0].Applied.Count);Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);
        await f.Controller.StopAsync();f.Native.Configure=s=>s.Races=10;
        await f.Controller.StartAsync();Assert.Equal(3,f.Native.Sessions[1].Applied.Count);
        Assert.Equal(SeasonPhase.Applying,f.Controller.Status.Phase);Assert.True(f.Controller.Status.Requested);
        Assert.Null(f.Controller.Status.TreeColor);Assert.False(f.Native.Sessions[1].Disposed);
        f.Native.Sessions[1].Races=0;await f.Controller.UpdateDate(f.Source.Current);
        Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);Assert.True(f.Controller.Status.TreeColor!.Value.Confirmed);
    }

    [Fact]
    public async Task IdentityChangeDuringDateRetryRestoresAndRereadsBeforeReconnect()
    {
        await using var f=new Fixture();f.Native.Configure=s=>
        {if(s.Identity==Identity){s.Races=1;s.OnRace=()=>f.Source.Current=new(Identity with{Scene=99},Date());}};
        await f.Controller.StartAsync();Assert.Single(f.Native.Sessions[0].Applied);Assert.True(f.Native.Sessions[0].Disposed);
        Assert.Equal(f.Source.Current.Identity,f.Native.Sessions[1].Identity);Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);
    }

    [Theory][InlineData(true)][InlineData(false)]
    public async Task DateRejectedWorkerRequiresConfirmedRecoveryBeforeBoundedReconnect(bool restored)
    {
        await using var f=new Fixture();f.Native.Configure=s=>{s.Races=1;s.RaceStopsSession=true;s.Restored=restored;};
        await f.Controller.StartAsync();
        Assert.Equal(restored?SeasonPhase.Reconnecting:SeasonPhase.Faulted,f.Controller.Status.Phase);
        Assert.Equal(restored?3:1,f.Native.Sessions.Count);Assert.Equal(restored,f.Controller.Status.OriginalRestored);
        Assert.All(f.Native.Sessions,s=>Assert.Single(s.Applied));Assert.All(f.Native.Sessions,s=>Assert.Equal(restored,s.Disposed));
        if(restored){Assert.True(f.Controller.Status.Requested);f.Native.Configure=null;await f.Controller.UpdateDate(f.Source.Current);
            Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);Assert.Equal(4,f.Native.Sessions.Count);}
    }

    [Fact]
    public async Task MissingDrawKeepsRequestAndDatePollingWithoutPublishingCompletion()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();var session=f.Native.Sessions[0];session.Pending=true;
        f.Source.Current=new(Identity,Date(month:6));await f.Controller.UpdateDate(f.Source.Current);
        Assert.Equal(SeasonPhase.Applying,f.Controller.Status.Phase);Assert.True(f.Controller.Status.Requested);
        Assert.Null(f.Controller.Status.TreeColor);Assert.False(session.Disposed);
        int before=f.Source.Reads;f.Pulse.Tick();
        for(int i=0;i<100&&f.Source.Reads==before;i++)await Task.Delay(5);
        Assert.True(f.Source.Reads>before);Assert.Equal(SeasonPhase.Applying,f.Controller.Status.Phase);
        session.Pending=false;await f.Controller.UpdateDate(f.Source.Current);
        Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);Assert.True(f.Controller.Status.TreeColor!.Value.Confirmed);
        session.Pending=true;await f.Controller.UpdateDate(new(Identity,Date(month:7)));
        await f.Controller.StopAsync();Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);
        Assert.False(f.Controller.Status.Requested);Assert.True(f.Controller.Status.OriginalRestored);Assert.True(session.Disposed);
    }

    [Theory][InlineData(1048512UL,true)][InlineData(2097024UL,true)][InlineData(3145536UL,true)]
    [InlineData(4194048UL,true)][InlineData(3495040UL,true)][InlineData(0UL,false)][InlineData(699009UL,false)]
    [InlineData(2097120UL,true)][InlineData(5242752UL,true)][InlineData(20971008UL,true)]
    [InlineData(20971009UL,false)][InlineData(22019520UL,true)][InlineData(62388160UL,true)][InlineData(62388168UL,false)]
    public void GpuAllocationBoundIncludesAuthenticatedCatalogAndReferenceTextures(ulong bytes,bool expected)
    {Assert.Equal(expected,SeasonTreeColorReceipt.ValidGpuBytes(bytes));}

    [Theory][InlineData(174752U,true)][InlineData(349520U,true)][InlineData(349519U,false)][InlineData(0U,false)]
    public void OnlyCompleteAuthenticatedAtlasMipPixelCountsAreAccepted(uint pixels,bool expected)
    {Assert.Equal(expected,SeasonTreeColorReceipt.ValidAlphaPixels(pixels));}

    [Theory][InlineData(true)][InlineData(false)]
    public async Task StaleEpochOrDrawCounterCannotAcknowledgeAnotherDate(bool epoch)
    {
        await using var f=new Fixture();await f.Controller.StartAsync();
        f.Native.Sessions[0].Receipt=r=>epoch?r with{Epoch=1,CompletedEpoch=1}:r with{Draws=3};
        await f.Controller.UpdateDate(new(Identity,Date(day:2)));
        Assert.Equal(SeasonPhase.Faulted,f.Controller.Status.Phase);Assert.True(f.Controller.Status.OriginalRestored);
    }

    [Fact]
    public async Task UnconfirmedRecoveryKeepsSessionAndRetryRestoresIt()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();var session=f.Native.Sessions[0];session.Restored=false;
        await f.Controller.StopAsync();Assert.False(f.Controller.Status.OriginalRestored);Assert.False(session.Disposed);
        await f.Controller.StartAsync();Assert.Single(f.Native.Sessions);
        session.Restored=true;await f.Controller.StopAsync();Assert.True(session.Disposed);Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);
    }

    [Fact]
    public async Task OffCancelsPendingMaterialDrawAndKeepsRecoveryIndependentOfCancellation()
    {
        await using var f=new Fixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phases=new List<SeasonPhase>();f.Controller.StatusChanged+=s=>phases.Add(s.Phase);
        f.Native.Configure=s=>{s.Entered=entered;s.Block=true;};
        Task start=f.Controller.StartAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));await start;
        Assert.Equal(SeasonPhase.Off,f.Controller.Status.Phase);Assert.True(f.Native.Sessions[0].Disposed);
        Assert.DoesNotContain(SeasonPhase.Faulted,phases);
    }

    [Fact]
    public async Task UnchangedDateTimerRenewsLeaseAndSceneChangeRestoresThenRereads()
    {
        await using var f=new Fixture();await f.Controller.StartAsync();f.Pulse.Tick();
        for(int i=0;i<100&&f.Native.Sessions[0].Heartbeats==0;i++)await Task.Delay(5);
        Assert.Equal(1,f.Native.Sessions[0].Heartbeats);Assert.Equal(2,f.Source.Reads);Assert.Single(f.Native.Sessions[0].Applied);
        f.Source.Current=new(Identity with{Device=88,Context=99},Date(month:6));
        f.Native.Sessions[0].OnStop=()=>f.Source.Current=f.Source.Current with{Date=Date(month:7)};
        await f.Controller.UpdateDate(new(Identity with{Device=88,Context=99},Date(month:6)));
        Assert.True(f.Native.Sessions[0].Disposed);Assert.Equal(f.Source.Current.Identity,f.Native.Sessions[1].Identity);
        Assert.Equal(Date(month:7),f.Controller.Status.Date);Assert.Equal(SeasonPhase.PartialOn,f.Controller.Status.Phase);
    }
}
