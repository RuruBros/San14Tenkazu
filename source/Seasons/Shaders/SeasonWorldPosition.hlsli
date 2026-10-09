#ifndef SEASON_WORLD_POSITION
#define SEASON_WORLD_POSITION
// 0..3 = row-major world-to-clip, 4..7 = inverse, 8.x = validity.
StructuredBuffer<float4> SeasonMatrices : register(t101);
bool SeasonWorldFromPixel(float4 pixel, float4 viewport, float2 depthRange, out float3 world)
{
    float2 ndc=(pixel.xy-viewport.xy)*viewport.zw*float2(2,-2)+float2(-1,1);
    float z=(pixel.z-depthRange.x)*depthRange.y;
    float4 position=ndc.x*SeasonMatrices[4]+ndc.y*SeasonMatrices[5]+z*SeasonMatrices[6]+SeasonMatrices[7];
    bool valid=SeasonMatrices[8].x==1&&all(isfinite(position))&&abs(position.w)>1e-8;
    world=valid?position.xyz/position.w:0;
    return valid&&all(isfinite(world));
}
float4 SeasonProjectWorld(float3 world)
{
    return world.x*SeasonMatrices[0]+world.y*SeasonMatrices[1]+world.z*SeasonMatrices[2]+SeasonMatrices[3];
}
#endif
