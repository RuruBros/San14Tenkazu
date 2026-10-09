// The connected color stage preserves coverage. Leaf removal, regional snow,
// Depth/shadow classification and weather remain separate required stages.
Texture2D<float4> Original : register(t0);
Texture2D<float4> Green : register(t1);
Texture2D<float4> Autumn : register(t2);
RWTexture2D<float4> Result : register(u0);
cbuffer Parameters : register(b0) { uint Mip; uint EncodeSrgb; uint Baseline; uint Evergreen; float4 Weights; }
float3 Encode(float3 x) {
    return lerp(12.92*x,1.055*pow(max(x,0),1.0/2.4)-0.055,step(0.0031308,x));
}
[numthreads(8,8,1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint w,h; Result.GetDimensions(w,h); if(id.x>=w || id.y>=h) return;
    float4 source=Original.Load(int3(id.xy,Mip));
    // Authenticated, UV-aligned original material variants. No whole-frame tint
    // or amplified RGB factors. Evergreen needles keep their green material.
    // Original alpha is deliberately retained: this stage claims no leaf loss.
    float autumnWeight=Evergreen?0:(Weights.z+Weights.w);
    float3 rgb=Baseline?source.rgb:lerp(Green.Load(int3(id.xy,Mip)).rgb,Autumn.Load(int3(id.xy,Mip)).rgb,autumnWeight);
    if(EncodeSrgb) rgb=Encode(rgb);
    Result[id.xy]=float4(rgb,source.a);
}
