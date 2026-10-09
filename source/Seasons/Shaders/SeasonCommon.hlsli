// Shared CPU/GPU contract for seasonal artwork. Bindings belong to the caller's verified pass.
// Raw climate uses modern JMA monthly normals; the response curves are artwork parameters.
struct SeasonIntensity
{
    float NewLeaves;
    float Foliage;
    float Autumn;
    float LeafRetention;
    float SnowCover;
    float Snowfall;
    float HeatHaze;
};

float SeasonSmooth(float minimum, float maximum, float value)
{
    float t = saturate((value - minimum) / (maximum - minimum));
    return t * t * (3.0 - 2.0 * t);
}

float2 SeasonSampleClimate(Texture2DArray<float2> atlas, SamplerState clampLinear,
    float2 gameXZ, float4 extentMinInverseSpan, uint2 monthSlices, float fraction)
{
    float2 uv = saturate((gameXZ - extentMinInverseSpan.xy) * extentMinInverseSpan.zw);
    // Explicit LOD 0 keeps climate independent of terrain mip/zoom and particle screen size.
    float2 a = atlas.SampleLevel(clampLinear, float3(uv, monthSlices.x), 0);
    float2 b = atlas.SampleLevel(clampLinear, float3(uv, monthSlices.y), 0);
    return lerp(a, b, fraction);
}

SeasonIntensity SeasonFromClimate(float2 climate, float4 weights)
{
    SeasonIntensity result;
    float cold = 1.0 - SeasonSmooth(0, 8, climate.x);
    float moisture = saturate(climate.y / 40.0);
    result.NewLeaves = weights.x * SeasonSmooth(4, 18, climate.x);
    result.Foliage = weights.y;
    result.Autumn = weights.z;
    result.LeafRetention = 1.0 - weights.w * (1.0 - SeasonSmooth(4, 14, climate.x));
    result.SnowCover = weights.w * cold * (0.3 + 0.7 * moisture);
    result.Snowfall = weights.w * cold * moisture * 0.35;
    // Retained in the CPU/GPU inspection contract; the user removed distortion.
    result.HeatHaze = 0;
    return result;
}
