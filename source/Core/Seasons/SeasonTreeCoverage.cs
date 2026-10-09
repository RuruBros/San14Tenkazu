namespace San14Tenkazu.Seasons;

internal enum SeasonFoliagePolicy { Unverified=0, Deciduous=1, Evergreen=2 }
internal readonly record struct SeasonTreeMask(double Leaf,double Branch);

internal static class SeasonTreeCoverage
{
    internal static double At(double sourceAlpha,SeasonTreeMask mask,double leafRetention,SeasonFoliagePolicy policy)
    {
        if(policy is not (SeasonFoliagePolicy.Deciduous or SeasonFoliagePolicy.Evergreen))
            throw new InvalidDataException("A verified per-resource foliage policy is required");
        if(!double.IsFinite(sourceAlpha) || sourceAlpha is <0 or >1 || !double.IsFinite(leafRetention) || leafRetention is <0 or >1 ||
            !double.IsFinite(mask.Leaf) || !double.IsFinite(mask.Branch) || mask.Leaf is <0 or >1 || mask.Branch is <0 or >1 ||
            (sourceAlpha>0 && Math.Abs(mask.Leaf+mask.Branch-1)>1e-6))
            throw new InvalidDataException("Finite alpha/retention and normalized leaf/branch weights are required");
        double keep=policy==SeasonFoliagePolicy.Evergreen?1:leafRetention;
        return sourceAlpha*(mask.Branch+mask.Leaf*keep);
    }
}
