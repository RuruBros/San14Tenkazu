// Callers must validate the resource policy and the normalized mask before rendering.
// Policy 1 = deciduous, 2 = evergreen. Unverified resource IDs are rejected on the CPU.
float SeasonTreeAlpha(float sourceAlpha,float2 leafBranchWeights,float retention,uint policy)
{
    float keep=(policy&3)==2?1.0:retention;
    return sourceAlpha*(leafBranchWeights.y+leafBranchWeights.x*keep);
}
void SeasonTreeClip(float sourceAlpha,float2 leafBranchWeights,float retention,uint policy,float cutoff)
{
    clip(SeasonTreeAlpha(sourceAlpha,leafBranchWeights,retention,policy)-cutoff);
}

// Coverage textures contain alpha-premultiplied contributions at every mip.
// CPU validation must pair them with the diffuse alpha and identical sampling.
float SeasonTreeAlphaFromCoverage(float2 leafBranchCoverage,float retention,uint policy)
{
    float keep=(policy&3)==2?1.0:retention;
    return leafBranchCoverage.y+leafBranchCoverage.x*keep;
}
void SeasonTreeClipFromCoverage(float2 leafBranchCoverage,float retention,uint policy,float cutoff)
{
    clip(SeasonTreeAlphaFromCoverage(leafBranchCoverage,retention,policy)-cutoff);
}
