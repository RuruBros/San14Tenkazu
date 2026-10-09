using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonClimateAtlasTests
{
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
