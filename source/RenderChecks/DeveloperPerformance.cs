#if LOCAL_DEVELOPER
using System.Text.Json;
using System.IO;
internal static partial class RenderChecks
{
    private static void CheckPerformanceSummary()
    {
        string fixture="""
        {"version":1,"qpcFrequency":1000,"cpuStages":["guard"],"frames":[{"sampled":1,"cpu":[[4,20,12,8]]}],
         "presents":[[10,15,1,1],[30,33,1,1],[11,12,2,1]],
         "gpu":[{"frame":1,"status":2,"frequency":1000,"points":[[0,20],[1,25]]},
                  {"frame":2,"status":1,"frequency":0,"points":[]},{"frame":3,"status":3,"frequency":0,"points":[]}]}
        """;
        using var parsed=JsonDocument.Parse(JsonSerializer.Serialize(San14Tenkazu.DeveloperPerformanceSummary.Read(fixture)));
        var root=parsed.RootElement;
        Require(root.GetProperty("pending").GetInt32()==1&&root.GetProperty("invalid").GetInt32()==1,"Missing/disjoint GPU samples must remain incomplete");
        Require(!root.GetProperty("causeConfirmed").GetBoolean()&&root.GetProperty("present").GetArrayLength()==2,"Present thread lanes must remain separate without claiming a cause");
        Require(root.GetProperty("gpu")[0].GetProperty("cost").GetProperty("count").GetInt32()==1&&root.GetProperty("gpu")[0].GetProperty("cost").GetProperty("p50").GetDouble()==5,"GPU delta must use its own frequency and exclude pending data");
        Require(root.GetProperty("cpu")[0].GetProperty("sampledFrameCosts").GetProperty("p50").GetDouble()==12,"CPU exclusive cost must not count nested time twice");
        bool invalid=false;try{San14Tenkazu.DeveloperPerformanceSummary.Read(fixture.Replace("\"qpcFrequency\":1000","\"qpcFrequency\":0"));}catch(InvalidDataException){invalid=true;}
        Require(invalid,"Zero frequency must reject the result");
        Console.WriteLine("PASS CPU/GPU performance parser: separate clocks/thread lanes, exclusive scopes, pending/disjoint not zero, no false cause confirmation");
    }
}
#endif
