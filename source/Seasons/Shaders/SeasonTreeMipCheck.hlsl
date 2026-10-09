// Own sampling fixture only; no game shader binding layout or pass replacement.
#include "SeasonTreeCoverage.hlsli"
struct SampleCase { float2 Uv; float Lod; float Retention; uint Policy; uint3 Padding; float4 Expected; };
cbuffer TestConstants : register(b0) { uint Count; uint3 Padding; };
Texture2D<float4> Source : register(t0);
Texture2D<float2> Coverage : register(t1);
StructuredBuffer<SampleCase> Cases : register(t2);
SamplerState LinearClamp : register(s0);
RWStructuredBuffer<float4> Results : register(u0);
[numthreads(64,1,1)]
void Check(uint3 id:SV_DispatchThreadID)
{
    if(id.x>=Count)return;
    SampleCase test=Cases[id.x];
    float alpha=Source.SampleLevel(LinearClamp,test.Uv,test.Lod).a;
    float2 contributions=Coverage.SampleLevel(LinearClamp,test.Uv,test.Lod);
    Results[id.x]=float4(SeasonTreeAlphaFromCoverage(contributions,test.Retention,test.Policy),alpha,contributions);
}
