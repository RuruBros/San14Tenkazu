#define NOMINMAX
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <vector>
#include <array>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <cmath>
#include <algorithm>
using Microsoft::WRL::ComPtr;
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static void hr(HRESULT result){require(SUCCEEDED(result),"Tree parity GPU API failed");}
static std::vector<char> read(const wchar_t* path){std::ifstream f(std::filesystem::path(path),std::ios::binary|std::ios::ate);require(bool(f),"Parity input missing");auto length=f.tellg();require(length>0&&length<65536,"Parity input length rejected");std::vector<char> data(static_cast<size_t>(length));f.seekg(0);f.read(data.data(),length);require(bool(f),"Parity input truncated");return data;}
static ComPtr<ID3DBlob> compile(const wchar_t* path,const char* entry,const char* profile,const D3D_SHADER_MACRO* defines=nullptr){
    ComPtr<ID3DBlob> code,errors;auto result=D3DCompileFromFile(path,defines,D3D_COMPILE_STANDARD_FILE_INCLUDE,entry,profile,D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
    if(errors)std::cerr<<static_cast<const char*>(errors->GetBufferPointer());hr(result);return code;
}
int wmain(int argc,wchar_t** argv){try{
    require(argc==4,"Usage: SeasonTreeGameChecks.exe SeasonGameTree.hlsl candidate-color.dxbc actual-PS-CB0.bin");
    auto candidate=read(argv[2]),constants=read(argv[3]);require(constants.size()==80,"Expected actual color CB0 size 80");
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    D3D_SHADER_MACRO defines[]={{"SEASON_BASELINE","1"},{nullptr,nullptr}};auto code=compile(argv[1],"TreeColor","ps_5_0",defines);
    ComPtr<ID3D11PixelShader> original,replacement;hr(device->CreatePixelShader(candidate.data(),candidate.size(),nullptr,&original));hr(device->CreatePixelShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&replacement));
    const char* vertex=R"(
        cbuffer V:register(b1){float4 TestTangent;float4 TestNormal;};
        struct O{float4 p:SV_Position;float4 t:TEXCOORD0;float3 n:TEXCOORD1;float2 uv:TEXCOORD2;};
        O main(uint id:SV_VertexID){O o;float2 v=float2((id<<1)&2,id&2);o.p=float4(v*float2(2,-2)+float2(-1,1),.5,1);o.t=TestTangent;o.n=TestNormal.xyz;o.uv=v;return o;})";
    ComPtr<ID3DBlob> vsCode;hr(D3DCompile(vertex,strlen(vertex),nullptr,nullptr,nullptr,"main","vs_5_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&vsCode,nullptr));
    ComPtr<ID3D11VertexShader> vs;hr(device->CreateVertexShader(vsCode->GetBufferPointer(),vsCode->GetBufferSize(),nullptr,&vs));
    constexpr UINT width=16,height=8;
    std::array<ComPtr<ID3D11Texture2D>,5> targets;std::array<ComPtr<ID3D11RenderTargetView>,5> rtvs;
    D3D11_TEXTURE2D_DESC td{width,height,1,1,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    for(size_t i=0;i<5;i++){hr(device->CreateTexture2D(&td,nullptr,&targets[i]));hr(device->CreateRenderTargetView(targets[i].Get(),nullptr,&rtvs[i]));}
    td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;ComPtr<ID3D11Texture2D> staging;hr(device->CreateTexture2D(&td,nullptr,&staging));
    std::array<ComPtr<ID3D11ShaderResourceView>,4> views;
    for(UINT kind=0;kind<4;kind++){
        std::vector<float> pixels(width*height*4);for(UINT y=0;y<height;y++)for(UINT x=0;x<width;x++){
            const size_t p=(y*width+x)*4;pixels[p]=.1f+.7f*float(x)/width;pixels[p+1]=.2f+.5f*float(y)/height;pixels[p+2]=.8f;
            const float alphas[]={0,.49f,.5f,1};pixels[p+3]=alphas[x%4];
        }
        td={width,height,1,1,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};D3D11_SUBRESOURCE_DATA data{pixels.data(),width*16,0};ComPtr<ID3D11Texture2D> texture;
        hr(device->CreateTexture2D(&td,&data,&texture));hr(device->CreateShaderResourceView(texture.Get(),nullptr,&views[kind]));
    }
    D3D11_BUFFER_DESC bd{80,D3D11_USAGE_IMMUTABLE,D3D11_BIND_CONSTANT_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA data{constants.data(),0,0};ComPtr<ID3D11Buffer> cb;hr(device->CreateBuffer(&bd,&data,&cb));
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_POINT;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MaxLOD=D3D11_FLOAT32_MAX;ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));
    D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;ComPtr<ID3D11RasterizerState> raster;hr(device->CreateRasterizerState(&rd,&raster));
    D3D11_VIEWPORT vp{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&vp);context->RSSetState(raster.Get());context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vs.Get(),nullptr,0);
    auto* rawCb=cb.Get();context->PSSetConstantBuffers(0,1,&rawCb);ID3D11ShaderResourceView* rawViews[4];ID3D11SamplerState* rawSamplers[4];ID3D11RenderTargetView* rawTargets[5];
    for(UINT i=0;i<4;i++){rawViews[i]=views[i].Get();rawSamplers[i]=sampler.Get();}for(UINT i=0;i<5;i++)rawTargets[i]=rtvs[i].Get();
    context->PSSetShaderResources(0,4,rawViews);context->PSSetSamplers(0,4,rawSamplers);
    auto render=[&](ID3D11PixelShader* shader){
        const float clear[]={-.25f,-.25f,-.25f,-.25f};for(auto& r:rtvs)context->ClearRenderTargetView(r.Get(),clear);context->OMSetRenderTargets(5,rawTargets,nullptr);context->PSSetShader(shader,nullptr,0);context->Draw(3,0);context->OMSetRenderTargets(0,nullptr,nullptr);
        std::vector<float> pixels(5*width*height*4);for(size_t i=0;i<5;i++){
            context->CopyResource(staging.Get(),targets[i].Get());D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
            for(UINT y=0;y<height;y++)memcpy(pixels.data()+(i*height+y)*width*4,static_cast<char*>(mapped.pData)+y*mapped.RowPitch,width*16);context->Unmap(staging.Get(),0);
        }return pixels;
    };
    const std::array<std::array<float,8>,6> bases={{{1,0,0,1,0,1,0,0},{1,0,0,-1,0,1,0,0},{0,1,0,1,0,0,1,0},{0,1,0,-1,0,0,-1,0},{.3f,.9f,.1f,1,-.9f,.3f,.2f,0},{.3f,.9f,.1f,-1,.9f,-.3f,-.2f,0}}};
    double maxError=0;size_t samples=0,discarded=0;
    for(const auto& basis:bases){
        bd.ByteWidth=32;data.pSysMem=basis.data();ComPtr<ID3D11Buffer> vb;hr(device->CreateBuffer(&bd,&data,&vb));auto* raw=vb.Get();context->VSSetConstantBuffers(1,1,&raw);
        auto a=render(original.Get()),b=render(replacement.Get());for(size_t i=0;i<a.size();i++){
            double error=std::abs(double(a[i])-b[i]);maxError=std::max(maxError,error);require(std::isfinite(error)&&error<.00002,"Replacement MRT differs from archive candidate");samples++;discarded+=a[i]==-.25f;
        }
    }
    require(discarded>0&&discarded<samples,"Parity did not exercise alpha clipping");
    std::cout<<"{\"basisCases\":"<<bases.size()<<",\"floatComparisons\":"<<samples<<",\"discardedChannels\":"<<discarded<<",\"maxMrtError\":"<<maxError<<",\"archiveCandidateParity\":true,\"actualPixelBytecodeIdentity\":false,\"gameScreenVerified\":false}\n";return 0;
}catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}}
