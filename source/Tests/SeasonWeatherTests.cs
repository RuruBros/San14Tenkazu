using System.Numerics;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonWeatherTests
{
    private static readonly SeasonHazeParameters Haze=new(.25f,2,0,new(4,3));
    [Fact]
    public void ZeroHazePreservesUvAndPixelDisplacementIsBounded()
    {
        foreach(float phase in new[]{0,1,3,6})for(int x=0;x<=32;x++)for(int y=0;y<=16;y++)
        {
            var uv=new Vector2(x/32f,y/16f);
            Assert.Equal(Vector2.Zero,SeasonWeather.HazeOffsetPixels(uv,Haze with {Intensity=0,PhaseRadians=phase}));
            var offset=SeasonWeather.HazeOffsetPixels(uv,Haze with {PhaseRadians=phase});
            Assert.InRange(offset.X,-.5f,.5f);Assert.InRange(offset.Y,-.5f,.5f);
        }
    }
    [Fact]
    public void ForwardAndReversedDepthOccludeOppositeSidesAndIncludeEquality()
    {
        Assert.False(SeasonWeather.SnowVisible(.3f,.5f,SeasonDepthDirection.Forward));
        Assert.True(SeasonWeather.SnowVisible(.9f,.5f,SeasonDepthDirection.Forward));
        Assert.False(SeasonWeather.SnowVisible(.7f,.5f,SeasonDepthDirection.Reversed));
        Assert.True(SeasonWeather.SnowVisible(.1f,.5f,SeasonDepthDirection.Reversed));
        foreach(var direction in new[]{SeasonDepthDirection.Forward,SeasonDepthDirection.Reversed})
            Assert.True(SeasonWeather.SnowVisible(.5f,.5f,direction));
    }
    [Fact]
    public void SnowAlphaIsSoftBoundedAndZeroIntensityProducesNoCoverage()
    {
        Assert.Equal(.35f,SeasonWeather.SnowAlpha(Vector2.Zero,.35f,1));
        Assert.Equal(0,SeasonWeather.SnowAlpha(Vector2.One,.35f,1));
        for(int x=-16;x<=16;x++)for(int y=-16;y<=16;y++)
        {
            var uv=new Vector2(x/16f,y/16f);
            Assert.Equal(0,SeasonWeather.SnowAlpha(uv,0,1));
            Assert.InRange(SeasonWeather.SnowAlpha(uv,.35f,.5f),0,.175f);
        }
    }
    [Fact]
    public void ExplicitVolumeMotionWrapsNegativeTravelWithoutChangingDate()
    {
        Assert.Equal(new Vector3(.25f,.75f,.5f),SeasonWeather.SnowPosition(new(.25f,.25f,.5f),new(0,-1,0),.5));
        foreach(double phase in new[]{0,.25,.5,.999999})
        {
            var position=SeasonWeather.SnowPosition(new(.1f,.2f,.3f),new(2,-3,1),phase);
            Assert.InRange(position.X,0,1);Assert.InRange(position.Y,0,1);Assert.InRange(position.Z,0,1);
        }
    }
    [Fact]
    public void UnverifiedDepthAndInvalidArtworkInputsAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>SeasonWeather.SnowVisible(.5f,.5f,SeasonDepthDirection.Unverified));
        Assert.Throws<InvalidDataException>(()=>SeasonWeather.SnowVisible(.5f,.5f,(SeasonDepthDirection)99));
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-1,2})
        {
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.SnowVisible(bad,.5f,SeasonDepthDirection.Forward));
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.SnowAlpha(Vector2.Zero,bad,1));
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.SnowPosition(new(bad,0,0),Vector3.Zero,0));
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.HazeOffsetPixels(Vector2.Zero,Haze with {Intensity=bad}));
        }
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.HazeOffsetPixels(Vector2.Zero,Haze with {AmplitudePixels=9}));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.HazeOffsetPixels(Vector2.Zero,Haze with {Frequency=new(65,1)}));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.SnowPosition(Vector3.Zero,new(0,-9,0),0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonWeather.SnowPosition(Vector3.Zero,Vector3.Zero,1));
    }
}
