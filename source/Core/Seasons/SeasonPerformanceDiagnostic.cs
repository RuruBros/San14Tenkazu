#if LOCAL_DEVELOPER
using System.Text;
using System.Text.Json;
namespace San14Tenkazu.Seasons;

internal sealed partial class SeasonNativeSession
{
    internal async Task<(bool Collecting,string? Result)> PerformanceAsync(uint command,CancellationToken token)
    {
        if(command is <8 or >11)throw new ArgumentOutOfRangeException(nameof(command));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(restored is not null||startupFailure is not null)throw new IOException("Renderer is not active");
            // Once sent, consume the bounded acknowledgement even if the UI cancels;
            // leaving a reply in the pipe would desynchronize later recovery commands.
            var reply=await Send((SeasonBridgeProtocol.Command)command,null,CancellationToken.None).ConfigureAwait(false);
            if(reply.Error!=0)throw new IOException($"Performance diagnostic command {command}: Win32 {reply.Error}");
            if(command!=11)return(reply.Selection[0]==1,null);
            int size=checked((int)reply.Selection[0]);if(size<=0||size>8*1024*1024)throw new InvalidDataException("Performance result size exceeds its bound");
            byte[] bytes=new byte[size];using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(1500);
            await pipe!.ReadExactlyAsync(bytes,timeout.Token).ConfigureAwait(false);string text=Encoding.UTF8.GetString(bytes);
            using var parsed=JsonDocument.Parse(text);if(parsed.RootElement.GetProperty("version").GetUInt32()!=1)throw new InvalidDataException("Performance result version differs");
            return(false,text);
        }finally{gate.Release();}
    }
}
internal sealed partial class SeasonController
{
    internal async Task<(bool Collecting,string? Result)> PerformanceAsync(uint command,CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(!requested||status.Phase!=SeasonPhase.On||session is not SeasonNativeSession native)
                throw new InvalidOperationException("현재 활성화된 Native 렌더러가 필요합니다. 설정은 자동 변경하지 않습니다.");
            var current=await dates.ReadAsync(token).ConfigureAwait(false);current.Validate();
            if(current.Identity!=native.Identity)throw new InvalidDataException("Renderer identity changed");
            return await native.PerformanceAsync(command,token).ConfigureAwait(false);
        }finally{gate.Release();}
    }
}
#endif
