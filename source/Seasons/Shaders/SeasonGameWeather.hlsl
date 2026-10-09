#include "SeasonGameParameters.hlsli"
#include "SeasonWorldPosition.hlsli"
#include "SeasonWeather.hlsli"
cbuffer GameWeather:register(b13){float2 WeatherDepthRange;uint WeatherDepthDirection;float WeatherPadding;};
Texture2D<float> WeatherDepth:register(t1);
uint WeatherHash(uint v){v^=v>>16;v*=2246822519u;v^=v>>13;v*=3266489917u;return v^(v>>16);}
struct SnowOutput{float4 position:SV_Position;noperspective float2 flake:TEXCOORD0;nointerpolation float intensity:TEXCOORD1;nointerpolation float depth:TEXCOORD2;};
SnowOutput WeatherSnowVertex(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
{
    const float2 corners[6]={float2(-1,-1),float2(-1,1),float2(1,-1),float2(1,-1),float2(-1,1),float2(1,1)};
    float2 middle=SeasonViewport.xy+.5/SeasonViewport.zw;float depth=WeatherDepth.Load(int3(int2(middle),0));float3 center;
    bool valid=SeasonWorldFromPixel(float4(middle,depth,1),SeasonViewport,WeatherDepthRange,center);
    int2 cell=int2(floor(center.xz/1600))+int2(instance%64,instance/64)-32;
    uint seed=WeatherHash(asuint(cell.x)^WeatherHash(asuint(cell.y)));float2 jitter=float2(seed&65535,seed>>16)/65535.0;
    float3 world=float3((cell.x+jitter.x)*1600,center.y+800+frac(jitter.y-SeasonSeconds*.09)*7000,(cell.y+jitter.y)*1600);
    world.x+=sin(SeasonSeconds*.6+jitter.x*6.283)*250;
    SeasonIntensity season=SeasonAtWorld(world);float4 clipPosition=SeasonProjectWorld(world);float2 corner=corners[vertex];
    SnowOutput o;o.position=clipPosition;o.position.xy+=corner*(1+jitter.x*1.5)*2*SeasonViewport.zw*clipPosition.w;
    o.flake=corner;o.intensity=season.Snowfall;o.depth=clipPosition.z/clipPosition.w;
    if(!valid||!all(isfinite(clipPosition))||clipPosition.w<=1e-6||o.depth<0||o.depth>1){o.position=float4(2,2,2,1);o.intensity=0;}
    return o;
}
float4 WeatherSnow(SnowOutput input):SV_Target
{
    float sceneDepth=WeatherDepth.Load(int3(int2(input.position.xy),0));
    float particleDepth=input.depth/WeatherDepthRange.y+WeatherDepthRange.x;
    if(!SeasonSnowVisible(sceneDepth,particleDepth,WeatherDepthDirection))discard;
    float alpha=SeasonSnowAlpha(input.flake,input.intensity,.85);return float4(alpha.xxx,alpha);
}
