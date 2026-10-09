// Included after the translated, hash-authenticated installation PS bindings.
// These operations change the terrain material before the game's lighting and
// UI composition. The original MRT computation and original alpha are retained.
#include "SeasonGameParameters.hlsli"
#include "SeasonWorldPosition.hlsli"
#include "SeasonVegetation.hlsli"
cbuffer SeasonTerrainDepth : register(b13)
{
    uint TerrainUnused;
    float TerrainCutoffUnused;
    float2 TerrainDepthRange;
};
// Snapshot of the admitted detailed terrain's original material table. The
// distant PS has a different b0 layout and must not index it as this table.
StructuredBuffer<uint4> TerrainConstants : register(t103);
Texture2D<float4> TerrainPlantControl : register(t104);
Texture2DArray<float4> TerrainPlantDetail : register(t105);
SamplerState TerrainPlantSampler : register(s13);
float SeasonSummerPlot(float2 uv,float3 world,float3 normal)
{
    if((TerrainUnused&256)==0||SeasonWeights.y<=0||normal.y<.93)return 0;
    float4 live=t3.Sample(s3,uv),reference=TerrainPlantControl.Sample(s3,uv);
    if(any(live!=reference))return 0;
    int first=int(live.x*255),second=int(live.y*255);if(first<0||first>=255)return 0;
    int material=first;
    if(second>=0&&second<255&&live.z>.9)material=second;
    else if(second!=first&&live.z>.1)return 0;
    int layer=asint(TerrainConstants[material+7].x);uint kind=SeasonPlantKind(layer);
    if(kind==0)return 0;
    float2 scale=float2(asfloat(TerrainConstants[material+517].x),asfloat(TerrainConstants[material+772].x));
    if(any(scale<=1)||any(!isfinite(scale)))return 0;
    float2 detailUV=world.xz/scale;
    // An owned reference to the admitted immutable detail array is available
    // in both terrain paths. Gradients filter the crop/bank artwork at distance.
    float3 artwork=TerrainPlantDetail.SampleGrad(TerrainPlantSampler,float3(detailUV,layer),ddx(detailUV),ddy(detailUV)).rgb;
    return kind==1?SeasonCropInterior(artwork):SeasonGreenInterior(artwork);
}
float SeasonGroundNoise(float2 cell)
{
    uint2 p=uint2(int2(floor(cell)));uint v=p.x*1664525u+p.y*1013904223u;
    v^=v>>16;v*=2246822519u;v^=v>>13;return float(v&65535u)/65535.0;
}
float2 SeasonGroundPolicy(int layer)
{
    // Authored policies for the authenticated 19-layer E369753A source array.
    // x: vegetation/litter; y: accumulated snow. Unknown layers stay original.
    if(layer<0||layer>=19)return 0;
    if(layer==0||layer==7||layer==9||layer==12)return float2(1,1);
    if(layer==4||layer==17)return float2(.75,1);
    if(layer==5||layer==18)return float2(.45,1);
    if(layer==6||layer==13)return float2(.65,1);
    return float2(0,1);
}
float2 SeasonTerrainOct(float3 n)
{
    n/=max(dot(abs(n),1.0),1e-6);
    float2 folded=(1-abs(n.yx))*float2(n.x>=0?1:-1,n.y>=0?1:-1);
    return (n.z>=0?n.xy:folded)*.5+.5;
}
void SeasonTerrainMaterial(TerrainInput input,inout TerrainOutput output)
{
    float3 control=t3.Sample(s3,input.uv).xyz;
    int first=int(control.x*255),second=int(control.y*255);
    if(first<0||first>=255)return;
    int firstLayer=asint(TerrainConstants[first+7].x);
    float2 policy=SeasonGroundPolicy(firstLayer);
    if(second>=0&&second<255)policy=lerp(policy,SeasonGroundPolicy(asint(TerrainConstants[second+7].x)),control.z);
#if defined(SEASON_DISTANT_TERRAIN)
    float3 world;
    if(!SeasonWorldFromPixel(input.position,SeasonViewport,TerrainDepthRange,world))return;
#else
    float3 world=input.world.xyz;
#endif
    SeasonIntensity season=SeasonAtWorld(world);
    float3 base=output.a.rgb;float luminance=dot(base,float3(.2126,.7152,.0722));
    float3 spring=luminance*float3(.80,1.17,.49),summer=luminance*float3(.61,1.22,.42);
    float3 autumn=luminance*float3(1.30,.90,.48);
    float grass=policy.x*SeasonSmooth(.4,.85,saturate(input.normal.y));
    float3 vegetation=lerp(base,spring,season.NewLeaves*.65);
    vegetation=lerp(vegetation,summer,season.Foliage*.7);
    vegetation=lerp(vegetation,autumn,season.Autumn*.60);
    output.a.rgb=lerp(base,vegetation,grass);
    float plot=SeasonSummerPlot(input.uv,world,input.normal);
    if(plot>0)
    {
        float gust=SeasonSummerCanopyLight(world.xz,SeasonSeconds);
        float3 leafColor=luminance*float3(.58,1.18,.33)*(1+gust*.16);
        // Material canopy lighting remains on the same world patch at every
        // LOD. Original terrain geometry, alpha, depth and lighting stay intact.
        output.a.rgb=lerp(output.a.rgb,leafColor,season.Foliage*plot*.65);
        output.r.w=lerp(output.r.w,.67,season.Foliage*plot*.3);
    }
    // Stable world-space small litter patches; camera motion never moves them.
    float litter=step(.65,SeasonGroundNoise(world.xz/85))*season.Autumn*grass*.18;
    output.a.rgb=lerp(output.a.rgb,luminance*float3(1.45,.65,.20),litter);
    float snow=season.SnowCover*policy.y*SeasonSmooth(.25,.8,saturate(input.normal.y));
    float grain=SeasonGroundNoise(world.xz/24)*.035;
    output.a.rgb=lerp(output.a.rgb,float3(.82,.87,.92)+grain,snow);
    output.r.xyz=lerp(output.r.xyz,float3(.035,.035,.035),snow);
    output.r.w=lerp(output.r.w,.43,snow);
    if(snow>0)
    {
        float3 n=normalize(input.normal);
#if defined(SEASON_DISTANT_TERRAIN)
        const uint viewRow=1;
#else
        const uint viewRow=2;
#endif
        float3 viewNormal=float3(dot(asfloat(GameConstants[viewRow].xyz),n),dot(asfloat(GameConstants[viewRow+1].xyz),n),dot(asfloat(GameConstants[viewRow+2].xyz),n));
        output.n.xy=lerp(output.n.xy,SeasonTerrainOct(viewNormal),snow*.7);
    }
}
