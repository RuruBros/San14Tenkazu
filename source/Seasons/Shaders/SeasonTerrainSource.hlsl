// Minimal live land-source experiment. It preserves the entire BC3 alpha block
// and color indices; this is not the final climate/material compositor.
StructuredBuffer<uint4> Original : register(t0);
RWStructuredBuffer<uint4> Result : register(u0);
cbuffer Parameters : register(b0) { uint Count; float Spring; float2 Padding; };
uint SpringEndpoint(uint packed)
{
    uint3 rgb = uint3((packed >> 11) & 31, (packed >> 5) & 63, packed & 31);
    float3 maximum = float3(31,63,31);
    float3 factor = lerp(1.0, float3(0.95,1.22,0.90), Spring);
    rgb = (uint3)round(clamp((float3)rgb * factor,0,maximum));
    return (rgb.x<<11)|(rgb.y<<5)|rgb.z;
}
[numthreads(64,1,1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if(id.x>=Count)return;
    uint4 block=Original[id.x];
    block.z=SpringEndpoint(block.z&65535)|(SpringEndpoint(block.z>>16)<<16);
    Result[id.x]=block;
}
