using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonTests
{
    private static SeasonDate Date(int year,int month,int day)
    {
        Assert.True(SeasonDate.TryCreate(year,month,day,out var date));return date;
    }
    [Theory]
    [InlineData(194,0,1)] [InlineData(194,13,1)] [InlineData(194,1,0)] [InlineData(194,1,31)]
    [InlineData(100,1,1)] [InlineData(400,1,1)]
    public void RejectsInvalidGameDates(int year,int month,int day)
    {
        Assert.False(SeasonDate.TryCreate(year,month,day,out var date));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonTimeline.At(date));
    }
    [Fact]
    public void EveryMonthHasThirtySamplesAndContinuousNextAnchor()
    {
        for(int month=1;month<=12;month++)
        {
            int next=month==12?1:month+1;
            var first=SeasonTimeline.At(Date(194,month,1));var last=SeasonTimeline.At(Date(194,month,30));
            Assert.Equal(next,first.NextMonth);Assert.Equal(0,first.Fraction);Assert.Equal(29/30.0,last.Fraction);
            for(int day=1;day<=30;day++)
            {
                var w=SeasonTimeline.At(Date(194,month,day)).Weights;
                var values=new[]{w.Spring,w.Summer,w.Autumn,w.Winter};
                Assert.All(values,v=>Assert.InRange(v,0,1));Assert.Equal(1,values.Sum(),12);
            }
        }
    }
    [Fact]
    public void YearWrapSamplesJanuaryAndBackwardLoadsHaveNoAccumulatedState()
    {
        double[] values=Enumerable.Range(1,12).Select(v=>(double)v).ToArray();
        Assert.Equal(12+(1-12)*29/30.0,SeasonTimeline.At(Date(194,12,30)).Sample(values),12);
        Assert.Equal(1,SeasonTimeline.At(Date(195,1,1)).Sample(values));
        var before=SeasonTimeline.At(Date(194,3,11));
        _=SeasonTimeline.At(Date(195,10,21));Assert.Equal(before,SeasonTimeline.At(Date(194,3,11)));
    }
    [Fact]
    public void ClimateRetainsExactStationDataAndInterpolatesSpaceAndMonth()
    {
        var a=new ClimateStation(1,1,0,0,Enumerable.Repeat(0.0,12).ToArray(),Enumerable.Repeat(20.0,12).ToArray());
        var b=new ClimateStation(2,2,10,0,Enumerable.Repeat(20.0,12).ToArray(),Enumerable.Repeat(60.0,12).ToArray());
        var climate=new SeasonClimate([a,b]);var date=SeasonTimeline.At(Date(194,1,1));
        Assert.Equal(new MonthlyClimate(0,20),climate.Sample(0,0,date));
        Assert.Equal(new MonthlyClimate(10,40),climate.Sample(5,0,date));
        Assert.Throws<ArgumentOutOfRangeException>(()=>climate.Sample(double.NaN,0,date));
    }
    [Fact]
    public void OfficialNormalsProduceRegionalWinterAndSummerDifferences()
    {
        var climate=SeasonClimate.Load();Assert.True(climate.Stations.Count>=5);
        var beijing=climate.Stations.Single(s=>s.Id==54511);var guangzhou=climate.Stations.Single(s=>s.Id==59287);
        var jan=SeasonTimeline.At(Date(194,1,1));var july=SeasonTimeline.At(Date(194,7,1));
        Assert.Equal(-2.8,beijing.Sample(jan).TemperatureC);Assert.Equal(13.8,guangzhou.Sample(jan).TemperatureC);
        var cold=SeasonClimate.Intensity(beijing.Sample(jan),jan.Weights);
        var warm=SeasonClimate.Intensity(guangzhou.Sample(jan),jan.Weights);
        Assert.True(cold.SnowCover>warm.SnowCover);Assert.True(cold.LeafRetention<warm.LeafRetention);
        Assert.Equal(0,warm.Snowfall);
        var heat=SeasonClimate.Intensity(guangzhou.Sample(july),july.Weights);
        Assert.Equal(0,heat.HeatHaze);
        foreach(var station in climate.Stations)
            for(int month=1;month<=12;month++) for(int day=1;day<=30;day++)
            {
                var t=SeasonTimeline.At(Date(194,month,day));var i=SeasonClimate.Intensity(station.Sample(t),t.Weights);
                Assert.All(new[]{i.NewLeaves,i.Foliage,i.Autumn,i.LeafRetention,i.SnowCover,i.Snowfall,i.HeatHaze},v=>Assert.InRange(v,0,1));
            }
    }
    [Fact]
    public void IncompleteNormalsCannotBeUsedAsClimate()
    {
        Assert.Throws<ArgumentException>(()=>new ClimateStation(1,1,0,0,new double[11],new double[12]));
        var invalid=new double[12];invalid[0]=double.NaN;
        Assert.Throws<ArgumentException>(()=>new ClimateStation(1,1,0,0,invalid,new double[12]));
    }
    [Fact]
    public void MalformedInterpolationCannotProduceGpuParameters()
    {
        double[] values=new double[12];
        foreach(var date in new[]{default(MonthBlend),new MonthBlend(12,13,0),new MonthBlend(3,5,0),
            new MonthBlend(3,4,double.NaN),new MonthBlend(3,4,-0.1),new MonthBlend(3,4,1.1)})
        {
            Assert.Throws<ArgumentOutOfRangeException>(()=>date.Sample(values));
            Assert.Throws<ArgumentOutOfRangeException>(()=>date.Weights);
        }
        values[0]=double.PositiveInfinity;
        Assert.Throws<ArgumentException>(()=>new MonthBlend(1,2,0).Sample(values));
        foreach(var invalid in new[]{default(SeasonWeights),new SeasonWeights(double.NaN,0,0,1),new SeasonWeights(-1,0,0,2)})
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonClimate.Intensity(new MonthlyClimate(0,10),invalid));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeights.Lerp(SeasonWeights.Month(1),SeasonWeights.Month(3),double.NaN));
    }
    [Fact]
    public void ClimatePositionOverflowIsRejectedInsteadOfReturningNan()
    {
        var climate=SeasonClimate.Load();var date=SeasonTimeline.At(Date(194,1,1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>climate.Sample(double.MaxValue,0,date));
    }
}
