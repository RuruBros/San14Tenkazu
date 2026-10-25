namespace San14Tenkazu.Seasons;

// Serializes the complete native lifecycle. The independent one-second source
// reads the current world/date; map navigation never drives this controller.
// No production renderer is substituted with a simulated success implementation.
internal sealed partial class SeasonController(ISeasonDateSource dates, ISeasonNativeBackend backend,
    Func<ISeasonPulse>? pulses = null, SeasonScope scope = SeasonScope.Full) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Func<ISeasonPulse> makePulse=pulses ?? (()=>new SeasonSecondPulse());
    private SeasonStatus status=new(SeasonPhase.Off,null,0,true,Scope:scope);
    private ISeasonNativeSession? session;
    private CancellationTokenSource? lifetime;
    private Task? monitor;
    private ulong generation,lastFrame;
    private SeasonTreeColorReceipt? lastTreeColor;
    private bool requested,seasonal=true;
    private int disposed;
    internal SeasonStatus Status => Volatile.Read(ref status);
    internal event Action<SeasonStatus>? StatusChanged;

    private void Set(SeasonPhase phase, SeasonDate? date=null, bool restored=false, string? error=null)
    {
        var next=new SeasonStatus(phase,date,generation,restored,error,scope==SeasonScope.Full&&!seasonal?SeasonScope.Weather:scope,
            phase==SeasonPhase.PartialOn?lastTreeColor:null,requested);Volatile.Write(ref status,next);
        if (StatusChanged is not { } observers) return;
        foreach (Action<SeasonStatus> observer in observers.GetInvocationList())
            try { observer(next); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError("Season status observer: {0}",exception); }
    }
    private void CancelLifetime()
    {
        try { Volatile.Read(ref lifetime)?.Cancel(); }
        catch (ObjectDisposedException) { } // A concurrent completed stop already disposed it.
    }

    // Called while holding gate. A retired pulse can finish/dispose after another
    // StartAsync has acquired gate and installed its own lifetime/session.
    private bool CurrentWatcher(CancellationToken token) => requested && !token.IsCancellationRequested &&
        lifetime is { } current && current.Token==token;

    private static void RecordRetiredWatcher(Exception exception)
    {
        if(exception is not OperationCanceledException)
            System.Diagnostics.Trace.TraceError("Season retired date watcher: {0}",exception);
    }

    internal async Task StartAsync(CancellationToken cancellationToken=default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);
            if (requested) return;
            if (session is not null)
            {
                Set(SeasonPhase.Faulted,Status.Date,error:"Original restoration must be confirmed before another connection");return;
            }
            lifetime?.Dispose();lifetime=new();requested=true;
            using var operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellationToken);
            try
            {
                Set(SeasonPhase.Connecting,restored:true);
                var snapshot=await dates.ReadAsync(operation.Token).ConfigureAwait(false);snapshot.Validate();
                operation.Token.ThrowIfCancellationRequested();
                await Connect(snapshot,operation.Token).ConfigureAwait(false);
                var watchToken=lifetime.Token;monitor=Task.Run(()=>Watch(watchToken));
            }
            catch (OperationCanceledException) when(lifetime.IsCancellationRequested)
            { } // StopAsync owns recovery and OFF; a deliberate OFF is not a fault.
            catch (Exception exception) { await Fail(exception).ConfigureAwait(false); }
        }
        finally { gate.Release(); }
    }
    internal async Task SetSeasonalAsync(bool enabled,CancellationToken token=default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);if(seasonal==enabled)return;seasonal=enabled;
            if(requested&&session is not null){var snapshot=await dates.ReadAsync(token).ConfigureAwait(false);snapshot.Validate();await Apply(snapshot,token).ConfigureAwait(false);}
            else Set(Status.Phase,Status.Date,Status.OriginalRestored,Status.Error);
        }
        catch(Exception error){await Fail(error).ConfigureAwait(false);}
        finally{gate.Release();}
    }

    private async ValueTask Connect(SeasonSnapshot snapshot,CancellationToken token,int attempts=3)
    {
        if (generation==ulong.MaxValue) throw new InvalidOperationException("Season generation exhausted");
        generation++;lastFrame=0;lastTreeColor=null;
        session=await backend.StartAsync(snapshot,generation,token).ConfigureAwait(false);
        if (session.Identity!=snapshot.Identity || session.Generation!=generation)
            throw new InvalidDataException("Native season session belongs to another rendering generation");
        token.ThrowIfCancellationRequested();
        await Apply(snapshot,token,attempts).ConfigureAwait(false);
    }

    private async ValueTask Apply(SeasonSnapshot snapshot,CancellationToken token,int attempts=3)
    {
        if(scope==SeasonScope.TreeColor)
        {
            for(int attempt=0;attempt<attempts;attempt++)
            {
                var date=snapshot.Date;Set(SeasonPhase.Applying,date);
                try
                {
                    var color=await session!.ApplyTreeColorAsync(SeasonParameters.At(date),token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if(color.Generation!=generation || color.Date!=date || !color.Confirmed ||
                        (lastTreeColor is {} previous && (color.Epoch<=previous.Epoch || color.Draws<=previous.Draws)))
                        throw new InvalidDataException("Native tree color Draw/date/epoch restoration receipt is invalid");
                    lastTreeColor=color;Set(SeasonPhase.PartialOn,date);return;
                }
                catch(SeasonNativeDrawPendingException)
                {
                    // No matching color Draw yet is a pending request, not a
                    // rendered receipt or a renderer failure. The next pulse
                    // reads the live date again and resumes this native epoch.
                    return;
                }
                catch(SeasonNativeDateChangedException change)
                {
                    // Native may observe a later date between the read and the Draw.
                    // Sample the live chain again; never apply an invented next date.
                    snapshot=await dates.ReadAsync(token).ConfigureAwait(false);snapshot.Validate();
                    if(change.ReconnectRequired || snapshot.Identity!=session!.Identity)
                    {
                        Set(SeasonPhase.Reconnecting,snapshot.Date);
                        await Restore().ConfigureAwait(false);token.ThrowIfCancellationRequested();
                        snapshot=await dates.ReadAsync(token).ConfigureAwait(false);snapshot.Validate();
                        if(attempt+1<attempts)await Connect(snapshot,token,attempts-attempt-1).ConfigureAwait(false);
                        else Set(SeasonPhase.Reconnecting,snapshot.Date,restored:true);
                        return;
                    }
                    if(attempt+1==attempts){Set(SeasonPhase.Applying,snapshot.Date);return;}
                }
            }
        }
        for(int attempt=0;attempt<attempts;attempt++)
        {
            SeasonDate fullDate=snapshot.Date;Set(SeasonPhase.Applying,fullDate);
            try
            {
                var receipt=await session!.ApplyAsync(SeasonParameters.At(fullDate) with{Seasonal=seasonal},token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if(receipt.Generation!=generation||receipt.Date!=fullDate||!receipt.Rendered||receipt.Frame<=lastFrame)
                    throw new InvalidDataException("Native season passes have not been confirmed for this date/frame");
                lastFrame=receipt.Frame;Set(SeasonPhase.On,fullDate);return;
            }
            catch(SeasonNativeDrawPendingException){return;}
            catch(SeasonNativeDateChangedException change)
            {
                snapshot=await dates.ReadAsync(token).ConfigureAwait(false);snapshot.Validate();
                if(change.ReconnectRequired||snapshot.Identity!=session!.Identity)
                {
                    Set(SeasonPhase.Reconnecting,snapshot.Date);await Restore().ConfigureAwait(false);token.ThrowIfCancellationRequested();
                    snapshot=await dates.ReadAsync(token).ConfigureAwait(false);snapshot.Validate();
                    if(attempt+1<attempts)await Connect(snapshot,token,attempts-attempt-1).ConfigureAwait(false);
                    else Set(SeasonPhase.Reconnecting,snapshot.Date,restored:true);
                    return;
                }
                if(attempt+1==attempts){Set(SeasonPhase.Applying,snapshot.Date);return;}
            }
        }
    }

    internal async Task UpdateDate(SeasonSnapshot snapshot,CancellationToken cancellationToken=default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!requested) return;
            using var operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime!.Token,cancellationToken);
            try { await Update(snapshot,operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when(lifetime.IsCancellationRequested) { }
            catch (Exception exception) { await Fail(exception).ConfigureAwait(false); }
        }
        finally { gate.Release(); }
    }

    private async ValueTask Update(SeasonSnapshot snapshot,CancellationToken token)
    {
        snapshot.Validate();token.ThrowIfCancellationRequested();
        if (session is null || snapshot.Identity!=session.Identity)
        {
            Set(SeasonPhase.Reconnecting,snapshot.Date);
            await Restore().ConfigureAwait(false);
            // Recovery may span a load or another scene transition. Do not reuse
            // the snapshot that caused the old connection to be invalidated.
            token.ThrowIfCancellationRequested();
            snapshot=await dates.ReadAsync(token).ConfigureAwait(false);snapshot.Validate();
            token.ThrowIfCancellationRequested();await Connect(snapshot,token).ConfigureAwait(false);
        }
        else if (snapshot.Date!=Status.Date || Status.Phase==SeasonPhase.Applying) await Apply(snapshot,token).ConfigureAwait(false);
        else await session.HeartbeatAsync(token).ConfigureAwait(false);
    }

    private async Task Watch(CancellationToken token)
    {
        try
        {
            await using var pulse=makePulse();
            while (await pulse.WaitAsync(token).ConfigureAwait(false))
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!CurrentWatcher(token)) return;
                    try
                    {
                        var snapshot=await dates.ReadAsync(token).ConfigureAwait(false);
                        await Update(snapshot,token).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        if(CurrentWatcher(token))await Fail(exception).ConfigureAwait(false);
                        else RecordRetiredWatcher(exception);
                        return;
                    }
                }
                finally { gate.Release(); }
            }
            if (!token.IsCancellationRequested)
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try { if (CurrentWatcher(token)) await Fail(new IOException("Season date watcher ended")).ConfigureAwait(false); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if(CurrentWatcher(token))await Fail(exception).ConfigureAwait(false);
                else RecordRetiredWatcher(exception);
            }
            finally { gate.Release(); }
        }
    }

    private async ValueTask Restore()
    {
        if (session is null) return;
        Set(SeasonPhase.Restoring,Status.Date);
        var receipt=await session.StopAsync().ConfigureAwait(false);
        // Compare to the session's own generation even when a malformed start
        // returned a mismatched session. Do not dispose an undrained callback.
        if (receipt.Generation!=session.Generation || !receipt.Confirmed)
            throw new IOException("Native original rendering/resource restoration is incomplete");
        await session.DisposeAsync().ConfigureAwait(false);session=null;
    }

    private async ValueTask Fail(Exception exception)
    {
        requested=false;CancelLifetime();SeasonDate? date=Status.Date;
        bool restored=false;string error=exception.Message;
        try { await Restore().ConfigureAwait(false);restored=true; }
        catch (Exception recovery) { error+="; restoration: "+recovery.Message; }
        Set(SeasonPhase.Faulted,date,restored,error);
    }

    internal async Task StopAsync(CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelLifetime(); // Interrupt a pending start/apply before waiting for its gate.
        // Admission cancellation has already begun shutdown. Recovery must still
        // reach its receipt even if the caller subsequently cancels its request.
        await gate.WaitAsync().ConfigureAwait(false);
        Task? pending;
        try
        {
            requested=false;CancelLifetime();pending=monitor;monitor=null;
            try { await Restore().ConfigureAwait(false);Set(SeasonPhase.Off,restored:true); }
            catch (Exception exception) { Set(SeasonPhase.Faulted,Status.Date,error:exception.Message); }
        }
        finally { gate.Release(); }
        // A watcher waiting for gate can now finish; never join it while holding gate.
        if (pending is not null) await pending.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed,1);
        await StopAsync().ConfigureAwait(false);
        // Failed recovery deliberately retains the session and its owned refs.
        // The caller must keep this faulted controller to retry recovery/report it.
        if (session is null) lifetime?.Dispose();
    }
}
