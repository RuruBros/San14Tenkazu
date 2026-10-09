// Owned WARP fixture register layout, never a game replacement shader.
#include "SeasonTerrainComposition.hlsli"
struct TerrainCase
{
    float2 Uv; float Lod; float UpFacing;
    float4 Ground;
    float GroundCoverage; float SnowEligibility; float Snow; float Padding;
    float GroundStrength; float SnowStrength; float SnowSlopeMin; float SnowSlopeMax;
    float3 OriginalColor; float OriginalRoughness;
    float3 OriginalNormal; float Reserved;
    float4 ExpectedColorRoughness; float4 ExpectedNormalSnow; float4 ExpectedGround;
};
struct TerrainResult { float4 ColorRoughness; float4 NormalSnow; float4 Ground; };
cbuffer TestConstants:register(b0) { uint Count; uint3 Padding; };
Texture2DArray<float4> Diffuse:register(t0);
Texture2DArray<float4> Normals:register(t1);
Texture2DArray<float4> Roughness:register(t2);
StructuredBuffer<TerrainCase> Cases:register(t3);
SamplerState LinearWrap:register(s0);
RWStructuredBuffer<TerrainResult> Results:register(u0);
SeasonTerrainMaterial MaterialAt(float2 uv,float lod,uint slice)
{
    SeasonTerrainMaterial result;
    result.Albedo=Diffuse.SampleLevel(LinearWrap,float3(uv,slice),lod).rgb;
    float3 tangentNormal=Normals.SampleLevel(LinearWrap,float3(uv,slice),lod).rgb*2-1;
    result.Normal=dot(tangentNormal,tangentNormal)>1e-12?normalize(tangentNormal):float3(0,0,1);
    result.Roughness=Roughness.SampleLevel(LinearWrap,float3(uv,slice),lod).r;
    return result;
}
[numthreads(64,1,1)]
void Check(uint3 id:SV_DispatchThreadID)
{
    if(id.x>=Count)return;
    TerrainCase test=Cases[id.x];
    SeasonTerrainMaterial original;original.Albedo=test.OriginalColor;
    original.Normal=test.OriginalNormal;original.Roughness=test.OriginalRoughness;
    SeasonTerrainParameters p;p.GroundCoverage=test.GroundCoverage;p.SnowEligibility=test.SnowEligibility;
    p.UpFacing=test.UpFacing;p.GroundStrength=test.GroundStrength;p.SnowStrength=test.SnowStrength;
    p.SnowSlopeMin=test.SnowSlopeMin;p.SnowSlopeMax=test.SnowSlopeMax;
    TerrainResult output;float snow;
    SeasonTerrainMaterial material=SeasonComposeTerrain(original,MaterialAt(test.Uv,test.Lod,0),
        MaterialAt(test.Uv,test.Lod,1),MaterialAt(test.Uv,test.Lod,2),MaterialAt(test.Uv,test.Lod,3),
        MaterialAt(test.Uv,test.Lod,4),test.Ground,test.Snow,p,output.Ground,snow);
    output.ColorRoughness=float4(material.Albedo,material.Roughness);
    output.NormalSnow=float4(material.Normal,snow);Results[id.x]=output;
}
