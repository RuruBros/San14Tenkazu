// Own material compositor; the caller must verify material eligibility, UVs and the common tangent basis.
// Albedo inputs are linear light (sRGB views decode before this function). No pass or register is inferred.
struct SeasonTerrainMaterial { float3 Albedo; float3 Normal; float Roughness; };
struct SeasonTerrainParameters
{
    float GroundCoverage; float SnowEligibility; float UpFacing;
    float GroundStrength; float SnowStrength; float SnowSlopeMin; float SnowSlopeMax;
};
SeasonTerrainMaterial SeasonComposeTerrain(SeasonTerrainMaterial original,
    SeasonTerrainMaterial spring, SeasonTerrainMaterial summer, SeasonTerrainMaterial autumn,
    SeasonTerrainMaterial winterLitter, SeasonTerrainMaterial snowMaterial,
    float4 groundInfluence, float snowInfluence, SeasonTerrainParameters parameters,
    out float4 groundWeights, out float snowWeight)
{
    // Host validation requires finite unit inputs, slope interval >= 1e-4 and sum <= 1 (+ roundoff).
    groundWeights=groundInfluence*(parameters.GroundCoverage*parameters.GroundStrength/
        max(1,dot(groundInfluence,1.0)));
    float t=saturate((parameters.UpFacing-parameters.SnowSlopeMin)/
        (parameters.SnowSlopeMax-parameters.SnowSlopeMin));
    snowWeight=snowInfluence*parameters.SnowEligibility*parameters.SnowStrength*t*t*(3-2*t);
    float remaining=1-dot(groundWeights,1.0);
    if(remaining==1&&snowWeight==0)return original;
    SeasonTerrainMaterial result;
    result.Albedo=original.Albedo*remaining+spring.Albedo*groundWeights.x+summer.Albedo*groundWeights.y+
        autumn.Albedo*groundWeights.z+winterLitter.Albedo*groundWeights.w;
    result.Normal=original.Normal*remaining+spring.Normal*groundWeights.x+summer.Normal*groundWeights.y+
        autumn.Normal*groundWeights.z+winterLitter.Normal*groundWeights.w;
    result.Roughness=original.Roughness*remaining+spring.Roughness*groundWeights.x+summer.Roughness*groundWeights.y+
        autumn.Roughness*groundWeights.z+winterLitter.Roughness*groundWeights.w;
    result.Albedo=lerp(result.Albedo,snowMaterial.Albedo,snowWeight);
    result.Normal=lerp(result.Normal,snowMaterial.Normal,snowWeight);
    result.Roughness=lerp(result.Roughness,snowMaterial.Roughness,snowWeight);
    float squared=dot(result.Normal,result.Normal);
    result.Normal=squared>1e-12?result.Normal*rsqrt(squared):original.Normal;
    // Alpha, geometry, lighting and depth are untouched by this material function.
    return result;
}
