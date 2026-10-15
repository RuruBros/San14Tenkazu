using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonClimateAtlasTests
{
    [Fact]
    public void ReusedCoordinateWeightsPreserveEveryMonthlyDoubleIncludingExactAnchorsAndTies()
    {
        var climate=SeasonClimate.Load();
        var points=climate.Stations.Select(s=>(s.X,s.Z)).Concat(new[]{(0.0,0.0),(102400.0,102400.0),(204800.0,204800.0),(-10.0,204810.0)});
        foreach(var (x,z) in points)
        {
            var months=new MonthlyClimate[12];climate.SampleMonths(x,z,months);
            for(int month=0;month<12;month++)Assert.Equal(climate.Sample(x,z,new(month+1,month==11?1:month+2,0)),months[month]);
        }
        double[] Values(double offset)=>Enumerable.Range(0,12).Select(i=>offset+i).ToArray();
        var ties=new SeasonClimate([new ClimateStation(2,2,1,0,Values(10),Values(100)),new ClimateStation(1,1,-1,0,Values(20),Values(200))]);
        var tied=new MonthlyClimate[12];ties.SampleMonths(0,0,tied);
        for(int month=0;month<12;month++)Assert.Equal(ties.Sample(0,0,new(month+1,month==11?1:month+2,0)),tied[month]);
        Assert.Throws<ArgumentException>(()=>climate.SampleMonths(0,0,new MonthlyClimate[11]));
        Assert.Throws<ArgumentOutOfRangeException>(()=>climate.SampleMonths(double.NaN,0,new MonthlyClimate[12]));
        Assert.Throws<ArgumentOutOfRangeException>(()=>climate.SampleMonths(double.MaxValue,0,new MonthlyClimate[12]));
    }

    [Fact]
    public void RowParallelAtlasPreservesOriginalMonthlyLayoutBitForBit()
    {
        var climate=SeasonClimate.Load();var extent=new ClimateExtent(-37,13,204811,204823);
        foreach(var (width,height) in new[]{(1,1),(19,7),(5,23)})
        {
            var atlas=SeasonClimateAtlas.Build(climate,width,height,extent);
            for(int month=0;month<12;month++)for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                double gx=extent.MinX+(x+0.5)/width*(extent.MaxX-extent.MinX),gz=extent.MinZ+(y+0.5)/height*(extent.MaxZ-extent.MinZ);
                var expected=climate.Sample(gx,gz,new(month+1,month==11?1:month+2,0));int at=((month*height+y)*width+x)*2;
                Assert.Equal(BitConverter.SingleToInt32Bits((float)expected.TemperatureC),BitConverter.SingleToInt32Bits(atlas.Data.Span[at]));
                Assert.Equal(BitConverter.SingleToInt32Bits((float)expected.PrecipitationMm),BitConverter.SingleToInt32Bits(atlas.Data.Span[at+1]));
            }
        }
    }

    [Fact]
    public void TexelCentersMatchSourceNormalsAndDecemberWraps()
    {
        var climate=SeasonClimate.Load();var extent=new ClimateExtent(0,0,204800,204800);
        var atlas=SeasonClimateAtlas.Build(climate,16,8,extent);
        Assert.Equal(16*8*12*2,atlas.Data.Length);
        for(int month=1;month<=12;month++)for(int y=0;y<8;y++)for(int x=0;x<16;x++)
        {
            double gx=(x+0.5)/16*204800,gz=(y+0.5)/8*204800;
            var date=new MonthBlend(month,month==12?1:month+1,29/30.0);
            var expected=climate.Sample(gx,gz,date);var actual=atlas.Sample(gx,gz,date);
            Assert.InRange(Math.Abs(expected.TemperatureC-actual.TemperatureC),0,0.00001);
            Assert.InRange(Math.Abs(expected.PrecipitationMm-actual.PrecipitationMm),0,0.0001);
        }
    }
    [Fact]
    public void BilinearSamplingUsesCentersAndClampsOutsideWorld()
    {
        var climate=new SeasonClimate([
            new ClimateStation(1,1,0,0,Enumerable.Repeat(0.0,12).ToArray(),Enumerable.Repeat(20.0,12).ToArray()),
            new ClimateStation(2,2,10,10,Enumerable.Repeat(20.0,12).ToArray(),Enumerable.Repeat(60.0,12).ToArray())]);
        var atlas=SeasonClimateAtlas.Build(climate,2,2,new(0,0,10,10));var date=new MonthBlend(1,2,0);
        var middle=atlas.Sample(5,5,date);Assert.Equal(10,middle.TemperatureC,5);Assert.Equal(40,middle.PrecipitationMm,5);
        Assert.Equal(atlas.Sample(0,0,date),atlas.Sample(-double.MaxValue,-double.MaxValue,date));
        Assert.Equal(atlas.Sample(10,10,date),atlas.Sample(double.MaxValue,double.MaxValue,date));
        Assert.True(atlas.Sample(2.5,2.5,date).TemperatureC<atlas.Sample(7.5,7.5,date).TemperatureC);
        Assert.Throws<ArgumentOutOfRangeException>(()=>atlas.Sample(double.NaN,0,date));
        Assert.Throws<ArgumentOutOfRangeException>(()=>atlas.Sample(0,0,new(12,13,0)));
    }
    [Fact]
    public void SpatialFieldIsContinuousWhenFourthNearestAnchorChanges()
    {
        double[] Flat(double v)=>Enumerable.Repeat(v,12).ToArray();
        // The two distant anchors exchange fourth-nearest rank across x=0. A hard cutoff jumps.
        var climate=new SeasonClimate([
            new ClimateStation(1,1,0,-1,Flat(5),Flat(10)),new ClimateStation(2,2,0,1,Flat(5),Flat(10)),
            new ClimateStation(3,3,0,2,Flat(5),Flat(10)),new ClimateStation(4,4,-3,0,Flat(-20),Flat(0)),
            new ClimateStation(5,5,3,0,Flat(40),Flat(200))]);
        var date=new MonthBlend(1,2,0);
        var left=climate.Sample(-0.000001,0,date);var right=climate.Sample(0.000001,0,date);
        Assert.InRange(Math.Abs(left.TemperatureC-right.TemperatureC),0,0.00001);
        Assert.InRange(Math.Abs(left.PrecipitationMm-right.PrecipitationMm),0,0.0001);
    }
    [Fact]
    public void InvalidAtlasExtentAndAllocationAreRejected()
    {
        var climate=SeasonClimate.Load();
        foreach(var extent in new[]{new ClimateExtent(0,0,0,10),new(double.NaN,0,10,10),new(-double.MaxValue,0,double.MaxValue,10)})
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonClimateAtlas.Build(climate,4,4,extent));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonClimateAtlas.Build(climate,1025,4,new(0,0,10,10)));
    }
}
