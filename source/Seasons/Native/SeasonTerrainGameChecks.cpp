#define NOMINMAX
#include "SeasonShaderTranslation.h"
#include <d3d11.h>
#include <array>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <cmath>
#include <algorithm>
using Microsoft::WRL::ComPtr;
static void require(bool v,const char* why){if(!v)throw std::runtime_error(why);}
static void hr(HRESULT v){require(SUCCEEDED(v),"Terrain parity GPU API failed");}
static std::vector<char> read(const wchar_t* path){std::ifstream f(std::filesystem::path(path),std::ios::binary|std::ios::ate);require(bool(f),"Original terrain shader missing");auto n=f.tellg();require(n>0&&n<1024*1024,"Terrain shader size rejected");std::vector<char> v(static_cast<size_t>(n));f.seekg(0);f.read(v.data(),n);require(bool(f),"Shader truncated");return v;}
static ComPtr<ID3DBlob> compile(const std::string& source,const char* entry,const char* profile){
    ComPtr<ID3DBlob> code,errors;auto result=D3DCompile(source.data(),source.size(),nullptr,nullptr,nullptr,entry,profile,
        D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
    if(errors)std::cerr<<static_cast<const char*>(errors->GetBufferPointer());hr(result);return code;
}
int wmain(int argc,wchar_t** argv){try{
    require(argc==3||argc==4,"Usage: SeasonTerrainGameChecks.exe original-terrain.dxbc generated.hlsl [--distant]");
    bool distant=argc==4&&std::wstring(argv[3])==L"--distant";require(argc==3||distant,"Unknown terrain fixture mode");
    auto original=read(argv[1]);auto source=seasons::shader::translate(original.data(),original.size(),{},distant);
    std::ofstream out(std::filesystem::path(argv[2]),std::ios::binary);out.write(source.data(),source.size());out.close();
    auto translated=compile(source,"TerrainColor","ps_5_0");
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    ComPtr<ID3D11PixelShader> a,b;hr(device->CreatePixelShader(original.data(),original.size(),nullptr,&a));
    hr(device->CreatePixelShader(translated->GetBufferPointer(),translated->GetBufferSize(),nullptr,&b));
    auto vertex=compile(distant?R"(
cbuffer Fixture:register(b1){float4 N;float4 T;float4 Offset;};
struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float3 t:TEXCOORD1;float3 n:TEXCOORD2;float2 uv:TEXCOORD3;};
O V(uint id:SV_VertexID){float2 v=float2((id<<1)&2,id&2);O o;o.p=float4(v*float2(2,-2)+float2(-1,1),.5,1);
o.a=float4(1000,0,0,1);o.t=T.xyz;o.n=N.xyz;o.uv=v;return o;})":R"(
cbuffer Fixture:register(b1){float4 N;float4 T;float4 Offset;};
struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float4 b:TEXCOORD1;float4 c:TEXCOORD2;float4 w:TEXCOORD3;float3 t:TEXCOORD4;float3 n:TEXCOORD5;float2 uv:TEXCOORD6;};
O V(uint id:SV_VertexID){float2 v=float2((id<<1)&2,id&2);O o;o.p=float4(v*float2(2,-2)+float2(-1,1),.5,1);
o.a=float4(0,0,4,16);o.b=float4(1000,0,0,1);o.c=float4(v.x,0,v.y,1);o.w=float4(v.x*512+Offset.x,0,v.y*512+Offset.z,1);o.t=T.xyz;o.n=N.xyz;o.uv=v;return o;})","V","vs_5_0");
    ComPtr<ID3D11VertexShader> vs;hr(device->CreateVertexShader(vertex->GetBufferPointer(),vertex->GetBufferSize(),nullptr,&vs));
    constexpr UINT width=32,height=16;std::array<ComPtr<ID3D11RenderTargetView>,5> targets;std::array<ComPtr<ID3D11Texture2D>,5> textures;
    D3D11_TEXTURE2D_DESC td{width,height,1,1,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    for(UINT i=0;i<5;i++){hr(device->CreateTexture2D(&td,nullptr,&textures[i]));hr(device->CreateRenderTargetView(textures[i].Get(),nullptr,&targets[i]));}
    td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;ComPtr<ID3D11Texture2D> staging;hr(device->CreateTexture2D(&td,nullptr,&staging));
    std::array<ComPtr<ID3D11ShaderResourceView>,8> views;
    for(UINT slot=3;slot<=10;slot++){
        bool array=slot>=7&&slot<=9;UINT layers=array?19:1;std::vector<float> pixels(16*16*layers*4);std::vector<D3D11_SUBRESOURCE_DATA> data;
        for(UINT layer=0;layer<layers;layer++){
            for(UINT y=0;y<16;y++)for(UINT x=0;x<16;x++){
                auto at=((layer*16+y)*16+x)*4;
                pixels[at]=.12f+float((x+layer)%16)*.04f;pixels[at+1]=.18f+float((y+layer)%16)*.03f;pixels[at+2]=.8f;pixels[at+3]=.3f+float(layer%5)*.1f;
                if(slot==3){pixels[at]=float((x/4+y/4)%4)/255;pixels[at+1]=x<4?1.f:float((y/3+1)%4)/255;pixels[at+2]=x<8?.995f:float(y)/16;}
                if(slot==10)pixels[at]=pixels[at+1]=pixels[at+2]=1;
            }data.push_back({pixels.data()+layer*16*16*4,16*16,0});
        }
        td={16,16,1,layers,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};ComPtr<ID3D11Texture2D> texture;
        hr(device->CreateTexture2D(&td,data.data(),&texture));hr(device->CreateShaderResourceView(texture.Get(),nullptr,&views[slot-3]));
    }
    std::array<float,1027*4> cb{};cb[4]=cb[5]=cb[6]=1;cb[7]=2;cb[8]=cb[13]=cb[18]=1;cb[21]=4096;cb[22]=50;cb[24]=.6f;cb[26]=1000;cb[27]=4000;
    for(UINT i=0;i<255;i++){uint32_t layer=i%19;std::memcpy(&cb[(7+i)*4],&layer,4);cb[(262+i)*4]=.5f;cb[(517+i)*4]=100;cb[(772+i)*4]=120;}
    if(distant){cb.fill(0);cb[0]=cb[1]=cb[2]=1;cb[3]=2;cb[4]=cb[9]=cb[14]=1;cb[17]=4096;cb[18]=50;cb[20]=.6f;
        for(UINT i=0;i<255;i++)cb[(6+i)*4]=100;}
    D3D11_BUFFER_DESC bd{sizeof(cb),D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA data{cb.data(),0,0};ComPtr<ID3D11Buffer> constants;hr(device->CreateBuffer(&bd,&data,&constants));
    bd.ByteWidth=48;ComPtr<ID3D11Buffer> fixture;hr(device->CreateBuffer(&bd,nullptr,&fixture));auto* raw=fixture.Get();context->VSSetConstantBuffers(1,1,&raw);raw=constants.Get();context->PSSetConstantBuffers(0,1,&raw);
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_POINT;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MaxLOD=D3D11_FLOAT32_MAX;
    ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));ID3D11SamplerState* samplers[8];ID3D11ShaderResourceView* srvs[8];ID3D11RenderTargetView* rtvs[5];
    for(UINT i=0;i<8;i++){samplers[i]=sampler.Get();srvs[i]=views[i].Get();}for(UINT i=0;i<5;i++)rtvs[i]=targets[i].Get();context->PSSetShaderResources(3,8,srvs);context->PSSetSamplers(3,8,samplers);
    D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;ComPtr<ID3D11RasterizerState> raster;hr(device->CreateRasterizerState(&rd,&raster));context->RSSetState(raster.Get());
    D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vs.Get(),nullptr,0);
    auto render=[&](ID3D11PixelShader* shader){
        float clear[]={-.25f,-.25f,-.25f,-.25f};for(auto& target:targets)context->ClearRenderTargetView(target.Get(),clear);
        context->OMSetRenderTargets(5,rtvs,nullptr);context->PSSetShader(shader,nullptr,0);context->Draw(3,0);context->OMSetRenderTargets(0,nullptr,nullptr);
        std::vector<float> result(5*width*height*4);for(UINT i=0;i<5;i++){context->CopyResource(staging.Get(),textures[i].Get());D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
            for(UINT y=0;y<height;y++)std::memcpy(result.data()+(i*height+y)*width*4,static_cast<char*>(mapped.pData)+y*mapped.RowPitch,width*16);context->Unmap(staging.Get(),0);}return result;
    };
    std::array<std::array<float,12>,5> cases={{{0,1,0,0,1,0,0,0,0,0,0,0},{.8f,.6f,0,0,0,0,1,0,0,0,0,0},{0,.6f,.8f,0,1,0,0,0,0,0,0,0},{.57735f,.57735f,.57735f,0,.7071f,-.7071f,0,0,0,0,0,0},{0,1,0,0,1,0,0,0,100000,0,100000,0}}};
    size_t comparisons=0;double maximum=0;
    for(const auto& c:cases){context->UpdateSubresource(fixture.Get(),0,nullptr,c.data(),0,0);auto expected=render(a.Get()),actual=render(b.Get());
        for(size_t i=0;i<actual.size();i++){double e=std::abs(double(actual[i])-expected[i]);maximum=std::max(maximum,e);
            if(!std::isfinite(e)||e>0.0002){std::cerr<<"Mismatch at "<<i<<": "<<expected[i]<<" / "<<actual[i]<<"\n";throw std::runtime_error("Terrain translation changed original MRT");}++comparisons;}}
    std::cout<<"{\"cases\":"<<cases.size()<<",\"floatComparisons\":"<<comparisons<<",\"maxMrtError\":"<<maximum<<",\"archiveCandidateParity\":true,\"gameScreenVerified\":false}\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
