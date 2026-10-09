// Standalone raster/Depth/shadow coverage check; not a game pass replacement or binding layout.
#include "SeasonTreeCoverage.hlsli"
cbuffer TestConstants : register(b0) { float Retention; uint Policy; float Cutoff; uint Padding; };
Texture2D<float4> Source : register(t0);
Texture2D<float2> Masks : register(t1);
struct PixelInput { float4 Position:SV_Position; };
PixelInput Vertex(uint vertex:SV_VertexID)
{
    PixelInput result;
    float2 position=vertex==0?float2(-1,1):vertex==1?float2(3,1):float2(-1,-3);
    result.Position=float4(position,0.5,1);return result;
}
float4 Color(PixelInput input):SV_Target
{
    int3 at=int3(int2(input.Position.xy),0);float4 pixel=Source.Load(at);
    SeasonTreeClip(pixel.a,Masks.Load(at),Retention,Policy,Cutoff);
    return float4(pixel.rgb,1);
}
void Depth(PixelInput input)
{
    int3 at=int3(int2(input.Position.xy),0);
    SeasonTreeClip(Source.Load(at).a,Masks.Load(at),Retention,Policy,Cutoff);
}
void Shadow(PixelInput input)
{
    int3 at=int3(int2(input.Position.xy),0);
    SeasonTreeClip(Source.Load(at).a,Masks.Load(at),Retention,Policy,Cutoff);
}
float4 ColorCoverage(PixelInput input):SV_Target
{
    int3 at=int3(int2(input.Position.xy),0);float4 pixel=Source.Load(at);
    SeasonTreeClipFromCoverage(Masks.Load(at),Retention,Policy,Cutoff);
    return float4(pixel.rgb,1);
}
void DepthCoverage(PixelInput input)
{
    int3 at=int3(int2(input.Position.xy),0);
    SeasonTreeClipFromCoverage(Masks.Load(at),Retention,Policy,Cutoff);
}
void ShadowCoverage(PixelInput input)
{
    int3 at=int3(int2(input.Position.xy),0);
    SeasonTreeClipFromCoverage(Masks.Load(at),Retention,Policy,Cutoff);
}
