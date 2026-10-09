using San14Tenkazu.Seasons;
using Xunit.Abstractions;

namespace San14Tenkazu.Tests;

// Deliberately controlled task ordering. These receipts are test doubles, never a product renderer.
public class SeasonControllerLifetimeTests(ITestOutputHelper output)
{
    private sealed class TraceLog:IDisposable
    {
        private readonly StringWriter text=new();
        private readonly System.Diagnostics.TextWriterTraceListener listener;
        internal TraceLog()
        {
            listener=new(text);System.Diagnostics.Trace.Listeners.Add(listener);
        }
        internal string Read(){listener.Flush();return text.ToString();}
        public void Dispose()
        {
            System.Diagnostics.Trace.Listeners.Remove(listener);listener.Dispose();text.Dispose();
        }
    }
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SeasonDate Date(int day=1)
    {
        Assert.True(SeasonDate.TryCreate(194,10,day,out var date));return date;
    }
    private static readonly SeasonRenderIdentity Identity=new(42,new DateTime(2026,10,6,0,0,0,DateTimeKind.Utc),
        1,2,3,4,5,6,7,8);
    private sealed class Source:ISeasonDateSource
    {
        public ValueTask<SeasonSnapshot> ReadAsync(CancellationToken token)
        {token.ThrowIfCancellationRequested();return ValueTask.FromResult(new SeasonSnapshot(Identity,Date()));}
    }
    private sealed class Native:ISeasonNativeBackend
    {
        internal readonly List<Session> Sessions=[];
        internal Action<Session>? Configure;
        public ValueTask<ISeasonNativeSession> StartAsync(SeasonSnapshot snapshot,ulong generation,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var session=new Session(snapshot.Identity,generation);
            Configure?.Invoke(session);Sessions.Add(session);return ValueTask.FromResult<ISeasonNativeSession>(session);
        }
    }
    private sealed class Session(SeasonRenderIdentity identity,ulong generation):ISeasonNativeSession
    {
        public SeasonRenderIdentity Identity=>identity;
        public ulong Generation=>generation;
        internal bool Disposed;
        internal int Stops;
        internal TaskCompletionSource? ApplyEntered,StopEntered,ContinueStop;
        internal bool BlockApply;
        private ulong frame;
        public async ValueTask<SeasonApplyReceipt> ApplyAsync(SeasonParameters parameters,CancellationToken token)
        {
            ApplyEntered?.TrySetResult();if(BlockApply)await Task.Delay(Timeout.Infinite,token);
            token.ThrowIfCancellationRequested();return new(Generation,parameters.Date,++frame,true);
        }
        public ValueTask HeartbeatAsync(CancellationToken token){token.ThrowIfCancellationRequested();return ValueTask.CompletedTask;}
        public async ValueTask<SeasonRestoreReceipt> StopAsync()
        {
            Stops++;StopEntered?.TrySetResult();if(ContinueStop is not null)await ContinueStop.Task;
            return new(Generation,true,true,true,true,true);
        }
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    private sealed class Pulse:ISeasonPulse
    {
        internal readonly TaskCompletionSource Entered=Signal(),Release=Signal(),Disposing=Signal();
        internal bool LateWaitFailure,LateDisposeFailure;
        public async ValueTask<bool> WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            if(LateWaitFailure){await Release.Task;throw new IOException("Retired watcher wait failed");}
            await Task.Delay(Timeout.Infinite,token);return true;
        }
        public async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();if(LateDisposeFailure){await Release.Task;throw new IOException("Retired watcher dispose failed");}
        }
    }
    private static Task Observe(SeasonController controller,SeasonPhase phase)
    {
        var signal=Signal();Action<SeasonStatus>? observer=null;
        observer=status=>{if(status.Phase==phase){controller.StatusChanged-=observer;signal.TrySetResult();}};
        controller.StatusChanged+=observer;return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AlreadyCancelledStopDoesNotCancelTheActiveLifetime()
    {
        var native=new Native();await using var controller=new SeasonController(new Source(),native,()=>new Pulse());
        await controller.StartAsync();using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>controller.StopAsync(cancelled.Token));
        await controller.UpdateDate(new(Identity,Date(11)));
        output.WriteLine($"cancelledStop: phase={controller.Status.Phase}, restored={controller.Status.OriginalRestored}, stops={native.Sessions[0].Stops}");
        Assert.Equal(SeasonPhase.On,controller.Status.Phase);Assert.Equal(Date(11),controller.Status.Date);
        Assert.False(native.Sessions[0].Disposed);Assert.Equal(0,native.Sessions[0].Stops);
    }

    [Fact]
    public async Task CancellationAfterStopBeginsCannotAbandonPendingRecovery()
    {
        var native=new Native();var entered=Signal();var stopEntered=Signal();var continueStop=Signal();
        native.Configure=session=>{session.BlockApply=true;session.ApplyEntered=entered;session.StopEntered=stopEntered;session.ContinueStop=continueStop;};
        await using var controller=new SeasonController(new Source(),native,()=>new Pulse());using var cancelled=new CancellationTokenSource();
        Task start=controller.StartAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop=controller.StopAsync(cancelled.Token);await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancelled.Cancel();
        bool abandoned;
        try
        {
            // Cancellation completion has a bounded opportunity while recovery is held by the barrier.
            Task winner=await Task.WhenAny(stop,Task.Delay(100));abandoned=winner==stop;
            output.WriteLine($"stopPendingRecovery: callerTaskCompletedBeforeRelease={abandoned}, status={controller.Status.Phase}");
        }
        finally {continueStop.TrySetResult();}
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        try {await stop.WaitAsync(TimeSpan.FromSeconds(5));}
        catch(OperationCanceledException){output.WriteLine("stopPendingRecovery: caller cancelled before OFF confirmation");throw;}
        Assert.False(abandoned);Assert.Equal(SeasonPhase.Off,controller.Status.Phase);Assert.True(controller.Status.OriginalRestored);
        Assert.True(native.Sessions[0].Disposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RetiredWatcherFailureCannotRestoreAReplacementSession(bool disposeFailure)
    {
        using var trace=new TraceLog();
        var native=new Native();var oldPulse=new Pulse {LateWaitFailure=!disposeFailure,LateDisposeFailure=disposeFailure};
        int pulses=0;await using var controller=new SeasonController(new Source(),native,
            ()=>Interlocked.Increment(ref pulses)==1?oldPulse:new Pulse());
        await controller.StartAsync();await oldPulse.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task off=Observe(controller,SeasonPhase.Off);Task stop=controller.StopAsync();await off;
        if(disposeFailure)await oldPulse.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try {await controller.StartAsync();}
        finally {oldPulse.Release.TrySetResult();}
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        output.WriteLine($"retiredWatcher: disposeFailure={disposeFailure}, phase={controller.Status.Phase}, generation={controller.Status.Generation}, replacementStops={native.Sessions[1].Stops}");
        Assert.Equal(SeasonPhase.On,controller.Status.Phase);Assert.Equal((ulong)2,controller.Status.Generation);
        Assert.True(native.Sessions[0].Disposed);Assert.False(native.Sessions[1].Disposed);Assert.Equal(0,native.Sessions[1].Stops);
        string log=trace.Read();Assert.Contains("Season retired date watcher",log);
        Assert.Contains(disposeFailure?"Retired watcher dispose failed":"Retired watcher wait failed",log);
        output.WriteLine(log);
    }
}
