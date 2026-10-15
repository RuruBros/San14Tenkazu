using System.Diagnostics;

namespace San14Tenkazu.Seasons;

// Monotonic stage measurements. An unfinished operation never reports success.
internal sealed class SeasonTiming(Action<string>? log,string operation):IDisposable
{
    private readonly string id=Guid.NewGuid().ToString("N")[..12];
    private readonly long started=Stopwatch.GetTimestamp();
    private long previous=Stopwatch.GetTimestamp();
    private bool completed;
    internal void Step(string stage,string details="")=>Write(stage,"ok",details);
    internal void Complete(string outcome="ok")
    {
        Write("total",outcome,"");completed=true;
    }
    private void Write(string stage,string outcome,string details)
    {
        long now=Stopwatch.GetTimestamp();
        try{log?.Invoke(FormattableString.Invariant($"Timing: operation={operation}, id={id}, stage={stage}, elapsedMs={Stopwatch.GetElapsedTime(previous,now).TotalMilliseconds:F3}, totalMs={Stopwatch.GetElapsedTime(started,now).TotalMilliseconds:F3}, outcome={outcome}{(details.Length==0?"":", "+details)}"));}
        catch(Exception error){Trace.TraceError("Season timing journal: {0}",error);}
        previous=now;
    }
    public void Dispose(){if(!completed)Write("total","incomplete","");}
}
