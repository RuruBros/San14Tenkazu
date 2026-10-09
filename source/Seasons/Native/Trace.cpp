// Bounded native draw observer. No shader, binding or gameplay replacement.
#include <d3d11.h>
#include <stdint.h>
#include <intrin.h>

struct DrawRecord {
    uint64_t caller, shader, vertexShader, targets[4], depth, views[12], constants[8], vertexConstants[8];
    uint32_t kind, count, start, base, instances, firstInstance;
    D3D11_VIEWPORT viewport;
    volatile long complete;
    uint32_t reserved;
};
struct DrawTrace {
    void* originals[4];
    volatile long enabled, count;
    uint32_t capacity, storageCapacity;
    DrawRecord records[1];
};
static_assert(sizeof(DrawRecord) == 344, "Trace ABI");
static_assert(offsetof(DrawTrace, records) == 48, "Trace ABI");

static __forceinline void capture(ID3D11DeviceContext* c, DrawTrace* d, uint64_t caller,
                                 UINT count, UINT start, INT base, UINT kind, UINT instances=1, UINT firstInstance=0) {
    if (!d->enabled) return;
    // Capacity is the number actually allocated, repeated in the immutable storage contract.
    // Reserve only accepted records: a full buffer cannot keep incrementing into signed overflow.
    if (!d->capacity || d->capacity>8192 || d->capacity!=d->storageCapacity) return;
    long slot;
    for (;;) {
        slot = _InterlockedCompareExchange(&d->count,0,0);
        if (slot < 0 || uint32_t(slot) >= d->capacity) return;
        if (_InterlockedCompareExchange(&d->count,slot+1,slot)==slot) break;
    }
    DrawRecord* r = &d->records[slot];
    r->caller = caller; r->kind = kind; r->count = count; r->start = start; r->base = base;
    r->instances = instances; r->firstInstance = firstInstance;
    ID3D11PixelShader* ps = nullptr; ID3D11VertexShader* vs = nullptr;
    c->PSGetShader(&ps, nullptr, nullptr); c->VSGetShader(&vs, nullptr, nullptr);
    r->shader = reinterpret_cast<uint64_t>(ps); r->vertexShader = reinterpret_cast<uint64_t>(vs);
    if (ps) ps->Release(); if (vs) vs->Release();
    ID3D11RenderTargetView* rt[4]; ID3D11DepthStencilView* depth = nullptr;
    c->OMGetRenderTargets(4, rt, &depth);
    for (int i=0;i<4;++i) { r->targets[i]=reinterpret_cast<uint64_t>(rt[i]); if(rt[i])rt[i]->Release(); }
    r->depth=reinterpret_cast<uint64_t>(depth); if(depth)depth->Release();
    ID3D11ShaderResourceView* views[12]; c->PSGetShaderResources(0,12,views);
    for(int i=0;i<12;++i){r->views[i]=reinterpret_cast<uint64_t>(views[i]);if(views[i])views[i]->Release();}
    ID3D11Buffer* cb[8]; c->PSGetConstantBuffers(0,8,cb);
    for(int i=0;i<8;++i){r->constants[i]=reinterpret_cast<uint64_t>(cb[i]);if(cb[i])cb[i]->Release();}
    c->VSGetConstantBuffers(0,8,cb);
    for(int i=0;i<8;++i){r->vertexConstants[i]=reinterpret_cast<uint64_t>(cb[i]);if(cb[i])cb[i]->Release();}
    UINT n=1;c->RSGetViewports(&n,&r->viewport);
    _InterlockedExchange(&r->complete,1);
}
extern "C" __declspec(noinline) void season_trace_indexed(ID3D11DeviceContext* c, UINT count,
    UINT start, INT base, DrawTrace* d, uint64_t caller) {
    capture(c,d,caller,count,start,base,0);
    reinterpret_cast<void(__stdcall*)(ID3D11DeviceContext*,UINT,UINT,INT)>(d->originals[0])(c,count,start,base);
}
extern "C" __declspec(noinline) void season_trace_draw(ID3D11DeviceContext* c, UINT count,
    UINT start, DrawTrace* d, uint64_t caller) {
    capture(c,d,caller,count,start,0,1);
    reinterpret_cast<void(__stdcall*)(ID3D11DeviceContext*,UINT,UINT)>(d->originals[1])(c,count,start);
}
extern "C" __declspec(noinline) void season_trace_indexed_instanced(ID3D11DeviceContext* c,
    UINT count, UINT instances, UINT start, INT base, UINT firstInstance, DrawTrace* d, uint64_t caller) {
    capture(c,d,caller,count,start,base,2,instances,firstInstance);
    reinterpret_cast<void(__stdcall*)(ID3D11DeviceContext*,UINT,UINT,UINT,INT,UINT)>(d->originals[2])
        (c,count,instances,start,base,firstInstance);
}
extern "C" __declspec(noinline) void season_trace_instanced(ID3D11DeviceContext* c,
    UINT count, UINT instances, UINT start, UINT firstInstance, DrawTrace* d, uint64_t caller) {
    capture(c,d,caller,count,start,0,3,instances,firstInstance);
    reinterpret_cast<void(__stdcall*)(ID3D11DeviceContext*,UINT,UINT,UINT,UINT)>(d->originals[3])
        (c,count,instances,start,firstInstance);
}
extern "C" __declspec(noinline) void season_trace_views(ID3D11DeviceContext* c,
    UINT start, UINT count, ID3D11ShaderResourceView* const* views, DrawTrace* d, uint64_t caller) {
    reinterpret_cast<void(__stdcall*)(ID3D11DeviceContext*,UINT,UINT,ID3D11ShaderResourceView* const*)>
        (d->originals[0])(c,start,count,views);
    capture(c,d,caller,count,start,0,4);
}
extern "C" __declspec(noinline) void season_trace_shader(ID3D11DeviceContext* c,
    ID3D11PixelShader* ps, ID3D11ClassInstance* const* classes, UINT count, DrawTrace* d, uint64_t caller) {
    reinterpret_cast<void(__stdcall*)(ID3D11DeviceContext*,ID3D11PixelShader*,ID3D11ClassInstance* const*,UINT)>
        (d->originals[1])(c,ps,classes,count);
    capture(c,d,caller,count,0,0,5);
}
