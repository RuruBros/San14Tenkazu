#pragma once
#include <cstdint>
namespace season::observe {
constexpr uint32_t MeshReadMagic=0x534d5231,MeshReadVersion=2,MeshBlobCount=12,MeshReadCapacity=4*1024*1024;
struct MeshBlob {
    uint64_t resource;
    uint32_t kind,slot,offset,bytes,format,width,height,mipLevels,arraySize,usage,bindFlags,miscFlags,stride,bindingOffset,structureStride,sourceOffset;
};
struct MeshRead {
    uint32_t magic,version,size,indexCount,renderThread,requireColor,state,hresult,capacity,bytes,blobCount,indexFormat,indexOffset,topology;
    uint32_t failureStage,failureKind,failureSlot,indexStart,instances,maxIndex;
    uint64_t view,resource,output,inputLayout;
    MeshBlob blobs[MeshBlobCount];
};
static_assert(sizeof(MeshBlob)==72&&sizeof(MeshRead)==976);
// A single diagnostic lease may read at most three independently selected
// resources. It still consumes the module's one-shot admission exactly once.
constexpr uint32_t MeshBatchMagic=0x534d4231,MeshBatchVersion=1,MeshBatchCount=3;
struct MeshReadBatch {
    uint32_t magic,version,size,count;
    MeshRead items[MeshBatchCount];
};
static_assert(sizeof(MeshReadBatch)==2944);
}
