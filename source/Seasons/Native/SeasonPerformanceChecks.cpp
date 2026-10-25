#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <vector>
#include <algorithm>
#include <iostream>
#include <fstream>
#include <cstring>
#include "SeasonPerformance.h"
using Microsoft::WRL::ComPtr;
void require(bool v,const char* m){if(!v)throw std::runtime_error(m);}
void hr(HRESULT h){if(FAILED(h))throw std::runtime_error("D3D fixture failure");}
int main(int argc,char** argv){try{
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    bool hardware=argc>1&&std::string(argv[1])=="hardware";
    hr(D3D11CreateDevice(nullptr,hardware?D3D_DRIVER_TYPE_HARDWARE:D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    const char code[]=R"(float4 vs(uint id:SV_VertexID):SV_Position{float2 p[3]={float2(-1,-1),float2(-1,3),float2(3,-1)};return float4(p[id],0,1);}float4 ps(float4 p:SV_Position):SV_Target{return float4(frac(p.x*.013),frac(p.y*.017),.6,1);})";
    ComPtr<ID3DBlob> v,p,e;hr(D3DCompile(code,sizeof(code),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&v,&e));hr(D3DCompile(code,sizeof(code),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&p,&e));
    ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;hr(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs));hr(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&ps));
    D3D11_TEXTURE2D_DESC d{512,512,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    ComPtr<ID3D11Texture2D> target,staging;hr(device->CreateTexture2D(&d,nullptr,&target));d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&d,nullptr,&staging));
    ComPtr<ID3D11RenderTargetView> view;hr(device->CreateRenderTargetView(target.Get(),nullptr,&view));auto* raw=view.Get();context->OMSetRenderTargets(1,&raw,nullptr);
    context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);D3D11_VIEWPORT vp{0,0,512,512,0,1};context->RSSetViewports(1,&vp);
    auto pixels=[&]{context->CopyResource(staging.Get(),target.Get());D3D11_MAPPED_SUBRESOURCE m{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&m));std::vector<BYTE> bytes(512*512*4);for(unsigned y=0;y<512;y++)std::memcpy(bytes.data()+y*2048,static_cast<BYTE*>(m.pData)+y*m.RowPitch,2048);context->Unmap(staging.Get(),0);return bytes;};
    context->Draw(3,0);auto expected=pixels();
    auto trace=std::make_unique<season_perf::Trace>();hr(trace->prepare(context.Get(),GetCurrentThreadId()));season_perf::local=trace.get();
    for(unsigned frame=0;frame<80;frame++){
        trace->presentCount=frame;trace->visit(context.Get(),GetCurrentThreadId());
        {season_perf::Cpu outer(season_perf::OriginalDraw);{season_perf::Cpu inner(season_perf::OriginalPrepare);context->Draw(3,0);}}
        trace->stamp(context.Get(),0);context->Draw(3,0);trace->stamp(context.Get(),1);trace->close(context.Get());
        // Fixture readback verifies output and supplies normal command completion; product never waits/flushes.
        require(pixels()==expected,"timing changed output pixels");trace->poll(context.Get());
    }
    trace->stop(3);trace->visit(context.Get(),GetCurrentThreadId());season_perf::local=nullptr;
    unsigned complete=0;for(unsigned i=0;i<trace->sampleCount;i++)if(trace->results[i].status==2){++complete;require(trace->results[i].frequency>0&&trace->results[i].count==2&&trace->results[i].ticks[1]>=trace->results[i].ticks[0],"invalid GPU timestamp pair");}
    require(complete>0,"no valid GPU timestamp samples");require(trace->frameCount==80&&trace->sampleCount==10,"bounded one-in-eight sampling differs");
    require(trace->frames[0].cpu[season_perf::OriginalDraw].inclusive>=trace->frames[0].cpu[season_perf::OriginalDraw].exclusive,"nested CPU time invalid");
    ComPtr<ID3D11PixelShader> current;context->PSGetShader(&current,nullptr,nullptr);require(current.Get()==ps.Get(),"diagnostic changed shader binding");
    auto invalid=std::make_unique<season_perf::Trace>(false);invalid->deadline=0;require(!invalid->active()&&invalid->reason==4,"preparation deadline not enforced");
    auto capacity=std::make_unique<season_perf::Trace>(false);hr(capacity->prepare(context.Get(),GetCurrentThreadId()));capacity->frameCount=season_perf::Trace::Frames;capacity->boundary(context.Get(),GetCurrentThreadId());require(capacity->reason==2,"capacity not enforced");
    auto wrong=std::make_unique<season_perf::Trace>();hr(wrong->prepare(context.Get(),GetCurrentThreadId()));wrong->boundary(context.Get(),GetCurrentThreadId()+1);require(wrong->reason==6,"thread owner not enforced");
    if(argc>2){std::ofstream file(argv[2]);file<<trace->json();}
    std::cout<<"{\"passed\":true,\"hardware\":"<<(hardware?"true":"false")<<",\"frames\":80,\"validGpuSamples\":"<<complete<<",\"pixelsEqual\":true,\"gameAccess\":false}\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
