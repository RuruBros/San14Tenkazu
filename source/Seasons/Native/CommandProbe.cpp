// One-shot capability inspection at the engine's static command-dispatch table.
// The game supplies the borrowed, live driver on its renderer thread. No scene,
// world, texture-owner or externally cached context pointer is dereferenced.
#include <d3d11_1.h>
#include <intrin.h>
#include <stdint.h>

typedef void* (*Command)(void*,void*,uint32_t);
struct CommandProbe {
    Command original;
    void* volatile* slot;
    void* installed;
    void* driverVtable;
    DWORD (WINAPI* threadId)();
    ULONGLONG (WINAPI* tick)();
    uint64_t deadline;
    volatile long state; // 0 armed, 1 running, 2 completed; never rearmed.
    long result;
    void* driver;
    void* context;
    void* device;
    uint32_t flags,type,thread,restored;
    GUID context1;
};
static_assert(sizeof(CommandProbe)==120);

extern "C" __declspec(noinline) void* season_command_probe(
    void* driver,void* packet,uint32_t command,CommandProbe* data) {
    if(_InterlockedCompareExchange(&data->state,1,0)==0) {
        // Only this winning invocation writes the static slot. Later entries may
        // have loaded the wrapper just before restoration and merely forward.
        void* current=_InterlockedCompareExchangePointer(data->slot,
            reinterpret_cast<void*>(data->original),data->installed);
        data->restored=current==data->installed || current==reinterpret_cast<void*>(data->original);
        data->result=E_FAIL;
        if(data->restored && data->tick()<data->deadline && driver &&
            *static_cast<void**>(driver)==data->driverVtable) {
            auto context=*reinterpret_cast<ID3D11DeviceContext**>(static_cast<uint8_t*>(driver)+0x10);
            if(context) {
                // Driver destruction joins its command worker before releasing
                // +0x10. Take a strong COM reference while this callback is live.
                context->AddRef();
                ID3D11Device* device=nullptr;context->GetDevice(&device);
                data->driver=driver;data->context=context;data->device=device;
                data->thread=data->threadId();data->type=context->GetType();
                if(device) {
                    data->flags=device->GetCreationFlags();
                    ID3D11DeviceContext1* context1=nullptr;
                    data->result=context->QueryInterface(data->context1,reinterpret_cast<void**>(&context1));
                    if(SUCCEEDED(data->result) && !context1)data->result=E_NOINTERFACE;
                    if(context1)context1->Release();
                    if(data->type!=D3D11_DEVICE_CONTEXT_IMMEDIATE ||
                        (data->flags&D3D11_CREATE_DEVICE_SINGLETHREADED))data->result=E_NOINTERFACE;
                    device->Release();
                }
                context->Release();
            }
        }
        _InterlockedExchange(&data->state,2);
    }
    return data->original(driver,packet,command);
}
