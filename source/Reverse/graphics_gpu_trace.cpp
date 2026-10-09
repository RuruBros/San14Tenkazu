// A temporary PSSetShaderResources observer. Arguments and native calls are
// forwarded unchanged; no GPU binding or texture contents are modified.
#include <d3d11.h>
#include <stdint.h>
#include <intrin.h>

struct TraceRecord {
    uint64_t context, caller;
    uint32_t start, count;
    uint64_t views[16];
    uint64_t nativeR15;
    uint64_t ancestor, savedRbx;
    volatile long complete;
    uint32_t reserved;
};
struct TraceData {
    void (__stdcall *original)(ID3D11DeviceContext*,UINT,UINT,ID3D11ShaderResourceView* const*);
    volatile long count;
    volatile long enabled;
    uint32_t capacity, reserved;
    uint64_t bindCaller;
    TraceRecord records[1];
};
static_assert(sizeof(TraceRecord)==184,"Python record layout");
static_assert(offsetof(TraceData,records)==32,"Python data layout");

extern "C" __declspec(noinline) void gpu_trace(ID3D11DeviceContext* context, UINT start, UINT count,
    ID3D11ShaderResourceView* const* views, TraceData* data, uint64_t caller, uint64_t nativeR15, uint64_t nativeStack) {
    if (data->enabled) {
        long slot=_InterlockedIncrement(&data->count)-1;
        if (slot>=0 && uint32_t(slot)<data->capacity) {
            auto record=&data->records[slot];
            record->context=reinterpret_cast<uint64_t>(context);
            record->caller=caller;
            record->nativeR15=nativeR15;
            if (caller==data->bindCaller) {
                // 0x966540 pushes seven registers and subtracts 0x30 before
                // its API call. The original caller and its RBX are recorded.
                record->ancestor=*reinterpret_cast<uint64_t*>(nativeStack+0x70);
                record->savedRbx=*reinterpret_cast<uint64_t*>(nativeStack+0x68);
            }
            record->start=start; record->count=count;
            UINT n=count<16?count:16;
            if (views) for (UINT i=0;i<n;++i) record->views[i]=reinterpret_cast<uint64_t>(views[i]);
            _InterlockedExchange(&record->complete,1);
        }
    }
    data->original(context,start,count,views);
}
