// Owned fixture bindings. No game register layout, scene/Depth SRV or UI order is assumed.
#include "SeasonWeather.hlsli"
cbuffer Weather:register(b0)
{
    float2 InverseViewport;float HazeIntensity;float AmplitudePixels;
    float PhaseRadians;float2 Frequency;float Padding;
    row_major float4x4 ViewProjection;
    uint DepthDirection;uint3 Reserved;
};
Texture2D<float4> Scene:register(t0);
Texture2D<float> Depth:register(t1);
struct SnowParticle { float3 Position;float RadiusPixels;float Intensity;float Opacity;float2 Reserved; };
StructuredBuffer<SnowParticle> Particles:register(t2);
SamplerState LinearClamp:register(s0);
struct Fullscreen { float4 Position:SV_Position; };
Fullscreen FullscreenVertex(uint id:SV_VertexID)
{
    Fullscreen result;float2 xy=float2((id<<1)&2,id&2);
    result.Position=float4(xy*float2(2,-2)+float2(-1,1),0,1);return result;
}
float4 Haze(Fullscreen input):SV_Target
{
    int2 pixel=int2(input.Position.xy);float4 original=Scene.Load(int3(pixel,0));
    if(HazeIntensity==0||AmplitudePixels==0)return original;
    float2 uv=input.Position.xy*InverseViewport;
    float2 offset=SeasonHazeOffsetPixels(uv,HazeIntensity,AmplitudePixels,PhaseRadians,Frequency);
    float3 distorted=Scene.SampleLevel(LinearClamp,uv+offset*InverseViewport,0).rgb;
    return float4(distorted,original.a);
}
struct SnowVertexOutput
{
    float4 Position:SV_Position;
    noperspective float2 Flake:TEXCOORD0;
    nointerpolation float Intensity:TEXCOORD1;
    nointerpolation float Opacity:TEXCOORD2;
    nointerpolation float ParticleDepth:TEXCOORD3;
};
SnowVertexOutput SnowVertex(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
{
    const float2 corners[6]={float2(-1,-1),float2(-1,1),float2(1,-1),float2(1,-1),float2(-1,1),float2(1,1)};
    SnowParticle particle=Particles[instance];float2 corner=corners[vertex];
    float4 clipPosition=mul(float4(particle.Position,1),ViewProjection);
    SnowVertexOutput result;
    clipPosition.xy+=corner*particle.RadiusPixels*2*InverseViewport*clipPosition.w;
    result.Position=clipPosition;result.Flake=corner;
    result.Intensity=particle.Intensity;result.Opacity=particle.Opacity;
    if(!all(isfinite(clipPosition))||clipPosition.w<=1e-6||clipPosition.z<0||clipPosition.z>clipPosition.w)
    {
        result.Position=float4(2,2,2,1);result.Intensity=0;result.ParticleDepth=0;return result;
    }
    result.ParticleDepth=clipPosition.z/clipPosition.w;return result;
}
float4 Snow(SnowVertexOutput input):SV_Target
{
    float sceneDepth=Depth.Load(int3(int2(input.Position.xy),0));
    if(!SeasonSnowVisible(sceneDepth,input.ParticleDepth,DepthDirection))discard;
    float alpha=SeasonSnowAlpha(input.Flake,input.Intensity,input.Opacity);
    // Premultiplied color; blend keeps destination alpha and never writes scene Depth.
    return float4(alpha.xxx,alpha);
}
float4 FixtureUi(Fullscreen input):SV_Target
{
    if(input.Position.x>=4||input.Position.y>=4)discard;
    return float4(.1,.8,.2,.65);
}
