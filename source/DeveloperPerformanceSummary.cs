#if LOCAL_DEVELOPER
using System.Text.Json;
namespace San14Tenkazu;
internal static class DeveloperPerformanceSummary
{
    internal static string GpuCsv(string json)
    {
        using var doc=JsonDocument.Parse(json);var text=new System.Text.StringBuilder("frame,status,stage,milliseconds\n");
        foreach(var r in doc.RootElement.GetProperty("gpu").EnumerateArray()){
            int status=r.GetProperty("status").GetInt32();ulong frame=r.GetProperty("frame").GetUInt64();
            if(status!=2){text.AppendLine($"{frame},{status},,");continue;}
            double frequency=r.GetProperty("frequency").GetDouble();var points=r.GetProperty("points").EnumerateArray().ToArray();
            for(int i=0;i+1<points.Length;i++)text.AppendLine(FormattableString.Invariant($"{frame},{status},{points[i][0].GetInt32()},{(points[i+1][1].GetDouble()-points[i][1].GetDouble())*1000/frequency:R}"));
        }return text.ToString();
    }
    internal static object Read(string json)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;double frequency=root.GetProperty("qpcFrequency").GetDouble();
        if(frequency<=0||root.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("Invalid performance clock/version");
        var frames=root.GetProperty("frames").EnumerateArray().ToArray();var presents=root.GetProperty("presents").EnumerateArray().ToArray();
        var gpu=root.GetProperty("gpu").EnumerateArray().ToArray();if(frames.Length>4096||presents.Length>4096||gpu.Length>512)throw new InvalidDataException("Performance capacity exceeded");
        string[] names=root.GetProperty("cpuStages").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        var cpu=names.Select((name,index)=>new{name,recordedCalls=frames.Sum(f=>f.GetProperty("cpu")[index][0].GetDouble()),sampledFrameCosts=Stats(frames.Where(f=>f.GetProperty("sampled").GetInt32()==1)
            .Select(f=>f.GetProperty("cpu")[index][2].GetDouble()*1000/frequency))}).ToArray();
        var groups=presents.Where(p=>p[1].GetUInt64()>=p[0].GetUInt64()&&p[0].GetUInt64()!=0).GroupBy(p=>p[2].GetUInt32()).Select(g=>{
            var sorted=g.OrderBy(p=>p[0].GetUInt64()).ToArray();return new{thread=g.Key,presentWall=Stats(sorted.Select(p=>(p[1].GetDouble()-p[0].GetDouble())*1000/frequency)),
                cadence=Stats(sorted.Zip(sorted.Skip(1),(a,b)=>(b[0].GetDouble()-a[0].GetDouble())*1000/frequency))};}).ToArray();
        var segments=new List<(int Stage,double Ms)>();foreach(var sample in gpu){if(sample.GetProperty("status").GetInt32()!=2)continue;
            double hz=sample.GetProperty("frequency").GetDouble();if(hz<=0)throw new InvalidDataException("Invalid GPU frequency");
            var points=sample.GetProperty("points").EnumerateArray().ToArray();for(int i=0;i+1<points.Length;i++){
                double elapsed=points[i+1][1].GetDouble()-points[i][1].GetDouble();if(elapsed<0)throw new InvalidDataException("GPU timestamps moved backwards");
                segments.Add((points[i][0].GetInt32(),elapsed*1000/hz));}}
        return new{version=1,cpu,present=groups,gpu=segments.GroupBy(x=>x.Stage).Select(g=>new{stage=g.Key,cost=Stats(g.Select(x=>x.Ms))}),
            gpuStageNames=new[]{"0 color/depth copy","1 weather bounds","2 seasonal vegetation","3 color recopy and bindings","4 rain/snow particles","5 area/marks","6 pipeline restoration","7 trailing interval","102/104/106/110 admitted tree/depth/land/distant geometry start; odd ID is end/gap"},
            queryCpuMilliseconds=root.TryGetProperty("queryTicks",out var q)?q.GetDouble()*1000/frequency:(double?)null,
            pending=gpu.Count(x=>x.GetProperty("status").GetInt32()==1),invalid=gpu.Count(x=>x.GetProperty("status").GetInt32() is 3 or 4),
            causeConfirmed=false,note="CPU values are wall elapsed; Present is engine cadence, not display FPS. Missing GPU samples are not zero. Per-thread nesting and coverage must be reviewed."};
    }
    private static object Stats(IEnumerable<double> source){var a=source.Order().ToArray();double? Percent(double p)=>a.Length==0?null:a[(int)Math.Ceiling(p*a.Length)-1];
        return new{count=a.Length,p50=Percent(.5),p95=Percent(.95),max=a.Length==0?(double?)null:a[^1],over33=a.Count(v=>v>33.3),over50=a.Count(v=>v>50),over100=a.Count(v=>v>100)};}
}
#endif
