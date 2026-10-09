using System.Numerics;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonTerrainTests
{
    private static readonly SeasonTerrainMaterial Original=new(new(.2f,.3f,.4f),Vector3.UnitZ,.7f);
    private static readonly SeasonTerrainMaterial[] Layers=[
        new(new(1,0,0),Vector3.UnitZ,.1f),new(new(0,1,0),Vector3.UnitZ,.2f),
        new(new(0,0,1),Vector3.UnitZ,.3f),new(new(.5f,.5f,0),Vector3.UnitZ,.4f),
        new(Vector3.One,Vector3.UnitZ,.9f)];
    private static readonly SeasonTerrainParameters Full=new(1,1,1,1,1,.5f,.9f);
    [Fact]
    public void IneligibleOrZeroStrengthTerrainReturnsOriginalExactly()
    {
        var intensity=new SeasonTerrainInfluence(new(.2f,.3f,.1f,.4f),1);
        foreach(var parameters in new[]{Full with {GroundCoverage=0,SnowEligibility=0},Full with {GroundStrength=0,SnowStrength=0}})
        {
            var result=SeasonTerrainComposition.Compose(Original,Layers,intensity,parameters);
            Assert.Equal(Original,result.Material);Assert.Equal(Vector4.Zero,result.GroundWeights);Assert.Equal(0,result.SnowWeight);
        }
        Assert.Equal(Original,SeasonTerrainComposition.Compose(Original,Layers,new(Vector4.Zero,0),Full).Material);
    }
    [Fact]
    public void SeasonalConvexBlendPreservesLinearColorAndRoughness()
    {
        var blend=SeasonTerrainComposition.Compose(Original,Layers,new(new(.25f,.25f,.25f,.25f),0),Full);
        Assert.Equal(new Vector3(.375f,.375f,.25f),blend.Material.Albedo);
        Assert.Equal(.25f,blend.Material.Roughness,6);Assert.Equal(Vector3.UnitZ,blend.Material.Normal);
        var half=SeasonTerrainComposition.Compose(Original,Layers,new(new(1,0,0,0),0),Full with {GroundCoverage=.5f});
        Assert.Equal(new Vector3(.6f,.15f,.2f),half.Material.Albedo);
        Assert.Equal(.4f,half.Material.Roughness,6);
    }
    [Fact]
    public void SnowEligibilityAndSlopeApplyAfterGroundBlend()
    {
        var influence=new SeasonTerrainInfluence(new(1,0,0,0),1);
        Assert.Equal(Layers[4],SeasonTerrainComposition.Compose(Original,Layers,influence,Full).Material);
        Assert.Equal(Layers[0],SeasonTerrainComposition.Compose(Original,Layers,influence,Full with {UpFacing=.5f}).Material);
        Assert.Equal(Layers[0],SeasonTerrainComposition.Compose(Original,Layers,influence,Full with {SnowEligibility=0}).Material);
        var middle=SeasonTerrainComposition.Compose(Original,Layers,influence,Full with {UpFacing=.7f});
        Assert.Equal(.5f,middle.SnowWeight,6);Assert.Equal(1,middle.Material.Albedo.X);
        Assert.Equal(.5f,middle.Material.Albedo.Y,6);Assert.Equal(.5f,middle.Material.Albedo.Z,6);
    }
    [Fact]
    public void OppositeNormalsUseOriginalDirectionInsteadOfNaN()
    {
        var layers=(SeasonTerrainMaterial[])Layers.Clone();layers[0]=layers[0] with {Normal=-Vector3.UnitZ};
        var result=SeasonTerrainComposition.Compose(Original,layers,new(new(.5f,0,0,0),0),Full);
        Assert.Equal(Vector3.UnitZ,result.Material.Normal);
    }
    [Fact]
    public void AllStationDatesHaveBoundedConvexContributionsAndFiniteUnitNormals()
    {
        var climate=SeasonClimate.Load();int cases=0;
        foreach(var station in climate.Stations)for(int month=1;month<=12;month++)for(int day=1;day<=30;day++)
        {
            Assert.True(SeasonDate.TryCreate(200,month,day,out var date));var blend=SeasonTimeline.At(date);
            var influence=SeasonTerrainInfluence.FromClimate(SeasonClimate.Intensity(station.Sample(blend),blend.Weights));
            var result=SeasonTerrainComposition.Compose(Original,Layers,influence,Full);
            float total=result.GroundWeights.X+result.GroundWeights.Y+result.GroundWeights.Z+result.GroundWeights.W;
            Assert.InRange(total,0,1.000001f);Assert.InRange(result.SnowWeight,0,1);
            Assert.InRange(result.Material.Roughness,0,1);
            foreach(float color in new[]{result.Material.Albedo.X,result.Material.Albedo.Y,result.Material.Albedo.Z})Assert.InRange(color,0,1);
            Assert.Equal(1,result.Material.Normal.Length(),6);cases++;
        }
        Assert.Equal(2520,cases);
    }
    [Fact]
    public void InvalidArtworkParametersAndContributionSumsAreRejected()
    {
        var influence=new SeasonTerrainInfluence(new(1,0,0,0),0);
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-.1f,1.1f})
        {
            foreach(var parameters in new[]{Full with {GroundCoverage=bad},Full with {SnowEligibility=bad},Full with {UpFacing=bad},
                Full with {GroundStrength=bad},Full with {SnowStrength=bad},Full with {SnowSlopeMin=bad},Full with {SnowSlopeMax=bad}})
                Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonTerrainComposition.Compose(Original,Layers,influence,parameters));
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonTerrainComposition.Compose(Original,Layers,new(new(bad,0,0,0),0),Full));
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonTerrainComposition.Compose(Original,Layers,new(Vector4.Zero,bad),Full));
        }
        foreach(var parameters in new[]{Full with {SnowSlopeMax=.5f},Full with {SnowSlopeMin=.95f},
            Full with {SnowSlopeMin=0,SnowSlopeMax=float.Epsilon},Full with {SnowSlopeMin=.8999999f}})
            Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonTerrainComposition.Compose(Original,Layers,influence,parameters));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SeasonTerrainComposition.Compose(Original,Layers,new(new(.4f,.4f,.4f,0),0),Full));
    }
    [Fact]
    public void InvalidMaterialSamplesAreRejectedEvenWhenDisabled()
    {
        foreach(var invalid in new[]{Original with {Albedo=new(float.NaN,0,0)},Original with {Albedo=new(-1,0,0)},
            Original with {Roughness=2},Original with {Normal=Vector3.Zero},Original with {Normal=new(float.PositiveInfinity,0,0)},
            Original with {Normal=Vector3.One}})
        {
            Assert.Throws<ArgumentException>(()=>SeasonTerrainComposition.Compose(invalid,Layers,new(Vector4.Zero,0),Full));
            var layers=(SeasonTerrainMaterial[])Layers.Clone();layers[4]=invalid;
            Assert.Throws<ArgumentException>(()=>SeasonTerrainComposition.Compose(Original,layers,new(Vector4.Zero,0),Full));
        }
        Assert.Throws<ArgumentException>(()=>SeasonTerrainComposition.Compose(Original,[],new(Vector4.Zero,0),Full));
    }
}
