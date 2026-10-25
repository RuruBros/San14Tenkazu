#define SEASON_BRIDGE_FIXTURE
#include "SeasonBridge.cpp"
#include <d3dcompiler.h>
#include <thread>
#include <iostream>
#include <string>
#include <fstream>
namespace {
ComPtr<ID3D11DeviceContext> fixtureContext;
ID3D11ShaderResourceView* fixtureSource=nullptr;
std::atomic<bool> hold{false},inside{false};
std::atomic<bool> prepareTracked{false};
void bump(void* d){auto* p=static_cast<BYTE*>(d)+0x2ad0;auto n=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0)+1;std::memcpy(p,&n,4);}
__declspec(noinline) void __fastcall mockDraw(void* d,uint32_t,uint32_t,uint32_t){
    fixtureContext->Draw(3,0);inside=true;while(hold.load())Sleep(1);bump(d);
}
__declspec(noinline) void __fastcall mockLand(void* d,uint32_t,uint32_t,uint32_t){fixtureContext->Draw(3,0);bump(d);}
__declspec(noinline) void __fastcall mockIndexed(void* d,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t){fixtureContext->Draw(3,0);bump(d);}
__declspec(noinline) uint8_t __fastcall mockPrepare(void*,uint32_t mode){if(mode==99)return 0;
    if(mode==98){prepareTracked=callbacks.load()>0&&callbackDepth>0;inside=true;while(hold.load())Sleep(1);SetLastError(1234);return 1;}
    if(fixtureSource)fixtureContext->PSSetShaderResources(0,1,&fixtureSource);return 1;}
__declspec(noinline) void __fastcall mockBatch(void* d){auto counter=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);
    if(mockPrepare(d,0)){mockIndexed(d,0,0,0,0,6);std::memcpy(static_cast<BYTE*>(d)+0x2ad0,&counter,4);}}
__declspec(noinline) void __fastcall mockMesh(void* d,uint32_t mode,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t){
    if(!mockPrepare(d,mode))return;fixtureContext->Draw(3,0);inside=true;while(hold.load())Sleep(1);bump(d);}
void checked(HRESULT hr){if(FAILED(hr))throw std::runtime_error("Owned WARP API failed");}
__declspec(noinline) void __fastcall mockInstanced(void* d,uint32_t m,uint32_t a,uint32_t b,uint32_t c,uint32_t e){mockIndexed(d,m,a,b,c,e);}
__declspec(noinline) void __fastcall mockIndirect(void* d,uint32_t,void*,uint32_t){if(mockPrepare(d,0))fixtureContext->Draw(3,0);bump(d);}
__declspec(noinline) void __fastcall mockAuto(void* d,uint32_t){if(mockPrepare(d,0))fixtureContext->Draw(3,0);bump(d);}
__declspec(noinline) void __fastcall mockInline(void* d,uint32_t,uint32_t,const void*,uint32_t){if(mockPrepare(d,0))fixtureContext->Draw(3,0);bump(d);}
__declspec(noinline) uint64_t __fastcall mockPresent(void*,void*){inside=true;while(hold.load())Sleep(1);SetLastError(4321);return 0x1234567812345678ull;}
ComPtr<ID3D11ShaderResourceView> sourceFor(ID3D11Device* device,const std::vector<BYTE>& bytes,DXGI_FORMAT format,const TreeAsset* descriptor=nullptr){
    if(!descriptor&&bytes.size()!=174752&&bytes.size()!=349520)throw std::runtime_error("Original GPU payload size differs");
    UINT width=descriptor?descriptor->width:512,height=descriptor?descriptor->height:bytes.size()==174752?256:512,levels=descriptor?descriptor->mips:height==256?7:8;
    const bool bc1=format==DXGI_FORMAT_BC1_UNORM||format==DXGI_FORMAT_BC1_UNORM_SRGB;
    D3D11_SUBRESOURCE_DATA mips[8]{};size_t at=0;
    for(UINT mip=0;mip<levels;mip++){UINT pitch=std::max(1u,((width>>mip)+3)/4)*(bc1?8u:16u),rows=std::max(1u,((height>>mip)+3)/4);
        mips[mip]={bytes.data()+at,pitch,pitch*rows};at+=size_t(pitch)*rows;}
    D3D11_TEXTURE2D_DESC d{width,height,levels,1,bc1?DXGI_FORMAT_BC1_TYPELESS:DXGI_FORMAT_BC7_TYPELESS,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
    ComPtr<ID3D11Texture2D> texture;checked(device->CreateTexture2D(&d,mips,&texture));
    D3D11_SHADER_RESOURCE_VIEW_DESC v{};v.Format=format;v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;v.Texture2D.MipLevels=levels;
    ComPtr<ID3D11ShaderResourceView> view;checked(device->CreateShaderResourceView(texture.Get(),&v,&view));return view;
}
// Independent raster sampling of every mip (the product uses a compute shader).
// RGB expectations are calculated below on the CPU from these decoded originals.
std::vector<BYTE> sampleView(ID3D11DeviceContext* context,ID3D11ShaderResourceView* source,const TreeAsset& asset,bool srgb){
    ComPtr<ID3D11Device> device;context->GetDevice(&device);
    auto effect=seasons::CreatePipelineState(context);seasons::PipelineScope scope(context,GetCurrentThreadId(),effect.Get());
    const char code[]=R"(Texture2D<float4> S:register(t0);cbuffer P:register(b0){uint Mip;uint Encode;uint2 Pad;}
    float4 vs(uint i:SV_VertexID):SV_Position{float2 p[3]={float2(-1,-1),float2(-1,3),float2(3,-1)};return float4(p[i],0,1);}
    float4 ps(float4 p:SV_Position):SV_Target{float4 v=S.Load(int3(uint2(p.xy),Mip));
      if(Encode)v.rgb=lerp(12.92*v.rgb,1.055*pow(max(v.rgb,0),1.0/2.4)-.055,step(.0031308,v.rgb));return v;})";
    ComPtr<ID3DBlob> v,p,e;checked(D3DCompile(code,sizeof(code),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&v,&e));
    checked(D3DCompile(code,sizeof(code),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&p,&e));
    ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;
    checked(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs));
    checked(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&ps));
    context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);context->PSSetShaderResources(0,1,&source);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);std::vector<BYTE> result;
    for(UINT mip=0;mip<asset.mips;mip++){
        UINT w=std::max(1u,512u>>mip),h=std::max(1u,asset.height>>mip);
        D3D11_TEXTURE2D_DESC d{w,h,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
        ComPtr<ID3D11Texture2D> output,staging;checked(device->CreateTexture2D(&d,nullptr,&output));
        d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;checked(device->CreateTexture2D(&d,nullptr,&staging));
        ComPtr<ID3D11RenderTargetView> target;checked(device->CreateRenderTargetView(output.Get(),nullptr,&target));auto* rt=target.Get();context->OMSetRenderTargets(1,&rt,nullptr);
        D3D11_VIEWPORT viewport{0,0,static_cast<float>(w),static_cast<float>(h),0,1};context->RSSetViewports(1,&viewport);
        uint32_t constants[]={mip,srgb?1u:0u,0,0};D3D11_BUFFER_DESC b{16,D3D11_USAGE_IMMUTABLE,D3D11_BIND_CONSTANT_BUFFER,0,0,0};
        D3D11_SUBRESOURCE_DATA data{constants,0,0};ComPtr<ID3D11Buffer> cb;checked(device->CreateBuffer(&b,&data,&cb));auto* raw=cb.Get();context->PSSetConstantBuffers(0,1,&raw);
        context->Draw(3,0);context->CopyResource(staging.Get(),output.Get());D3D11_MAPPED_SUBRESOURCE mapped{};checked(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        size_t at=result.size();result.resize(at+size_t(w)*h*4);
        for(UINT y=0;y<h;y++)std::memcpy(result.data()+at+y*w*4,static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch,w*4);
        context->Unmap(staging.Get(),0);
    }
    return result;
}
int materialChecks(const char* path){
    std::ifstream f(path,std::ios::binary);TreeSources sources;sources.bytes.assign(std::istreambuf_iterator<char>(f),std::istreambuf_iterator<char>());
    if(!sources.valid())return 30;auto changed=sources;changed.bytes[0]^=1;if(changed.valid())return 31;
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    checked(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    TreeColorCache cache(sources);bool valid=true;uint64_t pixelsChecked=0,changedRgb=0,epoch=1;
    uint32_t cases=0,generatedCount=0;uint64_t expectedBytes=0;
    for(uint32_t format=0;format<2;format++){
        std::vector<std::vector<BYTE>> originals;std::vector<ComPtr<ID3D11ShaderResourceView>> views;
        for(uint32_t i=0;i<std::size(TreeAssets);i++){
            std::vector<BYTE> bytes(sources.data(i),sources.data(i)+TreeAssets[i].bytes);
            views.push_back(sourceFor(device.Get(),bytes,static_cast<DXGI_FORMAT>(TreeAssets[i].format+format),&TreeAssets[i]));
            originals.push_back(sampleView(context.Get(),views.back().Get(),TreeAssets[i],format!=0));
        }
        for(uint32_t i=0;i<std::size(TreeAssets);i++){
            expectedBytes+=uint64_t(TreeAssets[i].pixels)*4+TreeAssets[TreeAssets[i].green].bytes+TreeAssets[TreeAssets[i].autumn].bytes;
            // One controller epoch spans several live dates. The material
            // cache must refresh its real GPU output even before a new request.
            for(uint32_t month:{7u,10u,1u,8u}){
                TreeColorParameters p{epoch,195,month,month==8?16u:1u,{}};
                if(month==7)p.weights[1]=1;else if(month==10)p.weights[2]=1;else if(month==1)p.weights[3]=1;else p.weights[1]=p.weights[2]=.5f;
                ID3D11ShaderResourceView* output{};uint32_t alpha{};bool generated=false;
                auto hr=cache.get(context.Get(),views[i].Get(),p,&output,alpha,generated,TreeAssets[i].counts[0]);
                valid=valid&&hr==S_OK&&alpha==TreeAssets[i].pixels&&cache.gpuBytes()==expectedBytes;generatedCount+=generated?1:0;
                if(hr!=S_OK||!output)return 32;
                auto actual=sampleView(context.Get(),output,TreeAssets[i],format!=0);
                const auto& green=originals[TreeAssets[i].green];const auto& autumn=originals[TreeAssets[i].autumn];float weight=TreeAssets[i].policy==2?0:(p.weights[2]+p.weights[3]);
                auto linear=[](double x){x/=255;return x<=.04045?x/12.92:std::pow((x+.055)/1.055,2.4);};
                auto encoded=[](double x){return x<=.0031308?12.92*x:1.055*std::pow(x,1/2.4)-.055;};
                for(size_t at=0;at<actual.size();at++){
                    if(at%4==3){valid=valid&&actual[at]==originals[i][at];++pixelsChecked;continue;}
                    double expected=format?encoded(linear(green[at])*(1-weight)+linear(autumn[at])*weight)*255:green[at]*(1-weight)+autumn[at]*weight;
                    valid=valid&&std::abs(actual[at]-expected)<=2.0;
                    if(originals[i][at/4*4+3]>127&&std::abs(int(actual[at])-int(originals[i][at]))>4)++changedRgb;
                }
                // Same epoch/weights reuses output. A count/payload mismatch is rejected.
                generated=false;valid=valid&&cache.get(context.Get(),views[i].Get(),p,&output,alpha,generated,TreeAssets[i].counts[0])==S_OK&&!generated;
                valid=valid&&cache.get(context.Get(),views[i].Get(),p,&output,alpha,generated,0)==S_FALSE;
                ++cases;
            }
            ++epoch;
        }
    }
    valid=valid&&cache.gpuBytes()==expectedBytes&&changedRgb>0;
    std::cout<<"{\"ownWarpProcess\":true,\"gameAccess\":false,\"materialCases\":"<<cases<<",\"alphaPixelsChecked\":"<<pixelsChecked<<
        ",\"changedOpaqueRgbSamples\":"<<changedRgb<<",\"ownedGpuBytes\":"<<cache.gpuBytes()<<",\"generated\":"<<generatedCount<<",\"sameRequestEpochCalendarChanges\":true,\"passed\":"<<(valid?"true":"false")<<"}\n";
    return valid?0:33;
}
Reply send(HANDLE pipe,Command command,uint64_t generation,uint64_t sequence,const TreeColorParameters* parameters=nullptr){
    Message m{Magic,Version,sizeof(Message),command,generation,sequence};DWORD bytes{};
    if(parameters){m.year=parameters->year;m.month=parameters->month;m.day=parameters->day;std::memcpy(m.weights,parameters->weights,16);}
    if(!WriteFile(pipe,&m,sizeof(m),&bytes,nullptr)||bytes!=sizeof(m))throw std::runtime_error("Owned pipe write failed");
    Reply reply{};if(!ReadFile(pipe,&reply,sizeof(reply),&bytes,nullptr)||bytes!=sizeof(reply)||reply.sequence!=sequence||reply.generation!=generation)
        throw std::runtime_error("Owned pipe reply failed");return reply;
}
}
int main(int argc,char** argv){try{
    const std::string mode=argc>1?argv[1]:"repeat";
    if(mode=="materials")return argc==3?materialChecks(argv[2]):34;
    const bool color=mode.starts_with("color");
    ComPtr<ID3D11Device> device;checked(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&fixtureContext));
    const char* text=color?"Texture2D<float4> Source:register(t0);float4 vs(uint id:SV_VertexID):SV_Position{float2 p[3]={float2(-1,-1),float2(-1,3),float2(3,-1)};return float4(p[id],0,1);}float4 ps():SV_Target{return Source.Load(int3(0,0,0));}":
        "float4 vs(uint id:SV_VertexID):SV_Position{float2 p[3]={float2(-1,-1),float2(-1,3),float2(3,-1)};return float4(p[id],0,1);}float4 ps():SV_Target{return float4(.2,.4,.6,1);}";
    ComPtr<ID3DBlob> v,p,e;checked(D3DCompile(text,strlen(text),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&v,&e));
    checked(D3DCompile(text,strlen(text),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&p,&e));
    ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;checked(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs));
    checked(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&ps));
    D3D11_TEXTURE2D_DESC td{16,16,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    ComPtr<ID3D11Texture2D> target,staging;checked(device->CreateTexture2D(&td,nullptr,&target));
    ComPtr<ID3D11RenderTargetView> rt;checked(device->CreateRenderTargetView(target.Get(),nullptr,&rt));
    auto* raw=rt.Get();fixtureContext->OMSetRenderTargets(1,&raw,nullptr);fixtureContext->VSSetShader(vs.Get(),nullptr,0);fixtureContext->PSSetShader(ps.Get(),nullptr,0);
    fixtureContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);D3D11_VIEWPORT vp{0,0,16,16,0,1};fixtureContext->RSSetViewports(1,&vp);
    BYTE driver[0x3100]{},state[0x80]{};auto* context=fixtureContext.Get();std::memcpy(driver+0x10,&context,8);
    // Recorded game Draw drivers have a context at +0x10 and zero at +0x3078;
    // the device comes from GetDevice on the already owned actual Draw thread.
    TreeSources materials;
    if(color){std::ifstream f(argv[argc-1],std::ios::binary);materials.bytes.assign(std::istreambuf_iterator<char>(f),std::istreambuf_iterator<char>());if(!materials.valid())return 22;}
    ComPtr<ID3D11Texture2D> sourceTexture;ComPtr<ID3D11ShaderResourceView> sourceView;
    if(color){
        if(argc!=(mode=="color-variant"?5:4))return 20;std::ifstream file(argv[2],std::ios::binary);std::vector<BYTE> bytes((std::istreambuf_iterator<char>(file)),std::istreambuf_iterator<char>());
        if(bytes.size()!=174752)return 21;D3D11_SUBRESOURCE_DATA mips[7]{};size_t at=0;
        for(UINT mip=0;mip<7;mip++){UINT pitch=std::max(1u,((512u>>mip)+3)/4)*16,rows=std::max(1u,((256u>>mip)+3)/4);
            mips[mip]={bytes.data()+at,pitch,pitch*rows};at+=size_t(pitch)*rows;}
        D3D11_TEXTURE2D_DESC sourceDesc{512,256,7,1,DXGI_FORMAT_BC7_TYPELESS,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
        checked(device->CreateTexture2D(&sourceDesc,mips,&sourceTexture));D3D11_SHADER_RESOURCE_VIEW_DESC view{};
        view.Format=mode=="color-linear"?DXGI_FORMAT_BC7_UNORM:DXGI_FORMAT_BC7_UNORM_SRGB;
        view.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;view.Texture2D.MipLevels=7;
        checked(device->CreateShaderResourceView(sourceTexture.Get(),&view,&sourceView));fixtureSource=sourceView.Get();
    }
    td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;checked(device->CreateTexture2D(&td,nullptr,&staging));
    auto pixels=[&]{std::vector<BYTE> bytes(1024);fixtureContext->CopyResource(staging.Get(),target.Get());D3D11_MAPPED_SUBRESOURCE m{};
        checked(fixtureContext->Map(staging.Get(),0,D3D11_MAP_READ,0,&m));for(UINT y=0;y<16;y++)std::memcpy(bytes.data()+y*64,static_cast<BYTE*>(m.pData)+y*m.RowPitch,64);
        fixtureContext->Unmap(staging.Get(),0);return bytes;};
    mockMesh(driver,0,0,0,0,918,1);auto originalPixels=pixels();
    uint32_t date=195|(4<<16)|(21<<24);std::memcpy(state+0x34,&date,4);
    FILETIME created{},exit{},kernel{},user{};if(!GetProcessTimes(GetCurrentProcess(),&created,&exit,&kernel,&user))return 3;
    bool valid=true;unsigned iterations=mode=="repeat"||mode=="color"?3:1;uint64_t observedTotal=0,colorTotal=0;
    for(unsigned iteration=0;iteration<iterations;iteration++){
        Bootstrap input{};input.magic=Magic;input.version=Version;input.size=sizeof(input);input.generation=iteration+1;
        input.controllerPid=GetCurrentProcessId();input.controllerStartFileTime=uint64_t(created.dwHighDateTime)<<32|created.dwLowDateTime;input.leaseMs=1000;
        if(color){input.sources=reinterpret_cast<uint64_t>(materials.bytes.data());input.sourceBytes=TreeSourceBytes;}
        input.state=reinterpret_cast<uint64_t>(state);input.context=reinterpret_cast<uint64_t>(context);input.device=reinterpret_cast<uint64_t>(device.Get());
        swprintf_s(input.pipe,L"\\\\.\\pipe\\san14-seasons-own-%u-%llu-%u",GetCurrentProcessId(),GetTickCount64(),iteration);
        std::wcerr<<L"Owned client pipe: "<<input.pipe<<L"\n";
        initialize(true);originalDraw=mockDraw;originalLand=mockLand;originalMesh=mockMesh;originalPrepare=mockPrepare;originalIndexed=mockIndexed;originalBatch=mockBatch;
        originalInstanced=mockInstanced;originalIndirect=mockIndirect;originalAuto=mockAuto;originalInline=mockInline;originalPresent=mockPresent;
        Recovery result{};std::atomic<bool> done=false;
        std::thread worker([&]{result=serve(input,true);done=true;});
        HANDLE pipe=INVALID_HANDLE_VALUE;
        DWORD lastConnectError=0;
        for(unsigned attempt=0;attempt<100&&pipe==INVALID_HANDLE_VALUE;attempt++){
            pipe=CreateFileW(input.pipe,GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);
            auto error=GetLastError();
            if(error!=lastConnectError||attempt==99)std::cerr<<"Owned connect attempt "<<attempt<<", tick="<<GetTickCount64()<<", Win32="<<error<<"\n";
            lastConnectError=error;
            if(pipe==INVALID_HANDLE_VALUE)Sleep(10);
        }
        if(pipe==INVALID_HANDLE_VALUE){auto error=GetLastError();worker.join();
            std::cerr<<"Owned pipe connect failed; client Win32 "<<error<<", native recovery error "<<result.error<<
                ", restored "<<result.dispatchDetached<<", resources "<<result.resourcesReleased<<"\n";return 9;}
        DWORD pipeMode=PIPE_READMODE_MESSAGE;SetNamedPipeHandleState(pipe,&pipeMode,nullptr,nullptr);
        // The pipe handle becomes visible before the Hook transaction commits.
        // A reply from serve proves that commit/connect completed; opening the
        // pipe alone does not authorize the fixture's first callback assertion.
        auto ready=send(pipe,Hello,input.generation,1);
        valid=valid&&ready.error==ERROR_IO_PENDING&&!ready.actualDraws;
        const bool prepareHeld=mode=="prepare-hold",presentHeld=mode=="present-hold",held=mode=="hold"||prepareHeld||presentHeld;hold=held;inside=false;bool early=false;
        std::thread release;if(held)release=std::thread([&]{while(!inside.load())Sleep(1);Sleep(1200);early=done.load();hold=false;});
        if(prepareHeld){auto value=mockPrepare(driver,98);auto error=GetLastError();valid=valid&&value==1&&error==1234&&prepareTracked;}
        else if(presentHeld){auto result=mockPresent(driver,nullptr);valid=valid&&result==0x1234567812345678ull&&GetLastError()==4321;}
        else mockDraw(driver,0,0,3);
        if(release.joinable())release.join();
        if(!held){
            mockLand(driver,0,1,0);mockMesh(driver,0,0,0,0,3,1);mockIndexed(driver,0,0,0,0,6);
            auto batchCounter=read<uint32_t>(reinterpret_cast<uint64_t>(driver),0x2ad0);mockBatch(driver);
            valid=valid&&read<uint32_t>(reinterpret_cast<uint64_t>(driver),0x2ad0)==batchCounter;
            auto hello=send(pipe,Hello,input.generation,2);valid=valid&&!hello.error&&hello.actualDraws==5&&hello.renderThread==GetCurrentThreadId();
            auto apply=send(pipe,Apply,input.generation,3);valid=valid&&apply.error==ERROR_NOT_SUPPORTED&&!apply.rendered&&!apply.capabilities;
            uint64_t sequence=4;
            if(color){
                TreeColorParameters parameters{sequence,195,4,21,{1,0,0,0}};
                valid=valid&&!send(pipe,ApplyTreeColor,input.generation,sequence++,&parameters).error;
                const bool heldColor=mode=="color-hold";hold=heldColor;inside=false;bool colorEarly=false;
                std::thread releaseColor;if(heldColor)releaseColor=std::thread([&]{while(!inside.load())Sleep(1);Sleep(1200);colorEarly=done.load();hold=false;});
                mockMesh(driver,0,0,0,0,918,1);if(releaseColor.joinable())releaseColor.join();
                valid=valid&&!colorEarly;
                ComPtr<ID3D11ShaderResourceView> restored;fixtureContext->PSGetShaderResources(0,1,&restored);valid=valid&&restored.Get()==fixtureSource;
                if(!heldColor){
                    auto colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                    std::cerr<<"Owned color reply: error="<<colorReply.error<<", draws="<<colorReply.treeDraws<<", swaps/restores="<<colorReply.swaps<<"/"<<colorReply.restores<<", generated="<<colorReply.generated<<", alpha="<<colorReply.alphaPixels<<"\n";
                    valid=valid&&!colorReply.error&&colorReply.completedEpoch==parameters.epoch&&colorReply.treeDraws==1&&colorReply.swaps==colorReply.restores&&
                        colorReply.alphaPixels==174752&&colorReply.generated==1&&colorReply.gpuBytes==1048512&&colorReply.colorScope==1&&!colorReply.rendered;
                    ++colorTotal;
                    // Repeated identical parameters reuse the owned GPU cache.
                    parameters.epoch=sequence;valid=valid&&!send(pipe,ApplyTreeColor,input.generation,sequence++,&parameters).error;
                    mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                    valid=valid&&!colorReply.error&&colorReply.generated==1&&colorReply.completedEpoch==parameters.epoch;++colorTotal;
                    if(mode=="color-recreate"){
                        // A recreated SRV/resource with the exact same payload
                        // must survive calendar refresh without GPU regeneration.
                        std::ifstream file(argv[2],std::ios::binary);std::vector<BYTE> bytes((std::istreambuf_iterator<char>(file)),std::istreambuf_iterator<char>());
                        auto createView=[&](DXGI_FORMAT format,bool altered){
                            auto data=bytes;if(altered)data[0]^=1;D3D11_SUBRESOURCE_DATA mips[7]{};size_t at=0;
                            for(UINT mip=0;mip<7;mip++){UINT pitch=std::max(1u,((512u>>mip)+3)/4)*16,rows=std::max(1u,((256u>>mip)+3)/4);
                                mips[mip]={data.data()+at,pitch,pitch*rows};at+=size_t(pitch)*rows;}
                            D3D11_TEXTURE2D_DESC d{512,256,7,1,DXGI_FORMAT_BC7_TYPELESS,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
                            ComPtr<ID3D11Texture2D> texture;checked(device->CreateTexture2D(&d,mips,&texture));
                            D3D11_SHADER_RESOURCE_VIEW_DESC v{};v.Format=format;v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;v.Texture2D.MipLevels=7;
                            ComPtr<ID3D11ShaderResourceView> view;checked(device->CreateShaderResourceView(texture.Get(),&v,&view));return view;
                        };
                        auto recreated=createView(DXGI_FORMAT_BC7_UNORM_SRGB,false);fixtureSource=recreated.Get();
                        auto draws=colorReply.treeDraws;mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.treeDraws==draws+1&&colorReply.generated==1&&colorReply.gpuBytes==1048512;
                        // More distinct negative candidates than the identity
                        // cache's bound are still rejected without killing ON.
                        draws=colorReply.treeDraws;
                        for(unsigned i=0;i<40;i++){auto unknown=createView(DXGI_FORMAT_BC7_UNORM_SRGB,true);fixtureSource=unknown.Get();mockMesh(driver,0,0,0,0,918,1);}
                        colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);valid=valid&&!colorReply.error&&colorReply.treeDraws==draws;
                        fixtureSource=recreated.Get();mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.treeDraws==draws+1&&colorReply.generated==1;
                        auto linear=createView(DXGI_FORMAT_BC7_UNORM,false);fixtureSource=linear.Get();mockMesh(driver,0,0,0,0,918,1);
                        colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);valid=valid&&!colorReply.error&&colorReply.generated==2&&colorReply.gpuBytes==2097024;
                        fixtureSource=recreated.Get();mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.generated==2&&colorReply.gpuBytes==2097024;
                        // A stale, valid date is retriable on the same worker.
                        uint32_t next=195|(6<<16)|(1<<24);InterlockedExchange(reinterpret_cast<volatile LONG*>(state+0x34),static_cast<LONG>(next));
                        auto stale=send(pipe,ApplyTreeColor,input.generation,sequence++,&parameters);
                        valid=valid&&stale.error==ERROR_RETRY&&!send(pipe,Heartbeat,input.generation,sequence++).error;
                        parameters={sequence,195,6,1,{0,1,0,0}};
                        valid=valid&&!send(pipe,ApplyTreeColor,input.generation,sequence++,&parameters).error;
                        auto pending=send(pipe,TreeColorStatus,input.generation,sequence++);valid=valid&&pending.error==ERROR_IO_PENDING&&pending.completedEpoch!=parameters.epoch;
                        mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.completedEpoch==parameters.epoch&&colorReply.swaps==colorReply.restores&&colorReply.generated==3;
                        colorTotal+=5;fixtureSource=sourceView.Get();
                    }
                    if(mode=="color-variant"){
                        std::ifstream firstFile(argv[2],std::ios::binary),julyFile(argv[3],std::ios::binary);
                        std::vector<BYTE> first((std::istreambuf_iterator<char>(firstFile)),std::istreambuf_iterator<char>()),
                            july((std::istreambuf_iterator<char>(julyFile)),std::istreambuf_iterator<char>());
                        valid=valid&&materialSha(first.data(),first.size(),"b696b37084bdcee62cade9988cc3372f2f82cfcb1b33052fa2398064678af520")&&
                            materialSha(july.data(),july.size(),"5376cdde46d4d0affebccec27dbf04705d7665eedebf8aaf14bf5614b1699653");
                        uint32_t next=195|(6<<16)|(30<<24);InterlockedExchange(reinterpret_cast<volatile LONG*>(state+0x34),static_cast<LONG>(next));
                        parameters={sequence,195,6,30,{0,1,0,0}};valid=valid&&!send(pipe,ApplyTreeColor,input.generation,sequence++,&parameters).error;
                        mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.generated==2&&colorReply.completedEpoch==parameters.epoch;
                        next=195|(7<<16)|(1<<24);InterlockedExchange(reinterpret_cast<volatile LONG*>(state+0x34),static_cast<LONG>(next));
                        parameters={sequence,195,7,1,{0,1,0,0}};valid=valid&&!send(pipe,ApplyTreeColor,input.generation,sequence++,&parameters).error;
                        auto pending=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&pending.error==ERROR_IO_PENDING&&pending.completedEpoch!=parameters.epoch;
                        auto julyView=sourceFor(device.Get(),july,DXGI_FORMAT_BC7_UNORM_SRGB);fixtureSource=julyView.Get();
                        mockPrepare(driver,0);fixtureContext->Draw(3,0);auto julyOriginal=pixels();
                        mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        ComPtr<ID3D11ShaderResourceView> julyRestored;fixtureContext->PSGetShaderResources(0,1,&julyRestored);
                        valid=valid&&!colorReply.error&&colorReply.completedEpoch==parameters.epoch&&colorReply.generated==3&&
                            colorReply.gpuBytes==2097024&&colorReply.alphaPixels==174752&&colorReply.swaps==colorReply.restores&&
                            julyRestored.Get()==julyView.Get()&&pixels()!=julyOriginal&&!colorReply.rendered;
                        auto duplicate=sourceFor(device.Get(),july,DXGI_FORMAT_BC7_UNORM_SRGB);fixtureSource=duplicate.Get();
                        mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.generated==3&&colorReply.gpuBytes==2097024;
                        auto julyLinear=sourceFor(device.Get(),july,DXGI_FORMAT_BC7_UNORM);fixtureSource=julyLinear.Get();
                        mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.generated==4&&colorReply.gpuBytes==3145536;
                        auto firstLinear=sourceFor(device.Get(),first,DXGI_FORMAT_BC7_UNORM);fixtureSource=firstLinear.Get();
                        mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.generated==5&&colorReply.gpuBytes==4194048;
                        auto draws=colorReply.treeDraws;auto altered=july;altered[0]^=1;
                        auto unknown=sourceFor(device.Get(),altered,DXGI_FORMAT_BC7_UNORM_SRGB);fixtureSource=unknown.Get();
                        mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.treeDraws==draws&&colorReply.gpuBytes==4194048;
                        fixtureSource=sourceView.Get();mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                        valid=valid&&!colorReply.error&&colorReply.generated==5&&colorReply.treeDraws==draws+1&&colorReply.gpuBytes==4194048;
                        colorTotal+=6;
                        std::cerr<<"Owned June/July variant: error="<<colorReply.error<<", generated="<<colorReply.generated<<
                            ", gpuBytes="<<colorReply.gpuBytes<<", alpha="<<colorReply.alphaPixels<<", completedEpoch="<<colorReply.completedEpoch<<"\n";
                    }
                    if(mode=="color"){
                        uint32_t dates[6][3]={{195,5,30},{195,6,1},{195,11,30},{195,12,30},{196,1,1},{195,4,21}};
                        uint32_t expectedGenerated[6]={2,3,4,5,5,6};
                        for(unsigned i=0;i<std::size(dates);i++){
                            parameters={sequence,dates[i][0],dates[i][1],dates[i][2]};
                            auto season=[](uint32_t month){return month>=3&&month<=5?0:month>=6&&month<=8?1:month>=9&&month<=11?2:3;};
                            const float t=static_cast<float>(parameters.day-1)/30;parameters.weights[season(parameters.month)]+=1-t;
                            parameters.weights[season(parameters.month==12?1:parameters.month+1)]+=t;
                            auto value=parameters.year|(parameters.month<<16)|(parameters.day<<24);
                            InterlockedExchange(reinterpret_cast<volatile LONG*>(state+0x34),static_cast<LONG>(value));
                            valid=valid&&!send(pipe,ApplyTreeColor,input.generation,sequence++,&parameters).error;
                            mockMesh(driver,0,0,0,0,918,1);colorReply=send(pipe,TreeColorStatus,input.generation,sequence++);
                            valid=valid&&!colorReply.error&&colorReply.completedEpoch==parameters.epoch&&colorReply.generated==expectedGenerated[i]&&
                                colorReply.swaps==colorReply.restores&&colorReply.gpuBytes==1048512&&colorReply.alphaPixels==174752;++colorTotal;
                        }
                    }
                }
            }
            if(mode=="repeat"||mode=="color"||mode=="color-recreate"||mode=="color-variant")valid=valid&&!send(pipe,Stop,input.generation,sequence).error;
            if(mode=="expire")Sleep(1200);
        }
        CloseHandle(pipe);worker.join();
        valid=valid&&!early&&!result.error&&result.callbacksDrained&&result.originalGpuState&&result.dispatchDetached&&result.protectionRestored&&result.resourcesReleased;
        observedTotal+=result.actualDraws;
        if(color){mockMesh(driver,0,0,0,0,918,1);valid=valid&&pixels()==originalPixels;}
    }
    fixtureContext->CopyResource(staging.Get(),target.Get());D3D11_MAPPED_SUBRESOURCE mapped{};checked(fixtureContext->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
    for(UINT y=0;y<16;y++)for(UINT x=0;x<16;x++){auto* px=static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch+x*4;
        valid=valid&&std::memcmp(px,originalPixels.data()+(y*16+x)*4,4)==0;}
    fixtureContext->Unmap(staging.Get(),0);
    std::cout<<"{\"ownWarpProcess\":true,\"gameAccess\":false,\"mode\":\""<<mode<<"\",\"sessions\":"<<iterations<<
        ",\"actualDraws\":"<<observedTotal<<",\"originalPixels\":256,\"bootstrapSize\":"<<sizeof(Bootstrap)<<
        ",\"colorDrawsChecked\":"<<colorTotal<<",\"renderedReceiptManufactured\":false,\"passed\":"<<(valid?"true":"false")<<"}\n";
    return valid?0:7;
}catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 9;}}
