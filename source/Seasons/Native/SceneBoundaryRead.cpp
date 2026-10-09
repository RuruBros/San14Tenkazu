// New capability: exact GPU backbuffer readback around one authenticated UI
// candidate Draw. This is not another load of the one-shot FrameObserver.
#define SEASON_FRAME_SCENE_READ
#define SEASON_FRAME_FIXTURE
#include "SceneBoundaryRead.h"
#include "FrameObserver.cpp"
extern "C" __declspec(dllexport) DWORD WINAPI SeasonSceneBoundaryRead(season::boundary::Request* r){
    if(!r||r->frame.moduleBase!=reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr))||
       r->frame.durationMs>1000||r->data.magic!=season::boundary::Magic||r->data.version!=season::boundary::Version||
       r->data.size!=sizeof(season::boundary::Data)||!r->data.output||r->data.capacity>32*1024*1024||
       !r->data.driver||!r->data.scenePsOwner||!r->data.sceneVsOwner||!r->data.scenePs||!r->data.sceneVs||
       !r->data.target||!r->data.uiPsOwner||!r->data.uiVsOwner||!r->data.uiPs||!r->data.uiVs||!r->data.inputLayout)
        return ERROR_INVALID_PARAMETER;
    HMODULE pinned{};
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&SeasonSceneBoundaryRead),&pinned))return GetLastError();
    boundary=&r->data;initialize();auto error=run(&r->frame,false);
    r->frame.error=error;InterlockedExchange(reinterpret_cast<volatile LONG*>(&r->frame.status),error?4:2);return error;
}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(h);return TRUE;}
