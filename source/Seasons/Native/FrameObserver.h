#pragma once
#include <cstdint>
namespace season::frame {
inline constexpr uint32_t Magic=0x53465231,Version=2;
#ifdef SEASON_FRAME_SCENE_READ
inline constexpr uint32_t Capacity=64,PatchCount=3;
#else
inline constexpr uint32_t Capacity=8192,PatchCount=11;
#endif
// Lightweight actual engine Draw chronology; resource descriptions are resolved
// separately. Pointer records do not extend any game resource's lifetime.
struct Row {
    uint64_t sequence,qpcBegin,qpcEnd,driver,context,psOwner,vsOwner,ps,vs;
    uint64_t views[12],targets[8],depth,vertexBuffers[8],indexBuffer,inputLayout;
    uint32_t strides[8],offsets[8],indexFormat,indexOffset,topology;
    float viewport[6];
    uint32_t kind,thread,mode,start,count,instances,counterBefore,counterAfter,complete;
    uint32_t presentEnabled,syncInterval,presentFlags;
    uint64_t swapchain;
    uint64_t bindingSampleSequence;
    uint32_t bindingsCaptured,reserved;
};
struct PatchReceipt {
    uint64_t address,trampoline;
    uint32_t size,originalProtection,finalProtection,codeRestored,protectionRestored,reserved;
};
struct Request {
    uint32_t magic,version,size,durationMs;
    uint64_t moduleBase,context,device,sceneDriver,swapchain,qpcFrequency;
    int32_t cancel,status,error,records,active,rejected,skipped,saturated;
    uint32_t callbacksDrained,referencesReleased,failedThread,failedStage;
    uint32_t sparseBindings,bindingSamples,renderThread,otherDrawThreads,targetPresentReturns,presentReturns;
    PatchReceipt patches[PatchCount];
    Row rows[Capacity];
};
}
