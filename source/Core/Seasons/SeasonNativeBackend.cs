using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;

namespace San14Tenkazu.Seasons;

internal sealed class SeasonNativeDateChangedException(SeasonDate requested,SeasonDate actual,bool reconnectRequired=false):IOException(
    $"Native game date changed from {requested.Year}/{requested.Month}/{requested.Day} to {actual.Year}/{actual.Month}/{actual.Day}")
{
    internal SeasonDate Actual=>actual;
    internal bool ReconnectRequired=>reconnectRequired;
}

internal sealed class SeasonNativeDrawPendingException:IOException
{
    internal SeasonNativeDrawPendingException():base("Waiting for the requested season render passes"){}
}

internal sealed class SeasonNativeBackend(GameIdentity game,string buildDirectory,Action<string>? log=null,Func<byte[]>? sources=null,Func<byte[]>? sceneData=null):ISeasonNativeBackend
{
    public async ValueTask<ISeasonNativeSession> StartAsync(SeasonSnapshot snapshot,ulong generation,CancellationToken token)
    {
        snapshot.Validate();token.ThrowIfCancellationRequested();
        if(!OperatingSystem.IsWindows()||!Environment.Is64BitProcess)throw new PlatformNotSupportedException("Native seasons require 64-bit Windows");
        if(snapshot.Identity.ProcessId!=game.ProcessId||snapshot.Identity.ProcessStartUtc!=game.StartTimeUtc||
            GameMemory.ReadSeasonSnapshot(game).Identity!=snapshot.Identity)throw new InvalidDataException("Season process/render identity changed before connection");
        var session=new SeasonNativeSession(snapshot.Identity,generation,log);
        // Once a loader or session worker has started, retain a recoverable
        // session on failure. Start must never lose ownership of partial work.
        await session.Connect(game,buildDirectory,sources,token,sceneData).ConfigureAwait(false);return session;
    }
}

internal sealed class SeasonNativeSession(SeasonRenderIdentity identity,ulong generation,Action<string>? log):ISeasonNativeSession
{
    private readonly SemaphoreSlim gate=new(1,1);
    private NamedPipeClientStream? pipe;
    private SeasonNativeProcess? process;
    private Exception? startupFailure;
    private ulong sequence;
    private SeasonParameters? pendingParameters;
    private ulong pendingEpoch;
    private SeasonRestoreReceipt? restored;
    public SeasonRenderIdentity Identity=>identity;
    public ulong Generation=>generation;
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle,out uint processId);
    private void Record(string message){try{log?.Invoke(message);}catch(Exception e){Trace.TraceError("Season native journal: {0}",e);}}
    internal async Task Connect(GameIdentity game,string buildDirectory,Func<byte[]>? sources,CancellationToken token,Func<byte[]>? sceneData=null)
    {
        try
        {
            if(generation==0)throw new InvalidDataException("Native generation must be nonzero");
            string name=$"san14-seasons-{game.ProcessId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
            using var controller=Process.GetCurrentProcess();
            byte[] materialSources=await Task.Run(()=>sources?.Invoke()??throw new InvalidDataException("Original season materials are required"),token).ConfigureAwait(false);
            SeasonTreeSources.Validate(materialSources);
            byte[]? scene=sceneData is null?null:await Task.Run(sceneData,token).ConfigureAwait(false);
            if(scene is not null)SeasonSceneData.Validate(scene);
            var input=new SeasonBridgeProtocol.Bootstrap{Magic=SeasonBridgeProtocol.Magic,Version=SeasonBridgeProtocol.Version,Size=448,
                Generation=generation,ControllerStartFileTime=(ulong)controller.StartTime.ToUniversalTime().ToFileTimeUtc(),
                ControllerPid=(uint)Environment.ProcessId,LeaseMs=3000,Module=identity.ModuleBase,World=identity.World,
                State=identity.State,Manager=identity.Manager,Scene=identity.Scene,Driver=identity.Driver,
                Device=identity.Device,Context=identity.Context,Pipe=@"\\.\pipe\"+name};
            process=new SeasonNativeProcess(game,buildDirectory,Record);
            await Task.Run(()=>process.Start(input,materialSources,token,scene),CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            pipe=new(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10000,token).ConfigureAwait(false);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint pid)||pid!=(uint)identity.ProcessId)
                throw new InvalidDataException("Native pipe server belongs to another process");
            for(int attempt=0;attempt<20;attempt++)
            {
                var reply=await Send(SeasonBridgeProtocol.Command.Hello,null,token).ConfigureAwait(false);
                if(reply.Error==0&&reply.ActualDraws>0&&reply.RenderThread>0){Record($"Native connected: generation={generation}, actualDraws={reply.ActualDraws}, thread={reply.RenderThread}, capabilities={reply.Capabilities}");return;}
                if(reply.Error!=997)throw new IOException($"Native connection failed: Win32 {reply.Error}");
                await Task.Delay(50,token).ConfigureAwait(false);
            }
            throw new IOException("Native actual Draw/context ownership has not been confirmed");
        }
        catch(Exception error){startupFailure=error;Record($"Native connection incomplete: {error}");pipe?.Dispose();pipe=null;}
    }
    private async ValueTask<SeasonBridgeProtocol.Reply> Send(SeasonBridgeProtocol.Command command,SeasonParameters? parameters,CancellationToken token)
    {
        if(pipe is null||!pipe.IsConnected)throw new IOException("Native season pipe is disconnected");
        if(sequence==ulong.MaxValue)throw new InvalidOperationException("Native message sequence exhausted");
        ulong next=++sequence;using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(1500);
        await pipe.WriteAsync(SeasonBridgeProtocol.Encode(command,generation,next,parameters),timeout.Token).ConfigureAwait(false);
        byte[] bytes=new byte[192];await pipe.ReadExactlyAsync(bytes,timeout.Token).ConfigureAwait(false);
        return SeasonBridgeProtocol.Decode(bytes,generation,next);
    }
    public async ValueTask<SeasonApplyReceipt> ApplyAsync(SeasonParameters parameters,CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(restored is not null)throw new InvalidOperationException("Native season session stopped");
            if(startupFailure is not null)throw new IOException("Native season connection failed: "+startupFailure.Message,startupFailure);
            if(pendingParameters!=parameters)
            {
                var reply=await Send(SeasonBridgeProtocol.Command.Apply,parameters,token).ConfigureAwait(false);
                if(reply.Date!=parameters.Date)throw new SeasonNativeDateChangedException(parameters.Date,reply.Date,reply.Error==13);
                if(reply.Error!=0||reply.MaterialEpoch==0)throw new IOException($"Native scene request rejected: Win32 {reply.Error}");
                pendingParameters=parameters;pendingEpoch=reply.MaterialEpoch;
            }
            for(int attempt=0;attempt<15;attempt++)
            {
                await Task.Delay(50,token).ConfigureAwait(false);
                var reply=await Send(SeasonBridgeProtocol.Command.SceneStatus,null,token).ConfigureAwait(false);
                if(attempt==0||attempt==14)Record($"Scene selection: epoch/completed={reply.MaterialEpoch}/{reply.CompletedEpoch}, stages=[{string.Join(',',reply.Selection)}], swaps/restores={reply.Swaps}/{reply.Restores}, bytes={reply.GpuBytes}");
                if(reply.Error is not (0 or 997))throw new IOException($"Native scene rendering failed: Win32 {reply.Error}");
                if(reply.Date!=parameters.Date)throw new SeasonNativeDateChangedException(parameters.Date,reply.Date);
                if(reply.MaterialEpoch!=pendingEpoch)throw new InvalidDataException("Scene epoch changed before the frame receipt");
                if(reply.Rendered&&reply.CompletedEpoch==pendingEpoch&&reply.Capabilities==(parameters.Seasonal?7u:8u)&&reply.ColorScope==0&&
                    reply.Selection[13]>0&&reply.Selection[14]>0&&(!parameters.Seasonal||reply.Swaps>0)&&reply.Swaps==reply.Restores&&reply.GpuStateVerified==1)
                {
                    Record($"Scene native pass receipt: epoch={pendingEpoch}, date={reply.Date.Year}/{reply.Date.Month}/{reply.Date.Day}, trees={reply.TreeDraws}, land={reply.Selection[13]}, composite={reply.Selection[14]}; screen verification is separate");
                    return new(generation,parameters.Date,reply.Selection[14],true);
                }
            }
            throw new SeasonNativeDrawPendingException();
        }
        finally{gate.Release();}
    }
    public async ValueTask HeartbeatAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(restored is not null||startupFailure is not null)throw new IOException("Native lease is stopped or unavailable");
            var reply=await Send(SeasonBridgeProtocol.Command.Heartbeat,null,token).ConfigureAwait(false);
            if(reply.Error!=0)throw new IOException($"Native heartbeat rejected: Win32 {reply.Error}");
        }
        finally{gate.Release();}
    }
    async ValueTask<SeasonTreeColorReceipt> ISeasonNativeSession.ApplyTreeColorAsync(SeasonParameters parameters,CancellationToken token)
    {
        var reply=await ApplyTreeColorAsync(parameters,token).ConfigureAwait(false);
        return new(reply.Generation,reply.Date,reply.MaterialEpoch,reply.CompletedEpoch,reply.TreeDraws,reply.Swaps,reply.Restores,
            reply.Generated,reply.AlphaPixels,reply.GpuBytes,reply.ColorScope==1,reply.GpuStateVerified==1,reply.Rendered);
    }
    // Scoped evidence remains separate from the full-season frame receipt.
    internal async ValueTask<SeasonBridgeProtocol.Reply> ApplyTreeColorAsync(SeasonParameters parameters,CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(restored is not null||startupFailure is not null)throw new IOException("Native material connection is unavailable: "+startupFailure?.Message,startupFailure);
            if(pendingParameters!=parameters)
            {
                var pending=await Send(SeasonBridgeProtocol.Command.ApplyTreeColor,parameters,token).ConfigureAwait(false);
                // Legacy v3 date rejection ended its worker with INVALID_DATA;
                // current v3 uses RETRY and keeps the verified session alive.
                if(pending.Date!=parameters.Date)throw new SeasonNativeDateChangedException(parameters.Date,pending.Date,pending.Error==13);
                if(pending.Error!=0||pending.MaterialEpoch==0)throw new IOException($"Tree color request rejected: Win32 {pending.Error}");
                pendingParameters=parameters;pendingEpoch=pending.MaterialEpoch;
            }
            for(int attempt=0;attempt<15;attempt++)
            {
                await Task.Delay(50,token).ConfigureAwait(false);
                var reply=await Send(SeasonBridgeProtocol.Command.TreeColorStatus,null,token).ConfigureAwait(false);
                if(attempt==0||attempt==14)Record($"Tree selection: attempt={attempt}, actualDraws={reply.ActualDraws}, epoch/completed={reply.MaterialEpoch}/{reply.CompletedEpoch}, stages=[{string.Join(',',reply.Selection)}], swaps/restores={reply.Swaps}/{reply.Restores}, generated={reply.Generated}");
                if(reply.Error is not (0 or 997))throw new IOException($"Tree color rendering failed: Win32 {reply.Error}");
                if(reply.Date!=parameters.Date)throw new SeasonNativeDateChangedException(parameters.Date,reply.Date);
                if(reply.MaterialEpoch!=pendingEpoch)throw new InvalidDataException("Tree color epoch changed before its Draw receipt");
                if(reply.Error==0&&reply.CompletedEpoch==pendingEpoch&&reply.TreeDraws>0&&reply.Swaps>0&&reply.Swaps==reply.Restores&&
                    reply.ColorScope==1&&reply.GpuStateVerified==1&&SeasonTreeColorReceipt.ValidAlphaPixels(reply.AlphaPixels)&&reply.Generated>0&&SeasonTreeColorReceipt.ValidGpuBytes(reply.GpuBytes)&&!reply.Rendered)
                {Record($"Tree color actual Draw: epoch={reply.CompletedEpoch}, date={reply.Date.Year}/{reply.Date.Month}/{reply.Date.Day}, draws={reply.TreeDraws}, swaps/restores={reply.Swaps}/{reply.Restores}, generated={reply.Generated}, alpha={reply.AlphaPixels}, textureBytes={reply.GpuBytes}");return reply;}
            }
            Record($"Tree color pending: epoch={pendingEpoch}, date={parameters.Date.Year}/{parameters.Date.Month}/{parameters.Date.Day}; no completed Draw receipt");
            throw new SeasonNativeDrawPendingException();
        }
        finally{gate.Release();}
    }
    internal async ValueTask<SeasonBridgeProtocol.Reply> ReadTreeColorStatusAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(restored is not null||startupFailure is not null)throw new IOException("Native material connection is unavailable: "+startupFailure?.Message,startupFailure);
            return await Send(SeasonBridgeProtocol.Command.TreeColorStatus,null,token).ConfigureAwait(false);
        }
        finally{gate.Release();}
    }
    internal async ValueTask<SeasonBridgeProtocol.Reply> ReadSceneStatusAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try{return await Send(SeasonBridgeProtocol.Command.SceneStatus,null,token).ConfigureAwait(false);}
        finally{gate.Release();}
    }
    // Used by the native recovery checks. No Stop message is sent: the helper
    // must restore itself after pipe loss or lease expiration. The ordinary
    // terminated-worker receipt is still required before reading/freeing data.
    internal async ValueTask<SeasonRestoreReceipt> CheckAutomaticRecoveryAsync(bool disconnect)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(restored is not null||startupFailure is not null||process is null)
                throw new IOException("An active native session is required for automatic recovery");
            Record($"Native automatic recovery requested: cause={(disconnect?"pipe-disconnect":"heartbeat-expiration")}, stopMessageSent=False");
            if(disconnect){pipe?.Dispose();pipe=null;}
            var receipt=await Task.Run(()=>process.Recover(generation)).ConfigureAwait(false);
            if(receipt.Confirmed){restored=receipt;pipe?.Dispose();pipe=null;}
            Record($"Native automatic recovery: generation={generation}, confirmed={receipt.Confirmed}");
            return receipt;
        }
        finally{gate.Release();}
    }
    public async ValueTask<SeasonRestoreReceipt> StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(restored is {} done)return done;
            if(pipe is {IsConnected:true})try{await Send(SeasonBridgeProtocol.Command.Stop,null,CancellationToken.None).ConfigureAwait(false);}
                catch(Exception error){Record($"Native stop pipe: {error.Message}");}
            pipe?.Dispose();pipe=null;
            var receipt=process is null?new SeasonRestoreReceipt(generation,true,true,true,true,true):
                await Task.Run(()=>process.Recover(generation)).ConfigureAwait(false);
            if(receipt.Confirmed)restored=receipt;
            Record($"Native recovery: generation={generation}, confirmed={receipt.Confirmed}");return receipt;
        }
        finally{gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        var receipt=await StopAsync().ConfigureAwait(false);
        if(!receipt.Confirmed)throw new IOException("Native recovery is incomplete; retain this session");
        process?.Dispose();process=null;
    }
}
