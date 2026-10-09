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
namespace {
struct OwnedHandle {HANDLE value;~OwnedHandle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}};
struct OwnedLocal {void* value=nullptr;~OwnedLocal(){if(value)LocalFree(value);}};
bool copyBootstrap(Bootstrap* destination,const Bootstrap* source) noexcept{
    __try{*destination=*source;return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
using Draw=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t);
using Mesh=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t);
Draw originalDraw,originalLand;Mesh originalMesh;
using Prepare=uint8_t(__fastcall*)(void*,uint32_t);
Prepare originalPrepare;
SRWLOCK dispatch=SRWLOCK_INIT;std::mutex connection;
std::atomic<long> callbacks{0};
std::atomic<bool> hooked{false};
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
        auto world=read<uint64_t>(i.module+0x277df18)+0x290;
        auto state=read<uint64_t>(world,0x86f58),manager=read<uint64_t>(i.module+0x1b9fb70);
        auto scene=read<uint64_t>(manager,0x210),parent=read<uint64_t>(scene,0x50),driver=read<uint64_t>(parent,0x18);
        return world==i.world&&state==i.state&&manager==i.manager&&read<uint32_t>(manager,8)==0&&scene==i.scene&&
            driver==i.driver&&read<uint64_t>(driver,0x10)==i.context&&read<uint64_t>(driver,0x3078)==i.device;
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
    auto active=s.pending.load();return s.admission&&request&&active&&(active==request||(request->full&&active->full));
}
void completedMaterialPass(Session& s,const TreeColorParameters& p,uint32_t pass){
    if(s.fullEpoch!=p.epoch||s.fullClock!=packedDate(p)){s.fullEpoch=p.epoch;s.fullClock=packedDate(p);s.fullPasses=0;}
    s.fullPasses|=pass;
}
struct Scope {
    DWORD error;unsigned csr;std::shared_ptr<Session> session;
    Scope():error(GetLastError()),csr(_mm_getcsr()){
        callbacks.fetch_add(1);AcquireSRWLockShared(&dispatch);session=current.load();
        _mm_setcsr(csr);SetLastError(error);
    }
    void preserve(){error=GetLastError();csr=_mm_getcsr();}
    void restore(){_mm_setcsr(csr);SetLastError(error);}
    ~Scope(){session.reset();ReleaseSRWLockShared(&dispatch);callbacks.fetch_sub(1);_mm_setcsr(csr);SetLastError(error);}
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
    if(!vo||!po||read<uint64_t>(vo)!=s.input.module+0x14beb58||read<uint64_t>(po)!=s.input.module+0x14beb90)return false;
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
    uint32_t path=0;SeasonSceneTicket scene;
};
thread_local MaterialTicket* materialTicket=nullptr;
void enterTree(MaterialTicket& ticket) noexcept {
    auto s=ticket.scope->session;
    if(s&&treeCount(ticket.count)&&ticket.start==0)++s->selection[1];
    if(!s||!s->admission.load()||ticket.swapped||!treeCount(ticket.count)||ticket.start!=0||
       !s->context||s->thread.load()!=GetCurrentThreadId()||!s->sameScene())return;
    ticket.parameters=s->pending.load();if(!ticket.parameters)return;
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
            if(!vo||!po||read<uint64_t>(vo)!=s->input.module+0x14beb58||read<uint64_t>(po)!=s->input.module+0x14beb90)return;
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
            s->gpuBytes=s->tree->gpuBytes()+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes();
        }
        ticket.saved=source;context->PSSetShaderResources(0,1,&replacement);ticket.swapped=true;++s->swaps;
        ++s->selection[11];
    }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;}
}
void leaveTree(MaterialTicket& ticket,uint32_t before) noexcept {
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
    auto s=ticket.scope->session;
    if(!s||!s->admission||!s->context||s->thread!=GetCurrentThreadId()||!s->sameScene()||ticket.scene.changed)return;
    ticket.parameters=s->pending.load();if(!ticket.parameters||!ticket.parameters->full)return;
    if(read<uint64_t>(reinterpret_cast<uint64_t>(ticket.driver),0x10)!=s->input.context)return;
    try{
        if(!renderParameters(*s,ticket.parameters,ticket.rendered))return;const auto& p=ticket.rendered;
        uint32_t kind=sceneShader(*s,ticket.driver,3)?3u:sceneShader(*s,ticket.driver,5)?5u:0u;if(!kind)return;
        auto sourceResult=s->groundSources.admit(s->context.Get(),kind==3,p.weights[1]+p.weights[2]>0);
        if(sourceResult==S_FALSE){s->selection[15]=s->groundSources.rejection();return;}
        if(FAILED(sourceResult)){s->failure=static_cast<uint32_t>(sourceResult);s->admission=false;return;}
        if(!renderRequestActive(*s,ticket.parameters)||read<uint32_t>(s->input.state,0x34)!=packedDate(p))return;
        if(!s->sceneGpu)s->sceneGpu=std::make_unique<SeasonSceneGpu>(s->sceneData);
        auto hr=s->sceneGpu->enter(s->context.Get(),p,kind,0,ticket.scene,
            s->groundSources.riceControlReference(),s->groundSources.vegetationDetailReference());
        if(hr==S_OK){++s->swaps;s->gpuBytes=(s->tree?s->tree->gpuBytes():0)+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes();}
        else if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;}
    }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;}
}
void leaveLand(MaterialTicket& ticket,uint32_t before)noexcept{
    if(!ticket.scene.changed)return;auto s=ticket.scope->session;
    if(!s->sceneGpu->leave(s->context.Get(),ticket.scene)){s->failure=ERROR_INVALID_STATE;s->admission=false;return;}++s->restores;
    const auto& p=ticket.rendered;
    if(renderRequestActive(*s,ticket.parameters)&&read<uint32_t>(s->input.state,0x34)==packedDate(p)&&read<uint32_t>(reinterpret_cast<uint64_t>(ticket.driver),0x2ad0)==before+1){
        if(ticket.scene.kind==3){
            // The pinned original LandCode passes exactly these arguments to
            // DrawIndexedInstanced: index count from driver+90's +1c, instance
            // count from the third engine argument, with all starts/base zero.
            try{auto indices=read<uint32_t>(read<uint64_t>(reinterpret_cast<uint64_t>(ticket.driver),0x90),0x1c);
                auto hr=s->sceneGpu->collectRice(s->context.Get(),p,indices,ticket.count,s->groundSources.riceControlReference());
                if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;return;}
            }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;return;}
        }
        s->gpuBytes=(s->tree?s->tree->gpuBytes():0)+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes();
        completedMaterialPass(*s,p,ticket.scene.kind==5?8u:4u);++s->selection[13];
    }
}
void afterScene(Scope& scope,void* driver,uint32_t start,uint32_t count,uint32_t before)noexcept{
    auto s=scope.session;if(!s||!s->admission||!s->sceneGpu||start||count!=3||!s->context||s->thread!=GetCurrentThreadId()||!s->sameScene())return;
    auto p=s->pending.load();TreeColorParameters rendered{};
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
        if(hr==S_OK){++s->selection[14];s->gpuBytes=(s->tree?s->tree->gpuBytes():0)+s->sceneGpu->gpuBytes()+s->groundSources.gpuBytes();
            // Trees can be entirely culled at distant zoom. A real terrain Draw
            // followed by the admitted scene composite completes this epoch;
            // absent tree passes must not leave the product permanently pending.
            if((s->fullPasses&12)!=0&&s->fullEpoch==p->epoch&&s->pending.load()==p&&packedDate(*p)==packedDate(rendered)&&
                read<uint32_t>(s->input.state,0x34)==packedDate(rendered))s->completedEpoch.store(p->epoch,std::memory_order_release);s->fullPasses=0;}
        else if(FAILED(hr)){s->failure=static_cast<uint32_t>(hr);s->admission=false;}
    }catch(...){s->failure=ERROR_NOT_ENOUGH_MEMORY;s->admission=false;}
}
uint8_t __fastcall onPrepare(void* driver,uint32_t mode){
    // Prepare is nested inside the locked engine Draw. Taking the same SRW
    // lock recursively could deadlock behind an exclusive detach waiter.
    bool nested=materialTicket!=nullptr;if(!nested)AcquireSRWLockShared(&dispatch);
    auto result=originalPrepare(driver,mode);auto error=GetLastError();auto csr=_mm_getcsr();
    if(result&&nested&&materialTicket->driver==driver){if(materialTicket->path==1)enterLand(*materialTicket);else enterTree(*materialTicket);}
    if(!nested)ReleaseSRWLockShared(&dispatch);_mm_setcsr(csr);SetLastError(error);return result;
}
void __fastcall onDraw(void* d,uint32_t mode,uint32_t start,uint32_t count){Scope s;auto before=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);
    originalDraw(d,mode,start,count);s.preserve();observed(s,d,before);afterScene(s,d,start,count,before);}
void __fastcall onLand(void* d,uint32_t mode,uint32_t count,uint32_t unused){Scope s;auto before=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);
    MaterialTicket ticket{&s,d,count,0};ticket.path=1;auto* previous=materialTicket;materialTicket=previous?nullptr:&ticket;s.restore();
    originalLand(d,mode,count,unused);s.preserve();materialTicket=previous;leaveLand(ticket,before);observed(s,d,before);}
void __fastcall onMesh(void* d,uint32_t mode,uint32_t a,uint32_t b,uint32_t start,uint32_t count,uint32_t instances){Scope s;
    if(s.session&&treeCount(count)&&start==0)++s.session->selection[0];
    auto before=read<uint32_t>(reinterpret_cast<uint64_t>(d),0x2ad0);MaterialTicket ticket{&s,d,count,start};
    auto* previous=materialTicket;materialTicket=previous?nullptr:&ticket;s.restore();
    originalMesh(d,mode,a,b,start,count,instances);s.preserve();materialTicket=previous;
    leaveTree(ticket,before);observed(s,d,before);}
struct Patch {PVOID* original;void* replacement;uint32_t rva;const BYTE* expected;size_t size;BYTE* target;DWORD protect;std::vector<BYTE> before,installed;};
Patch patches[4];
void initialize(){using namespace season::frame;
    patches[0]={reinterpret_cast<PVOID*>(&originalDraw),reinterpret_cast<void*>(onDraw),0x960f90,DrawCode,sizeof(DrawCode)};
    patches[1]={reinterpret_cast<PVOID*>(&originalLand),reinterpret_cast<void*>(onLand),0x960ee0,LandCode,sizeof(LandCode)};
    patches[2]={reinterpret_cast<PVOID*>(&originalMesh),reinterpret_cast<void*>(onMesh),0x960e90,MeshCode,sizeof(MeshCode)};
    patches[3]={reinterpret_cast<PVOID*>(&originalPrepare),reinterpret_cast<void*>(onPrepare),0x963c60,ExpectedPrepareCode,sizeof(ExpectedPrepareCode)};
}
LONG transaction(bool attach){
    AcquireSRWLockExclusive(&dispatch);std::vector<HANDLE> threads;LONG result=NO_ERROR;
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
        if(!result)for(auto& p:patches){result=attach?DetourAttach(p.original,p.replacement):DetourDetach(p.original,p.replacement);if(result)break;}
        if(!result)result=DetourTransactionCommit();else DetourTransactionAbort();
    }}
    for(auto h:threads)CloseHandle(h);ReleaseSRWLockExclusive(&dispatch);return result;
}
bool restoreProtection(Patch& p){
    MEMORY_BASIC_INFORMATION info{};if(!VirtualQuery(p.target,&info,sizeof(info)))return false;
    if(info.Protect!=p.protect){DWORD previous{};if(!VirtualProtect(p.target,p.size,p.protect,&previous)||!VirtualQuery(p.target,&info,sizeof(info)))return false;}
    return info.Protect==p.protect;
}
Recovery finish(const std::shared_ptr<Session>& s,bool attached){
    Recovery receipt{Magic,Version,sizeof(Recovery),0,s->input.generation,s->draws.load(),s->thread.load()};
    s->admission=false;
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
        s->sceneGpu.reset();s->shaderIdentities.clear();s->groundSources.clear();s->context.Reset();s->device.Reset();receipt.resourcesReleased=1;}
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
    bool attached;Recovery receipt{};bool ended=false;
    void stop(){if(!ended){receipt=finish(session,attached);ended=true;}}
    bool alive(){uint32_t y{},m{},d{};
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
    if(!fixture)initialize();
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
            if(s->sources.bytes.size()!=TreeSourceBytes||!validTreeColor(parameters))reply.error=ERROR_INVALID_DATA;
            else if(message.year!=reply.year||message.month!=reply.month||message.day!=reply.day)reply.error=ERROR_RETRY;
            else s->pending.store(std::make_shared<TreeColorParameters>(parameters));
        }else if(message.command!=Heartbeat&&message.command!=Stop&&message.command!=TreeColorStatus&&message.command!=SceneStatus)reply.error=ERROR_INVALID_FUNCTION;
        if(auto pending=s->pending.load())reply.materialEpoch=pending->epoch;
        reply.completedEpoch=s->completedEpoch.load(std::memory_order_acquire);reply.treeDraws=s->treeDraws.load();
        reply.swaps=s->swaps.load();reply.restores=s->restores.load();reply.gpuBytes=s->gpuBytes.load();
        reply.generated=s->generated.load();reply.alphaPixels=s->alphaPixels.load();
        reply.colorScope=reply.completedEpoch?1:0;reply.gpuStateVerified=reply.swaps==reply.restores?1:0;
        for(unsigned i=0;i<16;i++)reply.selection[i]=s->selection[i].load();
        if(auto pending=s->pending.load();pending&&pending->full){
            reply.capabilities=7;reply.colorScope=0;
            reply.rendered=reply.completedEpoch==pending->epoch&&pending->year==reply.year&&pending->month==reply.month&&pending->day==reply.day&&
                reply.gpuStateVerified&&reply.selection[13]&&reply.selection[14];
        }
        // A scoped color receipt never sets the full season Rendered flag.
        if(message.command==TreeColorStatus&&(!reply.materialEpoch||reply.completedEpoch!=reply.materialEpoch))reply.error=ERROR_IO_PENDING;
        if(message.command==SceneStatus&&!reply.rendered)reply.error=ERROR_IO_PENDING;
        if(!io.exchange(&reply,sizeof(reply),true)||message.command==Stop||reply.error==ERROR_INVALID_DATA)break;
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
