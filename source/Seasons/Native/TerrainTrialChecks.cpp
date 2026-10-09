#define SEASON_OBSERVER_FIXTURE
#define SEASON_OBSERVER_INSTANCED
#define SEASON_OBSERVER_COLOR_ONLY
#define SEASON_OBSERVER_TERRAIN_TRIAL
#include "DrawObserver.cpp"
#include <d3dcompiler.h>
#include <thread>
#include <memory>
#include <iostream>

ID3D11ShaderResourceView* fixtureView;
std::atomic<bool> hold{false},inside{false};
__declspec(noinline) uint8_t __fastcall mockPrepare(void* driver,uint32_t mode){
    if(mode==99)return 0;
    auto* context=read<ID3D11DeviceContext*>(driver,0x10);context->PSSetShaderResources(4,1,&fixtureView);return 1;
}
__declspec(noinline) void __fastcall mockDraw(void* driver,uint32_t mode,uint32_t instances,uint32_t unused){
    (void)unused;if(!mockPrepare(driver,mode))return;
    inside=true;while(hold.load())Sleep(1);
    auto* context=read<ID3D11DeviceContext*>(driver,0x10);context->DrawIndexedInstanced(3,instances,0,0,0);
    ++*reinterpret_cast<uint32_t*>(static_cast<BYTE*>(driver)+0x2ad0);
}
int main(int argc,char** argv){
    const bool blocked=argc>1&&std::string(argv[1])=="hold";
    struct Storage {Request base;TerrainTrial parameters;};
    static_assert(offsetof(Storage,parameters)==sizeof(Request));
    auto storage=std::make_unique<Storage>();auto& request=storage->base;trial=&storage->parameters;
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)))return 1;
    const char shader[]=R"(Texture2D<float4> Land:register(t4);
    float4 vs(uint id:SV_VertexID):SV_Position{float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),0,1);}
    float4 ps():SV_Target{return Land.Load(int3(0,0,0));})";
    ComPtr<ID3DBlob> vsBlob,psBlob,errors;
    if(FAILED(D3DCompile(shader,sizeof(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&vsBlob,&errors))||
       FAILED(D3DCompile(shader,sizeof(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&psBlob,&errors)))return 2;
    ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;
    if(FAILED(device->CreateVertexShader(vsBlob->GetBufferPointer(),vsBlob->GetBufferSize(),nullptr,&vs))||
       FAILED(device->CreatePixelShader(psBlob->GetBufferPointer(),psBlob->GetBufferSize(),nullptr,&ps)))return 3;
    std::vector<uint32_t> input(22369616/4);
    for(size_t i=0;i<input.size();i+=4){input[i]=0x0000ffff;input[i+1]=0;input[i+2]=0x7b0a7b0a;input[i+3]=0;}
    D3D11_SUBRESOURCE_DATA mips[11]{};uint32_t at=0;
    for(UINT mip=0;mip<11;mip++){UINT pitch=std::max(1u,((4096u>>mip)+3)/4)*16,rows=std::max(1u,((4096u>>mip)+3)/4);
        mips[mip]={reinterpret_cast<BYTE*>(input.data())+at,pitch,pitch*rows};at+=pitch*rows;}
    D3D11_TEXTURE2D_DESC td{};td.Width=td.Height=4096;td.MipLevels=11;td.ArraySize=td.SampleDesc.Count=1;
    td.Format=DXGI_FORMAT_BC3_TYPELESS;td.Usage=D3D11_USAGE_IMMUTABLE;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> land;ComPtr<ID3D11ShaderResourceView> landView;
    D3D11_SHADER_RESOURCE_VIEW_DESC vd{};vd.Format=DXGI_FORMAT_BC3_UNORM;vd.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;vd.Texture2D.MipLevels=11;
    if(FAILED(device->CreateTexture2D(&td,mips,&land))||FAILED(device->CreateShaderResourceView(land.Get(),&vd,&landView)))return 4;
    fixtureView=landView.Get();
    td={};td.Width=td.Height=16;td.MipLevels=td.ArraySize=td.SampleDesc.Count=1;td.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
    td.BindFlags=D3D11_BIND_RENDER_TARGET;ComPtr<ID3D11Texture2D> color;ComPtr<ID3D11RenderTargetView> rt;
    if(FAILED(device->CreateTexture2D(&td,nullptr,&color))||FAILED(device->CreateRenderTargetView(color.Get(),nullptr,&rt)))return 5;
    const uint32_t indices[]={0,1,2};D3D11_BUFFER_DESC bd{};bd.ByteWidth=12;bd.BindFlags=D3D11_BIND_INDEX_BUFFER;
    bd.Usage=D3D11_USAGE_IMMUTABLE;D3D11_SUBRESOURCE_DATA ids{indices,0,0};ComPtr<ID3D11Buffer> ib;
    if(FAILED(device->CreateBuffer(&bd,&ids,&ib)))return 6;
    context->IASetIndexBuffer(ib.Get(),DXGI_FORMAT_R32_UINT,0);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);
    auto* rawRt=rt.Get();context->OMSetRenderTargets(1,&rawRt,nullptr);D3D11_VIEWPORT viewport{0,0,16,16,0,1};context->RSSetViewports(1,&viewport);
    td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;if(FAILED(device->CreateTexture2D(&td,nullptr,&staging)))return 7;
    auto pixels=[&]{std::vector<BYTE> bytes(16*16*4);context->CopyResource(staging.Get(),color.Get());D3D11_MAPPED_SUBRESOURCE m{};
        if(FAILED(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&m)))return std::vector<BYTE>();
        for(UINT y=0;y<16;y++)std::memcpy(bytes.data()+y*64,static_cast<BYTE*>(m.pData)+y*m.RowPitch,64);
        context->Unmap(staging.Get(),0);return bytes;};
    alignas(16) BYTE driver[0x3100]{};auto* raw=context.Get();std::memcpy(driver+0x10,&raw,8);
    alignas(8) BYTE owner[64]{};auto* ownerPtr=owner;std::memcpy(driver+0xb8,&ownerPtr,8);
    alignas(8) BYTE indexOwner[32]{};uint32_t count=3;std::memcpy(indexOwner+0x1c,&count,4);auto* indexPtr=indexOwner;std::memcpy(driver+0x90,&indexPtr,8);
    mockDraw(driver,0,1,0);auto baseline=pixels();if(baseline.size()!=1024)return 8;
    request.magic=Magic;request.version=Version;request.size=sizeof(Request);request.durationMs=blocked?50:500;
    request.context=reinterpret_cast<uint64_t>(raw);request.device=reinterpret_cast<uint64_t>(device.Get());
    trial->magic=TrialMagic;trial->version=TrialVersion;trial->size=sizeof(TerrainTrial);trial->year=195;trial->month=4;trial->day=21;
    trial->renderThread=GetCurrentThreadId();trial->psOwner=reinterpret_cast<uint64_t>(owner);trial->ps=reinterpret_cast<uint64_t>(ps.Get());
    trial->view=reinterpret_cast<uint64_t>(fixtureView);trial->resource=reinterpret_cast<uint64_t>(land.Get());
    trial->input=reinterpret_cast<uint64_t>(input.data());trial->bytes=22369616;originalPrepare=mockPrepare;
    inside=false;hold=blocked;DWORD result=0;bool earlyRecovery=false;uint32_t changedPixels=0;
    std::thread worker([&]{result=run(&request,mockDraw,true);request.error=result;
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&request.status),result?4:2);});
    auto status=[&]{return InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&request.status),0,0);};
    while(!status())Sleep(1);std::thread release;
    if(blocked)release=std::thread([&]{while(!inside.load())Sleep(1);Sleep(100);
        earlyRecovery=request.callbacksDrained||trial->gpuResourcesReleased||status()==2;hold=false;});
    while(status()==1){mockDraw(driver,99,1,0);mockDraw(driver,0,1,0);auto changed=pixels();
        for(size_t i=0;i<changed.size()/4;i++)changedPixels+=std::memcmp(changed.data()+i*4,baseline.data()+i*4,3)!=0;
        ComPtr<ID3D11ShaderResourceView> restored;context->PSGetShaderResources(4,1,&restored);
        if(restored.Get()!=fixtureView)return 9;Sleep(1);}
    worker.join();if(release.joinable())release.join();mockDraw(driver,0,1,0);
    const bool valid=result==0&&request.status==2&&changedPixels>=256&&pixels()==baseline&&!earlyRecovery&&request.records==1&&
        request.codeRestored&&request.protectionRestored&&request.callbacksDrained&&request.referencesReleased&&
        trial->state==2&&trial->swaps==trial->restores&&trial->gpuResourcesReleased&&trial->prepareCodeRestored&&trial->prepareProtectionRestored&&
        request.rows[0].views[4].object==trial->newView&&request.rows[0].views[4].resource==trial->newResource;
    std::cout<<"{\"ownWarpProcess\":true,\"gameAccess\":false,\"pairedHooks\":true,\"actualOwnComputeShader\":true,\"swaps\":"<<trial->swaps<<
        ",\"restores\":"<<trial->restores<<",\"changedPixels\":"<<changedPixels<<",\"originalPixelsRestored\":256,\"error\":"<<result<<
        ",\"hresult\":"<<trial->hresult<<",\"blockedCallback\":"<<(blocked?"true":"false")<<",\"earlyRecovery\":"<<(earlyRecovery?"true":"false")<<
        ",\"passed\":"<<(valid?"true":"false")<<"}\n";return valid?0:10;
}
