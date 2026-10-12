#include "SeasonGameParameters.hlsli"
#include "SeasonWorldPosition.hlsli"
#include "SeasonWeather.hlsli"
cbuffer GameWeather:register(b13){float2 WeatherDepthRange;uint WeatherDepthDirection;uint WeatherFlags;};
Texture2D<float> WeatherDepth:register(t1);
Texture2D<uint> RegionalWeather:register(t106);
StructuredBuffer<float4> WeatherVolume:register(t107);
struct RegionMark{float2 xz;uint mask;uint padding;uint dates[6];};
StructuredBuffer<RegionMark> RegionMarks:register(t108);
RWStructuredBuffer<float4> WeatherBoundsOutput:register(u0);
uint WeatherHash(uint v){v^=v>>16;v*=2246822519u;v^=v>>13;v*=3266489917u;return v^(v>>16);}
// The same recorded 468-unit hex transform as GameTerrain and the 46 climate anchors.
uint WeatherAtWorld(float2 xz)
{
    float2 hex=(xz-float2(37126,12930))/468;
    int column=(int)round((hex.x-1)/1.5);float best=1e20;int2 chosen=int2(-1,-1);
    [unroll] for(int dc=-1;dc<=1;dc++){
        int c=column+dc;float offset=(c&1)?0.5:1;int r=(int)round(hex.y/1.7320508075688772-offset);
        float2 center=float2(1.5*c+1,1.7320508075688772*(r+offset));float d=dot(center-hex,center-hex);
        if(c>=0&&c<220&&r>=0&&r<220&&d<best){best=d;chosen=int2(c,r);}
    }
    if(chosen.x<0||best>1.01)return 0;return RegionalWeather.Load(int3(chosen,0));
}
[numthreads(1,1,1)]
void WeatherBounds(uint3 id:SV_DispatchThreadID)
{
    float2 low=float2(1e20,1e20),high=-low;float height=-1e20;uint count=0;
    [loop] for(uint y=0;y<8;y++)[loop] for(uint x=0;x<8;x++){
        float2 pixel=SeasonViewport.xy+(float2(x,y)+0.5)/8/SeasonViewport.zw;
        float depth=WeatherDepth.Load(int3(int2(pixel),0));float3 world;
        if(!isfinite(depth)||depth<=WeatherDepthRange.x||depth>=WeatherDepthRange.x+1/WeatherDepthRange.y)continue;
        if(!SeasonWorldFromPixel(float4(pixel,depth,1),SeasonViewport,WeatherDepthRange,world)||!all(isfinite(world)))continue;
        low=min(low,world.xz);high=max(high,world.xz);height=max(height,world.y);count++;
    }
    float2 pad=(high-low)/7;
    WeatherBoundsOutput[0]=count?float4(low-pad,high+pad):0;
    WeatherBoundsOutput[1]=float4(count,height,0,0);
}
struct SnowOutput{float4 position:SV_Position;noperspective float2 flake:TEXCOORD0;nointerpolation float intensity:TEXCOORD1;nointerpolation float depth:TEXCOORD2;nointerpolation uint kind:TEXCOORD3;};
SnowOutput WeatherSnowVertex(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
{
    const float2 corners[6]={float2(-1,-1),float2(-1,1),float2(1,-1),float2(1,-1),float2(-1,1),float2(1,1)};
    float4 bounds=WeatherVolume[0];float2 step=max((bounds.zw-bounds.xy)/126,20);
    int2 cell=int2(floor(bounds.xy/step))+int2(instance%128,instance/128);
    uint seed=WeatherHash(asuint(cell.x)^WeatherHash(asuint(cell.y)));float2 jitter=float2(seed&65535,seed>>16)/65535.0;
    float2 ground=(cell+jitter)*step;uint weather=WeatherAtWorld(ground);
    if(!(WeatherFlags&64))weather&=~3u;if(!(WeatherFlags&128))weather&=~12u;
    uint kind=(weather&3)?1:2;
    float intensity=kind==1?(weather&3):((weather>>2)&3);
    float seconds=SeasonSeconds*(kind==1?0.85:0.13);
    float3 world=float3(ground.x,WeatherVolume[1].y+200+frac(jitter.y-seconds)*7000,ground.y);
    world.x+=sin(SeasonSeconds*.6+jitter.x*6.283)*(kind==1?60:180);
    float4 clipPosition=SeasonProjectWorld(world);float2 corner=corners[vertex];
    SnowOutput o;o.position=clipPosition;o.flake=corner;o.kind=kind;o.intensity=intensity;o.depth=clipPosition.z/clipPosition.w;
    if(kind==1){
        float4 end=SeasonProjectWorld(world+float3(70,250,0));
        float2 direction=(end.xy/end.w-clipPosition.xy/clipPosition.w)/SeasonViewport.zw;
        direction=dot(direction,direction)>1e-6?normalize(direction):float2(0,1);
        float2 offset=corner.x*float2(-direction.y,direction.x)*0.65+corner.y*direction*(7+3*jitter.x);
        o.position.xy+=offset*2*SeasonViewport.zw*clipPosition.w;
    }else o.position.xy+=corner*(1.3+jitter.x*1.8)*2*SeasonViewport.zw*clipPosition.w;
    if(WeatherVolume[1].x==0||intensity==0||(intensity==1&&(seed&1))||!all(isfinite(clipPosition))||clipPosition.w<=1e-6||o.depth<0||o.depth>1){o.position=float4(2,2,2,1);o.intensity=0;}
    return o;
}
float4 WeatherSnow(SnowOutput input):SV_Target
{
    if(input.intensity==0)discard;
    float sceneDepth=WeatherDepth.Load(int3(int2(input.position.xy),0));
    float particleDepth=input.depth/WeatherDepthRange.y+WeatherDepthRange.x;
    if(!SeasonSnowVisible(sceneDepth,particleDepth,WeatherDepthDirection))discard;
    float alpha=input.kind==1?saturate(1-abs(input.flake.x))*saturate(1-abs(input.flake.y))*(input.intensity==2?0.7:0.45):SeasonSnowAlpha(input.flake,1,.9);
    float3 tint=input.kind==1?float3(.68,.8,.92):float3(.95,.97,1);return float4(tint*alpha,alpha);
}

float3 RegionTint(uint kind){return kind==1?float3(.36,.69,.91):kind==2?float3(.87,.95,1):kind==3?float3(.25,.59,.96):kind==4?float3(.97,.53,.25):kind==6?float3(.44,.82,.94):kind==7?float3(.95,.36,.31):float3(.88,.73,.27);}
struct AreaOutput{float4 position:SV_Position;};
AreaOutput WeatherAreaVertex(uint vertex:SV_VertexID){AreaOutput o;float2 uv=float2((vertex<<1)&2,vertex&2);o.position=float4(uv*float2(2,-2)+float2(-1,1),0,1);return o;}
float4 WeatherArea(AreaOutput input):SV_Target
{
    float depth=WeatherDepth.Load(int3(int2(input.position.xy),0));float3 world;
    if(depth<=WeatherDepthRange.x||depth>=WeatherDepthRange.x+1/WeatherDepthRange.y||!SeasonWorldFromPixel(float4(input.position.xy,depth,1),SeasonViewport,WeatherDepthRange,world))discard;
    uint code=WeatherAtWorld(world.xz),kind=(code&16)?3:(code&32)?4:(code&64)?5:(code&128)?6:(code&256)?7:(code&3)?1:(code&12)?2:0;
    if(kind==0)discard;float alpha=.14;return float4(RegionTint(kind)*alpha,alpha);
}
struct MarkOutput{float4 position:SV_Position;noperspective float2 local:TEXCOORD0;nointerpolation uint kind:TEXCOORD1;nointerpolation uint date:TEXCOORD2;};
MarkOutput WeatherMarkVertex(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
{
    const float2 corners[6]={float2(0,0),float2(0,1),float2(1,0),float2(1,0),float2(0,1),float2(1,1)};
    uint city=instance/7,kind=instance%7+1;RegionMark mark=RegionMarks[city];float y=0;
    float4 clipPosition=SeasonProjectWorld(float3(mark.xz.x,y,mark.xz.y));
    [unroll]for(uint n=0;n<3;n++){
        float2 pixel=SeasonViewport.xy+(clipPosition.xy/clipPosition.w*float2(.5,-.5)+.5)/SeasonViewport.zw;
        float3 surface;
        if(all(isfinite(pixel))&&all(pixel>=SeasonViewport.xy)&&all(pixel<SeasonViewport.xy+1/SeasonViewport.zw)){
            float depth=WeatherDepth.Load(int3(int2(pixel),0));
            if(depth>WeatherDepthRange.x&&depth<WeatherDepthRange.x+1/WeatherDepthRange.y&&SeasonWorldFromPixel(float4(pixel,depth,1),SeasonViewport,WeatherDepthRange,surface))y=surface.y;
        }
        clipPosition=SeasonProjectWorld(float3(mark.xz.x,y,mark.xz.y));
    }
    MarkOutput o;o.local=corners[vertex]*float2(32,40);o.kind=kind;o.date=kind<3?mark.dates[5]:mark.dates[kind-3];
    float2 offset=float2(18+(kind<3?0:kind-2)*36,-20)+o.local;
    o.position=clipPosition;o.position.xy+=offset*2*SeasonViewport.zw*float2(1,-1)*clipPosition.w;
    if(!(mark.mask&(1u<<(kind-1)))||clipPosition.w<=1e-6||!all(isfinite(clipPosition)))o.position=float4(2,2,2,1);
    return o;
}
float Segment(float2 p,float2 a,float2 b){float2 d=b-a;return length(p-a-d*saturate(dot(p-a,d)/dot(d,d)));}
float Digit(float2 p,uint digit){
    const uint masks[10]={63,6,91,79,102,109,125,7,127,111};uint mask=masks[min(digit,9)];float distance=100;
    if(mask&1)distance=min(distance,Segment(p,float2(1,0),float2(4,0)));
    if(mask&2)distance=min(distance,Segment(p,float2(4,0),float2(4,3)));
    if(mask&4)distance=min(distance,Segment(p,float2(4,3),float2(4,6)));
    if(mask&8)distance=min(distance,Segment(p,float2(1,6),float2(4,6)));
    if(mask&16)distance=min(distance,Segment(p,float2(1,3),float2(1,6)));
    if(mask&32)distance=min(distance,Segment(p,float2(1,0),float2(1,3)));
    if(mask&64)distance=min(distance,Segment(p,float2(1,3),float2(4,3)));
    return saturate(1-distance);
}
float4 WeatherMark(MarkOutput input):SV_Target
{
    float2 p=input.local-float2(16,13);float d=100;
    if(input.kind==1){d=min(abs(length(p-float2(-4,-2))-4),abs(length(p-float2(2,-3))-5));for(int x=-5;x<=5;x+=5)d=min(d,Segment(p,float2(x,3),float2(x-2,7)));}
    else if(input.kind==2){[unroll]for(int n=0;n<3;n++){float angle=n*1.04719755;float2 dir=float2(cos(angle),sin(angle));d=min(d,Segment(p,-dir*8,dir*8));}}
    else if(input.kind==3){d=abs(p.y-sin(p.x*.55)*1.3);d=min(d,abs(p.y-5-sin(p.x*.55)*1.3));d=min(d,abs(p.y+5-sin(p.x*.55)*1.3));if(abs(p.x)>9)d=100;}
    else if(input.kind==4){d=abs(length(float2(p.x,p.y*.8))-6);if(p.y<0)d=min(d,Segment(p,float2(-5,0),float2(0,-9)));}
    else if(input.kind==6){[unroll]for(int n=0;n<3;n++){float angle=n*1.04719755;float2 dir=float2(cos(angle),sin(angle));d=min(d,Segment(p,-dir*6,dir*6));}d=min(d,Segment(p,float2(-8,8),float2(8,8)));}
    else if(input.kind==7){d=abs(length(p-float2(0,5))-3);d=min(d,Segment(p,float2(0,-8),float2(0,5)));d=min(d,Segment(p,float2(5,-5),float2(8,-5)));d=min(d,Segment(p,float2(5,-1),float2(8,-1)));}
    else{d=abs(length(p)-4);[unroll]for(int n=0;n<8;n++){float a=n*.78539816;float2 dir=float2(cos(a),sin(a));d=min(d,Segment(p,dir*6,dir*9));}}
    float symbol=saturate(1.8-d);float label=0;float2 text=input.local-float2(1,29);uint month=(input.date>>16)&255,day=input.date>>24;
    if(input.kind<3){label=saturate(1-abs(text.y-3))*step(5,text.x)*step(text.x,24);}
    else{label=max(Digit(text,month/10),Digit(text-float2(6,0),month%10));label=max(label,saturate(1-Segment(text,float2(13,6),float2(16,0))));label=max(label,Digit(text-float2(18,0),day/10));label=max(label,Digit(text-float2(24,0),day%10));}
    float border=step(min(min(input.local.x,32-input.local.x),min(input.local.y,40-input.local.y)),1.2);
    float3 color=lerp(float3(.035,.035,.04),RegionTint(input.kind),max(border,symbol));color=lerp(color,float3(.95,.95,.95),label);
    float alpha=.9;return float4(color*alpha,alpha);
}
