#pragma once
#include <cstdint>
namespace seasons::bridge {
inline constexpr uint32_t Magic=0x53425331,Version=5;
enum Command:uint32_t {Hello=1,Apply=2,Heartbeat=3,Stop=4,ApplyTreeColor=5,TreeColorStatus=6,SceneStatus=7};
struct Message {
    uint32_t magic,version,size,command;
    uint64_t generation,sequence;
    uint32_t year,month,day,reserved;
    float weights[4];
};
struct Reply {
    uint32_t magic,version,size,error;
    uint64_t generation,sequence,actualDraws;
    uint32_t renderThread,rendered,year,month,day,capabilities;
    uint64_t materialEpoch,completedEpoch,treeDraws,swaps,restores,gpuBytes;
    uint32_t generated,alphaPixels,colorScope,gpuStateVerified;
    uint32_t selection[16];
};
struct Recovery {
    uint32_t magic,version,size,error;
    uint64_t generation,actualDraws;
    uint32_t renderThread,callbacksDrained,originalGpuState,dispatchDetached,protectionRestored,resourcesReleased;
};
struct Bootstrap {
    uint32_t magic,version,size,reserved;
    uint64_t generation,controllerStartFileTime;
    uint32_t controllerPid,leaseMs;
    uint64_t module,world,state,manager,scene,driver,device,context;
    wchar_t pipe[128];
    Recovery recovery;
    uint64_t sources;
    uint32_t sourceBytes,sourceReserved;
    uint64_t sceneData;
    uint32_t sceneBytes,sceneReserved;
};
static_assert(sizeof(Message)==64&&sizeof(Reply)==192&&sizeof(Recovery)==56&&sizeof(Bootstrap)==448);
}
