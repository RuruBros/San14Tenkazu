#pragma once
#include <cstdint>
#include <cstddef>
namespace environment {
inline constexpr uint32_t Magic=0x314e5645,Version=5,ClimateBytes=46*12*4,EventCount=1024,DisasterCount=5;
enum Flags : uint32_t {RainMovement=1,SnowMovement=2,WeatherMorale=4,Flood=8,Wildfire=16,Drought=32,RainVisual=64,SnowVisual=128,Blizzard=256,Heatwave=512,All=1023};
inline constexpr uint32_t DisasterFlags[]{Flood,Wildfire,Drought,Blizzard,Heatwave};
struct Recovery {uint32_t magic=Magic,version=Version,error=0,complete=0;uint64_t generation=0,frames=0;uint32_t thread=0,reserved=0;};
struct Bootstrap {
    uint32_t magic,version,size,controllerPid;uint64_t controllerStartFileTime,module,generation;
    uint64_t worldClass,unitClass,tileClass,countyClass,cityClass;
    wchar_t pipe[128];Recovery recovery;
};
struct Values {
    int32_t rainReduction,heavyRainReduction,snowReduction,deepSnowReduction,weatherMoraleLoss;
    int32_t floodChance,floodTroopsPercent,floodMoraleLoss,floodFoodPercent;
    int32_t wildfireChance,wildfireTroopsPercent,wildfireMoraleLoss,wildfireFoodPercent,droughtChance,droughtFoodPercent;
    int32_t blizzardChance,blizzardTroopsPercent,blizzardMoraleLoss,blizzardFoodPercent;
    int32_t heatwaveChance,heatwaveTroopsPercent,heatwaveMoraleLoss,heatwaveFoodPercent;
};
inline constexpr Values DefaultValues{10,20,20,40,2,30,5,4,6,30,5,4,6,30,6,30,3,4,3,30,3,4,3};
struct Message {
    uint32_t magic,version,size,command;uint64_t generation,sequence,seed;
    uint32_t flags,reserved;Values values;uint8_t climate[ClimateBytes];
};
struct Event {
    uint64_t id;uint32_t date,kind,city,tile,unit,commander;
    int32_t troopsBefore,troopsAfter,moraleBefore,moraleAfter,foodBefore,foodAfter;
};
struct Reply {
    uint32_t magic=Magic,version=Version,size=sizeof(Reply),error=0;
    uint64_t generation=0,sequence=0,applied=0,frames=0,speedCalls=0,slowed=0,lastEvent=0;
    uint32_t thread=0,date=0,flags=0,reserved=0;Values values{};
    uint8_t weather[46]{};Event events[EventCount]{};
    uint32_t recentDates[46*DisasterCount]{};
    uint64_t noticesQueued=0;uint32_t noticesPending=0,noticeDate=0,noticeCity=0,noticeKind=0;
    uint64_t testId=0;uint32_t testMode=0,testState=0,testCity=0,testKind=0,testDate=0,testError=0;
};
static_assert(sizeof(Bootstrap)==376&&sizeof(Recovery)==40&&sizeof(Values)==92&&sizeof(Message)==2352&&sizeof(Event)==56&&sizeof(Reply)==58552);
}
