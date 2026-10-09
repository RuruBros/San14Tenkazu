#pragma once
#include "SeasonShaderProfiles.h"
namespace seasons::profile {
inline bool copy(void* to,uint64_t from,size_t size)noexcept{
    if(!from||size>65536)return false;
    __try{std::memcpy(to,reinterpret_cast<const void*>(from),size);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
template<class T>inline T at(uint64_t address){T result{};copy(&result,address,sizeof(result));return result;}
inline bool name(uint64_t pointer,const char* expected){
    char actual[256]{};size_t n=std::strlen(expected)+1;
    return n<=sizeof(actual)&&copy(actual,pointer,n)&&std::memcmp(actual,expected,n)==0;
}
inline bool matches(uint64_t reflection,const Profile& expected){
    std::array<BYTE,0x138> header{};if(!copy(header.data(),reflection,header.size()))return false;
    auto vt=at<uint64_t>(reflection);if(!vt)return false;
    struct Getter {uint32_t slot,bytes;const char* sha;};
    constexpr Getter getters[]={
        {3,47,"3090034afc81e5e1d344a4decc405c4a4b18a54a446d692e1f8e66946137a32f"},
        {6,84,"af787f0e43c5c931c35e05682918e335de87d6aec343299d843fb95c7af72139"},
        {7,73,"68d410844553e04ca21d9e11cc6c2e2dc3772e54f468a4c421dcc15d5575557f"},
        {8,73,"8e92aa911980187e59757bca5fb3f93366e4dcd5ff63820540ecc01d150295f0"}};
    for(const auto& g:getters){std::array<BYTE,84> code{};if(!copy(code.data(),at<uint64_t>(vt+g.slot*8),g.bytes)||!materialSha(code.data(),g.bytes,g.sha))return false;}
    if(at<uint32_t>(reflection+0x10)!=expected.version||
       std::memcmp(header.data()+0x20,expected.counters.data(),36*4)!=0)return false;
    auto resources=at<uint64_t>(reflection+0xf8),inputs=at<uint64_t>(reflection+0x110),outputs=at<uint64_t>(reflection+0x128);
    for(uint32_t i=0;i<expected.counters[2];i++){
        auto address=resources+i*40;const auto& e=expected.resources[i];std::array<uint32_t,7> fields{};
        if(!copy(fields.data(),address+8,28)||!name(at<uint64_t>(address),e.name)||std::memcmp(fields.data(),&e.kind,28))return false;
    }
    for(uint32_t pass=0;pass<2;pass++)for(uint32_t i=0;i<expected.counters[3+pass];i++){
        auto address=(pass?outputs:inputs)+i*32;const auto& e=(pass?expected.outputs:expected.inputs)[i];std::array<BYTE,32> b{};
        if(!copy(b.data(),address,32)||!name(at<uint64_t>(address),e.name)||std::memcmp(b.data()+8,&e.index,16)||
           b[24]!=e.mask||b[25]!=e.rw||at<uint32_t>(address+28)!=e.stream)return false;
    }return true;
}
}
