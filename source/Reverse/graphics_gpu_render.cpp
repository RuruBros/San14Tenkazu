// Execute a helper once, after a verified native texture slot bind returns.
// Native API arguments are forwarded unchanged. No persistent render hook.
#include <d3d11.h>
#include <stdint.h>
#include <intrin.h>

struct Dispatch {
    volatile long state;
    uint32_t result;
    void (__stdcall *original)(ID3D11DeviceContext*,UINT,UINT,ID3D11ShaderResourceView* const*);
    int (*helper)(void*,void*);
    void* arg1;
    void* arg2;
    ID3D11DeviceContext* context;
    ID3D11ShaderResourceView* view;
    UINT start, count;
    uint64_t stack;
};
static_assert(sizeof(Dispatch)==72,"Python dispatch layout");

extern "C" __declspec(noinline) void gpu_render_dispatch(ID3D11DeviceContext* context,UINT start,UINT count,
    ID3D11ShaderResourceView* const* views,Dispatch* data) {
    // The context owns the current view before a restore can release it.
    // Capture matching before the helper can change its supplied arguments.
    // UINTPTR_MAX schedules on the verified context/slot independently of the
    // resource being replaced. This never substitutes native API arguments.
    bool match=context==data->context && start==data->start && count==data->count && views &&
        (data->view==reinterpret_cast<ID3D11ShaderResourceView*>(UINTPTR_MAX) || views[0]==data->view);
    data->original(context,start,count,views);
    if (match && _InterlockedCompareExchange(&data->state,1,0)==0) {
        data->stack=reinterpret_cast<uint64_t>(_AddressOfReturnAddress());
        data->result=data->helper(data->arg1,data->arg2);
        _InterlockedExchange(&data->state,2);
    }
}
