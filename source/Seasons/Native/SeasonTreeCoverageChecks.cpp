// Real authenticated BC7 atlas mip chains, owned WARP textures, and the actual
// new TreeColor/TreeDepth entry points. No game process/Hook access.
#define NOMINMAX
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <vector>
#include <array>
#include <iostream>
#include <stdexcept>
#include <cmath>
#include <algorithm>
using Microsoft::WRL::ComPtr;
static void require(bool ok,const char* why){if(!ok)throw std::runtime_error(why);}
static void hr(HRESULT r){require(SUCCEEDED(r),"Coverage GPU API failed");}
static std::vector<unsigned char> read(const std::filesystem::path& p){
    std::ifstream f(p,std::ios::binary|std::ios::ate);require(bool(f),"Coverage input missing");auto size=f.tellg();require(size>32&&size<4000000,"Coverage file size rejected");
    std::vector<unsigned char> b(static_cast<size_t>(size));f.seekg(0);f.read(reinterpret_cast<char*>(b.data()),size);require(bool(f),"Coverage file truncated");return b;
}
static bool hash(const void* bytes,UINT size,const char* expected){
    BCRYPT_ALG_HANDLE provider{};require(BCryptOpenAlgorithmProvider(&provider,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0,"Hash provider unavailable");
    unsigned char digest[32];auto result=BCryptHash(provider,nullptr,0,static_cast<PUCHAR>(const_cast<void*>(bytes)),size,digest,32);BCryptCloseAlgorithmProvider(provider,0);require(result>=0,"Source hash failed");
    const char* hex="0123456789abcdef";for(UINT i=0;i<32;i++)if(expected[i*2]!=hex[digest[i]>>4]||expected[i*2+1]!=hex[digest[i]&15])return false;return true;
}
static ComPtr<ID3DBlob> compileFile(const wchar_t* path,const char* entry,const D3D_SHADER_MACRO* defines=nullptr){
    ComPtr<ID3DBlob> code,error;auto result=D3DCompileFromFile(path,defines,D3D_COMPILE_STANDARD_FILE_INCLUDE,entry,"ps_5_0",D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&error);
    if(error)std::cerr<<static_cast<const char*>(error->GetBufferPointer());hr(result);return code;
}
static ComPtr<ID3DBlob> compile(const char* source,const char* profile){
    ComPtr<ID3DBlob> code,error;auto result=D3DCompile(source,strlen(source),nullptr,nullptr,nullptr,"main",profile,D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS,0,&code,&error);
    if(error)std::cerr<<static_cast<const char*>(error->GetBufferPointer());hr(result);return code;
}
#include "SeasonTreeCatalog.h"
int wmain(int argc,wchar_t** argv){try{
    require(argc==3,"Usage: SeasonTreeCoverageChecks.exe SeasonGameTree.hlsl mask-directory");
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    auto buffer=[&](UINT bytes,const void* data,UINT bind= D3D11_BIND_CONSTANT_BUFFER,UINT stride=0){
        D3D11_BUFFER_DESC desc{bytes,D3D11_USAGE_IMMUTABLE,bind,0,stride?UINT(D3D11_RESOURCE_MISC_BUFFER_STRUCTURED):0u,stride};D3D11_SUBRESOURCE_DATA initial{data,0,0};ComPtr<ID3D11Buffer> b;hr(device->CreateBuffer(&desc,&initial,&b));return b;
    };
    auto texture=[&](UINT width,UINT height,UINT mips,UINT layers,DXGI_FORMAT format,const D3D11_SUBRESOURCE_DATA* data){
        D3D11_TEXTURE2D_DESC d{width,height,mips,layers,format,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};ComPtr<ID3D11Texture2D> t;hr(device->CreateTexture2D(&d,data,&t));ComPtr<ID3D11ShaderResourceView> v;hr(device->CreateShaderResourceView(t.Get(),nullptr,&v));return v;
    };
    D3D_SHADER_MACRO depthDefine[]={{"SEASON_DEPTH","1"},{nullptr,nullptr}};auto colorCode=compileFile(argv[1],"TreeColor"),depthCode=compileFile(argv[1],"TreeDepth",depthDefine);
    ComPtr<ID3D11PixelShader> colorPs,depthPs;hr(device->CreatePixelShader(colorCode->GetBufferPointer(),colorCode->GetBufferSize(),nullptr,&colorPs));hr(device->CreatePixelShader(depthCode->GetBufferPointer(),depthCode->GetBufferSize(),nullptr,&depthPs));
    const char* colorVertex=R"(struct O{float4 p:SV_Position;float4 t:TEXCOORD0;float3 n:TEXCOORD1;float2 uv:TEXCOORD2;};O main(uint id:SV_VertexID){O o;float2 q=float2((id<<1)&2,id&2);o.p=float4(q*float2(2,-2)+float2(-1,1),.5,1);o.t=float4(1,0,0,1);o.n=float3(0,1,0);o.uv=q;return o;})";
    const char* depthVertex=R"(struct O{float4 p:SV_Position;float2 uv:TEXCOORD0;};O main(uint id:SV_VertexID){O o;float2 q=float2((id<<1)&2,id&2);o.p=float4(q*float2(2,-2)+float2(-1,1),.5,1);o.uv=q;return o;})";
    auto cv=compile(colorVertex,"vs_5_0"),dv=compile(depthVertex,"vs_5_0");ComPtr<ID3D11VertexShader> colorVs,depthVs;
    hr(device->CreateVertexShader(cv->GetBufferPointer(),cv->GetBufferSize(),nullptr,&colorVs));hr(device->CreateVertexShader(dv->GetBufferPointer(),dv->GetBufferSize(),nullptr,&depthVs));
    auto verifyCode=compile(R"(Texture2D<float4> S:register(t0);Texture2D<float2> M:register(t1);RWTexture2D<float2> O:register(u0);cbuffer C:register(b0){uint mip;uint3 pad;};[numthreads(8,8,1)]void main(uint3 p:SV_DispatchThreadID){uint w,h;O.GetDimensions(w,h);if(p.x>=w||p.y>=h)return;float2 mask=M.Load(int3(p.xy,mip));O[p.xy]=float2(S.Load(int3(p.xy,mip)).a,mask.x+mask.y);})","cs_5_0");
    ComPtr<ID3D11ComputeShader> verifyCs;hr(device->CreateComputeShader(verifyCode->GetBufferPointer(),verifyCode->GetBufferSize(),nullptr,&verifyCs));
    float matrix[36]{};for(UINT row=0;row<4;row++){matrix[row*4+row]=1;matrix[16+row*4+row]=1;}matrix[32]=1;
    auto matrices=buffer(sizeof(matrix),matrix,D3D11_BIND_SHADER_RESOURCE,16);ComPtr<ID3D11ShaderResourceView> matrixView;hr(device->CreateShaderResourceView(matrices.Get(),nullptr,&matrixView));
    float climate[2]={-5,20};D3D11_SUBRESOURCE_DATA climateData[12];for(auto& data:climateData)data={climate,8,0};auto climateView=texture(1,1,1,12,DXGI_FORMAT_R32G32_FLOAT,climateData);
    float normal[4]={.5f,.5f,1,1};D3D11_SUBRESOURCE_DATA normalData{normal,16,0};auto materialView=texture(1,1,1,1,DXGI_FORMAT_R32G32B32A32_FLOAT,&normalData);
    float colorConstants[20]{};for(UINT i=0;i<4;i++)colorConstants[i]=1;colorConstants[4]=colorConstants[9]=colorConstants[14]=1;colorConstants[16]=.49843138456344604f;
    float depthConstants[12]{};depthConstants[3]=colorConstants[16];depthConstants[4]=1;depthConstants[9]=1;
    auto colorCb=buffer(sizeof(colorConstants),colorConstants),depthCb=buffer(sizeof(depthConstants),depthConstants);
    D3D11_DEPTH_STENCIL_DESC ds{};ds.DepthEnable=TRUE;ds.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL;ds.DepthFunc=D3D11_COMPARISON_LESS;ComPtr<ID3D11DepthStencilState> depthState;hr(device->CreateDepthStencilState(&ds,&depthState));
    D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;ComPtr<ID3D11RasterizerState> raster;hr(device->CreateRasterizerState(&rd,&raster));
    UINT64 alphaPixels=0,depthPixels=0,retainedBranchPixels=0;UINT cases=0;float maxAlphaError=0;
    for(const auto& source:TreeAssets)for(UINT viewVariant:{0u,1u}){
        auto sourceFormat=static_cast<DXGI_FORMAT>(source.format+viewVariant);wchar_t name[16];swprintf_s(name,L"%08X",source.ktid);
        auto bytes=read(std::filesystem::path(argv[2])/(std::wstring(name)+L".mask"));UINT header[9];memcpy(header,bytes.data(),36);
        require(header[0]==0x534D4153&&header[1]==2&&header[2]==source.width&&header[3]==source.height&&header[4]==source.mips&&header[5]==source.policy&&header[6]==source.bytes&&header[7]==source.pixels*8&&header[8]==source.format&&bytes.size()==36+size_t(header[6])+header[7],"Mask descriptor rejected");
        require(hash(bytes.data()+36,source.bytes,source.hash),"Original GPU payload hash differs");
        std::vector<D3D11_SUBRESOURCE_DATA> originalData,maskData;size_t at=36,maskAt=36+source.bytes;
        for(UINT mip=0;mip<source.mips;mip++){
            UINT w=source.width>>mip,h=source.height>>mip,pitch=((w+3)/4)*(source.format==71?8u:16u);originalData.push_back({bytes.data()+at,pitch,0});maskData.push_back({bytes.data()+maskAt,w*8,0});at+=size_t(pitch)*((h+3)/4);
            for(UINT p=0;p<w*h;p++){float values[2];memcpy(values,bytes.data()+maskAt+p*8,8);require(std::isfinite(values[0])&&std::isfinite(values[1])&&values[0]>=0&&values[1]>=0&&values[0]+values[1]<=1.000001f,"Mask contribution invalid");}
            maskAt+=size_t(w)*h*8;
        }
        require(at==36+source.bytes&&maskAt==bytes.size(),"Mask chain length differs");
        auto original=texture(source.width,source.height,source.mips,1,sourceFormat,originalData.data()),mask=texture(source.width,source.height,source.mips,1,DXGI_FORMAT_R32G32_FLOAT,maskData.data());
        for(UINT mip=0;mip<source.mips;mip++){
            UINT w=source.width>>mip,h=source.height>>mip;D3D11_TEXTURE2D_DESC td{w,h,1,1,DXGI_FORMAT_R32G32_FLOAT,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_UNORDERED_ACCESS,0,0};ComPtr<ID3D11Texture2D> output,readback;
            hr(device->CreateTexture2D(&td,nullptr,&output));ComPtr<ID3D11UnorderedAccessView> uav;hr(device->CreateUnorderedAccessView(output.Get(),nullptr,&uav));td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&td,nullptr,&readback));
            UINT constants[4]={mip,0,0,0};auto cb=buffer(16,constants);auto* raw=cb.Get();auto* out=uav.Get();ID3D11ShaderResourceView* inputs[]={original.Get(),mask.Get()};
            context->ClearState();context->CSSetShader(verifyCs.Get(),nullptr,0);context->CSSetConstantBuffers(0,1,&raw);context->CSSetShaderResources(0,2,inputs);context->CSSetUnorderedAccessViews(0,1,&out,nullptr);context->Dispatch((w+7)/8,(h+7)/8,1);context->ClearState();context->CopyResource(readback.Get(),output.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(readback.Get(),0,D3D11_MAP_READ,0,&mapped));
            for(UINT y=0;y<h;y++)for(UINT x=0;x<w;x++){const auto* values=reinterpret_cast<const float*>(static_cast<const char*>(mapped.pData)+y*mapped.RowPitch)+x*2;float error=std::abs(values[0]-values[1]);maxAlphaError=std::max(maxAlphaError,error);require(std::isfinite(error)&&error<.000002f,"Coverage sum differs from actual BC7 GPU alpha");alphaPixels++;}context->Unmap(readback.Get(),0);
        }
        // Render the full actual atlas through both new PS entry points. Pin
        // each integer/half mip with the same sampler to exercise filtering.
        constexpr UINT w=128,h=64;D3D11_TEXTURE2D_DESC td{w,h,1,1,DXGI_FORMAT_D32_FLOAT,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_DEPTH_STENCIL,0,0};ComPtr<ID3D11Texture2D> depth,depthRead;ComPtr<ID3D11DepthStencilView> dsv;
        hr(device->CreateTexture2D(&td,nullptr,&depth));hr(device->CreateDepthStencilView(depth.Get(),nullptr,&dsv));td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&td,nullptr,&depthRead));
        std::array<ComPtr<ID3D11RenderTargetView>,5> targets;ID3D11RenderTargetView* rawTargets[5];td.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_RENDER_TARGET;td.CPUAccessFlags=0;
        for(UINT i=0;i<5;i++){ComPtr<ID3D11Texture2D> target;hr(device->CreateTexture2D(&td,nullptr,&target));hr(device->CreateRenderTargetView(target.Get(),nullptr,&targets[i]));rawTargets[i]=targets[i].Get();}
        std::vector<float> winterSilhouette;size_t winterVisible=0;
        for(UINT lodStep=0;lodStep<source.mips*2-1;lodStep++)for(float retention:{0.f,.5f,1.f}){
            D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MinLOD=sd.MaxLOD=lodStep*.5f;ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));
            struct Parameters{float weights[4],extent[4];UINT months[2];float fraction,seconds,viewport[4];} parameters{{0,retention,0,1-retention},{0,0,1,1},{0,1},0,0,{0,0,1.f/w,1.f/h}};
            struct Tree{UINT policy;float cutoff,depthRange[2];} tree{source.policy,colorConstants[16],{.15f,1/.7f}};
            auto params=buffer(sizeof(parameters),&parameters),treeCb=buffer(sizeof(tree),&tree);
            auto render=[&](bool color){
                context->ClearState();context->ClearDepthStencilView(dsv.Get(),D3D11_CLEAR_DEPTH,1,0);context->OMSetDepthStencilState(depthState.Get(),0);context->RSSetState(raster.Get());D3D11_VIEWPORT vp{0,0,float(w),float(h),.15f,.85f};context->RSSetViewports(1,&vp);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                context->VSSetShader(color?colorVs.Get():depthVs.Get(),nullptr,0);context->PSSetShader(color?colorPs.Get():depthPs.Get(),nullptr,0);context->OMSetRenderTargets(color?5:0,color?rawTargets:nullptr,dsv.Get());
                auto* game=(color?colorCb:depthCb).Get();context->PSSetConstantBuffers(0,1,&game);ID3D11Buffer* privateCb[]={params.Get(),treeCb.Get()};context->PSSetConstantBuffers(12,2,privateCb);
                ID3D11ShaderResourceView* base[]={original.Get(),materialView.Get(),materialView.Get(),materialView.Get()};context->PSSetShaderResources(0,4,base);ID3D11ShaderResourceView* season[]={climateView.Get(),matrixView.Get(),mask.Get()};context->PSSetShaderResources(100,3,season);
                ID3D11SamplerState* ss[]={sampler.Get(),sampler.Get(),sampler.Get(),sampler.Get()};context->PSSetSamplers(0,4,ss);context->PSSetSamplers(12,1,ss);context->Draw(3,0);context->ClearState();context->CopyResource(depthRead.Get(),depth.Get());
                std::vector<float> values(w*h);D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(depthRead.Get(),0,D3D11_MAP_READ,0,&mapped));for(UINT y=0;y<h;y++)memcpy(values.data()+y*w,static_cast<const char*>(mapped.pData)+y*mapped.RowPitch,w*4);context->Unmap(depthRead.Get(),0);return values;
            };
            auto a=render(true),b=render(false);require(a==b,"Seasonal Color and Depth silhouettes differ");
            size_t visible=std::count_if(a.begin(),a.end(),[](float value){return value<1;});
            if(retention==0){winterSilhouette=a;winterVisible=visible;}
            else if(source.policy==2)require(a==winterSilhouette,"Evergreen retention changed with the season");
            else if(retention==1&&lodStep==0)require(visible>winterVisible*2,"Deciduous leaves did not disappear in winter");
            if(source.policy==1&&retention==0&&lodStep==0){require(visible<a.size(),"Winter branch case fully opaque");if(source.ktid==0xA224A71D||source.ktid==0xD9268C48)require(visible>0,"Verified branches missing");retainedBranchPixels+=visible;}
            depthPixels+=a.size();cases++;
        }
    }
    std::cout<<"{\"sourceAssets\":26,\"viewFormats\":2,\"gpuAlphaPixels\":"<<alphaPixels<<",\"maxAlphaError\":"<<maxAlphaError<<",\"colorDepthCases\":"<<cases<<",\"depthPixelsCompared\":"<<depthPixels<<",\"retainedBranchPixels\":"<<retainedBranchPixels<<",\"evergreenRetentionVerified\":true,\"gameProcessAccess\":false,\"gameScreenVerified\":false}\n";return 0;
}catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}}
