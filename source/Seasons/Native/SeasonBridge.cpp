#include "GameBuildFacts.g.h"
extern "C" __declspec(dllexport) const char San14BuildProfileIdentity[]=SAN14_BUILD_PROFILE_IDENTITY;
// Product connection/lifetime layer. No command table or D3D11 vtable patch.
// Material/weather rendering is not published as successful by this layer.
#define NOMINMAX
#include "SeasonBridgeProtocol.h"
#include "FrameSignatures.h"
#include "PrepareSignature.h"
#include <windows.h>
#include <tlhelp32.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <vector>
#include <cstring>
#include <intrin.h>
#include <sddl.h>
#include <string>
#include <optional>
#ifdef SEASON_BRIDGE_FIXTURE
#include <iostream>
#endif
#include "detours.h"
using Microsoft::WRL::ComPtr;
using namespace seasons::bridge;
#include "SeasonTreeColorGpu.h"
#include "SeasonSceneGpu.h"
#include "SeasonShaderAdmission.h"
#include "SeasonGroundSourceGpu.h"
#include "SeasonNoticeCausality.h"
namespace {
struct OwnedHandle {HANDLE value;~OwnedHandle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}};
struct OwnedLocal {void* value=nullptr;~OwnedLocal(){if(value)LocalFree(value);}};
bool copyBootstrap(Bootstrap* destination,const Bootstrap* source) noexcept{
    __try{*destination=*source;return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
using Draw=gamebuild::Abi::Draw;
using Mesh=gamebuild::Abi::Mesh;
using Indexed=gamebuild::Abi::Indexed;
Draw originalDraw,originalLand;Mesh originalMesh;
Indexed originalIndexed;
using Batch=gamebuild::Abi::Batch;Batch originalBatch;
using Prepare=gamebuild::Abi::Prepare;
Prepare originalPrepare;
Indexed originalInstanced;
using Indirect=void(__fastcall*)(void*,uint32_t,void*,uint32_t);Indirect originalIndirect;
using AutoDraw=void(__fastcall*)(void*,uint32_t);AutoDraw originalAuto;
using InlineDraw=void(__fastcall*)(void*,uint32_t,uint32_t,const void*,uint32_t);InlineDraw originalInline;
using Present=uint64_t(__fastcall*)(void*,void*);Present originalPresent;
SRWLOCK dispatch=SRWLOCK_INIT,presentDispatch=SRWLOCK_INIT;std::mutex connection;
std::atomic<long> callbacks{0};
thread_local unsigned callbackDepth=0;
thread_local unsigned presentDepth=0;
std::atomic<bool> hooked{false};std::atomic<bool> performanceRunning{false};
template<class T>T read(uint64_t p,size_t off=0) noexcept{
    T value{};if(!p)return value;
    __try{std::memcpy(&value,reinterpret_cast<void*>(p+off),sizeof(value));}__except(EXCEPTION_EXECUTE_HANDLER){}
    return value;
}
struct Session {
    bool fixture;
    Bootstrap input;std::atomic<bool> admission{true};std::atomic<uint64_t> draws{0};
    std::atomic<uint32_t> thread{0},failure{0};
    // Written only by the verified actual Draw thread, destroyed after detach
    // and drain. No GPU context method executes on the pipe/lease worker.
    ComPtr<ID3D11DeviceContext> context;ComPtr<ID3D11Device> device;
    std::atomic<std::shared_ptr<TreeColorParameters>> pending;
    std::unique_ptr<TreeColorCache> tree;
    TreeSources sources;
    SeasonSceneData sceneData;
    std::unique_ptr<SeasonSceneGpu> sceneGpu;
    std::unique_ptr<SeasonNoticeArt> noticeArt;
    std::unique_ptr<SeasonNoticeCausality> noticeCausality;
    std::unique_ptr<season_perf::Trace> performance;std::atomic<season_perf::Trace*> performanceActive{nullptr};
    SeasonGroundSources groundSources;
    uint64_t fullEpoch=0;uint32_t fullClock=0,fullPasses=0;
    struct ShaderIdentity {uint64_t vo,po;uint32_t kind;ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;bool accepted;};
    std::vector<ShaderIdentity> shaderIdentities;
    std::atomic<uint64_t> completedEpoch{0},treeDraws{0},swaps{0},restores{0},gpuBytes{0};
    std::atomic<uint32_t> generated{0},alphaPixels{0};
    // Ordered admission counters, returned by the pipe without raw mutable
    // request readback. Never substitute these for actual GPU/Draw completion.
    std::atomic<uint32_t> selection[16]{};
    uint64_t vsOwner=0,psOwner=0; // render-thread-only cache of authenticated owners
    explicit Session(const Bootstrap& value,bool ownFixture=false):fixture(ownFixture),input(value){}
    bool sameScene()const{
        if(fixture)return true;
        const auto& i=input;
        auto world=read<uint64_t>(i.module+gamebuild::Memory::BasePointerRva)+gamebuild::Memory::BaseAdjust;
        auto state=read<uint64_t>(world,gamebuild::Memory::StatePointer),manager=read<uint64_t>(i.module+gamebuild::Scene::ManagerRva);
        auto scene=read<uint64_t>(manager,gamebuild::Scene::SceneOffset),parent=read<uint64_t>(scene,gamebuild::Scene::ParentOffset),driver=read<uint64_t>(parent,gamebuild::Scene::DriverOffset);
        return world==i.world&&state==i.state&&manager==i.manager&&read<uint32_t>(manager,gamebuild::Scene::ManagerStateOffset)==0&&scene==i.scene&&
            driver==i.driver&&read<uint64_t>(driver,gamebuild::Scene::ContextOffset)==i.context&&read<uint64_t>(driver,gamebuild::Scene::DeviceOffset)==i.device;
    }
};
std::atomic<std::shared_ptr<Session>> current;
bool clock(const Session& s,uint32_t& year,uint32_t& month,uint32_t& day);
uint32_t packedDate(const TreeColorParameters& p){return p.year|(p.month<<16)|(p.day<<24);}
// A Full request authorizes calendar-driven rendering for the lifetime of this
// validated session. IPC dates remain acknowledgements, not a per-frame gate.
// Read the actual game clock; never extrapolate it or reuse stale date weights.
bool renderParameters(const Session& s,const std::shared_ptr<TreeColorParameters>& request,TreeColorParameters& result){
    if(!request||!validTreeColor(*request))return false;
    uint32_t year{},month{},day{};if(!clock(s,year,month,day))return false;
    result=*request;
    if(!result.full)return result.year==year&&result.month==month&&result.day==day;
    result.year=year;result.month=month;result.day=day;calendarWeights(result);return true;
}
bool renderRequestActive(const Session& s,const std::shared_ptr<TreeColorParameters>& request){
    auto active=s.pending.load();return s.admission&&request&&active&&(active==request||(request->full&&active->full&&request->seasonal==active->seasonal));
}
void completedMaterialPass(Session& s,const TreeColorParameters& p,uint32_t pass){
    if(s.fullEpoch!=p.epoch||s.fullClock!=packedDate(p)){s.fullEpoch=p.epoch;s.fullClock=packedDate(p);s.fullPasses=0;}
    s.fullPasses|=pass;
}
struct Scope {
    DWORD error;unsigned csr;std::shared_ptr<Session> session;bool present=false;season_perf::Trace* previousPerf=nullptr;
    Scope(bool isPresent=false):error(GetLastError()),csr(_mm_getcsr()),present(isPresent){
        previousPerf=season_perf::local;auto waitStart=performanceRunning.load()?season_perf::tick():0;callbacks.fetch_add(1);if((present?presentDepth++:callbackDepth++)==0)AcquireSRWLockShared(present?&presentDispatch:&dispatch);auto waitEnd=waitStart?season_perf::tick():0;session=current.load();
        // A late entry after Stop may call the original, but must not touch the
        // session's render resources while finish releases them after drain.
        if(session&&!session->admission.load())session.reset();
        auto perf=session?session->performanceActive.load():nullptr;
        if(perf&&!present&&session->thread==GetCurrentThreadId()&&session->context&&session->sameScene()){
            season_perf::local=perf;
            if(perf&&callbackDepth==1)perf->visit(session->context.Get(),GetCurrentThreadId());
            if(perf&&waitStart&&perf->active())perf->add(season_perf::DispatchWait,waitEnd-waitStart,waitEnd-waitStart);
        }else season_perf::local=nullptr;
        _mm_setcsr(csr);SetLastError(error);
    }
    void preserve(){error=GetLastError();csr=_mm_getcsr();}
    void restore(){_mm_setcsr(csr);SetLastError(error);}
    ~Scope(){season_perf::local=previousPerf;session.reset();if((present?--presentDepth:--callbackDepth)==0)ReleaseSRWLockShared(present?&presentDispatch:&dispatch);callbacks.fetch_sub(1);_mm_setcsr(csr);SetLastError(error);}
};
struct CausalTicket;
thread_local CausalTicket* causalTicket=nullptr;
struct CausalTicket {
    Scope& scope;void* driver;uint64_t id=0,parent=0,enteredQpc=0;uint32_t path,before;CausalTicket* previous;
    CausalTicket(Scope& owner,void* d,uint32_t kind):scope(owner),driver(d),path(kind),before(read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0)),previous(causalTicket){
        if(scope.session&&scope.session->noticeCausality){enteredQpc=notice_trace::qpc();id=scope.session->noticeCausality->id();parent=previous?previous->id:0;}causalTicket=this;}
    SeasonNoticeCausality::Row* row(uint32_t phase,uint64_t caller=0){
        auto s=scope.session;if(!s||!s->noticeCausality)return nullptr;
        auto context=read<uint64_t>(reinterpret_cast<uint64_t>(driver),0x10);
        auto* r=s->noticeCausality->begin(id,parent,path,phase,driver,context,s->thread.load(),caller,enteredQpc);
        if(r){r->before=before;r->after=read<uint32_t>(reinterpret_cast<uint64_t>(driver),0x2ad0);
            bool safe=s->admission&&s->thread==GetCurrentThreadId()&&context==s->input.context&&s->sameScene();
            s->noticeCausality->bindings(r,safe?s->context.Get():nullptr,safe&&s->context);}
        return r;
    }
    void record(uint32_t phase){auto* r=row(phase);if(r)scope.session->noticeCausality->complete(r);}
    ~CausalTicket(){causalTicket=previous;}
};
void observed(Scope& scope,void* driver,uint32_t before){
    auto s=scope.session;if(!s||!s->admission.load())return;
    const auto d=reinterpret_cast<uint64_t>(driver);
#ifdef SEASON_BRIDGE_FIXTURE
    std::cerr<<"Owned callback: context=0x"<<std::hex<<read<uint64_t>(d,0x10)<<", expected=0x"<<s->input.context<<std::dec<<
        ", counter="<<before<<"->"<<read<uint32_t>(d,0x2ad0)<<"\n";
#endif
    if(read<uint64_t>(d,0x10)!=s->input.context||read<uint32_t>(d,0x2ad0)!=before+1)return;
    // Full authenticated original code increments this counter only after its
    // real native Draw returns. Bind ownership on that actual calling thread.
    const auto tid=GetCurrentThreadId();uint32_t zero=0;
    s->thread.compare_exchange_strong(zero,tid);
    if(s->thread.load()!=tid){s->failure=ERROR_INVALID_THREAD_ID;s->admission=false;return;}
    if(!s->context){
        ComPtr<ID3D11DeviceContext> context=reinterpret_cast<ID3D11DeviceContext*>(s->input.context);
        ComPtr<ID3D11Device> device;context->GetDevice(&device);
        if(context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE||reinterpret_cast<uint64_t>(device.Get())!=s->input.device||
           device->GetCreationFlags()&D3D11_CREATE_DEVICE_SINGLETHREADED||FAILED(device->GetDeviceRemovedReason())){
            s->failure=ERROR_NOT_SUPPORTED;s->admission=false;return;
        }
        s->device=device;s->context=context;
    }
    if(FAILED(s->device->GetDeviceRemovedReason())){s->failure=ERROR_DEVICE_NOT_AVAILABLE;s->admission=false;return;}
    ++s->draws;
}
bool copyShader(void* destination,const void* source,size_t length) noexcept {
    __try{std::memcpy(destination,source,length);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
bool sceneShader(Session& s,void* driver,uint32_t kind){
    const auto d=reinterpret_cast<uint64_t>(driver);auto vo=read<uint64_t>(d,0xa8),po=read<uint64_t>(d,0xb8);
    if(!vo||!po||read<uint64_t>(vo)!=s.input.module+gamebuild::Scene::VertexOwnerClassRva||read<uint64_t>(po)!=s.input.module+gamebuild::Scene::PixelOwnerClassRva)return false;
    ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;s.context->VSGetShader(&vs,nullptr,nullptr);s.context->PSGetShader(&ps,nullptr,nullptr);
    if(reinterpret_cast<uint64_t>(vs.Get())!=read<uint64_t>(vo,40)||reinterpret_cast<uint64_t>(ps.Get())!=read<uint64_t>(po,40))return false;
    for(const auto& entry:s.shaderIdentities)if(entry.vo==vo&&entry.po==po&&entry.vs==vs&&entry.ps==ps&&entry.kind==kind)return entry.accepted;
    using namespace seasons::profile;
    const Profile* profile=kind==1?&TreeColor:kind==2?&TreeDepth:kind==3?&Terrain:kind==4?&Composite:kind==5?&DistantTerrain:nullptr;
    const char* hash=kind==1?"6d2b5627d33e14b2a0daf2fbf1ee24a9a0a58d8e625dc5a764529fc6380edb50":
        kind==2?"29444ce662032ce0defd2e1b8af96ba5b343b7bd22f7d7868b7c27aae1112c75":
        kind==3?"00c98553b5d70e71e3554f08d5f0ddf1cc8620eb443428ce28f49ef5dcc79d80":
        kind==5?"1503d465e79f35c88cfda8d2f6ac7afc8e5397c47f94e08d7879d5dc70b39029":
        "46e389a47d684c17a47ea14e0b1f0d7b5d7427b3ef177122c2842cb4dfe2fd76";
    bool accepted=false;auto bytes=read<uint64_t>(vo,48),length=read<uint64_t>(vo,56);
    if(profile&&bytes&&length>=32&&length<=65536){std::vector<BYTE> code(static_cast<size_t>(length));
        accepted=copyShader(code.data(),reinterpret_cast<const void*>(bytes),code.size())&&materialSha(code.data(),code.size(),hash)&&matches(read<uint64_t>(po,16),*profile);}
    if(s.shaderIdentities.size()==32)s.shaderIdentities.erase(s.shaderIdentities.begin());
    s.shaderIdentities.push_back({vo,po,kind,vs,ps,accepted});return accepted;
}
struct MaterialTicket {
    Scope* scope;void* driver;uint32_t count,start;bool swapped=false;
    std::shared_ptr<TreeColorParameters> parameters;ComPtr<ID3D11ShaderResourceView> saved;
    TreeColorParameters rendered{};
    uint32_t path=0;SeasonSceneTicket scene;NoticeArtTicket notice;bool performanceGeometry=false;
};
thread_local MaterialTicket* materialTicket=nullptr;
#ifdef SEASON_BRIDGE_FIXTURE
BOOL (WINAPI* fixtureReadNotice)(environment::WeatherSnapshot*)=nullptr;
#endif
void enterTree(MaterialTicket& ticket) noexcept {
    season_perf::Cpu measured(season_perf::TreeEnter);
    auto s=ticket.scope->session;
    if(s&&treeCount(ticket.count)&&ticket.start==0)++s->selection[1];
    if(!s||!s->admission.load()||ticket.swapped||!treeCount(ticket.count)||ticket.start!=0||
       !s->context||s->thread.load()!=GetCurrentThreadId()||!s->sameScene())return;
    ticket.parameters=s->pending.load();if(!ticket.parameters)return;
    if(!ticket.parameters->seasonal)return;
    ++s->selection[2];
    const auto d=reinterpret_cast<uint64_t>(ticket.driver);
    // The actual Draw driver is distinct from the scene driver. +0x3078 is
    // verified only on the scene chain; actual Draw drivers recorded zero.
    // Use the device already owned via the actual context on this thread.
    if(read<uint64_t>(d,0x10)!=s->input.context||reinterpret_cast<uint64_t>(s->device.Get())!=s->input.device)return;
    ++s->selection[3];
    auto* context=s->context.Get();
    try {
        const auto vo=read<uint64_t>(d,0xa8),po=read<uint64_t>(d,0xb8);
        bool full=ticket.parameters->full;uint32_t sceneKind=1;
        if(full){
            ComPtr<ID3D11RenderTargetView> target;ComPtr<ID3D11DepthStencilView> depth;context->OMGetRenderTargets(1,&target,&depth);
            sceneKind=target?1u:2u;if(!target&&!depth)return;
            if(!sceneShader(*s,ticket.driver,sceneKind))return;
            ++s->selection[sceneKind==1?4:5];
        }else if(!s->fixture){
            if(!vo||!po||read<uint64_t>(vo)!=s->input.module+gamebuild::Scene::VertexOwnerClassRva||read<uint64_t>(po)!=s->input.module+gamebuild::Scene::PixelOwnerClassRva)return;
            ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;context->VSGetShader(&vs,nullptr,nullptr);context->PSGetShader(&ps,nullptr,nullptr);
            if(reinterpret_cast<uint64_t>(vs.Get())!=read<uint64_t>(vo,40)||reinterpret_cast<uint64_t>(ps.Get())!=read<uint64_t>(po,40))return;
            ++s->selection[4];
            if(vo!=s->vsOwner||po!=s->psOwner){
                auto bytes=read<uint64_t>(vo,48),length=read<uint64_t>(vo,56);if(!bytes||length<32||length>65536)return;
                std::vector<BYTE> bytecode(static_cast<size_t>(length));
                if(!copyShader(bytecode.data(),reinterpret_cast<void*>(bytes),bytecode.size())||
                   !materialSha(bytecode.data(),bytecode.size(),"6d2b5627d33e14b2a0daf2fbf1ee24a9a0a58d8e625dc5a764529fc6380edb50"))return;
                s->vsOwner=vo;s->psOwner=po;
            }
            ++s->selection[5];
            ComPtr<ID3D11Buffer> vb,ib;UINT stride{},offset{},io{};DXGI_FORMAT format{};
            context->IAGetVertexBuffers(0,1,&vb,&stride,&offset);context->IAGetIndexBuffer(&ib,&format,&io);
            if(!vb||!ib||stride!=72||format!=DXGI_FORMAT_R16_UINT)return;
            ++s->selection[6];
        }
        ComPtr<ID3D11RenderTargetView> target;context->OMGetRenderTargets(1,&target,nullptr);if(!target&&!full)return;
        ++s->selection[7];
        if(!renderParameters(*s,ticket.parameters,ticket.rendered))return;
        const auto& p=ticket.rendered;
        if(FAILED(s->device->GetDeviceRemovedReason())){s->failure=ERROR_DEVICE_NOT_AVAILABLE;s->admission=false;return;}
        ++s->selection[8];
        ComPtr<ID3D11ShaderResourceView> source;context->PSGetShaderResources(0,1,&source);if(!source)return;
        ++s->selection[9];
        if(!s->tree)s->tree=std::make_unique<TreeColorCache>(s->sources);
        ID3D11ShaderResourceView* replacement{};uint32_t alpha{},asset{};bool generated=false;
        auto hr=s->tree->get(context,source.Get(),p,&replacement,alpha,generated,ticket.count,&asset);
        if(hr==S_FALSE)return;
        if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;return;}
        ++s->selection[10];
        s->generated.fetch_add(generated?1:0);s->alphaPixels=alpha;s->gpuBytes=s->tree->gpuBytes();
        if(!renderRequestActive(*s,ticket.parameters)||read<uint32_t>(s->input.state,0x34)!=packedDate(p))return;
        if(full){
            if(!s->sceneGpu)s->sceneGpu=std::make_unique<SeasonSceneGpu>(s->sceneData);
            hr=s->sceneGpu->enter(context,p,sceneKind,asset,ticket.scene);if(hr==S_FALSE)return;
            if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;return;}
            s->gpuBytes=s->tree->gpuBytes()+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes()+(s->noticeArt?s->noticeArt->gpuBytes():0);
        }
        ticket.saved=source;context->PSSetShaderResources(0,1,&replacement);ticket.swapped=true;++s->swaps;
        ++s->selection[11];
    }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;}
}
void leaveTree(MaterialTicket& ticket,uint32_t before) noexcept {
    season_perf::Cpu measured(season_perf::TreeLeave);
    if(!ticket.swapped)return;auto s=ticket.scope->session;auto* context=s->context.Get();
    auto* saved=ticket.saved.Get();context->PSSetShaderResources(0,1,&saved);
    if(ticket.scene.changed&&!s->sceneGpu->leave(context,ticket.scene)){s->failure=ERROR_INVALID_STATE;s->admission=false;return;}
    ComPtr<ID3D11ShaderResourceView> after;context->PSGetShaderResources(0,1,&after);
    if(after.Get()!=saved){s->failure=ERROR_INVALID_STATE;s->admission=false;return;}
    ++s->restores;
    ++s->selection[12];
    const auto& p=ticket.rendered;
    if(renderRequestActive(*s,ticket.parameters)&&read<uint32_t>(s->input.state,0x34)==packedDate(p)&&
        read<uint32_t>(reinterpret_cast<uint64_t>(ticket.driver),0x2ad0)==before+1){
        ++s->treeDraws;
        if(p.full)completedMaterialPass(*s,p,ticket.scene.kind==2?2u:1u);
        else s->completedEpoch.store(ticket.parameters->epoch,std::memory_order_release);
    }
}
void enterLand(MaterialTicket& ticket)noexcept{
    season_perf::Cpu measured(season_perf::LandEnter);
    auto s=ticket.scope->session;
    if(!s||!s->admission||!s->context||s->thread!=GetCurrentThreadId()||!s->sameScene()||ticket.scene.changed)return;
    ticket.parameters=s->pending.load();if(!ticket.parameters||!ticket.parameters->full)return;
    if(read<uint64_t>(reinterpret_cast<uint64_t>(ticket.driver),0x10)!=s->input.context)return;
    try{
        if(!renderParameters(*s,ticket.parameters,ticket.rendered))return;const auto& p=ticket.rendered;
        uint32_t kind=sceneShader(*s,ticket.driver,3)?3u:sceneShader(*s,ticket.driver,5)?5u:0u;if(!kind)return;
        auto sourceResult=p.seasonal?s->groundSources.admit(s->context.Get(),kind==3,p.weights[1]+p.weights[2]>0):S_OK;
        if(sourceResult==S_FALSE){s->selection[15]=s->groundSources.rejection();return;}
        if(FAILED(sourceResult)){s->failure=static_cast<uint32_t>(sourceResult);s->admission=false;return;}
        if(!renderRequestActive(*s,ticket.parameters)||read<uint32_t>(s->input.state,0x34)!=packedDate(p))return;
        if(!s->sceneGpu)s->sceneGpu=std::make_unique<SeasonSceneGpu>(s->sceneData);
        auto hr=s->sceneGpu->enter(s->context.Get(),p,kind,0,ticket.scene,
            s->groundSources.riceControlReference(),s->groundSources.vegetationDetailReference());
        if(hr==S_OK){if(ticket.scene.changed)++s->swaps;s->gpuBytes=(s->tree?s->tree->gpuBytes():0)+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes()+(s->noticeArt?s->noticeArt->gpuBytes():0);}
        else if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;}
    }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;}
}
void leaveLand(MaterialTicket& ticket,uint32_t before)noexcept{
    season_perf::Cpu measured(season_perf::LandLeave);
    if(!ticket.scene.changed&&!ticket.scene.observed)return;auto s=ticket.scope->session;
    if(!s->sceneGpu->leave(s->context.Get(),ticket.scene)){s->failure=ERROR_INVALID_STATE;s->admission=false;return;}if(ticket.scene.changed)++s->restores;
    const auto& p=ticket.rendered;
    if(renderRequestActive(*s,ticket.parameters)&&read<uint32_t>(s->input.state,0x34)==packedDate(p)&&read<uint32_t>(reinterpret_cast<uint64_t>(ticket.driver),0x2ad0)==before+1){
        if(ticket.scene.kind==3&&p.seasonal){
            // The pinned original LandCode passes exactly these arguments to
            // DrawIndexedInstanced: index count from driver+90's +1c, instance
            // count from the third engine argument, with all starts/base zero.
            try{auto indices=read<uint32_t>(read<uint64_t>(reinterpret_cast<uint64_t>(ticket.driver),0x90),0x1c);
                auto hr=s->sceneGpu->collectRice(s->context.Get(),p,indices,ticket.count,s->groundSources.riceControlReference());
                if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;return;}
            }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;return;}
        }
        s->gpuBytes=(s->tree?s->tree->gpuBytes():0)+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes()+(s->noticeArt?s->noticeArt->gpuBytes():0);
        completedMaterialPass(*s,p,ticket.scene.kind==5?8u:4u);++s->selection[13];
    }
}
void afterScene(Scope& scope,void* driver,uint32_t start,uint32_t count,uint32_t before)noexcept{
    auto s=scope.session;if(!s||!s->admission||!s->sceneGpu||start||count!=3||!s->context||s->thread!=GetCurrentThreadId()||!s->sameScene())return;
    auto p=s->pending.load();TreeColorParameters rendered{};
    if(auto* perf=s->performanceActive.load();perf&&perf->active()&&perf->date&&perf->date!=read<uint32_t>(s->input.state,0x34))perf->stop(6);
    if(!p||!p->full||!renderParameters(*s,p,rendered)||s->fullClock!=packedDate(rendered)||
        read<uint32_t>(reinterpret_cast<uint64_t>(driver),0x2ad0)!=before+1){
        if(sceneShader(*s,driver,4))s->sceneGpu->discardRiceFrame();return;
    }
    try{
        if(!sceneShader(*s,driver,4))return;
        ++s->selection[6];
        ComPtr<ID3D11RenderTargetView> target;s->context->OMGetRenderTargets(1,&target,nullptr);if(!target)return;
        ComPtr<IDXGISwapChain> chain=reinterpret_cast<IDXGISwapChain*>(read<uint64_t>(s->input.driver,0x3080));if(!chain)return;
        ComPtr<ID3D11Texture2D> backbuffer;auto hr=chain->GetBuffer(0,IID_PPV_ARGS(&backbuffer));if(FAILED(hr))return;
        ComPtr<ID3D11Resource> output;target->GetResource(&output);if(output.Get()!=backbuffer.Get())return;
        hr=s->sceneGpu->weather(s->context.Get(),rendered,target.Get());
        if(hr==S_OK){++s->selection[14];s->gpuBytes=(s->tree?s->tree->gpuBytes():0)+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes()+(s->noticeArt?s->noticeArt->gpuBytes():0);
            // Trees can be entirely culled at distant zoom. A real terrain Draw
            // followed by the admitted scene composite completes this epoch;
            // absent tree passes must not leave the product permanently pending.
            if((s->fullPasses&12)!=0&&s->fullEpoch==p->epoch&&s->pending.load()==p&&packedDate(*p)==packedDate(rendered)&&
                read<uint32_t>(s->input.state,0x34)==packedDate(rendered))s->completedEpoch.store(p->epoch,std::memory_order_release);s->fullPasses=0;}
        else if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;}
    }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;}
}
void enterNotice(MaterialTicket& ticket,bool observationOnly=false,std::shared_ptr<Session> observedSession={},NoticeObservation* observation=nullptr)noexcept{
    season_perf::Cpu measured(season_perf::Notice);
    // Geometry counts do not authenticate an image. Direct Draw and Batch paths
    // use different counts; the exact original sprite descriptor and full hash do.
    auto s=ticket.scope?ticket.scope->session:std::move(observedSession);if(!s||!s->noticeArt)return;s->noticeArt->record(0);
    struct Record {SeasonNoticeArt* art;NoticeObservation* event;~Record(){if(event)art->observe(*event);}} record{s->noticeArt.get(),observation};
    auto gate=[&](NoticeGate value){if(observation)observation->gate=value;};
    gate(NoticeGate::Admission);if(!s->admission)return;
    gate(NoticeGate::Count);if(!ticket.count)return;
    // Only the owner may read the lazily initialized context or perform D3D calls.
    gate(NoticeGate::Thread);if(s->thread!=GetCurrentThreadId())return;
    gate(NoticeGate::Context);if(!s->context)return;s->noticeArt->record(1);
    gate(NoticeGate::Scene);if(!s->sameScene())return;s->noticeArt->record(2);
    auto request=s->pending.load();TreeColorParameters p{};gate(NoticeGate::Request);if(!request||!request->full||!renderParameters(*s,request,p))return;s->noticeArt->record(3);
    try{
        auto env=GetModuleHandleW(L"EnvironmentBridge.dll");auto get=env?reinterpret_cast<BOOL(WINAPI*)(environment::WeatherSnapshot*)>(GetProcAddress(env,"EnvironmentReadWeather")):nullptr;
#ifdef SEASON_BRIDGE_FIXTURE
        get=fixtureReadNotice;
#else
        // Only reject an absent picture here; a positive hint still requires
        // the fresh full snapshot, reload, date, owner and original-art guards.
        auto visual=env?reinterpret_cast<uint32_t(WINAPI*)()>(GetProcAddress(env,"EnvironmentNoticeVisual")):nullptr;
        if(visual){auto kind=visual();if(kind<2||kind>5){if(observation)observation->kind=kind;gate(NoticeGate::Kind);return;}}
#endif
        auto weather=std::make_unique<environment::WeatherSnapshot>();
        gate(NoticeGate::Getter);if(!get)return;s->noticeArt->record(4);gate(NoticeGate::WeatherRead);if(!season_perf::readTimed(get,env,weather.get()))return;s->noticeArt->record(5);
        if(observation){observation->generation=weather->generation;observation->sequence=weather->sequence;observation->kind=weather->reserved;observation->date=weather->date;}
        gate(NoticeGate::WeatherHeader);if(weather->magic!=environment::WeatherMagic||weather->version!=environment::WeatherVersion||weather->size!=sizeof(*weather)||weather->generation==0||weather->sequence==0||weather->date!=packedDate(p))return;s->noticeArt->record(6);
        gate(NoticeGate::Kind);if(weather->reserved<2||weather->reserved>5)return;s->noticeArt->record(7);
        if(s->noticeArt->tracing()){
            bool sameDriver=read<uint64_t>(reinterpret_cast<uint64_t>(ticket.driver),0x10)==s->input.context;
            s->noticeArt->observeDriver(ticket.path,sameDriver);
            gate(NoticeGate::Driver);if(ticket.path==5&&!sameDriver)return;
            gate(NoticeGate::Bindings);s->noticeArt->observeBindings(s->context.Get(),ticket.path,observation);
        }
        if(observationOnly)return;
        auto hr=s->noticeArt->enter(s->context.Get(),weather->reserved,ticket.notice,ticket.path);
        if(hr==S_OK){++s->swaps;++s->selection[11];s->gpuBytes=(s->tree?s->tree->gpuBytes():0)+(s->sceneGpu?s->sceneGpu->gpuBytes():0)+s->groundSources.gpuBytes()+s->noticeArt->gpuBytes();}
        else if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;}
    }catch(...){gate(NoticeGate::Exception);s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;}
}
uint8_t __fastcall onPrepare(void* driver,uint32_t mode){
    // Prepare is nested inside the locked engine Draw. Taking the same SRW
    // lock recursively could deadlock behind an exclusive detach waiter.
    auto caller=reinterpret_cast<uint64_t>(_ReturnAddress());
    Scope scope;bool nested=materialTicket!=nullptr;scope.restore();
    uint8_t result;{season_perf::Cpu measured(season_perf::OriginalPrepare);result=originalPrepare(driver,mode);}scope.preserve();
    std::optional<CausalTicket> standalone;
    if(!causalTicket||causalTicket->driver!=driver)standalone.emplace(scope,driver,0);
    SeasonNoticeCausality::Row* causalRow=nullptr;
    if(causalTicket&&causalTicket->driver==driver)causalRow=causalTicket->row(SeasonNoticeCausality::Prepare,caller);
    if(causalRow){causalRow->mode=mode;causalRow->result=result;}
    auto s=scope.session;NoticeObservation observation;NoticeObservation* event=nullptr;
    if(s&&s->noticeArt&&s->noticeArt->tracing()){
        observation.id=s->noticeArt->nextObservation();observation.tick=GetTickCount64();
        observation.caller=caller>=s->input.module&&caller-s->input.module<0x1340000?caller-s->input.module:0;
        observation.driver=reinterpret_cast<uint64_t>(driver);observation.driverContext=read<uint64_t>(observation.driver,0x10);
        observation.sessionContext=s->input.context;observation.thread=GetCurrentThreadId();observation.ownerThread=s->thread.load();
        observation.nestedMismatch=nested&&materialTicket->driver!=driver;observation.path=nested&&!observation.nestedMismatch?materialTicket->path:5;
        observation.mode=mode;observation.result=result;event=&observation;
    }
    if(result&&nested&&materialTicket->driver==driver){if(materialTicket->path==1){enterLand(*materialTicket);
        if(event)enterNotice(*materialTicket,true,{},event);
    }else if(materialTicket->path==3||materialTicket->path==4){
        if(materialTicket->path==4){auto d=reinterpret_cast<uint64_t>(driver);bool unindexed=read<uint32_t>(d,0x2a40)==1;
            auto start=read<uint32_t>(d,unindexed?0x2a30:0x2a34),end=read<uint32_t>(d,unindexed?0x2a38:0x2a3c);
            materialTicket->start=start;materialTicket->count=end>start?end-start:0;}
        enterNotice(*materialTicket,false,{},event);}else {enterTree(*materialTicket);
        if(event)enterNotice(*materialTicket,true,{},event);
    }}else if(result&&event){
        // An engine Draw outside our wrappers still calls this authenticated
        // Prepare function. Observe only: a local ticket cannot own a later Draw
        // or restore its binding, so it must never substitute an image.
        MaterialTicket observed{nullptr,driver,1,0};observed.path=5;enterNotice(observed,true,s,event);
    }else if(event){event->gate=NoticeGate::PrepareFailed;s->noticeArt->observe(*event);}
    if(causalRow){causalRow->gate=event?static_cast<uint32_t>(event->gate):UINT32_MAX;causalRow->kind=event?event->kind:0;causalRow->date=event?event->date:0;
        causalRow->changed=materialTicket&&materialTicket->driver==driver&&materialTicket->notice.changed;s->noticeCausality->complete(causalRow);}
    if(auto perf=season_perf::local;perf&&materialTicket&&materialTicket->driver==driver&&materialTicket->scene.observed&&!materialTicket->performanceGeometry)
        materialTicket->performanceGeometry=perf->geometryBegin(s->context.Get(),materialTicket->scene.kind);
    return result;
}
void __fastcall onDraw(void* d,uint32_t mode,uint32_t start,uint32_t count){Scope s;CausalTicket causal{s,d,4};auto before=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);
    MaterialTicket ticket{&s,d,count,start};ticket.path=3;auto* previous=materialTicket;materialTicket=&ticket;s.restore();
    {season_perf::Cpu originalTimer(season_perf::OriginalDraw);originalDraw(d,mode,start,count);}s.preserve();causal.record(SeasonNoticeCausality::Returned);materialTicket=previous;
    if(ticket.notice.changed&&s.session){if(s.session->noticeArt->leave(s.session->context.Get(),ticket.notice))++s.session->restores;
        else{s.session->failure=ERROR_INVALID_STATE;s.session->admission=false;}}
    causal.record(SeasonNoticeCausality::Restored);observed(s,d,before);afterScene(s,d,start,count,before);}
void __fastcall onLand(void* d,uint32_t mode,uint32_t count,uint32_t unused){Scope s;CausalTicket causal{s,d,2};auto before=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);
    MaterialTicket ticket{&s,d,count,0};ticket.path=1;auto* previous=materialTicket;materialTicket=&ticket;s.restore();
    {season_perf::Cpu originalTimer(season_perf::OriginalDraw);originalLand(d,mode,count,unused);}s.preserve();causal.record(SeasonNoticeCausality::Returned);materialTicket=previous;leaveLand(ticket,before);if(ticket.performanceGeometry)season_perf::gpu(s.session->context.Get(),101+ticket.scene.kind*2);causal.record(SeasonNoticeCausality::Restored);observed(s,d,before);}
void __fastcall onMesh(void* d,uint32_t mode,uint32_t a,uint32_t b,uint32_t start,uint32_t count,uint32_t instances){Scope s;CausalTicket causal{s,d,3};
    if(s.session&&treeCount(count)&&start==0)++s.session->selection[0];
    auto before=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);MaterialTicket ticket{&s,d,count,start};ticket.path=2;
    auto* previous=materialTicket;materialTicket=&ticket;s.restore();
    {season_perf::Cpu originalTimer(season_perf::OriginalDraw);originalMesh(d,mode,a,b,start,count,instances);}s.preserve();causal.record(SeasonNoticeCausality::Returned);materialTicket=previous;
    leaveTree(ticket,before);if(ticket.performanceGeometry)season_perf::gpu(s.session->context.Get(),101+ticket.scene.kind*2);causal.record(SeasonNoticeCausality::Restored);observed(s,d,before);}
void __fastcall onIndexed(void* d,uint32_t mode,uint32_t a,uint32_t b,uint32_t start,uint32_t count){Scope s;CausalTicket causal{s,d,1};
    auto before=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);MaterialTicket ticket{&s,d,count,start};ticket.path=3;
    auto* previous=materialTicket;materialTicket=&ticket;s.restore();{season_perf::Cpu originalTimer(season_perf::OriginalDraw);originalIndexed(d,mode,a,b,start,count);}s.preserve();causal.record(SeasonNoticeCausality::Returned);materialTicket=previous;
    if(ticket.notice.changed&&s.session){if(s.session->noticeArt->leave(s.session->context.Get(),ticket.notice))++s.session->restores;
        else{s.session->failure=ERROR_INVALID_STATE;s.session->admission=false;}}
    causal.record(SeasonNoticeCausality::Restored);observed(s,d,before);}
struct Patch {PVOID* original;void* replacement;uint32_t rva;const BYTE* expected;size_t size;BYTE* target;DWORD protect;std::vector<BYTE> before,installed;};
void __fastcall onBatch(void* d){Scope s;CausalTicket causal{s,d,9};MaterialTicket ticket{&s,d,0,0};ticket.path=4;
    auto* previous=materialTicket;materialTicket=&ticket;s.restore();{season_perf::Cpu originalTimer(season_perf::OriginalDraw);originalBatch(d);}s.preserve();causal.record(SeasonNoticeCausality::Returned);materialTicket=previous;
    // The verified batch body draws between Prepare and return; its counter is
    // incremented then decremented. Restore the texture after that owned call.
    if(ticket.notice.changed&&s.session){if(s.session->noticeArt->leave(s.session->context.Get(),ticket.notice))++s.session->restores;
        else{s.session->failure=ERROR_INVALID_STATE;s.session->admission=false;}}
    causal.record(SeasonNoticeCausality::Restored);
}
void __fastcall onInstanced(void* d,uint32_t m,uint32_t count,uint32_t n,uint32_t start,uint32_t first){Scope s;CausalTicket causal{s,d,5};s.restore();originalInstanced(d,m,count,n,start,first);s.preserve();causal.record(SeasonNoticeCausality::Returned);}
void __fastcall onIndirect(void* d,uint32_t m,void* buffer,uint32_t offset){Scope s;CausalTicket causal{s,d,6};s.restore();originalIndirect(d,m,buffer,offset);s.preserve();causal.record(SeasonNoticeCausality::Returned);}
void __fastcall onAuto(void* d,uint32_t m){Scope s;CausalTicket causal{s,d,7};s.restore();originalAuto(d,m);s.preserve();causal.record(SeasonNoticeCausality::Returned);}
void __fastcall onInline(void* d,uint32_t m,uint32_t count,const void* vertices,uint32_t stride){Scope s;CausalTicket causal{s,d,8};s.restore();originalInline(d,m,count,vertices,stride);s.preserve();causal.record(SeasonNoticeCausality::Returned);}
uint64_t __fastcall onPresent(void* d,void* parameters){Scope s(true);
    auto enteredQpc=s.session&&s.session->noticeCausality?notice_trace::qpc():0;
    auto presentId=s.session&&s.session->noticeCausality?s.session->noticeCausality->id():0;
    auto record=[&](uint32_t phase){if(auto session=s.session;session&&session->noticeCausality){auto& trace=*session->noticeCausality;
        auto* row=trace.begin(presentId,0,10,phase,d,read<uint64_t>(reinterpret_cast<uint64_t>(d),0x10),session->thread.load(),0,enteredQpc);trace.complete(row);}};
    auto* perf=s.session?s.session->performanceActive.load():nullptr;season_perf::Present* row=nullptr;
    if(perf&&perf->active()){auto n=perf->presentCount.fetch_add(1);if(n<season_perf::Trace::Frames){row=&perf->presents[n];row->begin=season_perf::tick();row->thread=GetCurrentThreadId();row->depth=presentDepth;}else perf->stop(2);}
    record(SeasonNoticeCausality::PresentEnter);s.restore();auto result=originalPresent(d,parameters);s.preserve();
    if(row)row->end=season_perf::tick();record(SeasonNoticeCausality::PresentReturn);return result;
}
std::vector<Patch> patches(6);
void initialize(bool diagnostic=false){using namespace season::frame;patches.resize(diagnostic?11:6);
    patches[0]={reinterpret_cast<PVOID*>(&originalDraw),reinterpret_cast<void*>(onDraw),gamebuild::Engine::DrawRva,DrawCode,sizeof(DrawCode)};
    patches[1]={reinterpret_cast<PVOID*>(&originalLand),reinterpret_cast<void*>(onLand),gamebuild::Engine::LandRva,LandCode,sizeof(LandCode)};
    patches[2]={reinterpret_cast<PVOID*>(&originalMesh),reinterpret_cast<void*>(onMesh),gamebuild::Engine::MeshRva,MeshCode,sizeof(MeshCode)};
    patches[3]={reinterpret_cast<PVOID*>(&originalPrepare),reinterpret_cast<void*>(onPrepare),gamebuild::Engine::PrepareRva,ExpectedPrepareCode,sizeof(ExpectedPrepareCode)};
    patches[4]={reinterpret_cast<PVOID*>(&originalIndexed),reinterpret_cast<void*>(onIndexed),gamebuild::Engine::IndexedRva,IndexedCode,sizeof(IndexedCode)};
    patches[5]={reinterpret_cast<PVOID*>(&originalBatch),reinterpret_cast<void*>(onBatch),gamebuild::Engine::BatchRva,BatchCode,sizeof(BatchCode)};
    if(diagnostic){
        patches[6]={reinterpret_cast<PVOID*>(&originalInstanced),reinterpret_cast<void*>(onInstanced),0x960fe0,InstancedCode,sizeof(InstancedCode)};
        patches[7]={reinterpret_cast<PVOID*>(&originalIndirect),reinterpret_cast<void*>(onIndirect),0x961040,IndirectCode,sizeof(IndirectCode)};
        patches[8]={reinterpret_cast<PVOID*>(&originalAuto),reinterpret_cast<void*>(onAuto),0x960e20,AutoCode,sizeof(AutoCode)};
        patches[9]={reinterpret_cast<PVOID*>(&originalInline),reinterpret_cast<void*>(onInline),0x961090,InlineCode,sizeof(InlineCode)};
        patches[10]={reinterpret_cast<PVOID*>(&originalPresent),reinterpret_cast<void*>(onPresent),0x954730,PresentCode,sizeof(PresentCode)};
    }
}
LONG transaction(bool attach,size_t first=0){
    AcquireSRWLockExclusive(&presentDispatch);AcquireSRWLockExclusive(&dispatch);std::vector<HANDLE> threads;LONG result=NO_ERROR;
    auto snap=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if(snap==INVALID_HANDLE_VALUE)result=GetLastError();
    else{
        THREADENTRY32 e{sizeof(e)};if(!Thread32First(snap,&e))result=GetLastError();
        else do{if(e.th32OwnerProcessID!=GetCurrentProcessId()||e.th32ThreadID==GetCurrentThreadId())continue;
            auto h=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_SET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,e.th32ThreadID);
            if(!h){result=GetLastError();break;}
            try{threads.push_back(h);}catch(...){CloseHandle(h);result=ERROR_NOT_ENOUGH_MEMORY;break;}
        }while(Thread32Next(snap,&e));
        if(result==NO_ERROR&&GetLastError()!=ERROR_NO_MORE_FILES)result=GetLastError();CloseHandle(snap);
    }
    if(!result){result=DetourTransactionBegin();if(!result){
        for(auto h:threads){DWORD exit{};if(GetExitCodeThread(h,&exit)&&exit!=STILL_ACTIVE)continue;result=DetourUpdateThread(h);if(result)break;}
        if(!result)for(size_t i=first;i<patches.size();i++){auto& p=patches[i];result=attach?DetourAttach(p.original,p.replacement):DetourDetach(p.original,p.replacement);if(result)break;}
        if(!result)result=DetourTransactionCommit();else DetourTransactionAbort();
    }}
    for(auto h:threads)CloseHandle(h);ReleaseSRWLockExclusive(&dispatch);ReleaseSRWLockExclusive(&presentDispatch);return result;
}
bool restoreProtection(Patch& p){
    MEMORY_BASIC_INFORMATION info{};if(!VirtualQuery(p.target,&info,sizeof(info)))return false;
    if(info.Protect!=p.protect){DWORD previous{};if(!VirtualProtect(p.target,p.size,p.protect,&previous)||!VirtualQuery(p.target,&info,sizeof(info)))return false;}
    return info.Protect==p.protect;
}
uint32_t performanceStart(const std::shared_ptr<Session>& s){
    if(s->performance||s->noticeCausality||patches.size()!=6||!s->pending.load()||!s->pending.load()->full||!s->thread.load())return ERROR_INVALID_STATE;
    auto trace=std::make_unique<season_perf::Trace>();trace->date=read<uint32_t>(s->input.state,0x34);trace->seasonal=s->pending.load()->seasonal;using namespace season::frame;
    Patch extra{reinterpret_cast<PVOID*>(&originalPresent),reinterpret_cast<void*>(onPresent),0x954730,PresentCode,sizeof(PresentCode)};
    extra.target=s->fixture?reinterpret_cast<BYTE*>(originalPresent):reinterpret_cast<BYTE*>(s->input.module+extra.rva);MEMORY_BASIC_INFORMATION info{};
    if(!VirtualQuery(extra.target,&info,sizeof(info))||info.State!=MEM_COMMIT||info.Protect!=PAGE_EXECUTE_READ||(!s->fixture&&std::memcmp(extra.target,extra.expected,extra.size)))return ERROR_REVISION_MISMATCH;
    for(auto& patch:patches)if(std::memcmp(patch.target,patch.installed.data(),patch.size))return ERROR_REVISION_MISMATCH;
    extra.protect=info.Protect;extra.before.assign(extra.target,extra.target+extra.size);extra.installed.resize(extra.size);originalPresent=reinterpret_cast<Present>(extra.target);
    patches.push_back(std::move(extra));auto error=transaction(true,6);if(error){patches.pop_back();return error;}
    auto& installed=patches.back();std::memcpy(installed.installed.data(),installed.target,installed.size);
    s->performance=std::move(trace);if(!restoreProtection(installed)){s->failure=ERROR_INVALID_STATE;return ERROR_INVALID_STATE;}
    s->performanceActive.store(s->performance.get());performanceRunning=true;return 0;
}
uint32_t performanceDetach(const std::shared_ptr<Session>& s){
    if(!s->performance)return ERROR_INVALID_STATE;s->performance->stop(3);
    if(patches.size()!=7)return patches.size()==6?0:ERROR_INVALID_STATE;
    {AcquireSRWLockExclusive(&dispatch);bool open=false;for(auto& slot:s->performance->slots)open|=slot.open;ReleaseSRWLockExclusive(&dispatch);if(open)return ERROR_IO_PENDING;}
    auto& p=patches.back();if(std::memcmp(p.target,p.installed.data(),p.size))return ERROR_REVISION_MISMATCH;
    auto error=transaction(false,6);if(error)return error;
    if(std::memcmp(p.target,p.before.data(),p.size)||!restoreProtection(p)){s->failure=ERROR_INVALID_STATE;return ERROR_INVALID_STATE;}
    // Both locks also wait out a late wrapper before the trace is read. Storage is retained until session drain.
    AcquireSRWLockExclusive(&presentDispatch);AcquireSRWLockExclusive(&dispatch);
    s->performanceActive.store(nullptr);performanceRunning=false;if(auto f=s->performance->frame())f->end=season_perf::tick();
    for(auto& slot:s->performance->slots){slot.disjoint.Reset();for(auto& stamp:slot.stamps)stamp.Reset();}
    ReleaseSRWLockExclusive(&dispatch);ReleaseSRWLockExclusive(&presentDispatch);patches.pop_back();return 0;
}
Recovery finish(const std::shared_ptr<Session>& s,bool attached){
    Recovery receipt{Magic,Version,sizeof(Recovery),0,s->input.generation,s->draws.load(),s->thread.load()};
    s->admission=false;performanceRunning=false;
    if(attached){
        for(auto& p:patches)if(std::memcmp(p.target,p.installed.data(),p.size)){receipt.error=ERROR_INVALID_DATA;return receipt;}
        auto error=transaction(false);if(error){receipt.error=error;return receipt;}
        hooked=false;
    }
    // Late wrapper entries are pinned and use the original target with no
    // admission. Strong callback tokens outlive the detach transaction.
    const auto end=GetTickCount64()+2000;
    while(callbacks.load()!=0&&GetTickCount64()<end)Sleep(1);
    receipt.callbacksDrained=callbacks.load()==0;
    bool code=true,protection=true;
    for(auto& p:patches){code=code&&std::memcmp(p.target,p.before.data(),p.size)==0;protection=restoreProtection(p)&&protection;}
    receipt.dispatchDetached=code;receipt.protectionRestored=protection;
    receipt.originalGpuState=receipt.callbacksDrained&&s->swaps.load()==s->restores.load();
    if(receipt.callbacksDrained&&code&&protection&&receipt.originalGpuState){current.store(nullptr);s->tree.reset();s->pending.store(nullptr);
        if(s->sceneGpu)s->sceneGpu->saveCaptureAfterDrain();
        if(s->noticeArt)s->noticeArt->saveTraceAfterDrain();
        if(s->noticeCausality)s->noticeCausality->saveAfterDrain();
        s->noticeCausality.reset();
        s->noticeArt.reset();s->sceneGpu.reset();s->shaderIdentities.clear();s->groundSources.clear();s->context.Reset();s->device.Reset();receipt.resourcesReleased=1;}
    else receipt.error=ERROR_INVALID_STATE;
    receipt.actualDraws=s->draws.load();receipt.renderThread=s->thread.load();return receipt;
}
bool clock(const Session& s,uint32_t& year,uint32_t& month,uint32_t& day){
    const auto a=read<uint32_t>(s.input.state,0x34),b=read<uint32_t>(s.input.state,0x34);
    if(a!=b)return false;year=a&65535;month=(a>>16)&255;day=a>>24;
    return year>100&&year<400&&month>=1&&month<=12&&day>=1&&day<=30;
}
struct Io {
    HANDLE pipe,controller;std::shared_ptr<Session> session;uint64_t beat;
    bool attached;Recovery receipt{};bool ended=false,performanceRetireAttempted=false;
    void stop(){if(!ended){receipt=finish(session,attached);ended=true;}}
    bool alive(){uint32_t y{},m{},d{};if(session->performance){auto& perf=*session->performance;perf.active();
        if(!performanceRetireAttempted&&perf.stopAt&&season_perf::tick()-perf.stopAt>=5*perf.frequency){performanceRetireAttempted=true;auto error=performanceDetach(session);if(error)perf.stop(8);}}
        return !ended&&session->admission.load()&&!session->failure.load()&&GetTickCount64()-beat<session->input.leaseMs&&
            WaitForSingleObject(controller,0)==WAIT_TIMEOUT&&session->sameScene()&&clock(*session,y,m,d);
    }
    bool wait(OVERLAPPED& ov,DWORD& bytes){
        bool ok=true;
        while(WaitForSingleObject(ov.hEvent,50)==WAIT_TIMEOUT){
            if(!alive()){
#ifdef SEASON_BRIDGE_FIXTURE
                uint32_t y{},m{},d{};
                std::cerr<<"Owned I/O stop: elapsed="<<(GetTickCount64()-beat)<<", admission="<<session->admission.load()<<
                    ", failure="<<session->failure.load()<<", processWait="<<WaitForSingleObject(controller,0)<<
                    ", scene="<<session->sameScene()<<", clock="<<clock(*session,y,m,d)<<", date="<<y<<"/"<<m<<"/"<<d<<"\n";
#endif
                // Hook restoration does not depend on a client completing I/O.
                stop();CancelIoEx(pipe,&ov);ok=false;break;
            }
        }
        const bool completed=GetOverlappedResult(pipe,&ov,&bytes,TRUE)!=FALSE;
        return ok&&completed;
    }
    bool exchange(void* data,DWORD length,bool write){
        OVERLAPPED ov{};ov.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!ov.hEvent)return false;
        DWORD bytes{};bool ok=write?WriteFile(pipe,data,length,&bytes,&ov)!=FALSE:ReadFile(pipe,data,length,&bytes,&ov)!=FALSE;
        if(!ok&&GetLastError()==ERROR_IO_PENDING)ok=wait(ov,bytes);
        CloseHandle(ov.hEvent);return ok&&bytes==length;
    }
    bool connect(){
        OVERLAPPED ov{};ov.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!ov.hEvent)return false;
        bool ok=ConnectNamedPipe(pipe,&ov)!=FALSE;const auto error=ok?NO_ERROR:GetLastError();DWORD bytes{};
        if(error==ERROR_PIPE_CONNECTED)ok=true;else if(error==ERROR_IO_PENDING)ok=wait(ov,bytes);
        CloseHandle(ov.hEvent);
#ifdef SEASON_BRIDGE_FIXTURE
        std::cerr<<"Owned server connect: ok="<<ok<<", Win32="<<GetLastError()<<", ended="<<ended<<"\n";
#endif
        if(!ok)return false;
        ULONG client{};const bool identified=GetNamedPipeClientProcessId(pipe,&client)!=FALSE;
#ifdef SEASON_BRIDGE_FIXTURE
        std::cerr<<"Owned client identity: ok="<<identified<<", PID="<<client<<", expected="<<session->input.controllerPid<<"\n";
#endif
        return identified&&client==session->input.controllerPid;
    }
};
Recovery serve(const Bootstrap& input,bool fixture=false){
    auto s=std::make_shared<Session>(input,fixture);Recovery failed{Magic,Version,sizeof(Recovery),ERROR_INVALID_PARAMETER,input.generation,0,0,1,1,1,1,1};
    std::unique_lock lock(connection,std::try_to_lock);
    if(!lock.owns_lock()||current.load()){failed.error=ERROR_BUSY;return failed;}
    if(!fixture&&!s->sameScene())return failed;
    if(input.sources||input.sourceBytes){
        if(input.sourceBytes!=TreeSourceBytes||!input.sources||input.sourceReserved)return failed;
        s->sources.bytes.resize(TreeSourceBytes);
        if(!copyShader(s->sources.bytes.data(),reinterpret_cast<const void*>(input.sources),TreeSourceBytes)||!s->sources.valid())return failed;
    }else if(!fixture)return failed;
    if(input.sceneData||input.sceneBytes){
        if(!input.sceneData||input.sceneBytes<64||input.sceneBytes>64*1024*1024||input.sceneReserved)return failed;
        s->sceneData.bytes.resize(input.sceneBytes);
        if(!copyShader(s->sceneData.bytes.data(),reinterpret_cast<const void*>(input.sceneData),input.sceneBytes)||!s->sceneData.valid())return failed;
        if(s->sceneData.header()[1]==5||s->sceneData.header()[1]==6){
            std::wstring trace;if(s->sceneData.header()[1]==6)trace=std::wstring(reinterpret_cast<const wchar_t*>(s->sceneData.bytes.data()+s->sceneData.header()[14]+s->sceneData.header()[15]))+L".notice.json";
            if(!trace.empty())s->noticeCausality=std::make_unique<SeasonNoticeCausality>(input.generation,trace+L".causal.bin");
            s->noticeArt=std::make_unique<SeasonNoticeArt>(s->sceneData.bytes.data()+s->sceneData.header()[14],std::move(trace));}
        auto hr=s->sceneData.prepare();if(FAILED(hr)){failed.error=static_cast<uint32_t>(hr);return failed;}
    }
    auto controller=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,input.controllerPid);
    if(!controller){failed.error=GetLastError();return failed;}
    OwnedHandle controllerOwner{controller};
    FILETIME created{},exit{},kernel{},user{};
    if(!GetProcessTimes(controller,&created,&exit,&kernel,&user)||
       (uint64_t(created.dwHighDateTime)<<32|created.dwLowDateTime)!=input.controllerStartFileTime)return failed;
    // Private local pipe: explicitly grant only the game token's user and
    // SYSTEM. The default process DACL is not used for the transport.
    HANDLE token{};DWORD tokenBytes{};PSECURITY_DESCRIPTOR descriptor=nullptr;
    std::vector<BYTE> tokenUser;LPWSTR sid=nullptr;bool security=false;
    OwnedLocal sidOwner,descriptorOwner;
    if(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)){
        OwnedHandle tokenOwner{token};
        GetTokenInformation(token,TokenUser,nullptr,0,&tokenBytes);tokenUser.resize(tokenBytes);
        if(tokenBytes&&GetTokenInformation(token,TokenUser,tokenUser.data(),tokenBytes,&tokenBytes)&&
           ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(tokenUser.data())->User.Sid,&sid)){
            sidOwner.value=sid;
            const std::wstring dacl=std::wstring(L"D:P(A;;GA;;;SY)(A;;GA;;;")+sid+L")";
            security=ConvertStringSecurityDescriptorToSecurityDescriptorW(dacl.c_str(),SDDL_REVISION_1,&descriptor,nullptr)!=FALSE;
            descriptorOwner.value=descriptor;
        }
    }
    if(!security){failed.error=GetLastError();return failed;}
    SECURITY_ATTRIBUTES attributes{sizeof(attributes),descriptor,FALSE};
    auto pipe=CreateNamedPipeW(input.pipe,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_MESSAGE|PIPE_READMODE_MESSAGE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,4096,4096,0,&attributes);
    const auto pipeError=GetLastError();OwnedHandle pipeOwner{pipe};
#ifdef SEASON_BRIDGE_FIXTURE
    std::wcerr<<L"Owned server pipe: "<<input.pipe<<L", valid="<<(pipe!=INVALID_HANDLE_VALUE)<<L"\n";
#endif
    if(pipe==INVALID_HANDLE_VALUE){failed.error=pipeError;return failed;}
    if(s->noticeCausality&&!fixture&&!s->noticeCausality->arm(input.controllerPid,input.controllerStartFileTime)){failed.error=ERROR_NOT_SUPPORTED;return failed;}
    if(!fixture)initialize(s->noticeCausality!=nullptr);
    bool prepared=true;
    for(auto& p:patches){
        p.target=fixture?static_cast<BYTE*>(*p.original):reinterpret_cast<BYTE*>(input.module+p.rva);
        MEMORY_BASIC_INFORMATION info{};
        if(!VirtualQuery(p.target,&info,sizeof(info))||info.State!=MEM_COMMIT||info.Protect!=PAGE_EXECUTE_READ||
           (!fixture&&std::memcmp(p.target,p.expected,p.size))){prepared=false;break;}
        p.protect=info.Protect;p.before.assign(p.target,p.target+p.size);p.installed.resize(p.size);*p.original=p.target;
    }
    if(!prepared){failed.error=ERROR_REVISION_MISMATCH;return failed;}
    current.store(s);auto error=transaction(true);
    if(error){auto receipt=finish(s,false);receipt.error=error;return receipt;}
    hooked=true;
    bool protections=true;for(auto& p:patches){std::memcpy(p.installed.data(),p.target,p.size);protections=restoreProtection(p)&&protections;}
    Io io{pipe,controller,s,GetTickCount64(),true};
    uint64_t sequence=0;
    if(protections&&io.connect())while(io.alive()){
        Message message{};if(!io.exchange(&message,sizeof(message),false))break;
        if(message.magic!=Magic||message.version!=Version||message.size!=sizeof(message)||
           message.generation!=input.generation||message.sequence<=sequence)break;
        sequence=message.sequence;io.beat=GetTickCount64();
        Reply reply{Magic,Version,sizeof(Reply),0,input.generation,sequence,s->draws.load(),s->thread.load()};
        if(!clock(*s,reply.year,reply.month,reply.day)){reply.error=ERROR_INVALID_DATA;s->admission=false;}
        else if(message.command==Hello)reply.error=reply.actualDraws?0:ERROR_IO_PENDING;
        else if(message.command==Apply&&s->sceneData.bytes.empty())reply.error=ERROR_NOT_SUPPORTED;
        else if(message.command==ApplyTreeColor||message.command==Apply){
            TreeColorParameters parameters{sequence,message.year,message.month,message.day};std::memcpy(parameters.weights,message.weights,16);
            parameters.full=message.command==Apply;
            parameters.seasonal=message.reserved==0;
            if(message.reserved>1||(message.reserved&&message.command!=Apply)||s->sources.bytes.size()!=TreeSourceBytes||!validTreeColor(parameters))reply.error=ERROR_INVALID_DATA;
            else if(message.year!=reply.year||message.month!=reply.month||message.day!=reply.day)reply.error=ERROR_RETRY;
            else {if(auto perf=s->performanceActive.load();perf&&perf->seasonal!=uint32_t(parameters.seasonal))perf->stop(6);s->pending.store(std::make_shared<TreeColorParameters>(parameters));}
        }else if(message.command==8)reply.error=performanceStart(s);
        else if(message.command==9){if(!s->performance)reply.error=ERROR_INVALID_STATE;else s->performance->stop(3);}
        else if(message.command==10){if(!s->performance)reply.error=ERROR_INVALID_STATE;}
        else if(message.command==11)reply.error=performanceDetach(s);
        else if(message.command!=Heartbeat&&message.command!=Stop&&message.command!=TreeColorStatus&&message.command!=SceneStatus)reply.error=ERROR_INVALID_FUNCTION;
        if(auto pending=s->pending.load())reply.materialEpoch=pending->epoch;
        reply.completedEpoch=s->completedEpoch.load(std::memory_order_acquire);reply.treeDraws=s->treeDraws.load();
        reply.swaps=s->swaps.load();reply.restores=s->restores.load();reply.gpuBytes=s->gpuBytes.load();
        reply.generated=s->generated.load();reply.alphaPixels=s->alphaPixels.load();
        reply.colorScope=reply.completedEpoch?1:0;reply.gpuStateVerified=reply.swaps==reply.restores?1:0;
        for(unsigned i=0;i<16;i++)reply.selection[i]=s->selection[i].load();
        if(auto pending=s->pending.load();pending&&pending->full){
            reply.capabilities=pending->seasonal?7:8;reply.colorScope=0;
            reply.rendered=reply.completedEpoch==pending->epoch&&pending->year==reply.year&&pending->month==reply.month&&pending->day==reply.day&&
                reply.gpuStateVerified&&reply.selection[13]&&reply.selection[14];
        }
        // A scoped color receipt never sets the full season Rendered flag.
        if(message.command==TreeColorStatus&&(!reply.materialEpoch||reply.completedEpoch!=reply.materialEpoch))reply.error=ERROR_IO_PENDING;
        if(message.command==SceneStatus&&!reply.rendered)reply.error=ERROR_IO_PENDING;
        std::string performanceJson;
        if(message.command==10&&s->performance){s->performance->active();reply.selection[0]=s->performance->collecting?1:0;reply.selection[1]=s->performance->reason;}
        if(message.command==11&&!reply.error){performanceJson=s->performance->json();if(performanceJson.size()>8*1024*1024)reply.error=ERROR_BUFFER_OVERFLOW;else reply.selection[0]=uint32_t(performanceJson.size());}
        if(!io.exchange(&reply,sizeof(reply),true)||message.command==Stop||reply.error==ERROR_INVALID_DATA)break;
        if(message.command==11&&!reply.error&&!io.exchange(performanceJson.data(),DWORD(performanceJson.size()),true))break;
    }
    io.stop();DisconnectNamedPipe(pipe);return io.receipt;
}
}
#ifndef SEASON_BRIDGE_FIXTURE
extern "C" __declspec(dllexport) DWORD WINAPI SeasonBridgeSession(Bootstrap* request){
    if(!request)return ERROR_INVALID_PARAMETER;Bootstrap input{};
    if(!copyBootstrap(&input,request))return ERROR_INVALID_ADDRESS;
    if(input.magic!=Magic||input.version!=Version||input.size!=sizeof(Bootstrap)||!input.generation||
       !input.controllerPid||input.leaseMs<1000||input.leaseMs>5000||input.module!=reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr))||
       !input.world||!input.state||!input.manager||!input.scene||!input.driver||!input.device||!input.context||
       input.pipe[127]!=0||wcsncmp(input.pipe,L"\\\\.\\pipe\\san14-seasons-",23))return ERROR_INVALID_PARAMETER;
    HMODULE pinned{};if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&SeasonBridgeSession),&pinned))return GetLastError();
    Recovery receipt{};
    try{receipt=serve(input);}
    catch(...){
        receipt={Magic,Version,sizeof(Recovery),ERROR_NOT_ENOUGH_MEMORY,input.generation};
        if(auto s=current.load()){
            s->admission=false;
            try{receipt=finish(s,hooked.load());receipt.error=ERROR_NOT_ENOUGH_MEMORY;}catch(...){/* retain closed session and report unconfirmed recovery */}
        }
    }
    // The controller reads this complete immutable receipt only after this exact
    // worker handle is signaled. No running-request readback/cancel-byte race.
    request->recovery=receipt;return receipt.error;
}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(h);return TRUE;}
#endif
