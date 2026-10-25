#define SEASON_BRIDGE_FIXTURE
#include "SeasonBridge.cpp"
#include <fstream>
#include <iostream>
#include <chrono>
#include <thread>
namespace {
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
ID3D11DeviceContext* noticeContext=nullptr;ID3D11ShaderResourceView* noticeOriginal=nullptr;uint32_t noticeKind=0;bool noticeNested=false;
BOOL WINAPI readNoticeFixture(environment::WeatherSnapshot* output){*output={};output->generation=1;output->sequence=1;output->date=195|(4u<<16)|(21u<<24);output->reserved=noticeKind;return TRUE;}
uint8_t __fastcall prepareNoticeFixture(void*,uint32_t){noticeContext->PSSetShaderResources(0,1,&noticeOriginal);return 1;}
void drawNoticeFixture(void* d){if(onPrepare(d,0))noticeContext->Draw(3,0);}
void __fastcall batchNoticeFixture(void* d){if(noticeNested){auto count=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);onIndexed(d,0,0,0,0,18);std::memcpy(static_cast<BYTE*>(d)+0x2ad0,&count,4);}else drawNoticeFixture(d);}
void __fastcall directNoticeFixture(void* d,uint32_t,uint32_t,uint32_t){drawNoticeFixture(d);auto count=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0)+1;std::memcpy(static_cast<BYTE*>(d)+0x2ad0,&count,4);}
void __fastcall indexedNoticeFixture(void* d,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t){directNoticeFixture(d,0,0,3);}
void __fastcall meshNoticeFixture(void* d,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t){directNoticeFixture(d,0,0,3);}
void hr(HRESULT value){if(FAILED(value)){std::cerr<<"HRESULT="<<std::hex<<value<<std::dec<<"\n";throw std::runtime_error("Scene fixture GPU API failed");}}
uint8_t __fastcall prepareWithoutBindings(void*,uint32_t mode){return mode==99?0:1;}
__declspec(noinline) uint8_t observeCallerA(void* driver,uint32_t mode=0){volatile auto result=onPrepare(driver,mode);return result;}
__declspec(noinline) uint8_t observeCallerB(void* driver,uint32_t mode=0){volatile auto result=onPrepare(driver,mode);return result;}
uint64_t causalStart=0,causalCapture=0,causalCaptureQpc=0;uint32_t causalStop=0;bool causalUnavailable=false;
BOOL WINAPI causalExchange(notice_trace::Query* q){
    if(causalUnavailable)return FALSE;LARGE_INTEGER f{};QueryPerformanceFrequency(&f);
    q->state={};q->state.environment=17;q->state.season=66;q->state.test=100;q->state.startedMs=causalStart;q->state.frequency=f.QuadPart;
    if(q->command==4&&!causalStop)causalStop=q->reserved;
    q->state.captureStartedMs=causalCapture;q->state.captureStartedQpc=causalCaptureQpc;q->state.stopReason=causalStop;
    q->state.phase=notice_trace::Requested;q->state.records=1;q->state.ended=q->command==2||q->command==3;
    q->count=q->command==2&&q->first==0?1:0;if(q->count)q->entries[0]={1,q->state};return TRUE;
}
void __fastcall causalIndirect(void* d,uint32_t,void*,uint32_t){drawNoticeFixture(d);}
void __fastcall causalAuto(void* d,uint32_t){drawNoticeFixture(d);}
void __fastcall causalInline(void* d,uint32_t,uint32_t,const void*,uint32_t){drawNoticeFixture(d);}
uint64_t __fastcall causalPresent(void*,void*){SetLastError(7123);return 0x1234567887654321ull;}
void causalChecks(const std::shared_ptr<Session>& owned,ID3D11DeviceContext* context,BYTE* driver){
    ComPtr<ID3D11ShaderResourceView> keep=noticeOriginal;auto path=std::filesystem::temp_directory_path()/(L"San14-causal-"+std::to_wstring(GetCurrentProcessId())+L".bin");
    auto reset=[&]{causalStart=notice_trace::milliseconds();causalCapture=causalStart;causalCaptureQpc=notice_trace::qpc();causalStop=0;owned->noticeCausality=std::make_unique<SeasonNoticeCausality>(66,path.wstring());owned->noticeCausality->fixtureExchange(causalExchange,17);};reset();
    causalCapture=0;
    for(unsigned n=0;n<70000;n++){auto& t=*owned->noticeCausality;auto* row=t.begin(t.id(),0,1,1,driver,owned->input.context,GetCurrentProcessId());t.bindings(row,context,true);t.complete(row);}
    require(!owned->noticeCausality->fixtureCount()&&std::all_of(owned->noticeCausality->fixtureDescriptors().begin(),owned->noticeCausality->fixtureDescriptors().end(),[](auto& d){return !d.view&&!d.hashState;}),"preparation must not consume rows descriptors or GPU readbacks");reset();
    originalPrepare=prepareNoticeFixture;originalInstanced=indexedNoticeFixture;originalIndirect=causalIndirect;originalAuto=causalAuto;originalInline=causalInline;originalPresent=causalPresent;
    auto swaps=owned->swaps.load();noticeKind=0;
    onDraw(driver,0,0,3);onLand(driver,0,3,0);onMesh(driver,0,0,0,0,3,1);onIndexed(driver,0,0,0,0,3);
    noticeNested=true;onBatch(driver);noticeNested=false;
    for(auto kind:{0u,3u}){noticeKind=kind;onInstanced(driver,0,3,1,0,0);onIndirect(driver,0,nullptr,0);onAuto(driver,0);onInline(driver,0,3,nullptr,0);}
    require(owned->swaps==swaps,"diagnostic-only Draw wrappers must never substitute original art");
    context->Flush(); // Fixture submission only. The product never flushes for diagnostics.
    for(unsigned n=0;n<20;n++){auto& trace=*owned->noticeCausality;auto row=trace.begin(trace.id(),0,1,SeasonNoticeCausality::Returned,driver,owned->input.context,GetCurrentThreadId());trace.bindings(row,context,true);trace.complete(row);Sleep(1);}
    bool paths[11]{},authenticated=false,linked=false;
    const auto& trace=*owned->noticeCausality;
    for(unsigned n=0;n<trace.fixtureCount();n++){const auto& row=trace.fixtureRows()[n];paths[row.path]=true;linked|=row.parent!=0;require(row.complete&&row.environment.test==100,"causal rows retain completed request identity");}
    for(auto& d:trace.fixtureDescriptors())authenticated|=d.hashState==2;
    for(unsigned i=1;i<=9;i++)require(paths[i],"causal trace omitted a known Draw wrapper");
    require(authenticated&&linked,"kind-zero original authentication and nested parent evidence are required");
    auto present=onPresent(driver,nullptr);auto error=GetLastError();require(present==0x1234567887654321ull&&error==7123,"Present result and LastError changed");
    auto count=trace.fixtureCount();require(count>=2&&trace.fixtureRows()[count-2].id==trace.fixtureRows()[count-1].id,"Present entry and return must share identity");
    std::thread other([&]{onPrepare(driver,0);});other.join();
    bool excluded=false;for(unsigned n=0;n<trace.fixtureCount();n++)excluded|=trace.fixtureRows()[n].safety==2;
    require(excluded,"foreign thread must be recorded without GPU observation");
    // Use the no-binding original for rejected Prepare; it must not manufacture a draw.
    originalPrepare=prepareWithoutBindings;require(onPrepare(driver,99)==0,"failed Prepare result changed");
    owned->noticeCausality->saveAfterDrain();require(std::filesystem::file_size(path)>128,"causal binary evidence missing");
    notice_trace::fixtureMilliseconds=1000;reset();auto& bounded=*owned->noticeCausality;
    for(unsigned n=0;n<SeasonNoticeCausality::Capacity+2;n++){auto row=bounded.begin(bounded.id(),0,1,1,driver,0,0);bounded.complete(row);}
    require(bounded.fixtureCount()==SeasonNoticeCausality::Capacity,"causal row capacity is not bounded");
    require(causalStop==notice_trace::RowCapacity,"capacity cancellation must retain its explicit cause");
    causalUnavailable=true;notice_trace::State state;require(!bounded.active(state),"unavailable snapshot must not produce evidence");causalUnavailable=false;
    reset();causalStart=1;notice_trace::fixtureMilliseconds=5999;require(owned->noticeCausality->active(state),"capture age is independent of older request time");
    notice_trace::fixtureMilliseconds=6000;require(!owned->noticeCausality->active(state)&&causalStop==notice_trace::CaptureDeadline,"causal deadline must reject additional observations");
    reset();auto& boundary=*owned->noticeCausality;auto* row=boundary.begin(boundary.id(),0,1,SeasonNoticeCausality::Returned,driver,0,0,0,causalCaptureQpc-1);boundary.complete(row);
    require(row&&row->boundary,"capture starting within an existing wrapper must remain a partial boundary");notice_trace::fixtureMilliseconds=0;
    owned->noticeCausality.reset();originalPrepare=prepareWithoutBindings;
    std::cerr<<"PASS causal original authentication before kind gate, nine Draw paths, nesting, observe-only extra wrappers, Present ABI, wrong-thread exclusion, bounded records and deadline; gameAccess=false\n";
}
void noticeObservationChecks(const std::shared_ptr<Session>& owned,ID3D11Device* device,ID3D11DeviceContext* context,BYTE* driver,const BYTE* payload,const char* output){
    ComPtr<ID3D11ShaderResourceView> original=noticeOriginal;
    auto path=output?std::filesystem::path(output):std::filesystem::temp_directory_path()/(L"San14Tenkazu-correlated-"+std::to_wstring(GetCurrentProcessId())+L".json");
    require(!std::filesystem::exists(path),"Correlated evidence must not overwrite a previous run");
    owned->noticeArt=std::make_unique<SeasonNoticeArt>(payload,path.wstring());auto& art=*owned->noticeArt;
    owned->input.module=reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr));originalPrepare=prepareWithoutBindings;
    context->ClearState();noticeKind=3;const auto swaps=owned->swaps.load();
    // First fill the unrelated-binding budget. Original candidates have their
    // own budget and must still retain exact caller/slot pairs afterwards.
    D3D11_TEXTURE2D_DESC desc{8,8,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_SHADER_RESOURCE,0,0};
    ComPtr<ID3D11Texture2D> unrelated;hr(device->CreateTexture2D(&desc,nullptr,&unrelated));
    std::vector<ComPtr<ID3D11ShaderResourceView>> keep;
    for(uint32_t i=0;i<70;i++){ComPtr<ID3D11ShaderResourceView> view;hr(device->CreateShaderResourceView(unrelated.Get(),nullptr,&view));auto* raw=view.Get();context->PSSetShaderResources(0,1,&raw);observeCallerA(driver);keep.push_back(std::move(view));}
    ID3D11ShaderResourceView* empty=nullptr;context->PSSetShaderResources(0,1,&empty);
    auto* raw=original.Get();context->PSSetShaderResources(5,1,&raw);observeCallerA(driver);
    context->PSSetShaderResources(5,1,&empty);context->PSSetShaderResources(127,1,&raw);observeCallerB(driver);
    uint64_t callerA=0,callerB=0;
    for(const auto& r:art.fixtureBindings())if(r.calls&&r.evidence.accepted){
        require(r.first.gate==NoticeGate::Bindings&&r.first.driverContext==r.first.sessionContext&&r.first.thread==r.first.ownerThread&&r.first.generation==1&&r.first.sequence==1&&r.first.kind==3,
            "Authenticated evidence lost its same-call ownership/weather context");
        require(r.evidence.hash=="3bd4b1d2d18b50ad64613ca80dc06102b52f56877788767d10a618dfa9147bac"&&r.evidence.resource&&r.evidence.hashAttempted,"Correlated original must retain actual GPU authentication");
        if(r.slot==5)callerA=r.first.caller;else if(r.slot==127)callerB=r.first.caller;else require(false,"Caller/slot association invented another slot");}
    require(callerA&&callerB&&callerA!=callerB,"Distinct real Prepare return addresses were not associated with their own slots");
    require(art.fixtureDropped()[3]>0&&art.fixtureDropped()[2]==0,"Unrelated observations consumed the original candidate budget");
    // A source hash/readback fault remains a failed observation; no binding
    // substitution or relaxed original hash is permitted even in this fixture.
    ComPtr<ID3D11Resource> resource;original->GetResource(&resource);D3D11_SHADER_RESOURCE_VIEW_DESC srv{};original->GetDesc(&srv);
    ComPtr<ID3D11ShaderResourceView> fresh;hr(device->CreateShaderResourceView(resource.Get(),&srv,&fresh));raw=fresh.Get();context->PSSetShaderResources(127,1,&raw);
    art.fixtureReadbackError=E_OUTOFMEMORY;observeCallerB(driver);art.fixtureReadbackError=S_OK;
    require(std::any_of(art.fixtureBindings().begin(),art.fixtureBindings().end(),[](const auto& r){return r.calls&&r.evidence.error==E_OUTOFMEMORY&&r.evidence.hashAttempted&&!r.evidence.accepted;}),"Readback failure HRESULT disappeared from its event");
    raw=original.Get();context->PSSetShaderResources(127,1,&raw);
    std::array<std::thread,4> readers;for(auto& thread:readers)thread=std::thread([&]{for(int i=0;i<100;i++)observeCallerA(driver);});for(auto& thread:readers)thread.join();
    uint64_t rejectedThreads=0;for(const auto& r:art.fixtureGates())if(r.calls&&r.first.gate==NoticeGate::Thread)rejectedThreads+=r.calls;
    require(rejectedThreads==400,"Rejected threads raced or disappeared from the bounded CPU diagnostics");
    for(const auto& r:art.fixtureBindings())if(r.calls)require(r.first.thread==r.first.ownerThread,"Rejected thread reached GPU binding observation");
    BYTE otherDriver[0x4000]{};auto contextPointer=reinterpret_cast<uint64_t>(context);std::memcpy(otherDriver+0x10,&contextPointer,8);
    {Scope outer;MaterialTicket ticket{&outer,driver,1,0};ticket.path=3;auto* previous=materialTicket;materialTicket=&ticket;observeCallerA(otherDriver);materialTicket=previous;}
    require(std::any_of(art.fixtureGates().begin(),art.fixtureGates().end(),[](const auto& r){return r.calls&&r.first.nestedMismatch&&r.first.gate==NoticeGate::Bindings;}),"Nested different-driver Prepare was silently omitted");
    uint64_t wrongContext=0;std::memcpy(otherDriver+0x10,&wrongContext,8);observeCallerA(otherDriver);
    require(std::any_of(art.fixtureGates().begin(),art.fixtureGates().end(),[](const auto& r){return r.calls&&r.first.gate==NoticeGate::Driver;}),"Different context was not rejected before GPU observation");
    require(observeCallerA(driver,99)==0,"Prepare failure result was changed");
    require(std::any_of(art.fixtureGates().begin(),art.fixtureGates().end(),[](const auto& r){return r.calls&&r.first.gate==NoticeGate::PrepareFailed;}),"Prepare result zero was not recorded");
    auto get=fixtureReadNotice;fixtureReadNotice=nullptr;observeCallerA(driver);fixtureReadNotice=get;
    require(std::any_of(art.fixtureGates().begin(),art.fixtureGates().end(),[](const auto& r){return r.calls&&r.first.gate==NoticeGate::Getter;}),"Missing environment getter was not distinguished");
    for(uint32_t mode=200;mode<280;mode++)observeCallerA(driver,mode);
    for(uint32_t i=0;i<20;i++){NoticeObservation event;event.id=art.nextObservation();event.caller=0x100+i;event.path=5;event.result=1;event.gate=NoticeGate::Thread;art.observe(event);}
    require(art.fixtureDropped()[0]>0&&art.fixtureDropped()[1]>0&&art.fixtureDropped()[2]>0,"Gate/caller/candidate overflow must be explicit");
    require(owned->swaps==swaps&&callbacks.load()==0&&callbackDepth==0,"Observation acquired Draw ownership or leaked callback tokens");
    ComPtr<ID3D11ShaderResourceView> bound;context->PSGetShaderResources(127,1,&bound);require(bound==original,"Observation changed the original binding");
    // Late callbacks remain counted but cannot acquire resources after Stop.
    owned->admission=false;{Scope late;require(!late.session,"Stopped session resources leaked into a late callback");}owned->admission=true;
    art.saveTraceAfterDrain();require(std::filesystem::file_size(path)<=256*1024,"Bounded trace exceeded the managed reader limit");
    if(!output)std::filesystem::remove(path);originalPrepare=prepareNoticeFixture;
    std::cerr<<"PASS correlated caller/slot/hash events; rejected-thread concurrency, nested driver, Prepare failure, readback fault, independent overflow and late callback admission\n";
}
ComPtr<ID3DBlob> compile(const char* source,const char* entry,const char* profile){
    ComPtr<ID3DBlob> code,errors;auto result=D3DCompile(source,strlen(source),nullptr,nullptr,nullptr,entry,profile,D3DCOMPILE_ENABLE_STRICTNESS,0,&code,&errors);
    if(errors)std::cerr<<static_cast<const char*>(errors->GetBufferPointer());hr(result);return code;
}
void noticeArtChecks(ID3D11Device* device,ID3D11DeviceContext* context,const SeasonSceneData& scene,const char* originalPath,const char* observationOutput=nullptr){
    require(scene.header()[1]==5||scene.header()[1]==6,"Scene has no disaster art");
    const BYTE* payload=scene.bytes.data()+scene.header()[14];require(validNoticeArt(payload,scene.header()[15]),"Generated art failed authentication");
    std::ifstream file(originalPath,std::ios::binary);std::vector<BYTE> original{std::istreambuf_iterator<char>(file),std::istreambuf_iterator<char>()};
    require(original.size()==1048576,"Original flood texture is missing");SeasonNoticeArt art(payload);
    const char code[]=R"(Texture2D<float4> S:register(t0);
        float4 vs(uint i:SV_VertexID):SV_Position{float2 p[3]={float2(-1,-1),float2(-1,3),float2(3,-1)};return float4(p[i],0,1);}
        float4 ps(float4 p:SV_Position):SV_Target{return S.Load(int3(432,324,0));})";
    auto v=compile(code,"vs","vs_5_0"),p=compile(code,"ps","ps_5_0");ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;
    hr(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs));hr(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&ps));
    const char fullCode[]=R"(Texture2D<float4> S:register(t0);float4 ps(float4 p:SV_Position):SV_Target{return S.Load(int3(int2(p.xy),0));})";
    auto fullBytecode=compile(fullCode,"ps","ps_5_0");ComPtr<ID3D11PixelShader> fullShader;hr(device->CreatePixelShader(fullBytecode->GetBufferPointer(),fullBytecode->GetBufferSize(),nullptr,&fullShader));
    D3D11_TEXTURE2D_DESC fullDesc{1024,1024,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    ComPtr<ID3D11Texture2D> fullColor,fullStaging;ComPtr<ID3D11RenderTargetView> fullTarget;hr(device->CreateTexture2D(&fullDesc,nullptr,&fullColor));hr(device->CreateRenderTargetView(fullColor.Get(),nullptr,&fullTarget));
    fullDesc.Usage=D3D11_USAGE_STAGING;fullDesc.BindFlags=0;fullDesc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&fullDesc,nullptr,&fullStaging));
    std::vector<BYTE> gpuOriginalAlpha;auto cpuOriginalAlpha=noticeBC3Alpha(original);
    auto readFull=[&](ID3D11RenderTargetView* restore){
        auto* target=fullTarget.Get();context->OMSetRenderTargets(1,&target,nullptr);D3D11_VIEWPORT viewport{0,0,1024,1024,0,1};context->RSSetViewports(1,&viewport);
        context->PSSetShader(fullShader.Get(),nullptr,0);context->Draw(3,0);context->CopyResource(fullStaging.Get(),fullColor.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(fullStaging.Get(),0,D3D11_MAP_READ,0,&mapped));std::vector<BYTE> rgba(1024*1024*4);
        for(UINT y=0;y<1024;y++)std::memcpy(rgba.data()+size_t(y)*4096,static_cast<BYTE*>(mapped.pData)+size_t(y)*mapped.RowPitch,4096);context->Unmap(fullStaging.Get(),0);
        context->OMSetRenderTargets(1,&restore,nullptr);viewport={0,0,1,1,0,1};context->RSSetViewports(1,&viewport);context->PSSetShader(ps.Get(),nullptr,0);return rgba;
    };
    BYTE gameState[0x80]{},driver[0x4000]{};uint32_t date=195|(4u<<16)|(21u<<24);std::memcpy(gameState+0x34,&date,4);
    auto contextPointer=reinterpret_cast<uint64_t>(context);std::memcpy(driver+0x10,&contextPointer,8);
    Bootstrap input{};input.state=reinterpret_cast<uint64_t>(gameState);input.context=contextPointer;
    auto tracePath=std::filesystem::temp_directory_path()/(L"San14Tenkazu-notice-kinds-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64())+L".json");
    require(!std::filesystem::exists(tracePath),"Trace fixture must never overwrite an existing file");
    auto owned=std::make_shared<Session>(input,true);owned->context=context;owned->device=device;owned->thread=GetCurrentThreadId();owned->noticeArt=std::make_unique<SeasonNoticeArt>(payload,tracePath.wstring());
    owned->pending.store(std::make_shared<TreeColorParameters>(TreeColorParameters{1,195,4,21,{1,0,0,0},true,false}));
    current.store(owned);noticeContext=context;fixtureReadNotice=readNoticeFixture;
    originalPrepare=prepareNoticeFixture;originalBatch=batchNoticeFixture;originalDraw=directNoticeFixture;originalIndexed=indexedNoticeFixture;originalMesh=meshNoticeFixture;originalLand=directNoticeFixture;
    for(UINT levels:{1u,9u})for(UINT gamma=0;gamma<2;gamma++){
        D3D11_TEXTURE2D_DESC d{1024,1024,levels,1,DXGI_FORMAT_BC3_TYPELESS,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
        std::array<std::vector<BYTE>,11> mipStorage;std::array<D3D11_SUBRESOURCE_DATA,11> data{};data[0]={original.data(),4096,1048576};
        for(UINT mip=1;mip<levels;mip++){UINT blocks=std::max(1u,(1024u>>mip)/4);mipStorage[mip].resize(size_t(blocks)*blocks*16);data[mip]={mipStorage[mip].data(),blocks*16,blocks*blocks*16};}
        ComPtr<ID3D11Texture2D> texture;hr(device->CreateTexture2D(&d,data.data(),&texture));
        D3D11_SHADER_RESOURCE_VIEW_DESC desc{};desc.Format=gamma?DXGI_FORMAT_BC3_UNORM_SRGB:DXGI_FORMAT_BC3_UNORM;desc.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;desc.Texture2D.MipLevels=levels;
        ComPtr<ID3D11ShaderResourceView> view;hr(device->CreateShaderResourceView(texture.Get(),&desc,&view));auto* raw=view.Get();context->PSSetShaderResources(0,1,&raw);
        D3D11_TEXTURE2D_DESC output{1,1,1,1,gamma?DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
        ComPtr<ID3D11Texture2D> color,staging;ComPtr<ID3D11RenderTargetView> target;hr(device->CreateTexture2D(&output,nullptr,&color));hr(device->CreateRenderTargetView(color.Get(),nullptr,&target));
        output.Usage=D3D11_USAGE_STAGING;output.BindFlags=0;output.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&output,nullptr,&staging));auto* rt=target.Get();context->OMSetRenderTargets(1,&rt,nullptr);
        D3D11_VIEWPORT vp{0,0,1,1,0,1};context->RSSetViewports(1,&vp);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);
        // Independent CPU decoding of the pinned BC3 block at (432,324):
        // 0005ffffffffffff11b56a83b2b8b6f5 -> RGBA (163,143,120,255).
        // Check the ORIGINAL pixels before substitution; uploading these bytes as
        // BC7 used to let every replacement-only test pass with the wrong format.
        context->Draw(3,0);context->CopyResource(staging.Get(),color.Get());
        D3D11_MAPPED_SUBRESOURCE baseline{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&baseline));
        const auto* originalPixel=static_cast<const BYTE*>(baseline.pData);
        bool originalMatches=std::abs(int(originalPixel[0])-163)<=2&&std::abs(int(originalPixel[1])-143)<=2&&std::abs(int(originalPixel[2])-120)<=2&&originalPixel[3]==255;
        context->Unmap(staging.Get(),0);require(originalMatches,"Original flood BC3 pixels differ from independent archive decoding");
        if(levels==1&&gamma==0){
            auto rgba=readFull(rt);gpuOriginalAlpha.resize(1024*1024);size_t transparent=0,partial=0;
            for(size_t pixel=0;pixel<gpuOriginalAlpha.size();pixel++){
                gpuOriginalAlpha[pixel]=rgba[pixel*4+3];require(std::abs(int(gpuOriginalAlpha[pixel])-cpuOriginalAlpha[pixel])<=1,"CPU BC3 alpha differs from independent original GPU decoding");
                if(pixel/1024<648&&pixel%1024<864){transparent+=gpuOriginalAlpha[pixel]==0;partial+=gpuOriginalAlpha[pixel]>0&&gpuOriginalAlpha[pixel]<255;}
            }
            require(transparent>=6000&&partial>=50000,"Original sprite lost its irregular transparent/feathered edge");
        }
        for(UINT kind=2;kind<=5;kind++){
            NoticeArtTicket ticket;require(art.enter(context,kind,ticket)==S_OK&&ticket.changed,"Owned disaster art was not substituted");
            ComPtr<ID3D11ShaderResourceView> substituted;context->PSGetShaderResources(0,1,&substituted);D3D11_SHADER_RESOURCE_VIEW_DESC replacementView{};substituted->GetDesc(&replacementView);
            require(replacementView.Texture2D.MipLevels==levels&&replacementView.Texture2D.MostDetailedMip==0,"Generated art view does not preserve the original mip range");
            if(levels==1&&gamma==0){
                auto rgba=readFull(rt);auto* painting=payload+32+size_t(kind-2)*1024*1024*4;
                for(size_t pixel=0;pixel<gpuOriginalAlpha.size();pixel++){
                    UINT expectedAlpha=(UINT(gpuOriginalAlpha[pixel])*painting[pixel*4+3]+127)/255;
                    require(std::abs(int(rgba[pixel*4+3])-int(expectedAlpha))<=1,"Replacement alpha did not retain original GPU-decoded silhouette");
                    require(rgba[pixel*4]==painting[pixel*4+2]&&rgba[pixel*4+1]==painting[pixel*4+1]&&rgba[pixel*4+2]==painting[pixel*4],"Silhouette preservation changed painting RGB");
                }
            }
            if(levels==9){
                ComPtr<ID3D11Resource> resource;substituted->GetResource(&resource);ComPtr<ID3D11Texture2D> image;hr(resource.As(&image));D3D11_TEXTURE2D_DESC desc{};image->GetDesc(&desc);
                require(desc.MipLevels==9,"Live nine-mip image was reduced to a single mip");desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                ComPtr<ID3D11Texture2D> readback;hr(device->CreateTexture2D(&desc,nullptr,&readback));context->CopyResource(readback.Get(),image.Get());
                D3D11_MAPPED_SUBRESOURCE level{};hr(context->Map(readback.Get(),1,D3D11_MAP_READ,0,&level));auto* sample=static_cast<const BYTE*>(level.pData)+162*level.RowPitch+216*4;
                auto* source=payload+32+size_t(kind-2)*1024*1024*4+(324*1024+432)*4;
                bool averaged=true;for(UINT channel=0;channel<4;channel++)averaged&=sample[channel]==(uint32_t(source[channel])+source[4+channel]+source[4096+channel]+source[4100+channel]+2)/4;
                for(UINT y=0;y<512;y++)for(UINT x=0;x<512;x++){
                    UINT sum=0;for(UINT dy=0;dy<2;dy++)for(UINT dx=0;dx<2;dx++){
                        size_t pixel=size_t(y*2+dy)*1024+x*2+dx;
                        sum+=(UINT(gpuOriginalAlpha[pixel])*payload[32+size_t(kind-2)*1024*1024*4+pixel*4+3]+127)/255;
                    }
                    auto actual=static_cast<const BYTE*>(level.pData)[size_t(y)*level.RowPitch+x*4+3];averaged&=std::abs(int(actual)-int((sum+2)/4))<=1;
                }
                context->Unmap(readback.Get(),1);require(averaged,"Generated art lower mip has incorrect GPU pixels or feathered alpha");
            }
            context->Draw(3,0);context->CopyResource(staging.Get(),color.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));auto* pixel=static_cast<const BYTE*>(mapped.pData);
            const BYTE* expected=payload+32+size_t(kind-2)*1024*1024*4+(324*1024+432)*4;
            bool matches=std::abs(int(pixel[0])-expected[2])<=2&&std::abs(int(pixel[1])-expected[1])<=2&&std::abs(int(pixel[2])-expected[0])<=2&&pixel[3]==expected[3];context->Unmap(staging.Get(),0);
            require(matches,"Rendered event art pixels or sRGB conversion differ");require(art.leave(context,ticket),"Original event texture binding was not restored");
            noticeOriginal=view.Get();noticeKind=kind;
            for(uint32_t path=0;path<4;path++){
                uint32_t start=24,end=start+18;std::memcpy(driver+0x2a34,&start,4);std::memcpy(driver+0x2a3c,&end,4);
                auto swaps=owned->swaps.load();
                noticeNested=path==3;if(path==0||path==3)onBatch(driver);else if(path==1)onDraw(driver,0,0,4);else onIndexed(driver,0,0,0,0,18);
                require(owned->swaps==swaps+1&&owned->swaps==owned->restores,"Owned GUI hook did not substitute and restore once");
                ComPtr<ID3D11ShaderResourceView> restored;context->PSGetShaderResources(0,1,&restored);require(restored.Get()==view.Get(),"GUI hook leaked its replacement binding");
                context->CopyResource(staging.Get(),color.Get());hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));pixel=static_cast<const BYTE*>(mapped.pData);
                matches=std::abs(int(pixel[0])-expected[2])<=2&&std::abs(int(pixel[1])-expected[1])<=2&&std::abs(int(pixel[2])-expected[0])<=2&&pixel[3]==expected[3];context->Unmap(staging.Get(),0);
                require(matches,"GUI hook drew the original image instead of the generated painting");
            }
        }
        NoticeArtTicket idle;require(art.enter(context,1,idle)==S_FALSE&&!idle.changed,"Original flood art was unexpectedly replaced");
        original[0]^=1;data[0].pSysMem=original.data();ComPtr<ID3D11Texture2D> foreign;hr(device->CreateTexture2D(&d,data.data(),&foreign));ComPtr<ID3D11ShaderResourceView> foreignView;hr(device->CreateShaderResourceView(foreign.Get(),&desc,&foreignView));raw=foreignView.Get();context->PSSetShaderResources(0,1,&raw);
        require(art.enter(context,2,idle)==S_FALSE&&!idle.changed,"Unrecognized GUI texture was replaced");original[0]^=1;
        auto wrongFormat=d;wrongFormat.Format=DXGI_FORMAT_BC7_TYPELESS;
        auto wrongViewDesc=desc;wrongViewDesc.Format=gamma?DXGI_FORMAT_BC7_UNORM_SRGB:DXGI_FORMAT_BC7_UNORM;
        ComPtr<ID3D11Texture2D> wrongTexture;hr(device->CreateTexture2D(&wrongFormat,data.data(),&wrongTexture));
        ComPtr<ID3D11ShaderResourceView> wrongView;hr(device->CreateShaderResourceView(wrongTexture.Get(),&wrongViewDesc,&wrongView));
        raw=wrongView.Get();context->PSSetShaderResources(0,1,&raw);
        require(art.enter(context,2,idle)==S_FALSE&&!idle.changed,"BC3 bytes uploaded as BC7 bypassed the source format contract");
        if(levels==9){desc.Texture2D.MostDetailedMip=1;desc.Texture2D.MipLevels=8;ComPtr<ID3D11ShaderResourceView> partial;hr(device->CreateShaderResourceView(texture.Get(),&desc,&partial));raw=partial.Get();context->PSSetShaderResources(0,1,&raw);
            require(art.enter(context,2,idle)==S_FALSE&&!idle.changed,"Nonzero base mip bypassed original-image authentication");}
    }
    auto beforeObservation=owned->swaps.load();noticeKind=3;
    context->PSSetShaderResources(5,1,&noticeOriginal);context->PSSetShaderResources(127,1,&noticeOriginal);
    onMesh(driver,0,0,0,0,4,1);onLand(driver,0,4,0);
    uint64_t differentContext=0;std::memcpy(driver+0x10,&differentContext,8);onMesh(driver,0,0,0,0,4,1);std::memcpy(driver+0x10,&contextPointer,8);
    require(onPrepare(driver,0)==1,"Standalone original Prepare result changed during observation");
    require(owned->swaps==beforeObservation,"Extra diagnostic paths must not substitute a texture");
    for(UINT slot:{0u,5u,127u}){ComPtr<ID3D11ShaderResourceView> unchanged;context->PSGetShaderResources(slot,1,&unchanged);require(unchanged.Get()==noticeOriginal,"Binding observation mutated a GPU resource slot");}
    owned->noticeArt->saveTraceAfterDrain();
    std::ifstream traceInput(tracePath);std::string trace{std::istreambuf_iterator<char>(traceInput),std::istreambuf_iterator<char>()};traceInput.close();
    for(auto name:{"kindCalls","kindSwaps","kindRestores"})require(trace.find(std::string("\"")+name+"\":[0,0,16,16,16,16]")!=std::string::npos,"Trace must distinguish all four actual GPU image kinds and their restored bindings");
    require(trace.find("\"pathCalls\":[0,1,2,48,16,1]")!=std::string::npos&&trace.find("\"matchingDriverCalls\":[0,1,1,48,16,1]")!=std::string::npos&&
        trace.find("\"otherDriverCalls\":[0,0,1,0,0,0]")!=std::string::npos&&trace.find("\"slots\":[33,9223372036854775808]")!=std::string::npos&&trace.find("\"traceLimitReached\":false")!=std::string::npos&&
        trace.find("\"unscopedCallers\":[{\"rva\":0,\"calls\":1}]")!=std::string::npos,
        "Diagnostic must distinguish the actual driver context, Mesh/Land and all 128 slots without changing substitution counts");
    std::filesystem::remove(tracePath);
    auto rejectedPath=tracePath;rejectedPath+=L".rejected";require(!std::filesystem::exists(rejectedPath),"Rejected trace must not overwrite evidence");
    SeasonNoticeArt rejected(payload,rejectedPath.wstring());
    D3D11_TEXTURE2D_DESC smallDesc{512,512,1,1,DXGI_FORMAT_BC3_UNORM,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
    D3D11_SUBRESOURCE_DATA smallData{original.data(),2048,262144};ComPtr<ID3D11Texture2D> smallTexture;
    hr(device->CreateTexture2D(&smallDesc,&smallData,&smallTexture));ComPtr<ID3D11ShaderResourceView> smallView;
    hr(device->CreateShaderResourceView(smallTexture.Get(),nullptr,&smallView));auto* smallRaw=smallView.Get();context->PSSetShaderResources(0,1,&smallRaw);
    for(int repeat=0;repeat<3;repeat++){NoticeArtTicket ticket;require(rejected.enter(context,3,ticket)==S_FALSE&&!ticket.changed,"Descriptor diagnostics must never admit a different-sized sprite");}
    ComPtr<ID3D11ShaderResourceView> unchanged;context->PSGetShaderResources(0,1,&unchanged);require(unchanged==smallView,"Rejected diagnostic changed the original binding");
    rejected.saveTraceAfterDrain();std::ifstream rejectedInput(rejectedPath);std::string rejectedTrace{std::istreambuf_iterator<char>(rejectedInput),std::istreambuf_iterator<char>()};rejectedInput.close();
    require(rejectedTrace.find("\"sha256\":\"30b330c45b8a389572fe6aa8d5de25f5910bba5c27f5b6c3fec034e8aa211d84\"")!=std::string::npos&&
        rejectedTrace.find("\"accepted\":false")!=std::string::npos&&rejectedTrace.find("\"hashAttempted\":true,\"readError\":0")!=std::string::npos,"Rejected BC3 trace must record real bounded GPU bytes without authenticating them");
    std::filesystem::remove(rejectedPath);
    auto descriptorsPath=tracePath;descriptorsPath+=L".descriptors";require(!std::filesystem::exists(descriptorsPath),"Descriptor trace must not overwrite evidence");
    SeasonNoticeArt descriptors(payload,descriptorsPath.wstring());
    D3D11_TEXTURE2D_DESC arrayDesc{32,16,1,2,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_SHADER_RESOURCE,0,0};
    ComPtr<ID3D11Texture2D> arrayTexture;hr(device->CreateTexture2D(&arrayDesc,nullptr,&arrayTexture));ComPtr<ID3D11ShaderResourceView> arrayView;
    hr(device->CreateShaderResourceView(arrayTexture.Get(),nullptr,&arrayView));auto* arrayRaw=arrayView.Get();context->PSSetShaderResources(0,1,&arrayRaw);
    descriptors.observeBindings(context,3);NoticeArtTicket arrayTicket;
    require(descriptors.enter(context,3,arrayTicket)==S_FALSE&&!arrayTicket.changed,"Uncompressed array descriptor must never admit a replacement");
    context->PSGetShaderResources(0,1,unchanged.ReleaseAndGetAddressOf());require(unchanged==arrayView,"Descriptor observation changed an uncompressed array binding");
    std::vector<ComPtr<ID3D11ShaderResourceView>> unrelatedViews;
    for(UINT i=0;i<130;i++){ComPtr<ID3D11ShaderResourceView> extra;hr(device->CreateShaderResourceView(arrayTexture.Get(),nullptr,&extra));auto* ptr=extra.Get();context->PSSetShaderResources(0,1,&ptr);descriptors.observeBindings(context,3);unrelatedViews.push_back(std::move(extra));}
    context->PSSetShaderResources(0,1,&smallRaw);NoticeArtTicket afterFull;
    require(descriptors.enter(context,3,afterFull)==S_FALSE&&!afterFull.changed,"Full descriptor observation must never admit a rejected BC3 sprite");
    descriptors.saveTraceAfterDrain();std::ifstream descriptorsInput(descriptorsPath);std::string descriptorTrace{std::istreambuf_iterator<char>(descriptorsInput),std::istreambuf_iterator<char>()};descriptorsInput.close();
    require(descriptorTrace.find("\"viewFormat\":28,\"dimension\":5")!=std::string::npos&&descriptorTrace.find("\"width\":32,\"height\":16,\"textureFormat\":28,\"mips\":1,\"array\":2")!=std::string::npos&&
        descriptorTrace.find("\"accepted\":false,\"sha256\":\"\",\"hashAttempted\":false,\"readError\":0")!=std::string::npos,"Unsupported format/array descriptors must be recorded without readback or source authentication");
    require(descriptorTrace.find("\"bc3TraceLimitReached\":false,\"otherTraceLimitReached\":true")!=std::string::npos&&descriptorTrace.find("\"sha256\":\"30b330c45b8a389572fe6aa8d5de25f5910bba5c27f5b6c3fec034e8aa211d84\"")!=std::string::npos,"Unrelated descriptors must not exhaust the independent BC3 evidence budget");
    std::filesystem::remove(descriptorsPath);
    noticeObservationChecks(owned,device,context,driver,payload,observationOutput);
    causalChecks(owned,context,driver);
    current.store(nullptr);fixtureReadNotice=nullptr;noticeContext=nullptr;noticeOriginal=nullptr;
    uint64_t mipBytes=0;for(UINT mip=0;mip<9;mip++)mipBytes+=uint64_t(1024>>mip)*(1024>>mip)*4;
    context->ClearState();require(art.gpuBytes()==8ull*(1024*1024*4+mipBytes),"Event art cache accounting differs");
    std::cerr<<"PASS 64 Batch/Draw/Indexed/nested GUI substitutions with one and nine mips, generated pixels and exact original binding restoration\n";
    std::cerr<<"PASS original BC3 flood pixels match independent CPU decoding; identical payload with BC7 descriptor rejected\n";
    std::cerr<<"PASS whole original GPU alpha versus BC3 decoding, all four paintings retain original irregular/feathered silhouette and RGB; lower mip alpha retains mask\n";
    std::cerr<<"PASS four authenticated paintings, both gamma variants, lower-mip GPU pixels, unknown sprite/nonzero base mip rejection and original binding restoration\n";
    std::cerr<<"PASS rejected 512 BC3 GPU hash is diagnostic only; repeated entries preserve the original binding and remain unadmitted\n";
    std::cerr<<"PASS diagnostic-only Mesh/Land and all 128 binding slots: authenticated source recorded, bindings and production swap counts unchanged\n";
    std::cerr<<"PASS actual driver-context mismatch counters and uncompressed texture-array descriptors; no binding change, readback or admission for unsupported formats\n";
    std::cerr<<"PASS unscoped Prepare observes authenticated original source without substitution; independent BC3 evidence survives unrelated descriptor saturation\n";
}
}
int main(int argc,char** argv){try{
    require(argc>=2&&argc<=5,"Usage: SeasonSceneChecks scene-data.bin [managed-calendar-vectors.txt] [flood-texture.bin] [observation.json]");SeasonSceneData data;
    std::ifstream input(argv[1],std::ios::binary);data.bytes.assign(std::istreambuf_iterator<char>(input),std::istreambuf_iterator<char>());
    auto started=std::chrono::steady_clock::now();auto prepared=data.prepare();if(!data.compileDiagnostic.empty())std::cerr<<data.compileDiagnostic;hr(prepared);
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    if(argc>=4)noticeArtChecks(device.Get(),context.Get(),data,argv[3],argc==5?argv[4]:nullptr);
    constexpr UINT width=128,height=64;SeasonSceneGpu scene(data);
    BYTE gameState[0x80]{};Bootstrap calendarInput{};calendarInput.state=reinterpret_cast<uint64_t>(gameState);
    Session calendarSession(calendarInput,true);
    auto request=std::make_shared<TreeColorParameters>(TreeColorParameters{17,195,2,11,{1.f/3,0,0,2.f/3},true});
    calendarSession.pending.store(request);
    auto setDate=[&](uint32_t y,uint32_t m,uint32_t d){uint32_t v=y|(m<<16)|(d<<24);std::memcpy(gameState+0x34,&v,4);};
    size_t calendarCases=0;
    if(argc>=3){
        std::ifstream vectors(argv[2]);require(bool(vectors),"Managed calendar vectors missing");uint32_t y,m,d;double expected[4];
        while(vectors>>y>>m>>d>>expected[0]>>expected[1]>>expected[2]>>expected[3]){
            setDate(y,m,d);TreeColorParameters rendered{};require(renderParameters(calendarSession,request,rendered),"Full effect dropped between controller updates");
            require(rendered.year==y&&rendered.month==m&&rendered.day==d&&rendered.epoch==request->epoch,"Actual calendar or request identity changed");
            for(UINT i=0;i<4;i++)require(std::abs(rendered.weights[i]-expected[i])<1e-6,"Native weights differ from managed calendar");++calendarCases;
        }
        require(vectors.eof()&&calendarCases==720,"Managed calendar vector count differs");
        for(auto invalid:std::array<std::array<uint32_t,3>,6>{{{100,2,1},{400,2,1},{195,0,1},{195,13,1},{195,2,0},{195,2,31}}}){
            setDate(invalid[0],invalid[1],invalid[2]);TreeColorParameters p{};require(!renderParameters(calendarSession,request,p),"Invalid live date was accepted");
        }
        setDate(195,3,1);auto scoped=std::make_shared<TreeColorParameters>(*request);scoped->full=false;TreeColorParameters p{};
        require(!renderParameters(calendarSession,scoped,p),"Scoped date guard was weakened");
        require(!renderParameters(calendarSession,{},p),"Unrequested rendering was admitted");
        calendarSession.admission=false;require(!renderRequestActive(calendarSession,request),"Stopped session admitted rendering");calendarSession.admission=true;
    }
    const float* climate=reinterpret_cast<const float*>(data.bytes.data()+data.header()[5]);
    UINT chosen=0;float best=-1;
    for(UINT i=0;i<256*256;i++){
        const float* c=climate+(256*256+i)*2;float snow=(c[0]<0?1.f:0.f)*std::min(c[1]/40,1.f);
        if(snow>best){best=snow;chosen=i;}
    }
    require(best>0,"The authentic atlas has no cold winter fixture");float x=132364,z=24683.697f;
    // A wide top-down view covers the production 1600-world-unit snow cells.
    // Positive height is closer under LESS depth; all 800..7800 heights fit.
    float matrix[16]={1.f/16000,0,0,0,0,0,-.00003f,0,0,-1.f/8000,0,0,-x/16000-1,z/8000+1,.5f,1};
    auto buffer=[&](UINT size,const void* contents){D3D11_BUFFER_DESC bd{size,D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA initial{contents,0,0};ComPtr<ID3D11Buffer> b;hr(device->CreateBuffer(&bd,contents?&initial:nullptr,&b));return b;};
    auto camera=buffer(64,matrix),sentinel=buffer(64,matrix);auto* raw=camera.Get();context->VSSetConstantBuffers(0,1,&raw);
    raw=sentinel.Get();context->PSSetConstantBuffers(12,1,&raw);
    std::array<ComPtr<ID3D11Texture2D>,5> colors;std::array<ComPtr<ID3D11RenderTargetView>,5> targets;ID3D11RenderTargetView* rtvs[5];
    D3D11_TEXTURE2D_DESC td{width,height,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    for(UINT i=0;i<5;i++){hr(device->CreateTexture2D(&td,nullptr,&colors[i]));hr(device->CreateRenderTargetView(colors[i].Get(),nullptr,&targets[i]));rtvs[i]=targets[i].Get();}
    ComPtr<ID3D11Texture2D> staging,depth;ComPtr<ID3D11DepthStencilView> dsv;
    td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&td,nullptr,&staging));
    td.Format=DXGI_FORMAT_D32_FLOAT;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_DEPTH_STENCIL;td.CPUAccessFlags=0;hr(device->CreateTexture2D(&td,nullptr,&depth));hr(device->CreateDepthStencilView(depth.Get(),nullptr,&dsv));
    D3D11_DEPTH_STENCIL_DESC dd{};dd.DepthEnable=TRUE;dd.DepthFunc=D3D11_COMPARISON_LESS;dd.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL;
    ComPtr<ID3D11DepthStencilState> depthState;hr(device->CreateDepthStencilState(&dd,&depthState));context->OMSetDepthStencilState(depthState.Get(),23);
    D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;ComPtr<ID3D11RasterizerState> raster;hr(device->CreateRasterizerState(&rd,&raster));context->RSSetState(raster.Get());
    D3D11_VIEWPORT vp{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&vp);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    const char* vertex=R"(cbuffer Fixture:register(b1){float4 Origin;};struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float4 b:TEXCOORD1;float4 c:TEXCOORD2;float4 w:TEXCOORD3;float3 t:TEXCOORD4;float3 n:TEXCOORD5;float2 uv:TEXCOORD6;};O main(uint id:SV_VertexID){float2 v=float2((id<<1)&2,id&2);O o;o.p=float4(v*float2(2,-2)+float2(-1,1),.5,1);o.a=float4(0,0,4,16);o.b=float4(1000,0,0,1);o.c=float4(v.x,0,v.y,1);o.w=float4(v.x*Origin.y+Origin.x,0,v.y*Origin.w+Origin.z,1);o.t=float3(1,0,0);o.n=float3(0,1,0);o.uv=v;return o;})";
    auto vsCode=compile(vertex,"main","vs_5_0");ComPtr<ID3D11VertexShader> vs;hr(device->CreateVertexShader(vsCode->GetBufferPointer(),vsCode->GetBufferSize(),nullptr,&vs));context->VSSetShader(vs.Get(),nullptr,0);
    float origin[4]={x,32000,z,16000};auto originCb=buffer(16,origin);raw=originCb.Get();context->VSSetConstantBuffers(1,1,&raw);
    ComPtr<ID3D11PixelShader> baseline;hr(device->CreatePixelShader(data.bytes.data()+data.header()[11],data.detailedShaderBytes(),nullptr,&baseline));context->PSSetShader(baseline.Get(),nullptr,0);
    std::array<float,1027*4> cb{};cb[4]=cb[5]=cb[6]=1;cb[7]=2;cb[8]=cb[13]=cb[18]=1;cb[21]=4096;cb[22]=50;cb[24]=.6f;cb[26]=1000;cb[27]=4000;
    for(UINT i=0;i<255;i++){cb[(262+i)*4]=.5f;cb[(517+i)*4]=100;cb[(772+i)*4]=120;}
    auto constants=buffer(sizeof(cb),cb.data());raw=constants.Get();context->PSSetConstantBuffers(0,1,&raw);
    std::array<ComPtr<ID3D11ShaderResourceView>,8> inputs;
    for(UINT slot=3;slot<=10;slot++){
        UINT layers=slot>=7&&slot<=9?19:1;float pixel[4]={.2f,.4f,.1f,.6f};if(slot==3)pixel[0]=pixel[1]=pixel[2]=0;if(slot==8){pixel[0]=pixel[1]=.5f;pixel[2]=1;}if(slot==10)pixel[0]=pixel[1]=pixel[2]=1;
        D3D11_SUBRESOURCE_DATA init[19];for(auto& d:init)d={pixel,16,0};D3D11_TEXTURE2D_DESC d{1,1,1,layers,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};ComPtr<ID3D11Texture2D> t;hr(device->CreateTexture2D(&d,init,&t));hr(device->CreateShaderResourceView(t.Get(),nullptr,&inputs[slot-3]));auto* v=inputs[slot-3].Get();context->PSSetShaderResources(slot,1,&v);
    }
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MaxLOD=D3D11_FLOAT32_MAX;
    ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));auto* ss=sampler.Get();for(UINT slot=3;slot<=12;slot++)context->PSSetSamplers(slot,1,&ss);
    auto pixels=[&]{context->CopyResource(staging.Get(),colors[0].Get());D3D11_MAPPED_SUBRESOURCE m{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&m));std::vector<BYTE> result(width*height*4);for(UINT y=0;y<height;y++)memcpy(result.data()+y*width*4,static_cast<BYTE*>(m.pData)+y*m.RowPitch,width*4);context->Unmap(staging.Get(),0);return result;};
    auto setup=[&]{float clear[4]={0,0,0,0};for(auto& r:targets)context->ClearRenderTargetView(r.Get(),clear);context->ClearDepthStencilView(dsv.Get(),D3D11_CLEAR_DEPTH,1,0);context->OMSetRenderTargets(5,rtvs,dsv.Get());};
    setup();context->Draw(3,0);auto original=pixels();std::vector<std::vector<BYTE>> seasons;UINT weatherChanged=0;
    {TreeColorParameters p{1,195,2,1,{0,0,0,1}};p.full=true;SeasonSceneTicket ticket;
        require(scene.enter(context.Get(),p,5,0,ticket)==S_FALSE&&!ticket.changed,"Distant terrain used an unavailable material table");}
    for(UINT season=0;season<4;season++){
        TreeColorParameters p{season+1,195,season==0?4u:season==1?7u:season==2?10u:2u,1,{}};p.weights[season]=1;p.full=true;
        setup();SeasonSceneTicket ticket;require(scene.enter(context.Get(),p,3,0,ticket)==S_OK,"Terrain entry rejected");context->Draw(3,0);require(scene.leave(context.Get(),ticket),"Terrain shader/binding restoration failed");seasons.push_back(pixels());
        require(seasons.back()!=original,"Terrain season has no effect in the independent fixture");
        ComPtr<ID3D11Buffer> saved;context->PSGetConstantBuffers(12,1,&saved);require(saved==sentinel,"Private binding sentinel changed");
        for(UINT kind:{1u,2u}){SeasonSceneTicket tree;require(scene.enter(context.Get(),p,kind,0,tree)==S_OK,"Tree pipeline entry rejected");require(scene.leave(context.Get(),tree),"Tree pipeline restoration failed");}
        environment::WeatherSnapshot weather;weather.generation=17;weather.sequence=1;weather.date=p.year|(p.month<<16)|(p.day<<24);weather.flags=192;
        std::fill(std::begin(weather.regions),std::end(weather.regions),uint8_t(1));weather.weather[0]=season==3?8:0;scene.fixtureWeather(weather);
        context->OMSetRenderTargets(1,rtvs,nullptr);auto before=pixels();require(scene.weather(context.Get(),p,targets[0].Get())==S_OK,"Weather pipeline rejected");auto after=pixels();
        if(before!=after)weatherChanged++;if(season==3)require(before!=after,"Winter particles did not draw");
        ComPtr<ID3D11PixelShader> restored;context->PSGetShader(&restored,nullptr,nullptr);require(restored==baseline,"Weather shader restoration failed");
        ComPtr<ID3D11VertexShader> restoredVs;context->VSGetShader(&restoredVs,nullptr,nullptr);require(restoredVs==vs,"Weather vertex shader restoration failed");
        ComPtr<ID3D11DepthStencilState> restoredDepth;UINT stencil{};context->OMGetDepthStencilState(&restoredDepth,&stencil);require(restoredDepth==depthState&&stencil==23,"Weather depth state restoration failed");
        setup();context->Draw(3,0);require(pixels()==original,"OFF original pixels differ");
    }
    for(UINT i=0;i<4;i++)for(UINT j=0;j<i;j++)require(seasons[i]!=seasons[j],"Four-season terrain outputs are not distinct");
    // The distant vertex contract has no world-position interpolator. Its
    // pixel depth must reconstruct the same world coordinates as detailed land.
    const char* distantVertex=R"(struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float3 t:TEXCOORD1;float3 n:TEXCOORD2;float2 uv:TEXCOORD3;};
O main(uint id:SV_VertexID){float2 v=float2((id<<1)&2,id&2);O o;o.p=float4(v*float2(2,-2)+float2(-1,1),.5,1);
o.a=float4(1000,0,0,1);o.t=float3(1,0,0);o.n=float3(0,1,0);o.uv=v;return o;})";
    auto farCode=compile(distantVertex,"main","vs_5_0");ComPtr<ID3D11VertexShader> farVs;hr(device->CreateVertexShader(farCode->GetBufferPointer(),farCode->GetBufferSize(),nullptr,&farVs));
    ComPtr<ID3D11PixelShader> farBaseline;auto detailed=data.detailedShaderBytes();hr(device->CreatePixelShader(data.bytes.data()+data.header()[11]+detailed,data.header()[12]-detailed,nullptr,&farBaseline));
    std::array<float,261*4> farCb{};farCb[0]=farCb[1]=farCb[2]=1;farCb[3]=2;farCb[4]=farCb[9]=farCb[14]=1;farCb[17]=4096;farCb[18]=50;farCb[20]=.6f;
    for(UINT i=0;i<255;i++)farCb[(6+i)*4]=100;
    auto farConstants=buffer(sizeof(farCb),farCb.data());raw=farConstants.Get();context->PSSetConstantBuffers(0,1,&raw);
    context->VSSetShader(farVs.Get(),nullptr,0);context->PSSetShader(farBaseline.Get(),nullptr,0);
    setup();context->Draw(3,0);auto farOriginal=pixels();
    std::vector<std::vector<BYTE>> farSeasons;
    for(UINT season=0;season<4;season++){
        TreeColorParameters p{season+5,195,season==0?4u:season==1?7u:season==2?10u:2u,1,{}};p.weights[season]=1;p.full=true;
        SeasonSceneTicket farTicket;setup();require(scene.enter(context.Get(),p,5,0,farTicket)==S_OK,"Distant terrain entry rejected");context->Draw(3,0);
        require(scene.leave(context.Get(),farTicket),"Distant terrain bindings not restored");farSeasons.push_back(pixels());
        require(farSeasons.back()!=farOriginal,"Distant terrain season has no effect");
        setup();context->Draw(3,0);require(pixels()==farOriginal,"Distant terrain OFF did not restore original pixels");
    }
    for(UINT i=0;i<4;i++)for(UINT j=0;j<i;j++)require(farSeasons[i]!=farSeasons[j],"Four-season distant outputs are not distinct");
    UINT minimumSummerWind=width*height,summerZoomCases=0,maximumSummerLodError=0;
    TreeColorParameters summer{19,195,7,1,{0,1,0,0},true};
    auto summerFrame=[&](UINT kind,float seconds,bool foliage=true,ID3D11ShaderResourceView* detail=nullptr){
        auto* currentCb=kind==3?constants.Get():farConstants.Get();context->PSSetConstantBuffers(0,1,&currentCb);
        context->VSSetShader(kind==3?vs.Get():farVs.Get(),nullptr,0);context->PSSetShader(kind==3?baseline.Get():farBaseline.Get(),nullptr,0);
        // Sentinels exercise restoration of the two new private source slots.
        ID3D11ShaderResourceView* sentinels[]={inputs[1].Get(),inputs[2].Get()};context->PSSetShaderResources(104,2,sentinels);
        setup();scene.fixtureTime(seconds);SeasonSceneTicket ticket;
        require(scene.enter(context.Get(),summer,kind,0,ticket,foliage?inputs[0].Get():nullptr,
            foliage?(detail?detail:inputs[4].Get()):nullptr)==S_OK,"Summer terrain entry failed");
        context->Draw(3,0);require(scene.leave(context.Get(),ticket),"Summer private source/sampler bindings not restored");return pixels();
    };
    for(float span:{10000.f,32000.f,96000.f}){
        float zoomMatrix[16];memcpy(zoomMatrix,matrix,sizeof(matrix));zoomMatrix[0]=2/span;zoomMatrix[9]=-4/span;
        zoomMatrix[12]=-2*x/span-1;zoomMatrix[13]=4*z/span+1;
        context->UpdateSubresource(camera.Get(),0,nullptr,zoomMatrix,0,0);
        float zoomOrigin[]={x,span,z,span*.5f};context->UpdateSubresource(originCb.Get(),0,nullptr,zoomOrigin,0,0);
        auto near0=summerFrame(3,0),far0=summerFrame(5,0),near1=summerFrame(3,2),far1=summerFrame(5,2);
        for(UINT kind=0;kind<2;kind++){
            const auto& first=kind?far0:near0;const auto& second=kind?far1:near1;UINT moving=0;
            for(UINT i=0;i<width*height;i++){
                require(first[i*4+3]==second[i*4+3],"Summer canopy changes alpha");moving+=memcmp(first.data()+i*4,second.data()+i*4,3)!=0;
                for(UINT c=0;c<3;c++)maximumSummerLodError=std::max(maximumSummerLodError,UINT(abs(int(near0[i*4+c])-int(far0[i*4+c]))));
            }
            require(moving>width*height/4,"Summer wind disappears at the tested zoom");minimumSummerWind=std::min(minimumSummerWind,moving);++summerZoomCases;
            std::cerr<<"SummerWorldSpan="<<span<<"; terrainKind="<<(kind?5:3)<<"; windPixels="<<moving<<"\n";
        }
        // Summer composition must not warp any original scene pixels. There
        // are no collected plants here, and summer has no snowfall.
        context->OMSetRenderTargets(1,rtvs,nullptr);auto beforeWeather=pixels();hr(scene.weather(context.Get(),summer,targets[0].Get()));
        require(pixels()==beforeWeather,"Summer weather distorts the scene after haze removal");
    }
    require(maximumSummerLodError<=2,"Summer canopy has a detailed/distant seam");
    context->UpdateSubresource(camera.Get(),0,nullptr,matrix,0,0);context->UpdateSubresource(originCb.Get(),0,nullptr,origin,0,0);
    scene.fixtureTime(0);
    for(uint32_t layer:{1u,4u,6u,18u}){
        memcpy(&cb[7*4],&layer,4);context->UpdateSubresource(constants.Get(),0,nullptr,cb.data(),0,0);
        require(summerFrame(3,0)==summerFrame(3,0,false),"Summer canopy leaked into excluded terrain");
    }
    uint32_t farmLayer=5;memcpy(&cb[7*4],&farmLayer,4);context->UpdateSubresource(constants.Get(),0,nullptr,cb.data(),0,0);
    float bankPixel[]={.8f,.8f,.75f,1};D3D11_SUBRESOURCE_DATA bankData[19];for(auto& initial:bankData)initial={bankPixel,16,0};
    D3D11_TEXTURE2D_DESC bankDesc{1,1,1,19,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
    ComPtr<ID3D11Texture2D> bank;ComPtr<ID3D11ShaderResourceView> bankView;hr(device->CreateTexture2D(&bankDesc,bankData,&bank));hr(device->CreateShaderResourceView(bank.Get(),nullptr,&bankView));
    require(summerFrame(3,0,true,bankView.Get())==summerFrame(3,0,false),"Summer canopy colored pale farm banks");
    uint32_t restoredLayer=0;memcpy(&cb[7*4],&restoredLayer,4);context->UpdateSubresource(constants.Get(),0,nullptr,cb.data(),0,0);
    std::cerr<<"SummerZoomCases="<<summerZoomCases<<"; minimumWindPixels="<<minimumSummerWind<<"; maximumLodError="<<maximumSummerLodError<<"; summerWeatherUnchanged=true\n";
    const auto& farWinter=farSeasons[3];
    // Keep one stale controller request while the actual clock advances across
    // winter/spring, a year boundary, and a backward load. Compare GPU pixels to
    // an independently requested current-date draw for both terrain contracts.
    constexpr uint32_t dateSteps[][3]={{195,2,11},{195,2,12},{195,2,16},{195,2,29},{195,2,30},{195,3,1},{195,12,30},{196,1,1},{195,2,11}};
    size_t continuousDraws=0;
    for(const auto& date:dateSteps){
        setDate(date[0],date[1],date[2]);TreeColorParameters actual{};
        require(renderParameters(calendarSession,request,actual),"Calendar gap rejected a terrain frame");
        auto fresh=std::make_shared<TreeColorParameters>(actual);fresh->epoch=100+continuousDraws;
        TreeColorParameters acknowledged{};require(renderParameters(calendarSession,fresh,acknowledged),"Current-date fixture failed");
        for(UINT kind:{3u,5u}){
            raw=kind==3?constants.Get():farConstants.Get();context->PSSetConstantBuffers(0,1,&raw);
            context->VSSetShader(kind==3?vs.Get():farVs.Get(),nullptr,0);context->PSSetShader(kind==3?baseline.Get():farBaseline.Get(),nullptr,0);
            auto drawDate=[&](const TreeColorParameters& p){setup();SeasonSceneTicket t;require(scene.enter(context.Get(),p,kind,0,t)==S_OK,"Calendar terrain entry failed");context->Draw(3,0);require(scene.leave(context.Get(),t),"Calendar GPU state not restored");return pixels();};
            auto expected=drawDate(acknowledged),observed=drawDate(actual);
            require(observed==expected,"Stale controller changed actual-date terrain pixels");
            require(observed!=(kind==3?original:farOriginal),"Terrain reverted to OFF while advancing dates");++continuousDraws;
        }
        completedMaterialPass(calendarSession,actual,4);
        require(calendarSession.fullClock==packedDate(actual)&&calendarSession.fullEpoch==request->epoch,"Frame pass calendar was not updated");
        require(calendarSession.completedEpoch==0,"Unacknowledged request manufactured a completion");
    }
    raw=farConstants.Get();context->PSSetConstantBuffers(0,1,&raw);context->VSSetShader(farVs.Get(),nullptr,0);context->PSSetShader(farBaseline.Get(),nullptr,0);
    auto climateAt=[&](double wx,double wz,UINT channel){
        double px=wx/800-.5,pz=wz/800-.5;int ix=int(std::floor(px)),iz=int(std::floor(pz));double fx=px-ix,fz=pz-iz;
        auto texel=[&](int xx,int zz){return climate[(256*256+std::clamp(zz,0,255)*256+std::clamp(xx,0,255))*2+channel];};
        return (texel(ix,iz)*(1-fx)+texel(ix+1,iz)*fx)*(1-fz)+(texel(ix,iz+1)*(1-fx)+texel(ix+1,iz+1)*fx)*fz;};
    double maxTransitionError=0;
    for(UINT yy=0;yy<height;yy++)for(UINT xx=0;xx<width;xx++){
        double wx=x+(xx+.5)*32000/width,wz=z+(yy+.5)*16000/height;
        double t=std::clamp(climateAt(wx,wz,0)/8,0.0,1.0),snow=(1-t*t*(3-2*t))*(.3+.7*std::clamp(climateAt(wx,wz,1)/40,0.0,1.0));
        for(UINT c=0;c<3;c++){size_t at=(yy*width+xx)*4+c;
            double expected=(int(farOriginal[at])-int(original[at]))*(1-snow),actual=int(farWinter[at])-int(seasons[3][at]);
            maxTransitionError=std::max(maxTransitionError,std::abs(actual-expected));}
    }
    require(maxTransitionError<=2,"Detailed/distant snow differs at the same world position");
    setup();context->Draw(3,0);require(pixels()==farOriginal,"Distant terrain OFF did not restore original pixels");
    // Standalone precipitation shares a region map, leaves original terrain intact,
    // changes with time and cannot affect an adjacent clear province.
    context->UpdateSubresource(camera.Get(),0,nullptr,matrix,0,0);raw=constants.Get();context->PSSetConstantBuffers(0,1,&raw);
    context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(baseline.Get(),nullptr,0);
    TreeColorParameters weatherOnly{27,195,7,1,{0,1,0,0},true,false};environment::WeatherSnapshot regional;
    regional.generation=17;regional.sequence=2;regional.date=195|(7u<<16)|(1u<<24);regional.flags=192;
    for(UINT tile=0;tile<environment::TileCount;tile++)regional.regions[tile]=tile%220<155?1:2;
    auto precipitation=[&](uint8_t code,float seconds,uint32_t flags=192u,uint32_t city=1u){
        std::memset(regional.weather,0,sizeof(regional.weather));regional.weather[city-1]=code;regional.flags=flags;scene.fixtureWeather(regional);scene.fixtureTime(seconds);
        setup();context->Draw(3,0);auto before=pixels();SeasonSceneTicket ticket;
        require(scene.enter(context.Get(),weatherOnly,3,0,ticket)==S_OK&&ticket.observed&&!ticket.changed,"Weather-only entry modified a seasonal material");
        context->Draw(3,0);require(scene.leave(context.Get(),ticket)&&pixels()==before,"Weather-only changed original terrain pixels");
        context->OMSetRenderTargets(1,rtvs,nullptr);hr(scene.weather(context.Get(),weatherOnly,targets[0].Get()));auto after=pixels();
        for(UINT yy=0;yy<height;yy++)for(UINT xx=100;xx<width;xx++)require(!memcmp(after.data()+(yy*width+xx)*4,before.data()+(yy*width+xx)*4,4),"Precipitation leaked into the clear region");
        ComPtr<ID3D11PixelShader> restored;context->PSGetShader(&restored,nullptr,nullptr);require(restored==baseline,"Standalone weather did not restore the original shader");
        return std::pair{before,after};
    };
    auto dry=precipitation(0,0),rain0=precipitation(2,0),rain1=precipitation(2,.37f),snow0=precipitation(8,0),snow1=precipitation(8,.37f),rainOff=precipitation(2,0,128),snowOff=precipitation(8,0,64);
    require(dry.first==dry.second&&rainOff.first==rainOff.second&&snowOff.first==snowOff.second,"Disabled or dry weather changed scene pixels");
    require(rain0.first!=rain0.second&&snow0.first!=snow0.second&&rain0.second!=rain1.second&&snow0.second!=snow1.second,"Regional rain/snow failed actual animated GPU output");
    require(rain0.second!=snow0.second,"Rain and snowfall have identical appearance");
    std::cerr<<"PASS regional rain/snow animated GPU pixels, clear region, independent seasonal OFF, individual visual flags, original state restoration\n";
    for(UINT tile=0;tile<environment::TileCount;tile++)regional.regions[tile]=tile%220<155?51:50;
    auto addedRain=precipitation(2,.37f,192,51),addedSnow=precipitation(8,.37f,192,51);
    require(addedRain.first==addedRain.second&&addedSnow.first==addedSnow.second,"Excluded city 51 changed GPU pixels");
    for(UINT city=47;city<=51;city++){
        std::fill(std::begin(regional.regions),std::end(regional.regions),static_cast<uint8_t>(city));
        for(UINT k=0;k<5;k++)regional.recentDates[(city-1)*5+k]=regional.date;
        auto excluded=precipitation(10,.37f,1023,city);
        require(excluded.first==excluded.second,"Excluded city weather/disaster marks changed actual GPU pixels");
    }
    std::memset(regional.recentDates,0,sizeof(regional.recentDates));
    std::cerr<<"PASS cities 47-51 exclude rain/snow and all five disaster GPU outputs, original state restoration\n";
    for(UINT tile=0;tile<environment::TileCount;tile++)regional.regions[tile]=tile%220<155?1:2;
    std::memset(regional.weather,0,sizeof(regional.weather));
    // Indicators use actual receipt dates and render without precipitation or seasonal materials.
    for(UINT tile=0;tile<environment::TileCount;tile++){
        float col=float(tile%220),row=float(tile/220),wx=37126+(1.5f*col+1)*468,wz=12930+1.7320508075688772f*(row+(int(col)&1?.5f:1.f))*468;
        regional.regions[tile]=wx>=133000&&wx<137000&&wz>=26000&&wz<30000?1:2;
    }
    auto indicator=[&](uint32_t flags,uint8_t code,uint32_t date){
        regional.flags=flags;regional.weather[0]=code;for(UINT kind=0;kind<5;kind++)regional.recentDates[kind]=date;
        scene.fixtureWeather(regional);scene.fixtureTime(0);setup();context->Draw(3,0);auto before=pixels();
        SeasonSceneTicket ticket;hr(scene.enter(context.Get(),weatherOnly,3,0,ticket));context->Draw(3,0);require(scene.leave(context.Get(),ticket),"Indicator material restoration failed");
        context->OMSetRenderTargets(1,rtvs,nullptr);hr(scene.weather(context.Get(),weatherOnly,targets[0].Get()));return std::pair{before,pixels()};
    };
    auto rainMark=indicator(1,2,0),snowMark=indicator(2,8,0),floodMark=indicator(8,0,regional.date),fireMark=indicator(16,0,regional.date),droughtMark=indicator(32,0,regional.date),blizzardMark=indicator(256,0,regional.date),heatwaveMark=indicator(512,0,regional.date);
    for(auto* mark:{&rainMark,&snowMark,&floodMark,&fireMark,&droughtMark,&blizzardMark,&heatwaveMark})require(mark->first!=mark->second,"An environment indicator has no GPU output");
    UINT badgePixels=0;for(UINT yy=0;yy<height;yy++)for(UINT xx=28;xx<54;xx++){
        size_t at=(yy*width+xx)*4;bool changed=memcmp(rainMark.first.data()+at,rainMark.second.data()+at,4)!=0;
        if(changed&&rainMark.second[at]<40&&rainMark.second[at+1]<40&&rainMark.second[at+2]<40)badgePixels++;
    }
    require(badgePixels>100,"Fixed-size icon background did not render outside the region wash");
    auto expired=indicator(8,0,195|(6u<<16)|(21u<<24)),future=indicator(8,0,195|(7u<<16)|(2u<<24)),disabled=indicator(0,2,regional.date);
    require(expired.first==expired.second&&future.first==future.second&&disabled.first==disabled.second,"Expired, future or OFF indicators remain visible");
    std::cerr<<"PASS current rain/snow and five recent-disaster icon/range GPU outputs, dated receipt expiry, independent particle OFF, all OFF restoration\n";
    scene.saveCaptureAfterDrain();
    if(data.header()[1]==4){
        std::ifstream captured(reinterpret_cast<const wchar_t*>(data.bytes.data()+data.header()[14]),std::ios::binary);
        std::vector<BYTE> bytes{std::istreambuf_iterator<char>(captured),std::istreambuf_iterator<char>()};
        require(bytes.size()>=16,"Capture output is missing");auto* h=reinterpret_cast<const uint32_t*>(bytes.data());
        constexpr size_t frameBytes=32+80+144+width*height*16;
        require(h[0]==0x57434150&&h[1]==2&&h[2]>=1&&h[2]<=2&&h[3]==0&&bytes.size()==16+h[2]*frameBytes,"Capture framing/error/count differs");
        // Spring has no haze/snow; compare all three actual capture stages with
        // the separately read terrain fixture, not just a draw receipt.
        size_t at=16+32+80+144+width*height*4;
        for(UINT stage=0;stage<3;stage++)require(std::memcmp(bytes.data()+at+stage*width*height*4,seasons[0].data(),width*height*4)==0,"Captured GPU pixels differ");
    }
    std::cout<<"{\"seasons\":4,\"calendarCases\":"<<calendarCases<<",\"continuousDateDraws\":"<<continuousDraws<<",\"weatherChangedCases\":"<<weatherChanged<<",\"distantSnowPixels\":"<<width*height<<",\"maxLodSnowErrorByte\":"<<maxTransitionError<<",\"gpuPayloadBytes\":"<<scene.gpuBytes()<<",\"restoredOriginalPixels\":"<<2*width*height<<",\"elapsedSeconds\":"<<std::chrono::duration<double>(std::chrono::steady_clock::now()-started).count()<<",\"gameAccess\":false,\"gameScreenVerified\":false,\"passed\":true}\n";return 0;
}catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}}
