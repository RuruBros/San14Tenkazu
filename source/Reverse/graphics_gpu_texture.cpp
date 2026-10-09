// Relocation-free frame routines using documented D3D11 APIs.
// A source asset is identified by byte-for-byte GPU readback before any swap.
#include <d3d11.h>
#include <stdint.h>

struct Identity {
    uint64_t owner;
    uint64_t ownerVtable;
    uint64_t field;
    uint64_t resource;
    uint64_t resourceVtable;
};
static __forceinline bool valid(const Identity& i) {
    return *reinterpret_cast<uint64_t*>(i.owner) == i.ownerVtable &&
        *reinterpret_cast<uint32_t*>(i.owner + 8) != 0 &&
        *reinterpret_cast<uint64_t*>(i.field) == i.resource &&
        *reinterpret_cast<uint64_t*>(i.resource) == i.resourceVtable;
}

struct ReadInput {
    Identity identity;
    ID3D11Device* device;
    ID3D11DeviceContext* context;
    uint8_t* pixels;
    uint64_t capacity;
};
struct ReadOutput {
    uint32_t complete;
    HRESULT result;
    D3D11_TEXTURE2D_DESC desc;
    uint32_t reserved;
    uint64_t bytes;
};
static_assert(sizeof(ReadOutput) == 64, "Python output layout");

extern "C" __declspec(noinline) int gpu_read(ReadInput* in, ReadOutput* out) {
    out->complete = 0;
    out->bytes = 0;
    out->result = E_INVALIDARG;
    if (!valid(in->identity)) return -1;
    auto source = reinterpret_cast<ID3D11Texture2D*>(in->identity.resource);
    source->GetDesc(&out->desc);
    D3D11_TEXTURE2D_DESC desc = out->desc;
    if (desc.Format != DXGI_FORMAT_BC3_TYPELESS || desc.SampleDesc.Count != 1) return -2;
    uint64_t required = 0;
    for (UINT m = 0; m != desc.MipLevels; ++m) {
        UINT w = desc.Width >> m; if (!w) w = 1;
        UINT h = desc.Height >> m; if (!h) h = 1;
        required += uint64_t((w + 3) / 4) * ((h + 3) / 4) * 16 * desc.ArraySize;
    }
    if (required > in->capacity) return -3;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ID3D11Texture2D* staging = nullptr;
    out->result = in->device->CreateTexture2D(&desc, nullptr, &staging);
    if (FAILED(out->result)) return -4;
    in->context->CopyResource(staging, source);
    for (UINT layer = 0; layer != desc.ArraySize; ++layer) {
        for (UINT mip = 0; mip != desc.MipLevels; ++mip) {
            UINT w = desc.Width >> mip; if (!w) w = 1;
            UINT h = desc.Height >> mip; if (!h) h = 1;
            UINT pitch = ((w + 3) / 4) * 16, rows = (h + 3) / 4;
            UINT sub = mip + layer * desc.MipLevels;
            D3D11_MAPPED_SUBRESOURCE mapped;
            out->result = in->context->Map(staging, sub, D3D11_MAP_READ, 0, &mapped);
            if (FAILED(out->result)) { staging->Release(); return -5; }
            if (mapped.RowPitch < pitch) {
                in->context->Unmap(staging, sub); staging->Release(); return -6;
            }
            // Volatile byte copies prevent a compiler-generated CRT dependency.
            auto dst = reinterpret_cast<volatile uint8_t*>(in->pixels + out->bytes);
            auto src = reinterpret_cast<const volatile uint8_t*>(mapped.pData);
            for (UINT row = 0; row != rows; ++row)
                for (UINT col = 0; col != pitch; ++col)
                    dst[uint64_t(row)*pitch+col] = src[uint64_t(row)*mapped.RowPitch+col];
            out->bytes += uint64_t(pitch) * rows;
            in->context->Unmap(staging, sub);
        }
    }
    staging->Release();
    out->complete = 1;
    return 0;
}

struct SwapInput {
    Identity identity;
    ID3D11Device* device;
    D3D11_SUBRESOURCE_DATA* initial;
    uint64_t views[2];
    D3D11_TEXTURE2D_DESC expected;
    uint32_t command; // 0 = swap, 1 = restore
};
struct SwapSession {
    uint32_t phase; // 0 = unchanged, 1 = replacement owns record, 2 = restored
    HRESULT result;
    uint64_t original[3];
    uint64_t replacement[3];
    D3D11_TEXTURE2D_DESC desc;
};
static_assert(sizeof(SwapInput) == 120, "Python input layout");
static_assert(sizeof(SwapSession) == 104, "Python session layout");

static __forceinline void release_replacement(SwapSession* s) {
    for (int i = 2; i >= 0; --i) {
        if (s->replacement[i]) {
            reinterpret_cast<IUnknown*>(s->replacement[i])->Release();
            s->replacement[i] = 0;
        }
    }
}

extern "C" __declspec(noinline) int gpu_swap(SwapInput* in, SwapSession* out) {
    auto record = reinterpret_cast<uint64_t*>(in->identity.field);
    if (in->command == 1) {
        if (out->phase != 1) return out->phase == 2 ? 0 : -10;
        if (*reinterpret_cast<uint64_t*>(in->identity.owner) != in->identity.ownerVtable ||
            !*reinterpret_cast<uint32_t*>(in->identity.owner + 8)) return -11;
        for (int i = 0; i != 3; ++i) if (record[i] != out->replacement[i]) return -12;
        // Transfer the session's original references back to the engine record.
        for (int i = 0; i != 3; ++i) record[i] = out->original[i];
        release_replacement(out);
        out->phase = 2;
        out->result = S_OK;
        return 0;
    }
    if (in->command != 0 || out->phase != 0 || !valid(in->identity)) return -1;
    if (record[1] != in->views[0] || record[2] != in->views[1]) return -2;
    auto original = reinterpret_cast<ID3D11Texture2D*>(in->identity.resource);
    original->GetDesc(&out->desc);
    auto a = reinterpret_cast<UINT*>(&out->desc);
    auto b = reinterpret_cast<UINT*>(&in->expected);
    for (int i = 0; i != 11; ++i) if (a[i] != b[i]) return -3;
    const bool fieldMap = out->desc.Width == 4096 && out->desc.Height == 4096 &&
        out->desc.MipLevels == 11 && out->desc.ArraySize == 1;
    const bool detailLayers = out->desc.Width == 1024 && out->desc.Height == 1024 &&
        out->desc.MipLevels == 9 && out->desc.ArraySize == 19;
    if (out->desc.Format != DXGI_FORMAT_BC3_TYPELESS || !(fieldMap || detailLayers) ||
        out->desc.Usage != D3D11_USAGE_IMMUTABLE || out->desc.BindFlags != D3D11_BIND_SHADER_RESOURCE ||
        out->desc.SampleDesc.Count != 1) return -4;
    ID3D11Texture2D* texture = nullptr;
    out->result = in->device->CreateTexture2D(&out->desc, in->initial, &texture);
    if (FAILED(out->result)) return -5;
    out->replacement[0] = reinterpret_cast<uint64_t>(texture);
    for (int i = 0; i != 2; ++i) {
        if (!in->views[i]) continue;
        auto view = reinterpret_cast<ID3D11ShaderResourceView*>(in->views[i]);
        D3D11_SHADER_RESOURCE_VIEW_DESC desc;
        view->GetDesc(&desc);
        ID3D11ShaderResourceView* replacement = nullptr;
        out->result = in->device->CreateShaderResourceView(texture, &desc, &replacement);
        if (FAILED(out->result)) { release_replacement(out); return -6; }
        out->replacement[i+1] = reinterpret_cast<uint64_t>(replacement);
    }
    if (!valid(in->identity) || record[1] != in->views[0] || record[2] != in->views[1]) {
        release_replacement(out); return -7;
    }
    for (int i = 0; i != 3; ++i) {
        out->original[i] = record[i];
        if (record[i]) reinterpret_cast<IUnknown*>(record[i])->AddRef();
    }
    // Transfer replacement references to the record and originals to the session.
    out->phase = 1;
    for (int i = 0; i != 3; ++i) record[i] = out->replacement[i];
    for (int i = 0; i != 3; ++i)
        if (out->original[i]) reinterpret_cast<IUnknown*>(out->original[i])->Release();
    out->result = S_OK;
    return 0;
}
