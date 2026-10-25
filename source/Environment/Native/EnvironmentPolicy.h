#pragma once
#include "EnvironmentProtocol.h"
#include <algorithm>
#include <cstring>
#include <cmath>
namespace environment {
inline uint64_t random(uint64_t seed,uint32_t date,uint32_t city,uint32_t kind) {
    uint64_t value=seed^(uint64_t(date)<<32)^(uint64_t(city)<<16)^kind;
    value+=0x9e3779b97f4a7c15;value=(value^(value>>30))*0xbf58476d1ce4e5b9;
    value=(value^(value>>27))*0x94d049bb133111eb;return value^(value>>31);
}
inline uint32_t weather(uint64_t seed,uint32_t date,uint32_t city,int temperature,int precipitation) {
    if(!effectCity(city))return 0;
    uint32_t chance=static_cast<uint32_t>(std::clamp(precipitation/5,50,650));
    if(temperature<=20) {
        uint32_t block=date/3;if(random(seed,block,city,2)%1000>=chance)return 0;
        return (random(seed,block,city,3)%3==0?2u:1u)<<2;
    }
    if(random(seed,date,city,1)%1000>=chance)return 0;
    return random(seed,date,city,4)%4==0?2u:1u;
}
inline int movement(uint32_t weatherValue,uint32_t flags,const Values& values) {
    int rain=int(weatherValue&3),snow=int((weatherValue>>2)&3),value=100;
    if((flags&RainMovement)&&rain)value-=rain==1?values.rainReduction:values.heavyRainReduction;
    if((flags&SnowMovement)&&snow)value-=snow==1?values.snowReduction:values.deepSnowReduction;
    return std::max(1,value);
}
inline uint32_t serial(uint32_t packed) {
    return (packed&65535)*360+(((packed>>16)&255)-1)*30+(packed>>24)-1;
}
inline bool validDate(uint32_t value) {
    auto y=value&65535,m=(value>>16)&255,d=value>>24;
    return y>=101&&y<=351&&m>=1&&m<=12&&d>=1&&d<=30;
}
inline bool water(uint32_t terrain){return terrain==13||terrain==14||terrain==22||terrain==23||terrain==24;}
inline bool forest(uint32_t terrain){return terrain>=5&&terrain<=7;}
inline bool valid(const Values& v) {
    auto range=[](int n,int max){return n>=0&&n<=max;};
    return range(v.rainReduction,99)&&range(v.heavyRainReduction,99)&&range(v.snowReduction,99)&&range(v.deepSnowReduction,99)
        &&range(v.weatherMoraleLoss,130)&&range(v.floodChance,1000)&&range(v.floodTroopsPercent,99)&&range(v.floodMoraleLoss,130)&&range(v.floodFoodPercent,100)
        &&range(v.wildfireChance,1000)&&range(v.wildfireTroopsPercent,99)&&range(v.wildfireMoraleLoss,130)&&range(v.wildfireFoodPercent,100)
        &&range(v.droughtChance,1000)&&range(v.droughtFoodPercent,100)
        &&range(v.blizzardChance,1000)&&range(v.blizzardTroopsPercent,99)&&range(v.blizzardMoraleLoss,130)&&range(v.blizzardFoodPercent,100)
        &&range(v.heatwaveChance,1000)&&range(v.heatwaveTroopsPercent,99)&&range(v.heatwaveMoraleLoss,130)&&range(v.heatwaveFoodPercent,100);
}
inline uint32_t disasters(uint64_t seed,uint32_t day,uint32_t city,uint32_t flags,const Values& v,
    uint32_t w,int t,int p,bool river,bool trees) {
    if(!effectCity(city))return 0;
    uint32_t result=0;
    if((flags&Flood)&&river&&(w&3)==2&&p>=800&&random(seed,day,city,10)%1000<uint32_t(v.floodChance))result|=Flood;
    if((flags&Wildfire)&&trees&&t>=180&&p<600&&!w&&random(seed,day,city,11)%1000<uint32_t(v.wildfireChance))result|=Wildfire;
    if((flags&Drought)&&t>=250&&p<400&&!w&&random(seed,day,city,12)%1000<uint32_t(v.droughtChance))result|=Drought;
    if((flags&Blizzard)&&w==8&&random(seed,day,city,13)%1000<uint32_t(v.blizzardChance))result|=Blizzard;
    if((flags&Heatwave)&&t>=300&&!w&&random(seed,day,city,14)%1000<uint32_t(v.heatwaveChance))result|=Heatwave;
    return result;
}
inline bool valid(const Message& value) {
    if(value.flags&~All||!valid(value.values)||!value.seed||value.reserved)return false;
    for(size_t source=0;source<ClimateSourceCount;source++) {
        auto row=value.climate+source*ClimateRowBytes;uint32_t tile;std::memcpy(&tile,row,4);
        if(tile>=TileCount)return false;
        for(size_t month=0;month<12;month++) {
            double t,p;std::memcpy(&t,row+4+month*16,8);std::memcpy(&p,row+12+month*16,8);
            if(!std::isfinite(t)||!std::isfinite(p)||t< -60||t>60||p<0||p>3000)return false;
        }
    }
    return true;
}
}
