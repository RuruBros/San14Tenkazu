#pragma once
#include <cstdint>
// In-process diagnostic ABI only. Weather and gameplay protocol remain unchanged.
namespace notice_trace {
inline constexpr uint32_t Magic=0x4e545231,Version=2,EventCapacity=4096,GateCount=48;
enum Stop:uint32_t {None=0,PreparationDeadline,CaptureDeadline,RowCapacity,DescriptorCapacity,EventCapacityStop,ManualCancel,StateChanged,QueueFailure,OwnerStop};
enum Gate:uint32_t {ForegroundPid=0,EngineForeground,RequestAge,ForegroundAge,NotBusy,BeginNotBusy,KindValid,LayoutPresent,TextPages,QueueHead,QueueNode,QueueReadback,ReadyFixture,
    ReadyFirst=16,ReadyQueue=32};
struct GateSummary {uint64_t evaluated=0,passed=0,rejected=0,firstQpc=0,lastQpc=0,firstValue=0,lastValue=0;};
#if defined(ENVIRONMENT_FIXTURE)||defined(SEASON_BRIDGE_FIXTURE)
inline uint64_t fixtureMilliseconds=0;
#endif
inline uint64_t milliseconds(){
#if defined(ENVIRONMENT_FIXTURE)||defined(SEASON_BRIDGE_FIXTURE)
    if(fixtureMilliseconds)return fixtureMilliseconds;
#endif
    return GetTickCount64();
}
enum Event:uint32_t {Armed=1,Requested,QueueBefore,QueueAfter,Poll,PictureBefore,PictureAfter,HideBefore,HideAfter,Reset,Cancel,Finished};
struct State {
    uint64_t qpc=0,frequency=0,environment=0,season=0,test=0,startedMs=0,state=0,layout=0;
    uint32_t phase=0,thread=0,kind=0,visual=0,shown=0,closing=0,active=0,pending=0,soundOwned=0;
    bool operator==(const State&) const = default;
    uint32_t layoutShown=0,category=0,picture=0,layoutValid=0,reason=0,records=0,dropped=0,ended=0;
    uint64_t captureStartedMs=0,captureStartedQpc=0;
    uint32_t stopReason=0,reserved=0;
};
struct Entry {uint64_t sequence=0;State state;};
struct Query {
    uint32_t magic=Magic,version=Version,size=sizeof(Query),command=0;
    uint64_t season=0,environment=0,controllerStart=0;
    uint32_t controllerPid=0,first=0,count=0,reserved=0;
    State state;Entry entries[16]{};GateSummary gates[GateCount]{};
};
using Exchange=BOOL(WINAPI*)(Query*);
inline uint64_t qpc(){LARGE_INTEGER value{};QueryPerformanceCounter(&value);return value.QuadPart;}
}
