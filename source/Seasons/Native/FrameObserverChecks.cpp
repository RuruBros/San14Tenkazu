// Real, owned WARP Draws and a hidden owned DXGI swapchain. Never opens SAN14.
#define SEASON_FRAME_FIXTURE
#include "FrameObserver.cpp"
#include <d3dcompiler.h>
#include <thread>
#include <memory>
#include <iostream>
#include <string>
#include <condition_variable>
#include <mutex>
namespace {
std::atomic<bool> held{false},insideHeld{false};
std::atomic<unsigned> abiMask{0},jobs{0},done{0};
std::atomic<bool> quit{false},badEntryError{false};
ComPtr<ID3D11Buffer> arguments;
void bump(void* d,unsigned bit){auto* p=static_cast<BYTE*>(d)+0x2ad0;uint32_t n{};std::memcpy(&n,p,4);++n;std::memcpy(p,&n,4);abiMask.fetch_or(1u<<bit);SetLastError(0x1234);}
void entryError(){if(GetLastError()!=0xabcdef)badEntryError=true;}
__declspec(noinline) uint8_t __fastcall mockPrepare(void*,uint32_t m){return m!=99;}
__declspec(noinline) void __fastcall mockIndexed(void* d,uint32_t m,uint32_t a,uint32_t b,uint32_t start,uint32_t count){
    entryError();if(a!=17||b!=19||start||count!=3){badEntryError=true;return;}if(m==99)return;
    read<ID3D11DeviceContext*>(d,0x10)->DrawIndexed(count,start,0);bump(d,0);
}
__declspec(noinline) void __fastcall mockLand(void* d,uint32_t m,uint32_t n,uint32_t unused){
    entryError();if(n!=2||unused!=23)badEntryError=true;if(m==99)return;
    insideHeld=true;while(held.load())Sleep(1);
    read<ID3D11DeviceContext*>(d,0x10)->DrawIndexedInstanced(3,n,0,0,0);bump(d,1);
}
__declspec(noinline) void __fastcall mockMesh(void* d,uint32_t m,uint32_t a,uint32_t b,uint32_t start,uint32_t count,uint32_t n){
    entryError();if(a!=17||b!=19||start||count!=3||n!=2)badEntryError=true;if(m==99)return;
    read<ID3D11DeviceContext*>(d,0x10)->DrawIndexedInstanced(count,n,start,0,0);bump(d,2);
}
__declspec(noinline) void __fastcall mockDraw(void* d,uint32_t m,uint32_t start,uint32_t count){
    entryError();if(start||count!=3)badEntryError=true;if(m==99)return;
    read<ID3D11DeviceContext*>(d,0x10)->Draw(count,start);bump(d,3);
}
__declspec(noinline) void __fastcall mockInstanced(void* d,uint32_t m,uint32_t count,uint32_t n,uint32_t start,uint32_t first){
    entryError();if(count!=3||n!=2||start||first)badEntryError=true;if(m==99)return;
    read<ID3D11DeviceContext*>(d,0x10)->DrawInstanced(count,n,start,first);bump(d,4);
}
__declspec(noinline) void __fastcall mockIndirect(void* d,uint32_t m,void* buffer,uint32_t offset){
    entryError();if(buffer!=arguments.Get()||offset)badEntryError=true;if(m==99)return;
    read<ID3D11DeviceContext*>(d,0x10)->DrawInstancedIndirect(static_cast<ID3D11Buffer*>(buffer),offset);bump(d,5);
}
__declspec(noinline) void __fastcall mockAuto(void* d,uint32_t m){
    entryError();if(m==99)return;read<ID3D11DeviceContext*>(d,0x10)->DrawAuto();bump(d,6);
}
__declspec(noinline) void __fastcall mockInline(void* d,uint32_t m,uint32_t count,const void* bytes,uint32_t stride){
    entryError();if(count!=3||bytes!=reinterpret_cast<void*>(0x9876)||stride!=24)badEntryError=true;if(m==99)return;
    read<ID3D11DeviceContext*>(d,0x10)->Draw(count,0);bump(d,7);
}
__declspec(noinline) void __fastcall mockBatch(void* d){
    entryError();if(mockPrepare(d,1)){read<ID3D11DeviceContext*>(d,0x10)->Draw(3,0);bump(d,8);}
    auto* p=static_cast<BYTE*>(d)+0x2ad0;uint32_t n{};std::memcpy(&n,p,4);--n;std::memcpy(p,&n,4);SetLastError(0x1234);
}
__declspec(noinline) uint64_t __fastcall mockPresent(void* d,void*){
    entryError();auto n=jobs.fetch_add(1)+1;while(done.load()<n)Sleep(1);
    auto* chain=read<IDXGISwapChain*>(d,0x3080);const auto hr=chain->Present(0,0);
    if(FAILED(hr))badEntryError=true;SetLastError(0x1234);return 0x1122334455667788ull;
}
void issue(void* d){
    SetLastError(0xabcdef);mockIndexed(d,0,17,19,0,3);
    SetLastError(0xabcdef);mockLand(d,0,2,23);
    SetLastError(0xabcdef);mockMesh(d,0,17,19,0,3,2);
    SetLastError(0xabcdef);mockDraw(d,0,0,3);
    SetLastError(0xabcdef);mockInstanced(d,0,3,2,0,0);
    SetLastError(0xabcdef);mockIndirect(d,0,arguments.Get(),0);
    SetLastError(0xabcdef);mockAuto(d,0);
    SetLastError(0xabcdef);mockInline(d,0,3,reinterpret_cast<void*>(0x9876),24);
    SetLastError(0xabcdef);mockBatch(d);
    if(GetLastError()!=0x1234)badEntryError=true;
}
void checked(HRESULT h,unsigned line,const char* expression){if(FAILED(h)){std::cerr<<"Owned WARP HRESULT 0x"<<std::hex<<static_cast<unsigned>(h)<<std::dec<<" at source line "<<line<<": "<<expression<<"\n";
    throw std::runtime_error("Owned WARP setup failed");}}
#define require(value) checked((value),__LINE__,#value)
}
int main(int argc,char** argv){try{
    const bool hold=argc>1&&std::string(argv[1])=="hold";
    const bool sparse=argc>1&&std::string(argv[1])=="sparse";
    const bool boundary=argc>1&&std::string(argv[1])=="boundary";
    WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"SeasonOwnedFrameFixture";
    if(!RegisterClassW(&wc))return 2;
    auto window=CreateWindowW(wc.lpszClassName,L"Owned frame check",WS_POPUP,0,0,16,16,nullptr,nullptr,wc.hInstance,nullptr);
    if(!window)return 2;
    DXGI_SWAP_CHAIN_DESC swap{};swap.BufferDesc.Width=16;swap.BufferDesc.Height=16;swap.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
    swap.SampleDesc.Count=1;swap.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;swap.BufferCount=1;swap.OutputWindow=window;swap.Windowed=TRUE;
    ComPtr<IDXGISwapChain> chain;ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL level{};require(D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&swap,&chain,&device,&level,&context));
    const char* shader="struct Input{float4 position:SV_POSITION;};struct Output{float4 position:SV_POSITION;float4 data:TEXCOORD0;};float4 vs(uint id:SV_VertexID):SV_Position{float2 p[3]={float2(-1,-1),float2(-1,3),float2(3,-1)};return float4(p[id],0,1);}float4 ps():SV_Target{return float4(.2,.4,.6,1);}[maxvertexcount(3)]void gs(triangle Input input[3],inout TriangleStream<Output> output){for(uint i=0;i<3;i++){Output v;v.position=input[i].position;v.data=input[i].position;output.Append(v);}}";
    ComPtr<ID3DBlob> v,p,g,error;require(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&v,&error));
    require(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&p,&error));
    ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;require(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs));
    require(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&ps));
    D3D11_SO_DECLARATION_ENTRY output{0,"TEXCOORD",0,0,4,0};UINT soStride=16;
    require(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"gs","gs_5_0",0,0,&g,&error));
    ComPtr<ID3D11GeometryShader> stream;require(device->CreateGeometryShaderWithStreamOutput(g->GetBufferPointer(),g->GetBufferSize(),&output,1,&soStride,1,D3D11_SO_NO_RASTERIZED_STREAM,nullptr,&stream));
    D3D11_BUFFER_DESC soDesc{48,D3D11_USAGE_DEFAULT,D3D11_BIND_STREAM_OUTPUT|D3D11_BIND_VERTEX_BUFFER,0,0,0};ComPtr<ID3D11Buffer> so;
    require(device->CreateBuffer(&soDesc,nullptr,&so));
    UINT indices[3]{0,1,2};D3D11_BUFFER_DESC ibDesc{12,D3D11_USAGE_IMMUTABLE,D3D11_BIND_INDEX_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA ibData{indices,0,0};
    ComPtr<ID3D11Buffer> ib;require(device->CreateBuffer(&ibDesc,&ibData,&ib));
    UINT args[4]{3,2,0,0};D3D11_BUFFER_DESC argsDesc{16,D3D11_USAGE_DEFAULT,0,0,D3D11_RESOURCE_MISC_DRAWINDIRECT_ARGS,0};D3D11_SUBRESOURCE_DATA argsData{args,0,0};
    require(device->CreateBuffer(&argsDesc,&argsData,&arguments));
    ComPtr<ID3D11Texture2D> texture;require(chain->GetBuffer(0,IID_PPV_ARGS(&texture)));ComPtr<ID3D11RenderTargetView> rt;
    require(device->CreateRenderTargetView(texture.Get(),nullptr,&rt));
    alignas(8) BYTE driver[0x3400]{};auto* raw=context.Get();std::memcpy(driver+0x10,&raw,8);auto* rawChain=chain.Get();std::memcpy(driver+0x3080,&rawChain,8);
    alignas(8) BYTE indexOwner[32]{};UINT three=3,one=1;std::memcpy(indexOwner+0x1c,&three,4);auto* io=indexOwner;std::memcpy(driver+0x90,&io,8);
    std::memcpy(driver+0x2a40,&one,4);std::memcpy(driver+0x2a38,&three,4);
    auto r=std::make_unique<Request>();r->magic=Magic;r->version=Version;r->size=sizeof(Request);r->durationMs=hold?50:250;
    r->context=reinterpret_cast<uint64_t>(raw);r->device=reinterpret_cast<uint64_t>(device.Get());r->sceneDriver=reinterpret_cast<uint64_t>(driver);r->swapchain=reinterpret_cast<uint64_t>(rawChain);
    r->sparseBindings=sparse||boundary;if(boundary){r->durationMs=10000;r->targetPresentReturns=3;}
    initialize();indexed=mockIndexed;land=mockLand;mesh=mockMesh;draw=mockDraw;instanced=mockInstanced;indirect=mockIndirect;
    automatic=mockAuto;inlineDraw=mockInline;batch=mockBatch;prepare=mockPrepare;present=mockPresent;
    held=hold;std::atomic<bool> setup{false};
    std::thread render([&]{r->renderThread=GetCurrentThreadId();context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->IASetIndexBuffer(ib.Get(),DXGI_FORMAT_R32_UINT,0);
        auto* soRaw=so.Get();UINT zero=0;context->SOSetTargets(1,&soRaw,&zero);context->GSSetShader(stream.Get(),nullptr,0);context->Draw(3,0);
        context->SOSetTargets(0,nullptr,nullptr);context->GSSetShader(nullptr,nullptr,0);context->IASetVertexBuffers(0,1,&soRaw,&soStride,&zero);
        auto* target=rt.Get();context->OMSetRenderTargets(1,&target,nullptr);D3D11_VIEWPORT viewport{0,0,16,16,0,1};context->RSSetViewports(1,&viewport);setup=true;
        unsigned n=0;while(!quit.load()){if(jobs.load()>n){issue(driver);done=++n;}else Sleep(1);}
    });
    while(!setup.load())Sleep(1);
    DWORD result{};std::thread observer([&]{result=run(r.get(),true);r->error=result;InterlockedExchange(reinterpret_cast<volatile LONG*>(&r->status),result?4:2);});
    auto status=[&]{return InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&r->status),0,0);};while(!status())Sleep(1);
    bool earlyRecovery=false;std::thread release;
    if(hold)release=std::thread([&]{while(!insideHeld.load()&&status()==1)Sleep(1);if(!insideHeld.load()){held=false;return;}Sleep(100);earlyRecovery=r->callbacksDrained||r->referencesReleased||status()==2;held=false;});
    unsigned frames=0;while(status()==1){SetLastError(0xabcdef);const auto value=mockPresent(driver,nullptr);
        if(value!=0x1122334455667788ull||GetLastError()!=0x1234)badEntryError=true;++frames;Sleep(1);}
    observer.join();if(release.joinable())release.join();
    // Execute after independent full code/protection restoration.
    SetLastError(0xabcdef);mockPresent(driver,nullptr);quit=true;render.join();
    bool valid=result==0&&status()==2&&!earlyRecovery&&!badEntryError&&r->callbacksDrained&&r->referencesReleased&&r->records>0&&abiMask==0x1ff;
    unsigned kinds[11]{};for(int i=0;i<r->records;i++){auto& row=r->rows[i];valid=valid&&row.complete==1&&row.context==r->context&&row.qpcEnd>=row.qpcBegin&&row.kind<11;
        if(row.kind<11)kinds[row.kind]++;if(row.kind<8)valid=valid&&row.counterAfter==row.counterBefore+1;
        if(row.kind==8)valid=valid&&row.counterAfter==row.counterBefore;}
    for(int i=0;i<r->records;i++){auto& row=r->rows[i];if(row.kind>=9)valid=valid&&row.presentEnabled&&row.presentFlags==0&&row.swapchain==r->swapchain;}
    for(const auto& receipt:r->patches)valid=valid&&receipt.codeRestored&&receipt.protectionRestored;
    valid=valid&&r->otherDrawThreads==0&&r->bindingSamples>0;
    for(int i=0;i<r->records;i++){const auto& row=r->rows[i];if(row.kind>=9)continue;
        if(row.bindingsCaptured)valid=valid&&row.bindingSampleSequence==row.sequence;
        else valid=valid&&(sparse||boundary)&&row.bindingSampleSequence<row.sequence&&r->rows[row.bindingSampleSequence].bindingsCaptured;}
    if(boundary)valid=valid&&r->presentReturns==3&&kinds[9]==3&&kinds[10]==3;
    D3D11_TEXTURE2D_DESC td{};texture->GetDesc(&td);td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;require(device->CreateTexture2D(&td,nullptr,&staging));context->CopyResource(staging.Get(),texture.Get());D3D11_MAPPED_SUBRESOURCE mapped{};require(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
    for(unsigned y=0;y<16;y++)for(unsigned x=0;x<16;x++){auto* pixel=static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch+x*4;valid=valid&&pixel[0]==51&&pixel[1]==102&&pixel[2]==153&&pixel[3]==255;}
    context->Unmap(staging.Get(),0);DestroyWindow(window);
    std::cout<<"{\"ownWarpProcess\":true,\"gameAccess\":false,\"blockedCallback\":"<<(hold?"true":"false")<<",\"earlyRecovery\":"<<(earlyRecovery?"true":"false")<<
        ",\"error\":"<<result<<",\"badEntryError\":"<<(badEntryError?"true":"false")<<",\"rowSize\":"<<sizeof(Row)<<",\"requestSize\":"<<sizeof(Request)<<
        ",\"records\":"<<r->records<<",\"frames\":"<<frames<<",\"sparseBindings\":"<<(r->sparseBindings?"true":"false")<<",\"bindingSamples\":"<<r->bindingSamples<<",\"presentReturns\":"<<r->presentReturns<<",\"targetPresentReturns\":"<<r->targetPresentReturns<<",\"kinds\":[";
    for(unsigned i=0;i<11;i++)std::cout<<(i?",":"")<<kinds[i];std::cout<<"],\"originalPixels\":256,\"passed\":"<<(valid?"true":"false")<<"}\n";
    return valid?0:7;
}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 9;}}
