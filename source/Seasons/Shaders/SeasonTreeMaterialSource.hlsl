Texture2D<float4> Original : register(t0);
RWTexture2D<float4> Result : register(u0);
cbuffer Parameters : register(b0) { uint Mip; uint EncodeSrgb; float Spring; uint Padding; }
float3 Encode(float3 x) {
    return lerp(12.92*x,1.055*pow(max(x,0),1.0/2.4)-0.055,step(0.0031308,x));
}
[numthreads(8,8,1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint w,h; Result.GetDimensions(w,h); if(id.x>=w || id.y>=h) return;
    float4 source=Original.Load(int3(id.xy,Mip));
    float3 rgb=saturate(source.rgb*lerp(float3(1,1,1),float3(0.92,1.30,0.84),Spring));
    if(EncodeSrgb) rgb=Encode(rgb);
    Result[id.xy]=float4(rgb,source.a);
}
