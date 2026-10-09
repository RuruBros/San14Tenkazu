// Bounded engine Draw observer. Material trial variants replace one owned SRV
// only inside the authenticated Draw, then restore it. No dispatch/table patch.
#if defined(SEASON_OBSERVER_TERRAIN_TRIAL) || defined(SEASON_OBSERVER_TREE_TRIAL)
#define SEASON_OBSERVER_MATERIAL_TRIAL
#endif
#include "DrawObserver.h"
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <tlhelp32.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <intrin.h>
#include <atomic>
#include <vector>
#include <algorithm>
#include <cstring>
#include "detours.h"
#ifdef SEASON_OBSERVER_TEXTURE_READ
#include "TextureReadObserver.h"
#endif
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
#include "MeshResourceRead.h"
#endif
#ifdef SEASON_OBSERVER_TERRAIN_TRIAL
#include "TerrainTrial.h"
#endif
#ifdef SEASON_OBSERVER_TREE_TRIAL
#include "TreeMaterialTrial.h"
#endif
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
#include "PrepareSignature.h"
#endif

using Microsoft::WRL::ComPtr;
using namespace season::observe;
#ifdef SEASON_OBSERVER_TERRAIN_TRIAL
#include "TerrainTrialGpu.h"
#endif
#ifdef SEASON_OBSERVER_TREE_TRIAL
#include "TreeMaterialTrialGpu.h"
#endif
namespace {
#ifdef SEASON_OBSERVER_MESH_INSTANCED
using Draw=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t);
constexpr size_t CodeSize=67,TargetRva=0x960e90;
#elif defined(SEASON_OBSERVER_INDEXED)
using Draw=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t,uint32_t,uint32_t);
constexpr size_t CodeSize=52,TargetRva=0x960e50;
#elif defined(SEASON_OBSERVER_INSTANCED)
using Draw=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t);
constexpr size_t CodeSize=82,TargetRva=0x960ee0;
#else
using Draw=void(__fastcall*)(void*,uint32_t,uint32_t,uint32_t);
constexpr size_t CodeSize=70,TargetRva=0x960f90;
#endif
Draw original;
SRWLOCK dispatchLock=SRWLOCK_INIT;
Request* receipt;
std::atomic<bool> admission{false};
std::atomic<long> active{0};
std::atomic<unsigned> reserved{0};
std::atomic<bool> used{false};
uint64_t deadline;
BYTE* target;
DWORD protection;
BYTE originalCode[CodeSize], installedCode[CodeSize];
template<class T> T read(const void* base,size_t offset) noexcept;
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
#include "MeshResourceReadGpu.h"
#endif
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
using Prepare=uint8_t(__fastcall*)(void*,uint32_t);
Prepare originalPrepare;
BYTE* prepareTarget;DWORD prepareProtection;
BYTE installedPrepareCode[sizeof(ExpectedPrepareCode)];
BYTE originalPrepareCode[sizeof(ExpectedPrepareCode)];
#ifdef SEASON_OBSERVER_TREE_TRIAL
TreeMaterialTrial* trial;
constexpr UINT MaterialSlot=0;
#else
TerrainTrial* trial;
constexpr UINT MaterialSlot=4;
#endif
ComPtr<ID3D11Texture2D> seasonTexture;ComPtr<ID3D11ShaderResourceView> seasonView;
struct DrawTicket {void* driver;ID3D11DeviceContext* context;bool admitted,swapped=false;ComPtr<ID3D11ShaderResourceView> saved;};
thread_local DrawTicket* currentTicket=nullptr;
void enterMaterial(DrawTicket& ticket) noexcept;
uint8_t __fastcall observedPrepare(void* driver,uint32_t mode){
    const bool nested=currentTicket!=nullptr;
    if(!nested)AcquireSRWLockShared(&dispatchLock);
    auto result=originalPrepare(driver,mode);auto lastError=GetLastError();auto mxcsr=_mm_getcsr();
    if(result&&currentTicket&&currentTicket->admitted&&currentTicket->driver==driver)enterMaterial(*currentTicket);
    if(!nested)ReleaseSRWLockShared(&dispatchLock);
    _mm_setcsr(mxcsr);SetLastError(lastError);return result;
}
void enterMaterial(DrawTicket& ticket) noexcept {
    if(ticket.swapped||GetCurrentThreadId()!=trial->renderThread||
       read<uint64_t>(ticket.driver,0xb8)!=trial->psOwner)return;
#ifdef SEASON_OBSERVER_TREE_TRIAL
    if(read<uint64_t>(ticket.driver,0xa8)!=trial->vsOwner)return;
    ComPtr<ID3D11VertexShader> vs;ticket.context->VSGetShader(&vs,nullptr,nullptr);
    ComPtr<ID3D11InputLayout> layout;ticket.context->IAGetInputLayout(&layout);
    if(reinterpret_cast<uint64_t>(vs.Get())!=trial->vs||reinterpret_cast<uint64_t>(layout.Get())!=trial->inputLayout)return;
#endif
    ComPtr<ID3D11PixelShader> shader;ticket.context->PSGetShader(&shader,nullptr,nullptr);
    ComPtr<ID3D11ShaderResourceView> view;ticket.context->PSGetShaderResources(MaterialSlot,1,&view);
    if(reinterpret_cast<uint64_t>(shader.Get())!=trial->ps||reinterpret_cast<uint64_t>(view.Get())!=trial->view)return;
    ComPtr<ID3D11Resource> resource;view->GetResource(&resource);
    if(reinterpret_cast<uint64_t>(resource.Get())!=trial->resource)return;
    ID3D11RenderTargetView* rt{};ticket.context->OMGetRenderTargets(1,&rt,nullptr);
    const bool color=rt!=nullptr;if(rt)rt->Release();if(!color)return;
    if(!seasonView){
#ifdef SEASON_OBSERVER_TREE_TRIAL
        trial->hresult=CreateSpringTree(ticket.context,view.Get(),*trial,seasonTexture,seasonView);
#else
        trial->hresult=CreateSpringLand(ticket.context,view.Get(),*trial,seasonTexture,seasonView,trial->changedBlocks);
#endif
        if(FAILED(trial->hresult)){trial->state=4;admission.store(false);return;}
        trial->newView=reinterpret_cast<uint64_t>(seasonView.Get());trial->newResource=reinterpret_cast<uint64_t>(seasonTexture.Get());
        trial->state=1;
    }
    ticket.saved=view;auto* replacement=seasonView.Get();ticket.context->PSSetShaderResources(MaterialSlot,1,&replacement);
    ticket.swapped=true;trial->swaps++;
}
void leaveMaterial(DrawTicket& ticket) noexcept {
    if(!ticket.swapped)return;
    auto* originalView=ticket.saved.Get();ticket.context->PSSetShaderResources(MaterialSlot,1,&originalView);
    ComPtr<ID3D11ShaderResourceView> after;ticket.context->PSGetShaderResources(MaterialSlot,1,&after);
    if(after.Get()==originalView)trial->restores++;
    else {trial->state=4;trial->hresult=E_FAIL;admission.store(false);}
    ticket.saved.Reset();
}
#endif
#ifdef SEASON_OBSERVER_TEXTURE_READ
TextureRead* textureRead;
void captureTexture(ID3D11DeviceContext* context,const Record& record) noexcept {
    auto& info=*textureRead;
    if(info.state||record.psOwner!=info.psOwner||record.views[info.slot].object!=info.view||
       record.views[info.slot].resource!=info.resource)return;
    info.state=1;
    ComPtr<ID3D11ShaderResourceView> view;context->PSGetShaderResources(info.slot,1,&view);
    ComPtr<ID3D11Resource> resource;if(view)view->GetResource(&resource);
    ComPtr<ID3D11Texture2D> source;
    auto hr=resource?resource.As(&source):E_INVALIDARG;
    if(SUCCEEDED(hr)){
        D3D11_TEXTURE2D_DESC d{};source->GetDesc(&d);
        info.width=d.Width;info.height=d.Height;info.mipLevels=d.MipLevels;info.format=d.Format;info.arraySize=d.ArraySize;
        info.sampleCount=d.SampleDesc.Count;info.sampleQuality=d.SampleDesc.Quality;info.usage=d.Usage;
        info.bindFlags=d.BindFlags;info.cpuAccessFlags=d.CPUAccessFlags;info.miscFlags=d.MiscFlags;
        D3D11_SHADER_RESOURCE_VIEW_DESC vd{};view->GetDesc(&vd);info.viewFormat=vd.Format;info.viewDimension=vd.ViewDimension;
        info.mostDetailedMip=vd.Texture2D.MostDetailedMip;info.viewMipLevels=vd.Texture2D.MipLevels;
        bool control=info.slot==3;UINT side=control?2049:4096;
        if(d.Width!=side||d.Height!=side||d.MipLevels!=(control?1u:11u)||d.ArraySize!=1||d.SampleDesc.Count!=1||
           d.Format!=(control?DXGI_FORMAT_R8G8B8A8_TYPELESS:DXGI_FORMAT_BC3_TYPELESS)||reinterpret_cast<uint64_t>(view.Get())!=info.view||
           reinterpret_cast<uint64_t>(resource.Get())!=info.resource)hr=E_INVALIDARG;
        else {
            d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
            ComPtr<ID3D11Device> device;context->GetDevice(&device);
            ComPtr<ID3D11Texture2D> staging;hr=device->CreateTexture2D(&d,nullptr,&staging);
            if(SUCCEEDED(hr)){
                context->CopyResource(staging.Get(),source.Get());
                auto* output=reinterpret_cast<BYTE*>(info.output);
                for(UINT mip=0;mip<d.MipLevels&&SUCCEEDED(hr);mip++){
                    const UINT pitch=control?d.Width*4:std::max(1u,((d.Width>>mip)+3)/4)*16;
                    const UINT rows=control?d.Height:std::max(1u,((d.Height>>mip)+3)/4);
                    const UINT bytes=pitch*rows;
                    if(info.bytes>info.capacity||bytes>info.capacity-info.bytes){hr=E_INVALIDARG;break;}
                    D3D11_MAPPED_SUBRESOURCE mapped{};hr=context->Map(staging.Get(),mip,D3D11_MAP_READ,0,&mapped);
                    if(FAILED(hr))break;
                    if(!mapped.pData||mapped.RowPitch<pitch)hr=E_INVALIDARG;
                    else for(UINT row=0;row<rows;row++)std::memcpy(output+info.bytes+row*pitch,
                        static_cast<const BYTE*>(mapped.pData)+row*mapped.RowPitch,pitch);
                    context->Unmap(staging.Get(),mip);
                    if(SUCCEEDED(hr))info.bytes+=bytes;
                }
            }
        }
    }
    // All staging/source/view/device references leave this render-thread scope.
    info.hresult=hr;info.state=SUCCEEDED(hr)?2:4;
    admission.store(false,std::memory_order_release);
}
#endif
// Saved-image SHA256 aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59.
// Entire RVA 960F90..960FD6, not just a guessed prologue.
#ifdef SEASON_OBSERVER_MESH_INSTANCED
constexpr BYTE ExpectedCode[]={
    0x40,0x53,0x48,0x83,0xec,0x30,0x48,0x8b,0xd9,0xe8,0xc2,0x2d,0x00,0x00,0x84,0xc0,
    0x74,0x2b,0x48,0x8b,0x4b,0x10,0x33,0xd2,0x44,0x8b,0x4c,0x24,0x60,0x44,0x8b,0x44,
    0x24,0x70,0x89,0x54,0x24,0x28,0x48,0x8b,0x01,0x89,0x54,0x24,0x20,0x8b,0x54,0x24,
    0x68,0xff,0x90,0xa0,0x00,0x00,0x00,0xff,0x83,0xd0,0x2a,0x00,0x00,0x48,0x83,0xc4,
    0x30,0x5b,0xc3};
#elif defined(SEASON_OBSERVER_INDEXED)
constexpr BYTE ExpectedCode[]={
    0x40,0x53,0x48,0x83,0xec,0x20,0x48,0x8b,0xd9,0xe8,0x02,0x2e,0x00,0x00,
    0x84,0xc0,0x74,0x1c,0x48,0x8b,0x4b,0x10,0x45,0x33,0xc9,0x44,0x8b,0x44,
    0x24,0x50,0x8b,0x54,0x24,0x58,0x48,0x8b,0x01,0xff,0x50,0x60,0xff,0x83,
    0xd0,0x2a,0x00,0x00,0x48,0x83,0xc4,0x20,0x5b,0xc3};
#elif defined(SEASON_OBSERVER_INSTANCED)
constexpr BYTE ExpectedCode[]={
    0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x30,0x41,0x8b,0xf8,0x48,
    0x8b,0xd9,0xe8,0x6b,0x2d,0x00,0x00,0x84,0xc0,0x74,0x2e,0x48,0x8b,0x4b,
    0x10,0x33,0xd2,0x48,0x8b,0x83,0x90,0x00,0x00,0x00,0x45,0x33,0xc9,0x89,
    0x54,0x24,0x28,0x44,0x8b,0xc7,0x89,0x54,0x24,0x20,0x4c,0x8b,0x11,0x8b,
    0x50,0x1c,0x41,0xff,0x92,0xa0,0x00,0x00,0x00,0xff,0x83,0xd0,0x2a,0x00,
    0x00,0x48,0x8b,0x5c,0x24,0x40,0x48,0x83,0xc4,0x30,0x5f,0xc3};
#else
constexpr BYTE ExpectedCode[]={
    0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xec,0x20,
    0x41,0x8b,0xf9,0x41,0x8b,0xf0,0x48,0x8b,0xd9,0xe8,0xb3,0x2c,0x00,0x00,
    0x84,0xc0,0x74,0x15,0x48,0x8b,0x4b,0x10,0x44,0x8b,0xc6,0x8b,0xd7,
    0x48,0x8b,0x01,0xff,0x50,0x68,0xff,0x83,0xd0,0x2a,0x00,0x00,
    0x48,0x8b,0x5c,0x24,0x30,0x48,0x8b,0x74,0x24,0x38,0x48,0x83,0xc4,0x20,0x5f,0xc3};
#endif
static_assert(sizeof(ExpectedCode)==sizeof(originalCode));

bool copyReadable(void* dest,const void* src,size_t size) noexcept {
    if(!src||!size)return false;
    MEMORY_BASIC_INFORMATION m{};
    if(!VirtualQuery(src,&m,sizeof(m))||m.State!=MEM_COMMIT||
       (m.Protect&(PAGE_GUARD|PAGE_NOACCESS))||
       reinterpret_cast<uintptr_t>(src)-reinterpret_cast<uintptr_t>(m.BaseAddress)>m.RegionSize||
       size>m.RegionSize-(reinterpret_cast<uintptr_t>(src)-reinterpret_cast<uintptr_t>(m.BaseAddress)))return false;
    __try {std::memcpy(dest,src,size);return true;}
    __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
template<class T> T read(const void* base,size_t offset) noexcept {
    T value{};copyReadable(&value,static_cast<const BYTE*>(base)+offset,sizeof(value));return value;
}
void resource(Binding& b,ID3D11View* view) noexcept {
    b.object=reinterpret_cast<uint64_t>(view);if(!view)return;
    ComPtr<ID3D11Resource> r;view->GetResource(&r);b.resource=reinterpret_cast<uint64_t>(r.Get());
    if(!r)return;D3D11_RESOURCE_DIMENSION dimension{};r->GetType(&dimension);b.dimension=dimension;
    if(dimension==D3D11_RESOURCE_DIMENSION_TEXTURE2D){
        ComPtr<ID3D11Texture2D> t;if(SUCCEEDED(r.As(&t))){D3D11_TEXTURE2D_DESC d{};t->GetDesc(&d);
            b.format=d.Format;b.width=d.Width;b.height=d.Height;b.mipLevels=d.MipLevels;b.arraySize=d.ArraySize;}
    }
}
void constants(Constant* out,ID3D11Buffer** buffers,void* driver,void* owner,unsigned stage) noexcept {
    const auto descriptorCount=read<uint32_t>(owner,0x1c);
    auto* descriptors=read<BYTE*>(owner,0x20);
    for(unsigned slot=0;slot<14;slot++){
        auto& c=out[slot];c.buffer=reinterpret_cast<uint64_t>(buffers[slot]);if(!buffers[slot])continue;
        D3D11_BUFFER_DESC desc{};buffers[slot]->GetDesc(&desc);c.byteWidth=desc.ByteWidth;
        // Match the actual bound GPU buffer to the verified CPU upload descriptor.
        // A CPU shadow copy is not a GPU content readback.
        if(descriptorCount>14||!descriptors)continue;
        for(unsigned j=0;j<descriptorCount;j++){
            BYTE d[16]{};if(!copyReadable(d,descriptors+j*16,16))break;
            uint64_t native{};uint32_t length{},binding{};
            std::memcpy(&native,d,8);std::memcpy(&length,d+8,4);std::memcpy(&binding,d+12,4);
            if(native!=c.buffer||binding!=slot||!length||length>c.byteWidth)continue;
            auto* source=read<void*>(driver,0x2388+(stage*14+slot)*8);
            c.cpuSource=reinterpret_cast<uint64_t>(source);
            auto n=std::min<uint32_t>(length,sizeof(c.bytes));
            if(copyReadable(c.bytes,source,n))c.copiedBytes=n;
            break;
        }
    }
}
void capture(Record& r,void* driver,ID3D11DeviceContext* context,uint32_t start,uint32_t count,
             uint32_t before,uint32_t after) noexcept {
    r.tick=GetTickCount64();r.thread=GetCurrentThreadId();r.driver=reinterpret_cast<uint64_t>(driver);
    r.context=reinterpret_cast<uint64_t>(context);r.contextType=context->GetType();
    r.start=start;r.count=count;r.counterBefore=before;r.counterAfter=after;
    r.nativeDraw=read<uint64_t>(read<void*>(context,0),
#ifdef SEASON_OBSERVER_INDEXED
        12*8
#elif defined(SEASON_OBSERVER_INSTANCED) || defined(SEASON_OBSERVER_MESH_INSTANCED)
        20*8
#else
        13*8
#endif
    );
    r.instances=1;
#ifdef SEASON_OBSERVER_MESH_INSTANCED
    r.drawKind=3;
#elif defined(SEASON_OBSERVER_INDEXED)
    r.drawKind=1;
#elif defined(SEASON_OBSERVER_INSTANCED)
    r.drawKind=2;
#else
    r.drawKind=0;
#endif
    r.psOwner=read<uint64_t>(driver,0xb8);r.vsOwner=read<uint64_t>(driver,0xa8);
    ComPtr<ID3D11Device> device;context->GetDevice(&device);r.device=reinterpret_cast<uint64_t>(device.Get());
    if(device)r.creationFlags=device->GetCreationFlags();
    ComPtr<ID3D11PixelShader> ps;ComPtr<ID3D11VertexShader> vs;
    context->PSGetShader(&ps,nullptr,nullptr);context->VSGetShader(&vs,nullptr,nullptr);
    r.ps=reinterpret_cast<uint64_t>(ps.Get());r.vs=reinterpret_cast<uint64_t>(vs.Get());
    ID3D11ShaderResourceView* views[12]{};ID3D11RenderTargetView* targets[4]{};
    ID3D11DepthStencilView* depth{};ID3D11Buffer* pc[14]{},*vc[14]{};
    context->PSGetShaderResources(0,12,views);context->OMGetRenderTargets(4,targets,&depth);
    context->PSGetConstantBuffers(0,14,pc);context->VSGetConstantBuffers(0,14,vc);
    for(unsigned i=0;i<12;i++){resource(r.views[i],views[i]);if(views[i])views[i]->Release();}
    for(unsigned i=0;i<4;i++){resource(r.targets[i],targets[i]);if(targets[i])targets[i]->Release();}
    resource(r.depth,depth);if(depth)depth->Release();
    constants(r.psConstants,pc,driver,reinterpret_cast<void*>(r.psOwner),4);
    constants(r.vsConstants,vc,driver,reinterpret_cast<void*>(r.vsOwner),0);
    for(unsigned i=0;i<14;i++){if(pc[i])pc[i]->Release();if(vc[i])vc[i]->Release();}
    UINT viewCount=1;D3D11_VIEWPORT viewport{};context->RSGetViewports(&viewCount,&viewport);
    if(viewCount==1)std::memcpy(r.viewport,&viewport,sizeof(viewport));
}
void __fastcall observedDraw(void* driver,uint32_t mode,uint32_t start,uint32_t count
#if defined(SEASON_OBSERVER_INDEXED) || defined(SEASON_OBSERVER_MESH_INSTANCED)
    ,uint32_t indexStart,uint32_t indexCount
#endif
#ifdef SEASON_OBSERVER_MESH_INSTANCED
    ,uint32_t instanceCount
#endif
    ) {
    const auto entryLastError=GetLastError();const auto entryMxcsr=_mm_getcsr();
    active.fetch_add(1,std::memory_order_acq_rel);
    // Detach takes the exclusive lock before enlisting threads. No callback can
    // cache/use the soon-to-be-freed Detours trampoline across that transaction.
    AcquireSRWLockShared(&dispatchLock);
    bool admitted=admission.load(std::memory_order_acquire)&&GetTickCount64()<deadline;
    ComPtr<ID3D11DeviceContext> context;
    uint32_t before=0;
    if(admitted){
        auto* raw=read<ID3D11DeviceContext*>(driver,0x10);
        // The engine owns driver/context throughout this verified Draw call.
        // Acquire and release the extra COM reference on this same thread.
        if(reinterpret_cast<uint64_t>(raw)==receipt->context){context=raw;before=read<uint32_t>(driver,0x2ad0);}
        else {InterlockedIncrement(reinterpret_cast<volatile LONG*>(&receipt->rejected));admitted=false;}
    }
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
#ifdef SEASON_OBSERVER_TREE_TRIAL
    admitted=admitted&&indexStart==0&&indexCount==trial->indexCount;
#endif
    DrawTicket ticket{driver,context.Get(),admitted};auto* previousTicket=currentTicket;currentTicket=&ticket;
#endif
    _mm_setcsr(entryMxcsr);SetLastError(entryLastError);
    original(driver,mode,start,count
#if defined(SEASON_OBSERVER_INDEXED) || defined(SEASON_OBSERVER_MESH_INSTANCED)
        ,indexStart,indexCount
#endif
#ifdef SEASON_OBSERVER_MESH_INSTANCED
        ,instanceCount
#endif
    );
    const auto lastError=GetLastError();const auto mxcsr=_mm_getcsr();
    if(admitted
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
       &&ticket.swapped&&reserved.load()==0
#endif
    ){
        auto after=read<uint32_t>(driver,0x2ad0);
        if(after==before+1){
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
            if(selectMeshRead(context.Get(),indexCount)){
#endif
#ifdef SEASON_OBSERVER_COLOR_ONLY
            // Avoid the expensive resource/constant description walk on Depth-only
            // draws. This is a selection rule, not a semantic pass classification.
            ID3D11RenderTargetView* color{};
            context->OMGetRenderTargets(1,&color,nullptr);
            bool selected=color!=nullptr;if(color)color->Release();
            if(selected){
#endif
            auto i=reserved.fetch_add(1,std::memory_order_acq_rel);
            if(i<Capacity){auto& r=receipt->rows[i];r.sequence=i;
                capture(r,driver,context.Get(),
#if defined(SEASON_OBSERVER_INDEXED) || defined(SEASON_OBSERVER_MESH_INSTANCED)
                    indexStart,indexCount,
#elif defined(SEASON_OBSERVER_INSTANCED)
                    0,read<uint32_t>(read<void*>(driver,0x90),0x1c),
#else
                    start,count,
#endif
                    before,after);
#ifdef SEASON_OBSERVER_TEXTURE_READ
                captureTexture(context.Get(),r);
#endif
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
                meshRead->indexStart=indexStart;meshRead->instances=instanceCount;
                captureMeshResources(context.Get());
#endif
#ifdef SEASON_OBSERVER_INSTANCED
                r.instances=start;
#endif
#ifdef SEASON_OBSERVER_MESH_INSTANCED
                r.instances=instanceCount;
#endif
                // Mark completion only after every temporary getter reference is released.
                context.Reset();r.captureEndTick=GetTickCount64();InterlockedExchange(reinterpret_cast<volatile LONG*>(&r.complete),1);
            }else admission.store(false,std::memory_order_release);
#ifdef SEASON_OBSERVER_COLOR_ONLY
            }else InterlockedIncrement(reinterpret_cast<volatile LONG*>(&receipt->skipped));
#endif
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
            }else InterlockedIncrement(reinterpret_cast<volatile LONG*>(&receipt->skipped));
#endif
        }else InterlockedIncrement(reinterpret_cast<volatile LONG*>(&receipt->skipped));
    }
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
    leaveMaterial(ticket);currentTicket=previousTicket;
#endif
    context.Reset();_mm_setcsr(mxcsr);SetLastError(lastError);
    ReleaseSRWLockShared(&dispatchLock);
    active.fetch_sub(1,std::memory_order_acq_rel);
}

// A thread handle stays open until commit/abort has resumed that exact thread.
LONG transaction(bool attach) {
    LONG result=NO_ERROR;
    // Drain the active Draw first. Threads which finish during that wait must
    // not become stale enlistment handles in the later Detours transaction.
    AcquireSRWLockExclusive(&dispatchLock);
    std::vector<HANDLE> handles;
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if(snap==INVALID_HANDLE_VALUE){auto error=GetLastError();ReleaseSRWLockExclusive(&dispatchLock);return error;}
    THREADENTRY32 entry{sizeof(entry)};
    if(!Thread32First(snap,&entry))result=GetLastError();
    else do {
        if(entry.th32OwnerProcessID!=GetCurrentProcessId()||entry.th32ThreadID==GetCurrentThreadId())continue;
        HANDLE thread=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_SET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,entry.th32ThreadID);
        if(!thread){result=GetLastError();receipt->failedThread=entry.th32ThreadID;receipt->failedStage=1;break;}
        handles.push_back(thread);
    }while(Thread32Next(snap,&entry));
    if(result==NO_ERROR&&GetLastError()!=ERROR_NO_MORE_FILES)result=GetLastError();
    CloseHandle(snap);
    if(result==NO_ERROR){
        result=DetourTransactionBegin();
        if(result==NO_ERROR){
            for(auto handle:handles){
                DWORD exit{};
                if(GetExitCodeThread(handle,&exit)&&exit!=STILL_ACTIVE)continue;
                result=DetourUpdateThread(handle);
                if(result!=NO_ERROR){receipt->failedThread=GetThreadId(handle);receipt->failedStage=2;
                    receipt->failedThreadExitRead=GetExitCodeThread(handle,&exit);receipt->failedThreadExit=exit;break;}
            }
            if(result==NO_ERROR)result=attach?DetourAttach(reinterpret_cast<PVOID*>(&original),observedDraw):
                                            DetourDetach(reinterpret_cast<PVOID*>(&original),observedDraw);
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
            if(result==NO_ERROR)result=attach?DetourAttach(reinterpret_cast<PVOID*>(&originalPrepare),observedPrepare):
                                            DetourDetach(reinterpret_cast<PVOID*>(&originalPrepare),observedPrepare);
#endif
            if(result==NO_ERROR)result=DetourTransactionCommit();else DetourTransactionAbort();
        }
    }
    ReleaseSRWLockExclusive(&dispatchLock);
    for(auto handle:handles)CloseHandle(handle);
    return result;
}
bool checkProtection() noexcept {
    MEMORY_BASIC_INFORMATION m{};
    if(!VirtualQuery(target,&m,sizeof(m)))return false;
    receipt->finalProtection=m.Protect;
    if(m.Protect!=protection){DWORD prior{};
        if(!VirtualProtect(target,sizeof(originalCode),protection,&prior)||
           !VirtualQuery(target,&m,sizeof(m)))return false;
        receipt->finalProtection=m.Protect;
    }
    return m.Protect==protection;
}
DWORD run(Request* request,Draw draw,bool fixture) {
    receipt=request;target=reinterpret_cast<BYTE*>(draw);
    if(request->magic!=Magic||request->version!=Version||request->size!=sizeof(Request)||
       request->durationMs<50||request->durationMs>
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
       30000
#elif defined(SEASON_OBSERVER_MESH_RESOURCE_READ)
       3000
#else
       500
#endif
       ||!request->context||!request->device)return ERROR_INVALID_PARAMETER;
    if(used.exchange(true))return ERROR_ALREADY_EXISTS;
    MEMORY_BASIC_INFORMATION m{};
    if(!VirtualQuery(target,&m,sizeof(m))||m.State!=MEM_COMMIT||
       (m.Protect!=PAGE_EXECUTE_READ&&m.Protect!=PAGE_EXECUTE_READWRITE))return ERROR_INVALID_ADDRESS;
    protection=m.Protect;receipt->originalProtection=protection;receipt->target=reinterpret_cast<uint64_t>(target);
    if(!copyReadable(originalCode,target,sizeof(originalCode))||
       (!fixture&&std::memcmp(originalCode,ExpectedCode,sizeof(ExpectedCode))))return ERROR_REVISION_MISMATCH;
    original=draw;receipt->workerThread=GetCurrentThreadId();
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
    prepareTarget=fixture?reinterpret_cast<BYTE*>(originalPrepare):reinterpret_cast<BYTE*>(request->moduleBase+0x963c60);
    MEMORY_BASIC_INFORMATION pm{};
    if(!VirtualQuery(prepareTarget,&pm,sizeof(pm))||pm.Protect!=PAGE_EXECUTE_READ||
       (!fixture&&std::memcmp(prepareTarget,ExpectedPrepareCode,sizeof(ExpectedPrepareCode))))return ERROR_REVISION_MISMATCH;
    if(!copyReadable(originalPrepareCode,prepareTarget,sizeof(originalPrepareCode)))return ERROR_INVALID_ADDRESS;
    prepareProtection=pm.Protect;originalPrepare=reinterpret_cast<Prepare>(prepareTarget);
#endif
    // A late callback may still be at the wrapper after detach. This module is
    // pinned; the dispatch lock prevents a cached trampoline pointer from surviving
    // detach, and original is updated while enlisted threads are suspended.
    DetourSetRetainRegions(TRUE);
    auto error=transaction(true);receipt->trampoline=reinterpret_cast<uint64_t>(original);
    if(error!=NO_ERROR){receipt->codeRestored=std::memcmp(target,originalCode,sizeof(originalCode))==0;
        receipt->protectionRestored=checkProtection();return error;}
    std::memcpy(installedCode,target,sizeof(installedCode));
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
    std::memcpy(installedPrepareCode,prepareTarget,sizeof(installedPrepareCode));
#endif
    if(!checkProtection()){admission.store(false);error=ERROR_INVALID_ACCESS;}
    else {
        deadline=GetTickCount64()+request->durationMs;admission.store(true,std::memory_order_release);
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&request->status),1);
        while(GetTickCount64()<deadline&&!InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&request->cancel),0,0)&&admission.load())Sleep(1);
    }
    admission.store(false,std::memory_order_release);
    // Keep pass-through code resident if a foreign patch appears. Never overwrite it.
    if(std::memcmp(target,installedCode,sizeof(installedCode))!=0)return ERROR_INVALID_DATA;
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
    if(std::memcmp(prepareTarget,installedPrepareCode,sizeof(installedPrepareCode))!=0)return ERROR_INVALID_DATA;
#endif
    request->restoreAttempts++;
    auto detach=transaction(false);
    if(detach!=NO_ERROR)return detach;
    auto drainDeadline=GetTickCount64()+2000;
    while(active.load(std::memory_order_acquire)!=0&&GetTickCount64()<drainDeadline)Sleep(1);
    request->callbacksDrained=active.load(std::memory_order_acquire)==0;
    request->active=active.load();request->records=std::min<unsigned>(reserved.load(),Capacity);
    request->codeRestored=std::memcmp(target,originalCode,sizeof(originalCode))==0;
    request->protectionRestored=checkProtection();
    request->referencesReleased=request->callbacksDrained;
#ifdef SEASON_OBSERVER_MATERIAL_TRIAL
    trial->prepareCodeRestored=std::memcmp(prepareTarget,originalPrepareCode,sizeof(originalPrepareCode))==0;
    MEMORY_BASIC_INFORMATION finalPrepare{};VirtualQuery(prepareTarget,&finalPrepare,sizeof(finalPrepare));
    if(finalPrepare.Protect!=prepareProtection){DWORD prior{};VirtualProtect(prepareTarget,sizeof(ExpectedPrepareCode),prepareProtection,&prior);
        VirtualQuery(prepareTarget,&finalPrepare,sizeof(finalPrepare));}
    trial->prepareProtectionRestored=finalPrepare.Protect==prepareProtection;
    trial->originalBindingsRestored=trial->swaps>0&&trial->swaps==trial->restores;
    if(request->callbacksDrained){seasonView.Reset();seasonTexture.Reset();trial->gpuResourcesReleased=1;}
    if(!trial->prepareCodeRestored||!trial->prepareProtectionRestored||!trial->originalBindingsRestored||!trial->gpuResourcesReleased)return ERROR_INVALID_STATE;
    trial->state=FAILED(trial->hresult)?4:2;
#endif
    if(!request->callbacksDrained||!request->codeRestored||!request->protectionRestored)return ERROR_INVALID_STATE;
    return error;
}
}
#ifndef SEASON_OBSERVER_FIXTURE
extern "C" __declspec(dllexport) DWORD WINAPI SeasonDrawObserve(Request* request) {
    auto base=reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr));
    if(!request||request->moduleBase!=base)return ERROR_INVALID_PARAMETER;
#ifdef SEASON_OBSERVER_TERRAIN_TRIAL
    trial=reinterpret_cast<TerrainTrial*>(request+1);MEMORY_BASIC_INFORMATION input{};
    if(trial->magic!=TrialMagic||trial->version!=TrialVersion||trial->size!=sizeof(TerrainTrial)||trial->month!=4||
       trial->year<=100||trial->year>=400||trial->day<1||trial->day>30||!trial->renderThread||
       !trial->psOwner||!trial->ps||!trial->view||!trial->resource||!trial->input||trial->bytes!=22369616||
       !VirtualQuery(reinterpret_cast<void*>(trial->input),&input,sizeof(input))||input.State!=MEM_COMMIT||input.Protect!=PAGE_READWRITE||
       input.BaseAddress!=reinterpret_cast<void*>(trial->input)||input.RegionSize<trial->bytes)return ERROR_INVALID_PARAMETER;
#endif
#ifdef SEASON_OBSERVER_TREE_TRIAL
    trial=reinterpret_cast<TreeMaterialTrial*>(request+1);MEMORY_BASIC_INFORMATION output{};
    if(trial->magic!=TreeTrialMagic||trial->version!=TreeTrialVersion||trial->size!=sizeof(TreeMaterialTrial)||trial->month!=4||
       trial->year<=100||trial->year>=400||trial->day<1||trial->day>30||!trial->renderThread||
       !trial->psOwner||!trial->ps||!trial->view||!trial->resource||!trial->vsOwner||!trial->vs||!trial->inputLayout||
       trial->indexCount!=918||!trial->output||trial->bytes!=TreeTrialOutputBytes||
       !VirtualQuery(reinterpret_cast<void*>(trial->output),&output,sizeof(output))||output.State!=MEM_COMMIT||output.Protect!=PAGE_READWRITE||
       output.BaseAddress!=reinterpret_cast<void*>(trial->output)||output.RegionSize<trial->bytes)return ERROR_INVALID_PARAMETER;
#endif
#ifdef SEASON_OBSERVER_TEXTURE_READ
    textureRead=reinterpret_cast<TextureRead*>(request+1);
    MEMORY_BASIC_INFORMATION output{};
    if(textureRead->magic!=TextureMagic||textureRead->version!=TextureVersion||textureRead->size!=sizeof(TextureRead)||
       (textureRead->slot!=3&&textureRead->slot!=4)||!textureRead->psOwner||!textureRead->view||!textureRead->resource||
       textureRead->capacity!=(textureRead->slot==3?16793604u:22369616u)||!textureRead->output||
       !VirtualQuery(reinterpret_cast<void*>(textureRead->output),&output,sizeof(output))||
       output.State!=MEM_COMMIT||output.Protect!=PAGE_READWRITE||output.BaseAddress!=reinterpret_cast<void*>(textureRead->output)||
       output.RegionSize<textureRead->capacity)return ERROR_INVALID_PARAMETER;
#endif
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
    meshRead=reinterpret_cast<MeshRead*>(request+1);meshBatch=nullptr;
    if(meshRead->magic==MeshBatchMagic){
        meshBatch=reinterpret_cast<MeshReadBatch*>(request+1);
        if(meshBatch->version!=MeshBatchVersion||meshBatch->size!=sizeof(MeshReadBatch)||
           !meshBatch->count||meshBatch->count>MeshBatchCount)return ERROR_INVALID_PARAMETER;
    }
    for(uint32_t i=0;i<(meshBatch?meshBatch->count:1u);i++){
    if(meshBatch)meshRead=&meshBatch->items[i];
    MEMORY_BASIC_INFORMATION meshOutput{};
    if(meshRead->magic!=MeshReadMagic||meshRead->version!=MeshReadVersion||meshRead->size!=sizeof(MeshRead)||
       !meshRead->indexCount||meshRead->indexCount>65536||!meshRead->renderThread||meshRead->requireColor>1||!meshRead->view||!meshRead->resource||
       meshRead->state||meshRead->bytes||meshRead->blobCount||meshRead->hresult||
       meshRead->capacity!=MeshReadCapacity||!meshRead->output||
       !VirtualQuery(reinterpret_cast<void*>(meshRead->output),&meshOutput,sizeof(meshOutput))||
       meshOutput.State!=MEM_COMMIT||meshOutput.Protect!=PAGE_READWRITE||meshOutput.BaseAddress!=reinterpret_cast<void*>(meshRead->output)||
       meshOutput.RegionSize<meshRead->capacity)return ERROR_INVALID_PARAMETER;
    if(meshBatch)for(uint32_t j=0;j<i;j++){
        const auto& previous=meshBatch->items[j];
        if(previous.renderThread!=meshRead->renderThread||previous.output==meshRead->output||
           (previous.indexCount==meshRead->indexCount&&previous.view==meshRead->view))return ERROR_INVALID_PARAMETER;
    }
    }
#endif
    HMODULE pinned{};
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&SeasonDrawObserve),&pinned))return GetLastError();
    DWORD error=run(request,reinterpret_cast<Draw>(base+TargetRva),false);
    request->error=error;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&request->status),error?4:2);
    return error;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){
    if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;
}
#endif
