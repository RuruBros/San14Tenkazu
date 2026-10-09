// Self-contained x64 frame function. Compiled .text must have zero relocations.
// Uses documented COM APIs; does not patch COM objects or change GPU bindings.
#include <d3d11.h>
#include <stdint.h>

struct Entry {
    uint64_t view;
    uint64_t resource;
    uint32_t dimension;
    uint32_t stage;
    uint32_t slot;
    D3D11_TEXTURE2D_DESC desc;
};
struct Output {
    uint32_t complete;
    uint32_t count;
    Entry entries[256];
};
static_assert(sizeof(Entry) == 72, "Python output layout");

struct Candidate {
    uint64_t wrapper;
    uint64_t vtable;
    uint64_t resource;
    uint64_t resourceVtable;
    uint64_t resourceField;
};
struct Input {
    uint32_t count;
    uint32_t reserved;
    Candidate entries[256];
};

extern "C" __declspec(noinline) int gpu_inspect(Input* input, Output* out) {
    out->complete = 0;
    out->count = 0;
    if (input->count > 256) return -1;
    for (uint32_t i = 0; i != input->count; ++i) {
        Candidate* c = input->entries + i;
        auto wrapper = reinterpret_cast<uint64_t*>(c->wrapper);
        if (wrapper[0] != c->vtable || *reinterpret_cast<uint64_t*>(c->resourceField) != c->resource ||
            !*reinterpret_cast<uint32_t*>(c->wrapper + 8) ||
            *reinterpret_cast<uint64_t*>(c->resource) != c->resourceVtable) return -2;
        Entry* e = out->entries + out->count++;
        e->view = c->wrapper;
        e->resource = c->resource;
        e->stage = 2;
        e->slot = i;
        auto resource = reinterpret_cast<ID3D11Resource*>(c->resource);
        D3D11_RESOURCE_DIMENSION dimension;
        resource->GetType(&dimension);
        e->dimension = dimension;
        if (dimension == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
            static_cast<ID3D11Texture2D*>(resource)->GetDesc(&e->desc);
    }
    out->complete = 1;
    return 0;
}

extern "C" __declspec(noinline) int gpu_probe(ID3D11DeviceContext* context, Output* out) {
    out->complete = 0;
    out->count = 0;
    ID3D11ShaderResourceView* views[128];
    for (uint32_t stage = 0; stage != 2; ++stage) {
        if (stage == 0) context->PSGetShaderResources(0, 128, views);
        else context->VSGetShaderResources(0, 128, views);
        for (uint32_t slot = 0; slot != 128; ++slot) {
            if (!views[slot]) continue;
            Entry* e = out->entries + out->count++;
            e->view = reinterpret_cast<uint64_t>(views[slot]);
            e->stage = stage;
            e->slot = slot;
            ID3D11Resource* resource = nullptr;
            views[slot]->GetResource(&resource);
            e->resource = reinterpret_cast<uint64_t>(resource);
            D3D11_RESOURCE_DIMENSION dimension = D3D11_RESOURCE_DIMENSION_UNKNOWN;
            if (resource) {
                resource->GetType(&dimension);
                if (dimension == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
                    static_cast<ID3D11Texture2D*>(resource)->GetDesc(&e->desc);
                resource->Release();
            }
            e->dimension = dimension;
            views[slot]->Release();
        }
    }
    out->complete = 1;
    return 0;
}
