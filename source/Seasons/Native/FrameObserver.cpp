// Bounded read-only engine Draw/Present chronology. No command table or native
// D3D11/DXGI vtable is patched. No GPU resource contents or bindings are changed.
#define NOMINMAX
#include "FrameObserver.h"
#include "FrameSignatures.h"
#include <windows.h>
#include <tlhelp32.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <intrin.h>
#include <atomic>
#include <vector>
#include <cstring>
#include "detours.h"
using Microsoft::WRL::ComPtr;
using namespace season::frame;
namespace {
using D6=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t);
using D4=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t);
using D7=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t);
using DI=void(__fastcall*)(void*,uint32_t,void*,uint32_t);
using D2=void(__fastcall*)(void*,uint32_t);
using DV=void(__fastcall*)(void*,uint32_t,uint32_t,const void*,uint32_t);
using D1=void(__fastcall*)(void*);
using Prep=uint8_t(__fastcall*)(void*,uint32_t);
using Present=uint64_t(__fastcall*)(void*,void*);
D6 indexed,instanced;D4 land,draw;D7 mesh;DI indirect;D2 automatic;DV inlineDraw;D1 batch;Prep prepare;Present present;
Request* request;
SRWLOCK drawLock=SRWLOCK_INIT,presentLock=SRWLOCK_INIT;
std::atomic<long> active{0};std::atomic<unsigned> reserved{0};std::atomic<bool> admission{false},used{false};
thread_local unsigned drawDepth=0,presentDepth=0;
uint64_t deadline;
struct Patch {PVOID* original;void* replacement;const BYTE* expected;size_t size;uint32_t rva;BYTE* target;std::vector<BYTE> before,installed;DWORD protection;};
Patch patches[PatchCount];
struct BindingKey {uint64_t driver,psOwner,vsOwner,sequence;uint32_t kind,mode;};
BindingKey bindingKeys[512]{};UINT bindingKeyCount=0; // Verified render thread only; no callback allocation.
bool copy(void* destination,const void* source,size_t n) noexcept {
    MEMORY_BASIC_INFORMATION m{};
    if(!source||!n||!VirtualQuery(source,&m,sizeof(m))||m.State!=MEM_COMMIT||
       (m.Protect&(PAGE_NOACCESS|PAGE_GUARD))||reinterpret_cast<uintptr_t>(source)<reinterpret_cast<uintptr_t>(m.BaseAddress)||
       n>m.RegionSize-(reinterpret_cast<uintptr_t>(source)-reinterpret_cast<uintptr_t>(m.BaseAddress)))return false;
    __try{std::memcpy(destination,source,n);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
template<class T>T read(void* p,size_t offset) noexcept {
    // Only engine-owned callback objects: the authenticated original call uses
    // the same driver fields. Guard a failed read with SEH; retain VirtualQuery
    // validation for Hook code/protection in copy/run/recovery.
    T result{};if(!p)return result;
    __try{std::memcpy(&result,static_cast<BYTE*>(p)+offset,sizeof(result));}
    __except(EXCEPTION_EXECUTE_HANDLER){return T{};}
    return result;
}
#ifdef SEASON_FRAME_SCENE_READ
#include "SceneBoundaryReadGpu.h"
#endif
uint64_t qpc(){LARGE_INTEGER t{};QueryPerformanceCounter(&t);return t.QuadPart;}
Row* reserve(){
    auto n=reserved.load();
    while(n<Capacity){if(reserved.compare_exchange_weak(n,n+1)){auto* r=&request->rows[n];r->sequence=n;return r;}}
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&request->saturated),1);admission=false;return nullptr;
}
struct Scope {
    bool outer,isPresent,admitted;DWORD error;unsigned csr;
    Scope(bool p=false):isPresent(p),error(GetLastError()),csr(_mm_getcsr()){
        active.fetch_add(1);outer=(p?presentDepth++:drawDepth++)==0;
        if(outer)AcquireSRWLockShared(p?&presentLock:&drawLock);
        admitted=admission.load()&&GetTickCount64()<deadline;
        _mm_setcsr(csr);SetLastError(error);
    }
    void restore(){_mm_setcsr(csr);SetLastError(error);}
    void preserve(){error=GetLastError();csr=_mm_getcsr();}
    ~Scope(){
        if(isPresent)--presentDepth;else --drawDepth;
        if(outer)ReleaseSRWLockShared(isPresent?&presentLock:&drawLock);
        active.fetch_sub(1);_mm_setcsr(csr);SetLastError(error);
    }
};
void state(Row& r,void* driver,uint32_t kind,uint32_t mode,uint32_t start,uint32_t count,uint32_t instances){
    r.qpcBegin=qpc();r.driver=reinterpret_cast<uint64_t>(driver);r.kind=kind;r.thread=GetCurrentThreadId();
    r.mode=mode;r.start=start;r.count=count;r.instances=instances;
    auto* raw=read<ID3D11DeviceContext*>(driver,0x10);
    r.context=reinterpret_cast<uint64_t>(raw);r.psOwner=read<uint64_t>(driver,0xb8);r.vsOwner=read<uint64_t>(driver,0xa8);
    if(GetCurrentThreadId()!=request->renderThread){
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&request->otherDrawThreads));r.reserved=1;
        r.bindingSampleSequence=UINT64_MAX;return; // CPU chronology only, never acquire/use that thread's GPU context.
    }
    if(request->sparseBindings){
        for(UINT i=0;i<bindingKeyCount;i++){const auto& key=bindingKeys[i];if(key.driver==r.driver&&key.psOwner==r.psOwner&&key.vsOwner==r.vsOwner&&key.kind==kind&&key.mode==mode){
            r.bindingSampleSequence=key.sequence;return;
        }}
    }
    ComPtr<ID3D11DeviceContext> context=raw;
    ComPtr<ID3D11PixelShader> ps;ComPtr<ID3D11VertexShader> vs;
    context->PSGetShader(&ps,nullptr,nullptr);context->VSGetShader(&vs,nullptr,nullptr);
    r.ps=reinterpret_cast<uint64_t>(ps.Get());r.vs=reinterpret_cast<uint64_t>(vs.Get());
    ID3D11ShaderResourceView* views[12]{};ID3D11RenderTargetView* targets[8]{};ID3D11DepthStencilView* depth{};
    context->PSGetShaderResources(0,12,views);context->OMGetRenderTargets(8,targets,&depth);
    for(unsigned i=0;i<12;i++){r.views[i]=reinterpret_cast<uint64_t>(views[i]);if(views[i])views[i]->Release();}
    for(unsigned i=0;i<8;i++){r.targets[i]=reinterpret_cast<uint64_t>(targets[i]);if(targets[i])targets[i]->Release();}
    r.depth=reinterpret_cast<uint64_t>(depth);if(depth)depth->Release();
    ID3D11Buffer* vertices[8]{};ID3D11Buffer* index{};DXGI_FORMAT fmt{};UINT offset{};ID3D11InputLayout* layout{};
    context->IAGetVertexBuffers(0,8,vertices,r.strides,r.offsets);
    context->IAGetIndexBuffer(&index,&fmt,&offset);context->IAGetInputLayout(&layout);
    D3D11_PRIMITIVE_TOPOLOGY topology{};context->IAGetPrimitiveTopology(&topology);r.topology=topology;
    for(unsigned i=0;i<8;i++){r.vertexBuffers[i]=reinterpret_cast<uint64_t>(vertices[i]);if(vertices[i])vertices[i]->Release();}
    r.indexBuffer=reinterpret_cast<uint64_t>(index);r.indexFormat=fmt;r.indexOffset=offset;if(index)index->Release();
    r.inputLayout=reinterpret_cast<uint64_t>(layout);if(layout)layout->Release();
    UINT n=1;D3D11_VIEWPORT viewport{};context->RSGetViewports(&n,&viewport);
    if(n==1)std::memcpy(r.viewport,&viewport,sizeof(viewport));
    // All getter/context COM references are destroyed before completion publication.
    r.bindingsCaptured=1;r.bindingSampleSequence=r.sequence;++request->bindingSamples;
    if(request->sparseBindings){
        if(bindingKeyCount<512)bindingKeys[bindingKeyCount++]={r.driver,r.psOwner,r.vsOwner,r.sequence,kind,mode};
        else {InterlockedExchange(reinterpret_cast<volatile LONG*>(&request->saturated),1);admission=false;}
    }
}
bool eligible(Scope& s,void* driver){
    if(!s.admitted)return false;
    if(read<uint64_t>(driver,0x10)!=request->context){InterlockedIncrement(reinterpret_cast<volatile LONG*>(&request->rejected));return false;}
    return true;
}
void recordDraw(Scope& s,void* driver,uint32_t before,uint32_t kind,uint32_t mode,uint32_t start,uint32_t count,uint32_t instances=1){
    if(!eligible(s,driver))return;
    auto after=read<uint32_t>(driver,0x2ad0);
    if(after!=before+1){InterlockedIncrement(reinterpret_cast<volatile LONG*>(&request->skipped));return;}
#ifdef SEASON_FRAME_SCENE_READ
    if(!boundaryAfterDraw(driver,kind,start,count,before,after))return;
#endif
    if(auto* r=reserve()){state(*r,driver,kind,mode,start,count,instances);r->counterBefore=before;r->counterAfter=after;
        r->qpcEnd=qpc();InterlockedExchange(reinterpret_cast<volatile LONG*>(&r->complete),1);}
}
void __fastcall onIndexed(void* d,uint32_t m,uint32_t a,uint32_t b,uint32_t start,uint32_t count){Scope s;auto before=read<uint32_t>(d,0x2ad0);
#ifdef SEASON_FRAME_SCENE_READ
    BoundaryTicket ticket{d,s.admitted,start,count};auto* previous=boundaryTicket;boundaryTicket=&ticket;
#endif
    s.restore();indexed(d,m,a,b,start,count);s.preserve();recordDraw(s,d,before,0,m,start,count);
#ifdef SEASON_FRAME_SCENE_READ
    boundaryTicket=previous;
#endif
}
void __fastcall onLand(void* d,uint32_t m,uint32_t n,uint32_t unused){Scope s;auto before=read<uint32_t>(d,0x2ad0);s.restore();land(d,m,n,unused);s.preserve();recordDraw(s,d,before,1,m,0,read<uint32_t>(read<void*>(d,0x90),0x1c),n);}
void __fastcall onMesh(void* d,uint32_t m,uint32_t a,uint32_t b,uint32_t start,uint32_t count,uint32_t n){Scope s;auto before=read<uint32_t>(d,0x2ad0);s.restore();mesh(d,m,a,b,start,count,n);s.preserve();recordDraw(s,d,before,2,m,start,count,n);}
void __fastcall onDraw(void* d,uint32_t m,uint32_t start,uint32_t count){Scope s;auto before=read<uint32_t>(d,0x2ad0);s.restore();draw(d,m,start,count);s.preserve();recordDraw(s,d,before,3,m,start,count);}
void __fastcall onInstanced(void* d,uint32_t m,uint32_t count,uint32_t n,uint32_t start,uint32_t first){Scope s;auto before=read<uint32_t>(d,0x2ad0);s.restore();instanced(d,m,count,n,start,first);s.preserve();recordDraw(s,d,before,4,m,start,count,n);}
void __fastcall onIndirect(void* d,uint32_t m,void* buffer,uint32_t offset){Scope s;auto before=read<uint32_t>(d,0x2ad0);s.restore();indirect(d,m,buffer,offset);s.preserve();recordDraw(s,d,before,5,m,offset,0,0);}
void __fastcall onAuto(void* d,uint32_t m){Scope s;auto before=read<uint32_t>(d,0x2ad0);s.restore();automatic(d,m);s.preserve();recordDraw(s,d,before,6,m,0,0,0);}
void __fastcall onInline(void* d,uint32_t m,uint32_t count,const void* vertices,uint32_t stride){Scope s;auto before=read<uint32_t>(d,0x2ad0);s.restore();inlineDraw(d,m,count,vertices,stride);s.preserve();recordDraw(s,d,before,7,m,0,count);}
struct BatchTicket {void* driver;Row* row=nullptr;uint32_t before=0;bool selected=false;};
thread_local BatchTicket* currentBatch=nullptr;
uint8_t __fastcall onPrepare(void* d,uint32_t mode){
    Scope s;s.restore();auto result=prepare(d,mode);s.preserve();
#ifdef SEASON_FRAME_SCENE_READ
    if(result&&boundaryTicket&&boundaryTicket->admitted&&boundaryTicket->driver==d&&eligible(s,d))boundaryBeforeIndexed(d);
#endif
    if(result&&currentBatch&&currentBatch->driver==d&&!currentBatch->row&&eligible(s,d)){
        auto* r=reserve();if(r){currentBatch->row=r;currentBatch->before=read<uint32_t>(d,0x2ad0);currentBatch->selected=true;
            const bool unindexed=read<uint32_t>(d,0x2a40)==1;
            const auto start=read<uint32_t>(d,unindexed?0x2a30:0x2a34);
            const auto end=read<uint32_t>(d,unindexed?0x2a38:0x2a3c);
            state(*r,d,8,mode,start,end-start,1);r->counterBefore=currentBatch->before;}
    }
    return result;
}
void __fastcall onBatch(void* d){
    Scope s;BatchTicket ticket{d};auto* previous=currentBatch;currentBatch=&ticket;s.restore();batch(d);s.preserve();currentBatch=previous;
    if(ticket.row){auto* r=ticket.row;r->counterAfter=read<uint32_t>(d,0x2ad0);r->qpcEnd=qpc();
        // The authenticated batch code adds one after native Draw, then subtracts
        // one on exit. A failed Prepare has no ticket and is never called a Draw.
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&r->complete),ticket.selected&&r->counterAfter==ticket.before?1:2);}
}
void presentRow(void* driver,void* parameters,uint32_t kind){
    if(reinterpret_cast<uint64_t>(driver)!=request->sceneDriver||read<uint64_t>(driver,0x10)!=request->context)return;
    auto* settings=parameters?parameters:static_cast<BYTE*>(driver)+0x2fc0;
    const bool testRequested=read<uint8_t>(settings,0)!=0;
    auto* alternate=read<void*>(settings,8);
    auto* chain=(testRequested&&alternate)?alternate:read<void*>(driver,0x3080);
    const bool enabled=chain&&(!testRequested||alternate);
    if(auto* r=reserve()){r->qpcBegin=qpc();r->kind=kind;r->thread=GetCurrentThreadId();r->driver=reinterpret_cast<uint64_t>(driver);
        r->context=request->context;r->swapchain=reinterpret_cast<uint64_t>(chain);r->presentEnabled=enabled;
        r->syncInterval=testRequested?0:read<uint32_t>(driver,0x305c);r->presentFlags=(testRequested&&!alternate)?DXGI_PRESENT_TEST:0;
        r->qpcEnd=qpc();InterlockedExchange(reinterpret_cast<volatile LONG*>(&r->complete),1);}
}
uint64_t __fastcall onPresent(void* d,void* parameters){
    Scope s(true);if(s.admitted)presentRow(d,parameters,9);
    // Present may wait for a different render thread. The detach worker always
    // takes presentLock before drawLock, so that render thread stays unblocked.
    _mm_setcsr(s.csr);SetLastError(s.error);auto result=present(d,parameters);s.preserve();
    if(s.admitted){presentRow(d,parameters,10);
        auto n=InterlockedIncrement(reinterpret_cast<volatile LONG*>(&request->presentReturns));
        if(request->targetPresentReturns&&n>=static_cast<LONG>(request->targetPresentReturns))admission=false;
    }return result;
}
template<class T,size_t N>void add(unsigned i,T& original,void* replacement,uint32_t rva,const BYTE(&code)[N]){
    patches[i]={reinterpret_cast<PVOID*>(&original),replacement,code,N,rva};
}
void initialize(){
#ifdef SEASON_FRAME_SCENE_READ
    add(0,draw,reinterpret_cast<void*>(onDraw),0x960f90,DrawCode);
    add(1,indexed,reinterpret_cast<void*>(onIndexed),0x960e50,IndexedCode);
    add(2,prepare,reinterpret_cast<void*>(onPrepare),0x963c60,PrepareCode);
#else
    add(0,indexed,reinterpret_cast<void*>(onIndexed),0x960e50,IndexedCode);
    add(1,land,reinterpret_cast<void*>(onLand),0x960ee0,LandCode);
    add(2,mesh,reinterpret_cast<void*>(onMesh),0x960e90,MeshCode);
    add(3,draw,reinterpret_cast<void*>(onDraw),0x960f90,DrawCode);
    add(4,instanced,reinterpret_cast<void*>(onInstanced),0x960fe0,InstancedCode);
    add(5,indirect,reinterpret_cast<void*>(onIndirect),0x961040,IndirectCode);
    add(6,automatic,reinterpret_cast<void*>(onAuto),0x960e20,AutoCode);
    add(7,inlineDraw,reinterpret_cast<void*>(onInline),0x961090,InlineCode);
    add(8,batch,reinterpret_cast<void*>(onBatch),0x9625f0,BatchCode);
    add(9,prepare,reinterpret_cast<void*>(onPrepare),0x963c60,PrepareCode);
    add(10,present,reinterpret_cast<void*>(onPresent),0x954730,PresentCode);
#endif
}
LONG transaction(bool attach){
    AcquireSRWLockExclusive(&presentLock);AcquireSRWLockExclusive(&drawLock);
    std::vector<HANDLE> threads;LONG result=NO_ERROR;HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if(snap==INVALID_HANDLE_VALUE)result=GetLastError();
    else {
        THREADENTRY32 e{sizeof(e)};
        if(!Thread32First(snap,&e))result=GetLastError();
        else do{if(e.th32OwnerProcessID!=GetCurrentProcessId()||e.th32ThreadID==GetCurrentThreadId())continue;
            HANDLE h=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_SET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,e.th32ThreadID);
            if(!h){request->failedThread=e.th32ThreadID;request->failedStage=1;result=GetLastError();break;}threads.push_back(h);
        }while(Thread32Next(snap,&e));
        if(result==NO_ERROR&&GetLastError()!=ERROR_NO_MORE_FILES)result=GetLastError();CloseHandle(snap);
    }
    if(result==NO_ERROR){result=DetourTransactionBegin();if(result==NO_ERROR){
        for(auto h:threads){DWORD exit{};if(GetExitCodeThread(h,&exit)&&exit!=STILL_ACTIVE)continue;
            result=DetourUpdateThread(h);if(result!=NO_ERROR){request->failedThread=GetThreadId(h);request->failedStage=2;break;}}
        if(result==NO_ERROR)for(auto& p:patches){result=attach?DetourAttach(p.original,p.replacement):DetourDetach(p.original,p.replacement);if(result!=NO_ERROR)break;}
        if(result==NO_ERROR)result=DetourTransactionCommit();else DetourTransactionAbort();
    }}
    ReleaseSRWLockExclusive(&drawLock);ReleaseSRWLockExclusive(&presentLock);for(auto h:threads)CloseHandle(h);return result;
}
bool protection(Patch& p,PatchReceipt& row){
    MEMORY_BASIC_INFORMATION m{};if(!VirtualQuery(p.target,&m,sizeof(m)))return false;
    if(m.Protect!=p.protection){DWORD previous{};if(!VirtualProtect(p.target,p.size,p.protection,&previous)||!VirtualQuery(p.target,&m,sizeof(m)))return false;}
    row.finalProtection=m.Protect;return m.Protect==p.protection;
}
bool recovery(){bool valid=true;
    for(unsigned i=0;i<PatchCount;i++){auto& p=patches[i];auto& r=request->patches[i];
        r.codeRestored=std::memcmp(p.target,p.before.data(),p.size)==0;r.protectionRestored=protection(p,r);
        valid=valid&&r.codeRestored&&r.protectionRestored;}return valid;
}
DWORD run(Request* r,bool fixture){
    request=r;
    if(r->magic!=Magic||r->version!=Version||r->size!=sizeof(Request)||r->durationMs<50||r->durationMs>(r->sparseBindings?10000u:500u)||r->sparseBindings>1||r->targetPresentReturns>3||
       !r->context||!r->device||!r->sceneDriver||!r->swapchain||!r->renderThread)return ERROR_INVALID_PARAMETER;
    if(used.exchange(true))return ERROR_ALREADY_EXISTS;
    LARGE_INTEGER f{};QueryPerformanceFrequency(&f);r->qpcFrequency=f.QuadPart;
    for(unsigned i=0;i<PatchCount;i++){auto& p=patches[i];auto& row=r->patches[i];
        p.target=fixture?static_cast<BYTE*>(*p.original):reinterpret_cast<BYTE*>(r->moduleBase+p.rva);
        MEMORY_BASIC_INFORMATION m{};if(!VirtualQuery(p.target,&m,sizeof(m))||m.State!=MEM_COMMIT||m.Protect!=PAGE_EXECUTE_READ)return ERROR_INVALID_ADDRESS;
        p.protection=m.Protect;p.before.resize(p.size);p.installed.resize(p.size);
        if(!copy(p.before.data(),p.target,p.size)||(!fixture&&std::memcmp(p.before.data(),p.expected,p.size)))return ERROR_REVISION_MISMATCH;
        *p.original=p.target;row.address=reinterpret_cast<uint64_t>(p.target);row.size=static_cast<uint32_t>(p.size);row.originalProtection=p.protection;
    }
    DetourSetRetainRegions(TRUE);auto error=transaction(true);
    if(error!=NO_ERROR){recovery();return error;}
    bool protections=true;
    for(unsigned i=0;i<PatchCount;i++){auto& p=patches[i];std::memcpy(p.installed.data(),p.target,p.size);
        request->patches[i].trampoline=reinterpret_cast<uint64_t>(*p.original);protections=protection(p,request->patches[i])&&protections;}
    deadline=GetTickCount64()+r->durationMs;admission=protections;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&r->status),1);
    while(admission.load()&&GetTickCount64()<deadline&&!InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&r->cancel),0,0))Sleep(1);
    admission=false;
    for(auto& p:patches)if(std::memcmp(p.target,p.installed.data(),p.size))return ERROR_INVALID_DATA;
    error=transaction(false);if(error!=NO_ERROR)return error;
    auto end=GetTickCount64()+2000;while(active.load()!=0&&GetTickCount64()<end)Sleep(1);
    r->active=active.load();r->callbacksDrained=r->active==0;r->referencesReleased=r->callbacksDrained;
    r->records=reserved.load();const bool recovered=recovery();
    return recovered&&r->callbacksDrained&&protections?NO_ERROR:ERROR_INVALID_STATE;
}
}
#ifndef SEASON_FRAME_FIXTURE
extern "C" __declspec(dllexport) DWORD WINAPI SeasonFrameObserve(Request* r){
    if(!r||r->moduleBase!=reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr)))return ERROR_INVALID_PARAMETER;
    HMODULE pinned{};if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(&SeasonFrameObserve),&pinned))return GetLastError();
    initialize();auto error=run(r,false);r->error=error;InterlockedExchange(reinterpret_cast<volatile LONG*>(&r->status),error?4:2);return error;
}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(h);return TRUE;}
#endif
