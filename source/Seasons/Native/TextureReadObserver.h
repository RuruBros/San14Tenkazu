#pragma once
#include "DrawObserver.h"
namespace season::observe {
// Appended after Request; separate diagnostic ABI. Only a previously observed
// 4096x4096 BC3 land texture or 2049x2049 RGBA control texture can be read.
struct TextureRead {
    uint32_t magic, version, size, slot;
    uint64_t psOwner, view, resource, output;
    uint32_t capacity, state, bytes, width, height, mipLevels, format, arraySize;
    int32_t hresult;
    uint32_t reserved;
    uint32_t sampleCount, sampleQuality, usage, bindFlags, cpuAccessFlags, miscFlags;
    uint32_t viewFormat, viewDimension, mostDetailedMip, viewMipLevels;
};
constexpr uint32_t TextureMagic=0x53545231, TextureVersion=2;
static_assert(sizeof(TextureRead)==128);
}
