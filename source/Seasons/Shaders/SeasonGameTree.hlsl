// Authenticated tree VS linkage. The baseline MRT equations are transcribed
// from archive candidate bcf53b...; candidate parity is tested separately from
// admission of an actual game PS. Metadata matching is not bytecode identity.
#include "SeasonGameParameters.hlsli"
#include "SeasonWorldPosition.hlsli"
#include "SeasonTreeCoverage.hlsli"
cbuffer GameTree : register(b0)
{
#if defined(SEASON_DEPTH)
    float4 ShadowBias;
    float ShadowStageCount;
    float3 ShadowPadding;
    float4 ShadowAlphaMap;
#else
    float4 GameSpecular;
    float4 GameWorldToView[3];
    float GameAlphaCutoff;
    int GameMaterialId;
    float2 GamePadding;
#endif
};
cbuffer SeasonTree : register(b13)
{
    uint TreePolicy;
    float TreeCutoff;
    float2 TreeDepthRange; // viewport min depth, inverse depth span
};
Texture2D<float4> TreeDiffuse : register(t0);
Texture2D<float4> TreeReflectance : register(t1);
Texture2D<float4> TreeOcclusion : register(t2);
Texture2D<float4> TreeNormal : register(t3);
Texture2D<float2> TreeCoverage : register(t102);
SamplerState TreeDiffuseSampler : register(s0);
SamplerState TreeReflectanceSampler : register(s1);
SamplerState TreeOcclusionSampler : register(s2);
SamplerState TreeNormalSampler : register(s3);

float SeasonTreeCoverageAt(float2 uv,float retention)
{
    float2 contributions=TreeCoverage.Sample(TreeDiffuseSampler,uv);
    return SeasonTreeAlphaFromCoverage(contributions,retention,TreePolicy);
}

bool SeasonTreeAtPixel(float4 position,out SeasonIntensity season)
{
    float3 world;bool valid=SeasonWorldFromPixel(position,SeasonViewport,TreeDepthRange,world);
    season=SeasonAtWorld(world);return valid;
}

#if defined(SEASON_DEPTH)
void TreeDepth(float4 position:SV_Position,float2 uv:TEXCOORD0)
{
    float alpha=ShadowStageCount>0?saturate(TreeDiffuse.Sample(TreeDiffuseSampler,uv).a*ShadowAlphaMap.y+ShadowAlphaMap.z):1;
#if !defined(SEASON_BASELINE)
    SeasonIntensity season;
    if(ShadowStageCount==1&&ShadowAlphaMap.y==1&&ShadowAlphaMap.z==0&&SeasonTreeAtPixel(position,season))
    {
        // One canonical coverage/cutoff is used by both seasonal passes. The
        // host admits only the captured compatible color/depth alpha contract.
        alpha=SeasonTreeCoverageAt(uv,season.LeafRetention);
        clip(alpha-TreeCutoff);return;
    }
#endif
    clip(alpha-ShadowBias.w);
}
#else
struct TreeInput
{
    float4 Position:SV_Position;
    float4 Tangent:TEXCOORD0;
    float3 Normal:TEXCOORD1;
    float2 Uv:TEXCOORD2;
};
struct TreeOutput
{
    float4 AlbedoOcclusion:SV_Target0;
    float4 NormalMaterial:SV_Target1;
    float4 ReflectanceRoughness:SV_Target2;
    float4 Reserved:SV_Target3;
    float4 SurfaceBasis:SV_Target4;
};
float2 TreeEncodeNormal(float3 normal)
{
    normal/=dot(abs(normal),1.0);
    float2 folded=(1-abs(normal.yx))*float2(normal.x>=0?1:-1,normal.y>=0?1:-1);
    return (normal.z>=0?normal.xy:folded)*.5+.5;
}
TreeOutput TreeColor(TreeInput input)
{
    float4 diffuse=TreeDiffuse.Sample(TreeDiffuseSampler,input.Uv);
    float3 normal=normalize(input.Normal),tangent=normalize(input.Tangent.xyz);
    float3 sampled=normalize(TreeNormal.Sample(TreeNormalSampler,input.Uv).xyz*2-1);
    // The captured shader's normal texture X axis is cross(T,N)*handedness,
    // and Y is T. Keep that ordering, rather than imposing a different TBN.
    float3 mapped=sampled.x*cross(tangent,normal)*input.Tangent.w+sampled.y*tangent+sampled.z*normal;
    float3 albedo=diffuse.rgb;float alpha=diffuse.a;float cutoff=GameAlphaCutoff;
#if !defined(SEASON_BASELINE)
    SeasonIntensity season;
    if(abs(GameAlphaCutoff-.49843138456344604)<.000001&&SeasonTreeAtPixel(input.Position,season))
    {
        float2 coverage=TreeCoverage.Sample(TreeDiffuseSampler,input.Uv);
        float leaf=coverage.x/max(coverage.x+coverage.y,1e-6);
        alpha=SeasonTreeCoverageAt(input.Uv,season.LeafRetention);cutoff=TreeCutoff;
        // Young-leaf artwork is local to the authenticated leaf mask. Branches
        // retain their source pigment; evergreen retention is independent.
        albedo=lerp(albedo,lerp(albedo,float3(.34,.52,.12),.22),season.NewLeaves*leaf);
        if((TreePolicy&4)==0&&(TreePolicy&3)==1)
        {
            float luminance=dot(albedo,float3(.2126,.7152,.0722));
            albedo=lerp(albedo,luminance*float3(.65,1.20,.38),season.Foliage*leaf*.65);
            albedo=lerp(albedo,luminance*float3(1.50,.78,.22),season.Autumn*leaf*.7);
        }
        float snow=season.SnowCover*SeasonSmooth(.25,.8,saturate(normal.y));
        albedo=lerp(albedo,float3(.83,.88,.92),snow);
    }
#endif
    clip(alpha-cutoff);
    float3 viewNormal=float3(dot(GameWorldToView[0].xyz,mapped),dot(GameWorldToView[1].xyz,mapped),dot(GameWorldToView[2].xyz,mapped));
    TreeOutput output;
    output.AlbedoOcclusion=float4(albedo,TreeOcclusion.Sample(TreeOcclusionSampler,input.Uv).x);
    output.NormalMaterial=float4(TreeEncodeNormal(viewNormal),0,float(uint(float(GameMaterialId))<<8)*(1.0/65535.0));
    output.ReflectanceRoughness=TreeReflectance.Sample(TreeReflectanceSampler,input.Uv)*GameSpecular*float4(1,1,1,.5);
    output.Reserved=0;
    output.SurfaceBasis=float4(TreeEncodeNormal(normal),TreeEncodeNormal(normalize(tangent-dot(tangent,mapped)*mapped)));
    return output;
}
#endif
