#pragma once
#include "DrawObserver.h"
namespace season::observe {
// Bounded experiment on the GPU-identified obj-n00-tree-01 color submesh.
// RGB only: this does not implement species policy or winter leaf removal.
struct TreeMaterialTrial {
    uint32_t magic,version,size,year,month,day,renderThread,padding;
    uint64_t psOwner,ps,view,resource,output;
    uint32_t bytes,state,swaps,restores,originalBindingsRestored,gpuResourcesReleased,prepareCodeRestored,prepareProtectionRestored;
    uint64_t newView,newResource;
    int32_t hresult;uint32_t changedPixels;
    uint64_t vsOwner,vs,inputLayout;
    uint32_t indexCount,viewFormat,newViewFormat,width,height,mips,alphaPixels,outputBytes,failureStage,gpuReadbackComplete;
};
constexpr uint32_t TreeTrialMagic=0x53544d31,TreeTrialVersion=1,TreeTrialOutputBytes=1398016;
static_assert(sizeof(TreeMaterialTrial)==192);
}
