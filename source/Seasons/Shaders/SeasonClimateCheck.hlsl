// Standalone WARP check only. This binding layout is not a SAN14 shader/pass identity.
#include "SeasonCommon.hlsli"
struct TestInput
{
    float2 GameXZ;
    uint2 MonthSlices;
    float Fraction;
    float3 Padding;
    float4 Weights;
};
struct TestOutput { float4 A; float4 B; float4 C; };
cbuffer TestConstants : register(b0) { float4 Extent; uint Count; uint3 Padding; };
Texture2DArray<float2> Climate : register(t0);
StructuredBuffer<TestInput> Inputs : register(t1);
SamplerState ClimateSampler : register(s0);
RWStructuredBuffer<TestOutput> Outputs : register(u0);
[numthreads(64,1,1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if(id.x >= Count) return;
    TestInput input = Inputs[id.x];
    float2 climate = SeasonSampleClimate(Climate, ClimateSampler, input.GameXZ, Extent, input.MonthSlices, input.Fraction);
    SeasonIntensity value = SeasonFromClimate(climate, input.Weights);
    TestOutput result;
    result.A = float4(climate, value.NewLeaves, value.Foliage);
    result.B = float4(value.Autumn, value.LeafRetention, value.SnowCover, value.Snowfall);
    result.C = float4(value.HeatHaze,0,0,0);
    Outputs[id.x] = result;
}
