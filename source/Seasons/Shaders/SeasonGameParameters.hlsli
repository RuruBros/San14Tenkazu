#ifndef SEASON_GAME_PARAMETERS
#define SEASON_GAME_PARAMETERS
#include "SeasonCommon.hlsli"
// Private slots, explicitly saved/restored around the admitted game Draw.
cbuffer SeasonGameParameters : register(b12)
{
    float4 SeasonWeights;
    float4 ClimateExtent; // min X/Z, inverse X/Z span
    uint2 ClimateMonths;
    float ClimateFraction;
    float SeasonSeconds;
    float4 SeasonViewport; // top left X/Y, inverse width/height
};
Texture2DArray<float2> SeasonClimate : register(t100);
SamplerState SeasonClamp : register(s12);
SeasonIntensity SeasonAtWorld(float3 position)
{
    return SeasonFromClimate(SeasonSampleClimate(SeasonClimate, SeasonClamp,
        position.xz, ClimateExtent, ClimateMonths, ClimateFraction), SeasonWeights);
}
#endif
