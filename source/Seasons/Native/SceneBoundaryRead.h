#pragma once
#include "FrameObserver.h"
namespace season::boundary {
inline constexpr uint32_t Magic=0x53425231,Version=1;
struct Image {
    uint64_t resource,view,backbuffer,qpc;
    uint32_t width,height,format,offset,bytes,reserved;
};
struct Data {
    uint32_t magic,version,size,state;
    uint64_t driver,scenePsOwner,sceneVsOwner,scenePs,sceneVs,target;
    uint64_t uiPsOwner,uiVsOwner,uiPs,uiVs,inputLayout,output;
    uint32_t capacity,bytes,images,hresult,failureStage,thread,counterBefore,counterAfter;
    Image image[3];
};
struct Request {season::frame::Request frame;Data data;};
}
