namespace San14Tenkazu.Seasons;

internal readonly record struct SeasonRenderIdentity(int ProcessId, DateTime ProcessStartUtc,
    ulong ModuleBase, ulong World, ulong State, ulong Manager, ulong Scene, ulong Driver, ulong Device, ulong Context)
{
    internal bool IsValid => ProcessId>0 && ProcessStartUtc!=default && ProcessStartUtc.Kind==DateTimeKind.Utc &&
        ModuleBase>0 && World>0 && State>0 && Manager>0 && Scene>0 && Driver>0 && Device>0 && Context>0;
}

internal readonly record struct SeasonSnapshot(SeasonRenderIdentity Identity, SeasonDate Date)
{
    internal void Validate()
    {
        if (!Identity.IsValid || !Date.IsValid) throw new InvalidDataException("Invalid season date/render snapshot");
    }
}

internal readonly record struct SeasonParameters(SeasonDate Date, MonthBlend Month, SeasonWeights Weights)
{
    internal bool Seasonal {get;init;}=true;
    internal static SeasonParameters At(SeasonDate date)
    {
        var month=SeasonTimeline.At(date);return new(date,month,month.Weights);
    }
}

internal enum SeasonScope { Full, TreeColor, Weather }
internal enum SeasonPhase { Off, Connecting, Applying, On, PartialOn, Restoring, Reconnecting, Faulted }
internal sealed record SeasonStatus(SeasonPhase Phase, SeasonDate? Date, ulong Generation,
    bool OriginalRestored, string? Error = null, SeasonScope Scope = SeasonScope.Full,
    SeasonTreeColorReceipt? TreeColor = null, bool Requested = false);
internal readonly record struct SeasonApplyReceipt(ulong Generation, SeasonDate Date, ulong Frame, bool Rendered);
// A selected material Draw receipt must never stand in for a full rendered frame.
internal readonly record struct SeasonTreeColorReceipt(ulong Generation, SeasonDate Date,
    ulong Epoch, ulong CompletedEpoch, ulong Draws, ulong Swaps, ulong Restores,
    uint Generated, uint AlphaPixels, ulong GpuBytes, bool ColorScope, bool GpuStateVerified, bool FullRendered)
{
    internal bool Confirmed => Epoch>0 && CompletedEpoch==Epoch && Draws>0 && Swaps>0 && Swaps==Restores &&
        Generated>0 && ValidAlphaPixels(AlphaPixels) && ValidGpuBytes(GpuBytes) && ColorScope && GpuStateVerified && !FullRendered;
    internal static bool ValidAlphaPixels(uint pixels)=>SeasonTreeSources.Assets.Any(a=>a.Pixels==pixels);
    // Each authenticated atlas has at most two output views. Validate the
    // actual sum of output and reference payloads, including mixed BC formats.
    private static readonly HashSet<ulong> Allocations=BuildAllocations();
    private static HashSet<ulong> BuildAllocations()
    {
        var totals=new HashSet<ulong>{0};
        foreach(var asset in SeasonTreeSources.Assets)
        {
            ulong allocation=(ulong)asset.Pixels*4+(ulong)SeasonTreeSources.Assets[asset.Green].Bytes+(ulong)SeasonTreeSources.Assets[asset.Autumn].Bytes;
            for(int view=0;view<2;view++)foreach(ulong value in totals.ToArray())totals.Add(value+allocation);
        }
        totals.Remove(0);return totals;
    }
    internal static bool ValidGpuBytes(ulong bytes)=>Allocations.Contains(bytes);
}
internal readonly record struct SeasonRestoreReceipt(ulong Generation, bool CallbacksDrained,
    bool OriginalGpuState, bool DispatchDetached, bool ProtectionRestored, bool ResourcesReleased)
{
    internal bool Confirmed => CallbacksDrained && OriginalGpuState && DispatchDetached && ProtectionRestored && ResourcesReleased;
}

internal interface ISeasonDateSource
{
    ValueTask<SeasonSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal interface ISeasonNativeSession : IAsyncDisposable
{
    SeasonRenderIdentity Identity { get; }
    ulong Generation { get; }
    ValueTask<SeasonApplyReceipt> ApplyAsync(SeasonParameters parameters, CancellationToken cancellationToken);
    ValueTask<SeasonTreeColorReceipt> ApplyTreeColorAsync(SeasonParameters parameters, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This session has no verified tree color renderer");
    // Must reject a stopped/expired native lease. Does not allocate GPU resources per tick.
    ValueTask HeartbeatAsync(CancellationToken cancellationToken);
    // Close native callback admission, drain, restore and release. Cancellation of
    // an apply must not prevent this recovery; a false receipt retains this session.
    ValueTask<SeasonRestoreReceipt> StopAsync();
}

internal interface ISeasonNativeBackend
{
    // Throw only after all connection side effects have been rolled back. An
    // incomplete connection must return a recoverable session, whose apply fails.
    ValueTask<ISeasonNativeSession> StartAsync(SeasonSnapshot snapshot, ulong generation, CancellationToken cancellationToken);
}

internal interface ISeasonPulse : IAsyncDisposable
{
    ValueTask<bool> WaitAsync(CancellationToken cancellationToken);
}

internal sealed class SeasonSecondPulse : ISeasonPulse
{
    private readonly PeriodicTimer timer=new(TimeSpan.FromSeconds(1));
    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) => timer.WaitForNextTickAsync(cancellationToken);
    public ValueTask DisposeAsync() { timer.Dispose();return ValueTask.CompletedTask; }
}
