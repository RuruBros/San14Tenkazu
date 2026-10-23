#pragma once
#include <cstdint>
namespace environment {
inline constexpr uint32_t WeatherMagic=0x32575645,WeatherVersion=4,TileCount=48400,CitySlots=51;
struct WeatherSnapshot {
    uint32_t magic=WeatherMagic,version=WeatherVersion,size=sizeof(WeatherSnapshot),date=0,flags=0,reserved=0;
    uint64_t generation=0,sequence=0;
    uint8_t regions[TileCount]{},weather[CitySlots]{};
    uint32_t recentDates[CitySlots*5]{};
};
static_assert(sizeof(WeatherSnapshot)==49512);
}
