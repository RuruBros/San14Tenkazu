#include "SeasonGameParameters.hlsli"
#include "SeasonWorldPosition.hlsli"
#include "SeasonVegetation.hlsli"

// Placement consumes the admitted game's VS outputs. No guessed height-map
// scale, instance transform, UV origin, or editor-only material mapping.
Texture2D<float4> RiceControl : register(t3);
Texture2DArray<float4> RiceDetail : register(t7);
Texture2D<float4> RiceAuthenticatedControl : register(t11);
SamplerState RiceControlSampler : register(s3);
SamplerState RiceDetailSampler : register(s7);
// Read the same b0 as the admitted original terrain PS. Keeping its native
// constant-buffer layout also avoids a second resource indirection per root.
cbuffer RiceMaterialTable : register(b0) { uint4 RiceTerrain[1027]; };
struct RiceTerrainVertex
{
    float4 position : SV_Position;
    float4 terrainFlags : TEXCOORD0;
    float4 instanceScale : TEXCOORD1;
    float4 terrainLocal : TEXCOORD2;
    float4 world : TEXCOORD3;
    float3 tangent : TEXCOORD4;
    float3 normal : TEXCOORD5;
    float2 uv : TEXCOORD6;
};
struct RiceRoot { float4 root : POSITION; };
float RiceHash(float2 p)
{
    uint2 c=uint2(int2(p));uint v=c.x*1664525u+c.y*1013904223u;
    v^=v>>16;v*=2246822519u;v^=v>>13;return float(v&65535u)/65535;
}
uint RiceCrop(float2 uv,float3 world)
{
    float4 live=RiceControl.SampleLevel(RiceControlSampler,uv,0);
    float4 authenticated=RiceAuthenticatedControl.SampleLevel(RiceControlSampler,uv,0);
    if(any(live!=authenticated))return 0;
    float3 control=live.xyz;
    int first=int(control.x*255),second=int(control.y*255);
    if(first<0||first>=255)return 0;
    // Interior of one material, away from blended roads and banks.
    int material=first;
    if(second>=0&&second<255&&control.z>.9)material=second;
    else if(second!=first&&control.z>.1)return 0;
    int layer=asint(RiceTerrain[material+7].x);
    uint kind=SeasonPlantKind(layer);
    if(kind==0||(kind==2&&SeasonWeights.y<=0))return 0;
    float2 scale=float2(asfloat(RiceTerrain[material+517].x),asfloat(RiceTerrain[material+772].x));
    if(any(scale<=1)||any(!isfinite(scale)))return 0;
    // The original PS uses world X/Z divided by the material's U/V scales.
    // Authored crop-interior policy: retain vegetated plots, exclude pale banks.
    float3 color=RiceDetail.SampleLevel(RiceDetailSampler,float3(world.xz/scale,layer),0).rgb;
    return (kind==1?SeasonCropInterior(color):SeasonGreenInterior(color))>0?kind:0;
}
[maxvertexcount(256)]
void RiceRoots(triangle RiceTerrainVertex input[3],inout PointStream<RiceRoot> stream)
{
    if(SeasonWeights.y+SeasonWeights.z<=0||SeasonMatrices[8].x!=1)return;
    float2 a=input[0].world.xz,b=input[1].world.xz,c=input[2].world.xz;
    float2 ab=b-a,ac=c-a;float determinant=ab.x*ac.y-ab.y*ac.x;
    if(abs(determinant)<1)return;
    float2 low=min(a,min(b,c)),high=max(a,max(b,c));
    // Distant geometry with subpixel stalks is culled before grid traversal.
    float3 middle=(input[0].world.xyz+input[1].world.xyz+input[2].world.xyz)/3;
    float4 bottom=SeasonProjectWorld(middle),top=SeasonProjectWorld(middle+float3(0,140,0));
    if(bottom.w<=0||top.w<=0||length((top.xy/top.w-bottom.xy/bottom.w)/SeasonViewport.zw*.5)<.65)return;
    const float spacing=80;
    int2 lo=int2(ceil(low/spacing-.5)),hi=int2(floor(high/spacing-.5));
    // At most 16x16 candidates; the source is a triangle, so at most 136 roots.
    if(any(hi-lo>15))return;
    [loop] for(int z=lo.y;z<=hi.y;z++)[loop] for(int x=lo.x;x<=hi.x;x++)
    {
        float2 cell=float2(x,z),anchor=(cell+.5)*spacing;
        float2 ap=anchor-a;
        float v=(ap.x*ac.y-ap.y*ac.x)/determinant;
        float w=(ab.x*ap.y-ab.y*ap.x)/determinant;float u=1-v-w;
        // Half-open edge ownership prevents duplicate plants on shared edges.
        float3 bary=float3(u,v,w);
        if(any(bary<-.00001))continue;
        float2 edges[3]={c-b,a-c,b-a};
        bool inside=true;
        [unroll] for(uint k=0;k<3;k++)if(abs(bary[k])<=.00001)
        {
            float2 e=edges[k]*(determinant>0?1:-1);
            inside=inside&&(e.y>0||(e.y==0&&e.x<0));
        }
        if(!inside)continue;
        float3 world=input[0].world.xyz*u+input[1].world.xyz*v+input[2].world.xyz*w;
        float3 normal=normalize(input[0].normal*u+input[1].normal*v+input[2].normal*w);
        if(normal.y<.93)continue;
        float2 uv=input[0].uv*u+input[1].uv*v+input[2].uv*w;
        uint kind=RiceCrop(uv,world);if(kind==0)continue;
        // Summer grass is sparser than crop clumps, with bounded world cells.
        if(kind==2&&((x+z)&1)!=0)continue;
        float4 clip=SeasonProjectWorld(world+float3(0,70,0));
        if(clip.w<=0||any(abs(clip.xy)>clip.w*1.04)||clip.z<0||clip.z>clip.w)continue;
        RiceRoot root;root.root=float4(world,RiceHash(cell)+(kind==2?2:0));stream.Append(root);
    }
}
RiceRoot RiceVertex(float4 root : POSITION) { RiceRoot o;o.root=root;return o; }
struct RicePixel { float4 position : SV_Position;float3 color : COLOR;float2 canopy : TEXCOORD0; };
float3 RiceBend(float3 local,float3 root,float phase)
{
    float t=saturate(local.y/140);
    float wave=sin(SeasonSeconds*1.7+root.x*.003+root.z*.004+phase);
    local.xz+=float2(1,.42)*(wave*15+sin(SeasonSeconds*.73+root.z*.005)*7)*t*t;
    float summer=saturate(SeasonWeights.y/max(SeasonWeights.y+SeasonWeights.z,.0001));
    float2 summerBend=float2(1,.42)*(SeasonSummerWind(root.xz,SeasonSeconds)*24+sin(phase)*2)*t*t;
    // Keep the autumn motion while interpolating toward coherent summer gusts.
    local.xz+=summer*(summerBend-float2(1,.42)*(wave*15+sin(SeasonSeconds*.73+root.z*.005)*7)*t*t);
    return root+local;
}
void RiceTriangle(float3 a,float3 b,float3 c,float3 color,float3 root,float phase,inout TriangleStream<RicePixel> stream)
{
    RicePixel o;o.color=color;o.canopy=-1;
    o.position=SeasonProjectWorld(RiceBend(a,root,phase));stream.Append(o);
    o.position=SeasonProjectWorld(RiceBend(b,root,phase));stream.Append(o);
    o.position=SeasonProjectWorld(RiceBend(c,root,phase));stream.Append(o);stream.RestartStrip();
}
void RiceRibbon(float3 a,float3 b,float3 side,float3 color,float3 root,float phase,inout TriangleStream<RicePixel> stream)
{
    RiceTriangle(a-side,a+side,b-side,color,root,phase,stream);
    RiceTriangle(b-side,a+side,b+side,color,root,phase,stream);
}
// A cell represents a six-stalk clump. Two intersecting world-space planes
// retain its silhouette at ordinary map zoom. Alpha coverage is filtered in
// the PS instead of rasterizing individual subpixel stalk triangles.
void RiceCanopyVertex(float3 local,float2 uv,float3 root,float phase,float seed,inout TriangleStream<RicePixel> stream)
{
    RicePixel o;o.position=SeasonProjectWorld(RiceBend(local,root,phase));o.color=float3(-1,seed,seed>=2?1:0);o.canopy=uv;stream.Append(o);
}
void RiceCanopy(float3 side,float height,float3 root,float phase,float seed,inout TriangleStream<RicePixel> stream)
{
    float3 top=float3(0,height,0);
    RiceCanopyVertex(-side,float2(0,0),root,phase,seed,stream);
    RiceCanopyVertex(side,float2(1,0),root,phase,seed,stream);
    RiceCanopyVertex(top-side,float2(0,1),root,phase,seed,stream);stream.RestartStrip();
    RiceCanopyVertex(top-side,float2(0,1),root,phase,seed,stream);
    RiceCanopyVertex(side,float2(1,0),root,phase,seed,stream);
    RiceCanopyVertex(top+side,float2(1,1),root,phase,seed,stream);stream.RestartStrip();
}
[maxvertexcount(108)]
void RicePlant(point RiceRoot input[1],inout TriangleStream<RicePixel> stream)
{
    float3 root=input[0].root.xyz;float tag=input[0].root.w,seed=tag-(tag>=2?2:0),phase=seed*6.2831853;
    bool grass=tag>=2;float summer=saturate(SeasonWeights.y/max(SeasonWeights.y+SeasonWeights.z,.0001));
    float height=grass?80+30*seed:120+20*seed;float3 stalk=float3(0,height,0);
    float angle=phase*.17;float3 across=float3(cos(angle),0,sin(angle))*38;
    RiceCanopy(across,height,root,phase,tag,stream);
    RiceCanopy(float3(-across.z,0,across.x),height,root,phase,tag,stream);
    float4 baseClip=SeasonProjectWorld(root),topClip=SeasonProjectWorld(root+stalk);
    float projectedHeight=length((topClip.xy/topClip.w-baseClip.xy/baseClip.w)/SeasonViewport.zw*.5);
    // Detailed central stalks only contribute when their geometry resolves.
    if(projectedHeight<6)return;
    float3 green=lerp(float3(.18,.23,.04),float3(.13,.34,.055),summer),gold=float3(.52,.37,.09);
    RiceRibbon(0,stalk,float3(1.5,0,0),green,root,phase,stream);
    RiceRibbon(0,stalk,float3(0,0,1.5),green*.85,root,phase,stream);
    [unroll] for(uint leaf=0;leaf<3;leaf++)
    {
        float angle=phase+leaf*2.399963;float3 direction=float3(cos(angle),0,sin(angle));
        float3 base=float3(0,28+leaf*20,0),tip=base+direction*40+float3(0,30-leaf*5,0);
        float3 side=float3(-direction.z,0,direction.x)*4;
        RiceTriangle(base,base+direction*20+side+float3(0,25,0),tip,green,root,phase,stream);
        RiceTriangle(base,tip,base+direction*20-side+float3(0,25,0),green*.8,root,phase,stream);
    }
    if(grass||SeasonWeights.z<=0)return;
    float3 previous=stalk;
    [unroll] for(uint segment=1;segment<=3;segment++)
    {
        float t=float(segment)/3;float3 next=stalk+float3(t*35,-t*t*28,0);
        RiceRibbon(previous,next,float3(0,0,1.6),gold*.72,root,phase,stream);previous=next;
    }
    [unroll] for(uint grain=0;grain<4;grain++)
    {
        float t=(grain+.5)/4;float3 center=stalk+float3(t*35,-t*t*28,0);
        center.z+=(grain&1)?5:-5;float3 top=center+float3(0,5,0),bottom=center-float3(0,5,0);
        float3 left=center-float3(3,0,0),right=center+float3(3,0,0),front=center+float3(0,0,3);
        RiceTriangle(top,left,front,gold,root,phase,stream);RiceTriangle(top,front,right,gold*.84,root,phase,stream);
        RiceTriangle(bottom,front,left,gold*.72,root,phase,stream);RiceTriangle(bottom,right,front,gold*.9,root,phase,stream);
    }
}
float RiceCoverage(float distance,float width,float footprint)
{
    float aa=max(footprint*.5,.0005);
    return saturate((width+aa-distance)/(2*aa));
}
float4 RiceColor(RicePixel input) : SV_Target
{
    float weight=saturate(SeasonWeights.y+SeasonWeights.z);
    if(input.color.x>=0)return float4(input.color,weight);
    float2 uv=input.canopy;float u=uv.x*6,cell=floor(u),x=frac(u),y=uv.y;
    float seed=input.color.y-(input.color.z>0?2:0);
    float center=.5+sin(cell*2.3+seed*6.28)*.07*y*y;
    float tip=.84+.08*sin(cell*1.9+seed*4);
    float footprint=max(fwidth(u),.0005);
    float stem=RiceCoverage(abs(x-center),.07,footprint)*(1-smoothstep(tip-.02,tip+.02,y));
    // When several stalks share a pixel, use their actual periodic coverage.
    stem=lerp(stem,.14*(1-smoothstep(tip-.02,tip+.02,y)),saturate(footprint-1));
    float leaves=0;
    [unroll] for(uint leaf=0;leaf<3;leaf++)
    {
        float2 start=float2(center,.22+leaf*.2),end=start+float2((leaf&1)?.42:-.42,.18);
        float2 direction=end-start;float t=saturate(dot(float2(x,y)-start,direction)/dot(direction,direction));
        float distance=length(float2(x,y)-start-direction*t);
        leaves=max(leaves,RiceCoverage(distance,.10*sin(t*3.14159265),fwidth(distance)));
    }
    float2 ear=float2((x-center-.10)/.31,(y-tip)/.09);
    float radius=length(ear);float grains=RiceCoverage(radius,1,fwidth(radius));
    grains*=input.color.z>0?0:saturate(SeasonWeights.z/max(SeasonWeights.y+SeasonWeights.z,.0001));
    float coverage=1-(1-stem)*(1-leaves)*(1-grains);
    clip(coverage-.001);
    float summer=saturate(SeasonWeights.y/max(SeasonWeights.y+SeasonWeights.z,.0001));
    float3 green=lerp(float3(.18,.23,.04),float3(.13,.34,.055),summer),gold=float3(.52,.37,.09);
    return float4(lerp(green,gold,grains),weight*coverage);
}
