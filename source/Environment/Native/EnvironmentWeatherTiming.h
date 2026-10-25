#pragma once
#include <cstdint>
namespace environment {
// Optional caller-owned timing receipt; the ordinary weather ABI is unchanged.
struct WeatherTiming {uint32_t size=sizeof(WeatherTiming),version=1;uint64_t wait=0,guard=0,copy=0,frameCalls=0,frameWait=0,frameTotal=0;uint32_t frameThread=0,reserved=0;};
static_assert(sizeof(WeatherTiming)==64);
}
