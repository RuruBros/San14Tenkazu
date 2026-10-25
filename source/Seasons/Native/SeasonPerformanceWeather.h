#pragma once
#include "../../Environment/Native/EnvironmentWeather.h"
#include "SeasonPerformance.h"
#include "../../Environment/Native/EnvironmentWeatherTiming.h"
namespace season_perf {
inline bool readTimed(BOOL(WINAPI* ordinary)(environment::WeatherSnapshot*),HMODULE module,environment::WeatherSnapshot* output){
    Cpu scope(WeatherRead);if(!local||!local->collecting.load()||!local->detailed())return ordinary&&ordinary(output);
    auto timed=module?reinterpret_cast<BOOL(WINAPI*)(environment::WeatherSnapshot*,environment::WeatherTiming*)>(GetProcAddress(module,"EnvironmentReadWeatherTiming")):nullptr;
    if(!timed){local->stop(7);return ordinary&&ordinary(output);}
    environment::WeatherTiming t;bool result=timed(output,&t)!=FALSE;
    local->add(EnvironmentWait,t.wait,t.wait);local->add(EnvironmentGuard,t.guard,t.guard);local->add(EnvironmentCopy,t.copy,t.copy);
    if(parent)parent->children+=t.wait+t.guard+t.copy;
    if(!result)local->stop(6);
    if(result){if(local->date&&local->date!=output->date)local->stop(6);std::array<uint64_t,4> frame{t.frameCalls,t.frameWait,t.frameTotal,t.frameThread};if(!local->environment)local->environmentFrameFirst=frame;local->environmentFrameLast=frame;
        if(local->environment&&(local->environment!=output->generation||local->environmentSequence!=output->sequence||local->date!=output->date||local->flags!=output->flags))local->stop(6);
        else {local->environment=output->generation;local->environmentSequence=output->sequence;local->date=output->date;local->flags=output->flags;}}
    return result;
}
}
