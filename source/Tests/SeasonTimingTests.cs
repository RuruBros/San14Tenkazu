using System.Globalization;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonTimingTests
{
    [Fact]
    public void FailureAndDeferredOutcomesAreNotReportedAsApplied()
    {
        var lines=new List<string>();
        using(var timing=new SeasonTiming(lines.Add,"failure"))timing.Step("source-read");
        Assert.Contains("outcome=incomplete",lines[^1]);
        using(var timing=new SeasonTiming(lines.Add,"deferred"))timing.Complete("waiting-for-pause");
        Assert.Contains("outcome=waiting-for-pause",lines[^1]);
        Assert.Equal(3,lines.Count);
    }

    [Fact]
    public void StageDurationsRemainParseableUnderCommaDecimalCulture()
    {
        var lines=new List<string>();var previousCulture=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");
            using(var timing=new SeasonTiming(lines.Add,"prepare"))
            {timing.Step("read");timing.Step("pack");timing.Complete();}
        }
        finally{CultureInfo.CurrentCulture=previousCulture;}
        double previousTotal=0;
        foreach(string line in lines)
        {
            string Value(string name)=>line.Split(", ").Single(v=>v.StartsWith(name+"="))[(name.Length+1)..];
            double elapsed=double.Parse(Value("elapsedMs"),CultureInfo.InvariantCulture);
            double total=double.Parse(Value("totalMs"),CultureInfo.InvariantCulture);
            Assert.True(elapsed>=0&&total>=elapsed&&total>=previousTotal);previousTotal=total;
        }
        Assert.Single(lines.Select(line=>line.Split(", ")[1]).Distinct());
        Assert.Contains("outcome=ok",lines[^1]);
    }

    [Fact]
    public void LoggingFailureCannotInterruptNativeOwnershipOrRecovery()
    {
        using var timing=new SeasonTiming(_=>throw new IOException("Journal unavailable"),"connect");
        timing.Step("worker-started");timing.Complete();
    }
}
