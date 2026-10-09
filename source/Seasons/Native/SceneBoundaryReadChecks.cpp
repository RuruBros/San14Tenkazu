// Actual WARP backbuffer copies around a real Draw, including an active Draw
// held beyond the hard deadline. Never opens the game.
#define SEASON_FRAME_SCENE_READ
#define SEASON_FRAME_FIXTURE
#include "SceneBoundaryRead.h"
#include "FrameObserver.cpp"
#include <d3dcompiler.h>
#include <thread>
#include <iostream>
#include <memory>
#include <string>
namespace {
ComPtr<ID3D11PixelShader> sceneShader,uiShader;
std::atomic<bool> held{false},entered{false};
void owner(void* d,uint64_t value){std::memcpy(static_cast<BYTE*>(d)+0xb8,&value,8);}
void bump(void* d){auto n=read<uint32_t>(d,0x2ad0)+1;std::memcpy(static_cast<BYTE*>(d)+0x2ad0,&n,4);SetLastError(0x1234);}
__declspec(noinline) uint8_t __fastcall mockPrepare(void* d,uint32_t){
    owner(d,0x222);read<ID3D11DeviceContext*>(d,0x10)->PSSetShader(uiShader.Get(),nullptr,0);return 1;
}
__declspec(noinline) void __fastcall mockScene(void* d,uint32_t,uint32_t start,uint32_t count){
    owner(d,0x111);auto* ctx=read<ID3D11DeviceContext*>(d,0x10);ctx->PSSetShader(sceneShader.Get(),nullptr,0);
    ctx->Draw(count,start);bump(d);
}
__declspec(noinline) void __fastcall mockUi(void* d,uint32_t mode,uint32_t,uint32_t,uint32_t start,uint32_t count){
    if(mockPrepare(d,mode)){entered=true;while(held.load())Sleep(1);
        read<ID3D11DeviceContext*>(d,0x10)->DrawIndexed(count,start,0);bump(d);}
}
void checked(HRESULT h){if(FAILED(h))throw std::runtime_error("Owned WARP API failed");}
}
int main(int argc,char** argv){try{
    const std::string mode=argc>1?argv[1]:"basic";
    WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"SeasonBoundaryOwned";
    if(!RegisterClassW(&wc))return 2;
    auto window=CreateWindowW(wc.lpszClassName,L"Owned scene read",WS_POPUP,0,0,16,16,nullptr,nullptr,wc.hInstance,nullptr);
    if(!window)return 2;
    DXGI_SWAP_CHAIN_DESC swap{};swap.BufferDesc.Width=16;swap.BufferDesc.Height=16;swap.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
    swap.SampleDesc.Count=1;swap.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;swap.BufferCount=1;swap.OutputWindow=window;swap.Windowed=TRUE;
    ComPtr<IDXGISwapChain> chain;ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL level{};
    checked(D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&swap,&chain,&device,&level,&context));
    const char* text="float4 vs(uint id:SV_VertexID):SV_Position{float2 p[3]={float2(-1,-1),float2(-1,3),float2(3,-1)};return float4(p[id%3],0,1);}float4 scene():SV_Target{return float4(.2,.4,.6,1);}float4 ui(float4 p:SV_Position):SV_Target{if(p.x>=8)discard;return float4(.8,.1,.3,1);}";
    ComPtr<ID3DBlob> v,p,u,error;
    checked(D3DCompile(text,strlen(text),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&v,&error));
    checked(D3DCompile(text,strlen(text),nullptr,nullptr,nullptr,"scene","ps_5_0",0,0,&p,&error));
    checked(D3DCompile(text,strlen(text),nullptr,nullptr,nullptr,"ui","ps_5_0",0,0,&u,&error));
    ComPtr<ID3D11VertexShader> vs;checked(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs));
    checked(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&sceneShader));
    checked(device->CreatePixelShader(u->GetBufferPointer(),u->GetBufferSize(),nullptr,&uiShader));
    ComPtr<ID3D11Texture2D> backbuffer;checked(chain->GetBuffer(0,IID_PPV_ARGS(&backbuffer)));
    ComPtr<ID3D11RenderTargetView> rt;checked(device->CreateRenderTargetView(backbuffer.Get(),nullptr,&rt));
    auto* rawRt=rt.Get();context->OMSetRenderTargets(1,&rawRt,nullptr);context->VSSetShader(vs.Get(),nullptr,0);
    context->PSSetShader(sceneShader.Get(),nullptr,0);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    UINT index[6]{0,1,2,0,1,2};D3D11_BUFFER_DESC desc{sizeof(index),D3D11_USAGE_IMMUTABLE,D3D11_BIND_INDEX_BUFFER,0,0,0};
    D3D11_SUBRESOURCE_DATA initial{index,0,0};ComPtr<ID3D11Buffer> ib;checked(device->CreateBuffer(&desc,&initial,&ib));
    context->IASetIndexBuffer(ib.Get(),DXGI_FORMAT_R32_UINT,0);D3D11_VIEWPORT vp{0,0,16,16,0,1};context->RSSetViewports(1,&vp);
    auto r=std::make_unique<season::boundary::Request>();std::vector<BYTE> output(3*16*16*4);
    BYTE driver[0x3100]{};auto* rawCtx=context.Get();std::memcpy(driver+0x10,&rawCtx,8);uint64_t vsOwner=0x333;std::memcpy(driver+0xa8,&vsOwner,8);
    auto& f=r->frame;f.magic=season::frame::Magic;f.version=2;f.size=sizeof(f);f.durationMs=mode=="hold"?50:500;
    f.context=reinterpret_cast<uint64_t>(context.Get());f.device=reinterpret_cast<uint64_t>(device.Get());f.sceneDriver=reinterpret_cast<uint64_t>(driver);
    f.swapchain=reinterpret_cast<uint64_t>(chain.Get());f.renderThread=GetCurrentThreadId();f.sparseBindings=1;
    auto& b=r->data;b.magic=season::boundary::Magic;b.version=1;b.size=sizeof(b);b.driver=f.sceneDriver;
    b.scenePsOwner=0x111;b.sceneVsOwner=0x333;b.scenePs=reinterpret_cast<uint64_t>(sceneShader.Get());b.sceneVs=reinterpret_cast<uint64_t>(vs.Get());
    b.target=reinterpret_cast<uint64_t>(rt.Get());b.uiPsOwner=0x222;b.uiVsOwner=0x333;b.uiPs=reinterpret_cast<uint64_t>(uiShader.Get());b.uiVs=b.sceneVs;
    b.inputLayout=0;b.output=reinterpret_cast<uint64_t>(output.data());b.capacity=mode=="limit"?1024:static_cast<uint32_t>(output.size());
    boundary=&b;initialize();draw=mockScene;indexed=mockUi;prepare=mockPrepare;
    const bool hold=mode=="hold";held=hold;DWORD result{};bool early=false;
    std::thread worker([&]{result=run(&f,true);f.error=result;InterlockedExchange(reinterpret_cast<volatile LONG*>(&f.status),result?4:2);});
    while(!InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&f.status),0,0))Sleep(1);
    std::thread release;if(hold)release=std::thread([&]{while(!entered.load())Sleep(1);Sleep(100);early=f.callbacksDrained||f.referencesReleased||f.status==2;held=false;});
    mockScene(driver,0,0,3);mockUi(driver,0,0,0,0,6);
    worker.join();if(release.joinable())release.join();
    bool valid=result==0&&f.status==2&&f.callbacksDrained&&f.referencesReleased&&!early;
    for(auto& patch:f.patches)valid=valid&&patch.codeRestored&&patch.protectionRestored;
    if(mode=="limit")valid=valid&&b.state==4&&b.hresult==static_cast<uint32_t>(E_INVALIDARG)&&b.images==1&&b.bytes==1024;
    else{
        valid=valid&&b.state==2&&!b.hresult&&b.images==3&&b.bytes==output.size()&&b.counterAfter==b.counterBefore+1;
        for(UINT i=0;i<256;i++){
            auto* scene=output.data()+i*4;auto* before=output.data()+1024+i*4;auto* after=output.data()+2048+i*4;
            valid=valid&&scene[0]==51&&scene[1]==102&&scene[2]==153&&scene[3]==255&&std::memcmp(scene,before,4)==0;
            valid=valid&&(i%16<8?(after[0]==204&&after[3]==255):std::memcmp(scene,after,4)==0);
        }
    }
    std::cout<<"{\"ownWarpProcess\":true,\"gameAccess\":false,\"mode\":\""<<mode<<"\",\"gpuImages\":"<<b.images<<
        ",\"gpuBytes\":"<<b.bytes<<",\"boundaryState\":"<<b.state<<",\"hresult\":"<<b.hresult<<",\"error\":"<<result<<
        ",\"earlyRecovery\":"<<(early?"true":"false")<<",\"passed\":"<<(valid?"true":"false")<<"}\n";
    DestroyWindow(window);return valid?0:7;
}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 9;}}
