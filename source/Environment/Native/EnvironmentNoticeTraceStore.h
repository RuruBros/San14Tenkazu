#pragma once
#include "EnvironmentNoticeTrace.h"
#include <array>
namespace notice_trace {
class Store {
    SRWLOCK lock=SRWLOCK_INIT;
    State latest{};
    std::array<Entry,EventCapacity> entries{};
    std::array<GateSummary,GateCount> gates{};
    std::atomic<uint32_t> firstStop{None};
public:
    std::atomic<bool> enabled{false};
    std::atomic<bool> stopRequested{false};
    bool arm(uint64_t environment,uint64_t season){
        AcquireSRWLockExclusive(&lock);
        bool ok=!enabled.load()&&!latest.season;
        if(ok){latest.environment=environment;latest.season=season;LARGE_INTEGER f{};QueryPerformanceFrequency(&f);latest.frequency=f.QuadPart;enabled=true;}
        ReleaseSRWLockExclusive(&lock);return ok;
    }
    void start(uint64_t test,uint32_t kind){
        if(!enabled)return;AcquireSRWLockExclusive(&lock);
        if(!latest.test){latest.test=test;latest.kind=kind;latest.startedMs=milliseconds();}
        ReleaseSRWLockExclusive(&lock);
    }
    bool gate(uint32_t index,bool passed,uint64_t value){
        if(!enabled||index>=GateCount)return passed;
        AcquireSRWLockExclusive(&lock);
        if(latest.test&&!latest.ended){auto& g=gates[index];auto now=qpc();if(!g.evaluated){g.firstQpc=now;g.firstValue=value;}
            ++g.evaluated;if(passed)++g.passed;else ++g.rejected;g.lastQpc=now;g.lastValue=value;}
        ReleaseSRWLockExclusive(&lock);return passed;
    }
    void stop(uint32_t reason){if(!enabled)return;uint32_t expected=None;firstStop.compare_exchange_strong(expected,reason);stopRequested=true;}
    void record(State value){
        if(!enabled)return;AcquireSRWLockExclusive(&lock);
        if(latest.ended){ReleaseSRWLockExclusive(&lock);return;}
        value.environment=latest.environment;value.season=latest.season;value.test=latest.test;
        value.startedMs=latest.startedMs;value.frequency=latest.frequency;value.kind=latest.kind;
        value.captureStartedMs=latest.captureStartedMs;value.captureStartedQpc=latest.captureStartedQpc;
        if(value.phase==QueueBefore&&value.test&&!value.captureStartedMs){value.captureStartedMs=milliseconds();value.captureStartedQpc=qpc();}
        value.stopReason=firstStop.load();
        value.records=latest.records;value.dropped=latest.dropped;value.qpc=qpc();value.thread=GetCurrentThreadId();
        if(value.phase==Finished)value.ended=1;
        State a=value,b=latest;a.qpc=b.qpc=0;a.records=b.records=0;a.dropped=b.dropped=0;
        if(value.phase!=Poll||!(a==b)){
            if(value.records<EventCapacity){auto index=value.records++;entries[index]={index+1,value};}
            else {++value.dropped;stop(EventCapacityStop);value.stopReason=firstStop.load();}
        }
        latest=value;ReleaseSRWLockExclusive(&lock);
    }
    // The render thread never waits for Environment's state lock or this lock.
    bool read(Query& query){
        if(!TryAcquireSRWLockShared(&lock))return false;
        bool ok=query.season==latest.season&&(!query.environment||query.environment==latest.environment)&&query.first<=latest.records;
        if(ok){query.state=latest;query.state.stopReason=firstStop.load();query.count=std::min(16u,latest.records-query.first);
            if(query.command==0)query.count=0;
            if(query.command==2)std::copy(gates.begin(),gates.end(),query.gates);
            for(uint32_t i=0;i<query.count;i++)query.entries[i]=entries[query.first+i];}
        ReleaseSRWLockShared(&lock);return ok;
    }
    bool expired() {
        if(stopRequested)return true;Query q;q.season=season();if(!read(q)||!q.state.startedMs)return false;
        auto now=milliseconds();
        if(now-q.state.startedMs>=120000){stop(PreparationDeadline);return true;}
        if(q.state.captureStartedMs&&now-q.state.captureStartedMs>=5000){stop(CaptureDeadline);return true;}
        return false;
    }
    uint64_t season(){AcquireSRWLockShared(&lock);auto result=latest.season;ReleaseSRWLockShared(&lock);return result;}
};
}
