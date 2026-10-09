using San14Tenkazu.Seasons;
namespace San14Tenkazu.Tests;
public class SeasonTreeCoverageTests
{
    [Fact]
    public void WinterRemovesLeavesAndPreservesBranchAndEvergreenAlpha()
    {
        Assert.Equal(0,SeasonTreeCoverage.At(1,new(1,0),0,SeasonFoliagePolicy.Deciduous));
        Assert.Equal(0.4,SeasonTreeCoverage.At(0.4,new(0,1),0,SeasonFoliagePolicy.Deciduous));
        Assert.Equal(0.4,SeasonTreeCoverage.At(0.4,new(1,0),0,SeasonFoliagePolicy.Evergreen));
        Assert.Equal(0.25,SeasonTreeCoverage.At(0.5,new(0.5,0.5),0,SeasonFoliagePolicy.Deciduous));
        Assert.Equal(0,SeasonTreeCoverage.At(0,new(0,0),0,SeasonFoliagePolicy.Deciduous));
    }
    [Fact]
    public void UndefinedPolicyOrMalformedMaskCannotReachRenderParameters()
    {
        Assert.Throws<InvalidDataException>(()=>SeasonTreeCoverage.At(1,new(1,0),0,SeasonFoliagePolicy.Unverified));
        foreach(var mask in new[]{new SeasonTreeMask(0,0),new(1,1),new(double.NaN,1),new(-0.1,1.1)})
            Assert.Throws<InvalidDataException>(()=>SeasonTreeCoverage.At(1,mask,0.5,SeasonFoliagePolicy.Deciduous));
        Assert.Throws<InvalidDataException>(()=>SeasonTreeCoverage.At(1,new(1,0),double.NaN,SeasonFoliagePolicy.Deciduous));
    }
}
