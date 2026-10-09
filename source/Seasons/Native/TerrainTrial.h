#pragma once
#include "DrawObserver.h"
namespace season::observe {
// One paused-date, one land pass, hard-deadline experiment. Not the product ABI.
struct TerrainTrial {
    uint32_t magic,version,size,year,month,day,renderThread,padding;
    uint64_t psOwner,ps,view,resource,input;
    uint32_t bytes,state,swaps,restores,originalBindingsRestored,gpuResourcesReleased,prepareCodeRestored,prepareProtectionRestored;
    uint64_t newView,newResource;
    int32_t hresult;uint32_t changedBlocks;
};
constexpr uint32_t TrialMagic=0x53545431,TrialVersion=1;
static_assert(sizeof(TerrainTrial)==128);
}
