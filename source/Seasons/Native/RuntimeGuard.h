#pragma once
#include <d3d11_1.h>
#include <wrl/client.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <type_traits>
#include <utility>
#include <stdint.h>

namespace seasons {
using Microsoft::WRL::ComPtr;
struct Date {
    uint32_t year, month, day;
    bool valid() const { return year>100 && year<400 && month>=1 && month<=12 && day>=1 && day<=30; }
};

// The high bit closes admission atomically with respect to active frame entries.
// Scene tokens and dates come from validated immutable Controller snapshots;
// this class never dereferences game world/scene/resource-owner addresses.
class StopGate {
    static constexpr uint64_t stopBit=uint64_t(1)<<63;
    std::atomic<uint64_t> state{0};
    std::mutex leaseLock;
    uint64_t heartbeat;
    const uint64_t generation, leaseMs;
    // A captured time can predate a concurrently captured heartbeat. Unsigned
    // subtraction alone mistakes that ordering for a nearly UINT64_MAX delay.
    // GetTickCount64 intervals here are bounded below half its wrap period.
    static bool forward(uint64_t elapsed) { return elapsed<(uint64_t(1)<<63); }
    void expireLocked(uint64_t now) {
        const uint64_t elapsed=now-heartbeat;
        if(forward(elapsed) && elapsed>=leaseMs)stop();
    }
public:
    StopGate(uint64_t epoch,uint64_t now,uint64_t duration):heartbeat(now),generation(epoch),leaseMs(duration) {
        if (!epoch || duration<50 || duration>60000)throw std::invalid_argument("Invalid native season lease");
    }
    void stop() { state.fetch_or(stopBit,std::memory_order_acq_rel); }
    bool stopping() const { return (state.load(std::memory_order_acquire)&stopBit)!=0; }
    bool drained() const { return state.load(std::memory_order_acquire)==stopBit; }
    bool beat(uint64_t now) {
        std::lock_guard lock(leaseLock);
        expireLocked(now); // A late renewal must not revive an expired lease.
        if(stopping())return false;
        if(forward(now-heartbeat))heartbeat=now;
        return true;
    }
    void expire(uint64_t now) {
        std::lock_guard lock(leaseLock);expireLocked(now);
    }
    bool enter(uint64_t epoch,Date date,uint64_t now) {
        if(epoch!=generation || !date.valid()){stop();return false;}
        std::lock_guard lock(leaseLock);expireLocked(now);
        uint64_t current=state.load(std::memory_order_acquire);
        for(;;) {
            if(current&stopBit)return false;
            if(current==stopBit-1){stop();return false;}
            if(state.compare_exchange_weak(current,current+1,std::memory_order_acq_rel))return true;
        }
    }
    void leave() { state.fetch_sub(1,std::memory_order_acq_rel); }
};

inline ComPtr<ID3DDeviceContextState> CreatePipelineState(ID3D11DeviceContext* owned) {
    if(!owned || owned->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE)
        throw std::invalid_argument("Owned immediate render context is required");
    ComPtr<ID3D11Device> device;owned->GetDevice(&device);
    if(FAILED(device->GetDeviceRemovedReason()))throw std::runtime_error("Render device was removed");
    ComPtr<ID3D11Device1> device1;
    if(FAILED(device.As(&device1)))throw std::runtime_error("D3D11.1 device is required");
    const auto level=device->GetFeatureLevel();
    const UINT flags=(device->GetCreationFlags()&D3D11_CREATE_DEVICE_SINGLETHREADED)
        ?D3D11_1_CREATE_DEVICE_CONTEXT_STATE_SINGLETHREADED:0;
    ComPtr<ID3DDeviceContextState> effect;
    if(FAILED(device1->CreateDeviceContextState(flags,&level,1,D3D11_SDK_VERSION,
        __uuidof(ID3D11Device),nullptr,&effect)))throw std::runtime_error("Context state creation failed");
    return effect;
}

// D3D11's context-state object preserves complete pipeline state, including
// UAVs, stream output, predication, tessellation and compute state. Requires
// an owned immediate context and execution on its rendering thread.
// https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11devicecontext1-swapdevicecontextstate
class PipelineScope {
    ComPtr<ID3D11DeviceContext1> context;
    ComPtr<ID3DDeviceContextState> effect,previous;
public:
    explicit PipelineScope(ID3D11DeviceContext* owned,DWORD renderThread,ID3DDeviceContextState* reusable=nullptr) {
        if(!renderThread || GetCurrentThreadId()!=renderThread)
            throw std::invalid_argument("Pipeline isolation must run on the verified rendering thread");
        if(!owned || owned->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE)
            throw std::invalid_argument("Owned immediate render context is required");
        ComPtr<ID3D11Device> device;owned->GetDevice(&device);
        if(FAILED(device->GetDeviceRemovedReason()) || FAILED(owned->QueryInterface(IID_PPV_ARGS(&context))))
            throw std::runtime_error("Context state isolation is unavailable");
        if(reusable) {
            ComPtr<ID3D11Device> stateDevice;reusable->GetDevice(&stateDevice);
            if(stateDevice!=device)throw std::invalid_argument("Pipeline state belongs to another device");
            effect=reusable;
        } else effect=CreatePipelineState(owned);
        context->SwapDeviceContextState(effect.Get(),&previous);
        if(!previous)throw std::runtime_error("Original context state was not captured");
    }
    PipelineScope(const PipelineScope&)=delete;PipelineScope& operator=(const PipelineScope&)=delete;
    ~PipelineScope() { if(previous)context->SwapDeviceContextState(previous.Get(),nullptr); }
    bool restorable()const{return bool(previous);}
};

// Creation requires an already owned context from a verified render callback.
// There is deliberately no raw-pointer bootstrap or external vtable mutation.
// Frame tokens keep this session alive through GPU restoration even after OFF.
class ContextSession:public std::enable_shared_from_this<ContextSession> {
    ComPtr<ID3D11DeviceContext> context;ComPtr<ID3D11Device> device;
    ComPtr<ID3DDeviceContextState> effect;
    const DWORD renderThread=GetCurrentThreadId();
    bool rendering=false; // Accessed only by the owning rendering thread.
    StopGate gate;PTP_TIMER timer=nullptr;
    static void CALLBACK watch(PTP_CALLBACK_INSTANCE,void* self,PTP_TIMER) {
        static_cast<ContextSession*>(self)->gate.expire(GetTickCount64());
    }
public:
    ContextSession(ID3D11DeviceContext* owned,uint64_t generation,uint64_t lease=3000):context(owned),gate(generation,GetTickCount64(),lease) {
        if(!owned)throw std::invalid_argument("Owned native context is required");
        owned->GetDevice(&device);
        if(device->GetCreationFlags()&D3D11_CREATE_DEVICE_SINGLETHREADED)
            throw std::runtime_error("A single-threaded device requires render-thread-only ownership; this session is unavailable");
        effect=CreatePipelineState(owned);
        timer=CreateThreadpoolTimer(watch,this,nullptr);
        if(!timer)throw std::runtime_error("Native season watchdog creation failed");
        LARGE_INTEGER due;due.QuadPart=-500000;FILETIME time{due.LowPart,DWORD(due.HighPart)};
        SetThreadpoolTimer(timer,&time,50,0);
    }
    ~ContextSession() {
        SetThreadpoolTimer(timer,nullptr,0,0);WaitForThreadpoolTimerCallbacks(timer,TRUE);CloseThreadpoolTimer(timer);
    }
    bool heartbeat(){return gate.beat(GetTickCount64());}
    void stop(){gate.stop();}
    bool drained()const{return gate.drained();}
    bool stopping()const{return gate.stopping();}
private:
    struct Frame {
        std::shared_ptr<ContextSession> session;
        explicit Frame(std::shared_ptr<ContextSession> value):session(std::move(value)){}
        Frame(const Frame&)=delete;Frame& operator=(const Frame&)=delete;
        Frame(Frame&&)=delete;
        ~Frame(){if(session)session->gate.leave();}
        explicit operator bool()const{return bool(session);}
    };
    Frame enter(ID3D11DeviceContext* active,uint64_t generation,Date date) {
        // Check thread and raw identity before invoking any context/device APIs.
        if(GetCurrentThreadId()!=renderThread || active!=context.Get() || rendering) {gate.stop();return Frame(nullptr);}
        if(FAILED(device->GetDeviceRemovedReason())){gate.stop();return Frame(nullptr);}
        // Acquire shared ownership before reserving a callback count; allocation failure cannot leak a count.
        auto self=shared_from_this();
        if(!gate.enter(generation,date,GetTickCount64()))return Frame(nullptr);
        return Frame(std::move(self));
    }
public:
    // Synchronous scope: admission and owned refs outlive GPU restoration, even
    // when OFF drops the Controller reference or the effect throws. A token or
    // pipeline scope cannot escape independently or be reordered by the caller.
    template<class Work> bool render(ID3D11DeviceContext* active,uint64_t generation,Date date,Work&& work) {
        static_assert(std::is_same_v<std::invoke_result_t<Work,ID3D11DeviceContext*>,void>,
            "Season rendering work must be synchronous and return void");
        auto frame=enter(active,generation,date);if(!frame)return false;
        struct Active {
            bool& flag;explicit Active(bool& value):flag(value){flag=true;}
            ~Active(){flag=false;}
        } activeFrame(rendering);
        try {
            PipelineScope scope(context.Get(),renderThread,effect.Get());
            std::forward<Work>(work)(context.Get());
        } catch(...) {gate.stop();throw;}
        return true;
    }
};
}
