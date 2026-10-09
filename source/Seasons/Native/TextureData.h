#pragma once
#include <d3d11.h>
#include <vector>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <stdexcept>

namespace seasons {
enum class TextureRole:uint32_t { TerrainDiffuse=1,TerrainNormal=2,TerrainRough=3,TreeDiffuse=4,Climate=5,TreeCoverage=6 };
struct TexturePart {UINT layer,mip,width,height;size_t offset;UINT rowPitch,length;};
struct TextureLayout {UINT width,height,mips,layers;DXGI_FORMAT format;std::vector<TexturePart> parts;};

// The caller owns immutable, hash-verified bytes throughout validation/upload.
// Only our generated 2D DX10 formats are supported; no game pointer is accepted.
inline TextureLayout ParseTexture(const std::vector<unsigned char>& data,TextureRole role){
    auto require=[](bool ok,const char* message){if(!ok)throw std::runtime_error(message);};
    const auto roleValue=static_cast<uint32_t>(role);require(roleValue>=1&&roleValue<=6,"Unknown season texture role");
    require(data.size()>=148&&data.size()<=128*1024*1024,"Season texture file size rejected");
    auto word=[&](size_t index){UINT value;memcpy(&value,data.data()+index*4,4);return value;};
    const bool climate=role==TextureRole::Climate,coverage=role==TextureRole::TreeCoverage;
    require(word(0)==0x20534444&&word(1)==124&&word(19)==32&&word(20)==4&&word(21)==0x30315844,"Season DDS header rejected");
    require(word(2)==(climate?0x100Fu:0x2100Fu)&&word(6)==0,"Season DDS flags/depth rejected");
    for(size_t i=8;i<=18;++i)require(word(i)==0,"Season DDS reserved header rejected");
    for(size_t i=22;i<=26;++i)require(word(i)==0,"Season DDS legacy pixel format rejected");
    for(size_t i=28;i<=31;++i)require(word(i)==0,"Season DDS volume/cube/reserved caps rejected");
    require(word(27)==(climate?0x1008u:0x401008u)&&word(33)==3&&word(34)==0&&word(36)==0,"Season DDS resource flags rejected");
    TextureLayout result{};
    result.format=(climate||coverage)?DXGI_FORMAT_R32G32_FLOAT:
        (role==TextureRole::TerrainDiffuse||role==TextureRole::TreeDiffuse)?DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:DXGI_FORMAT_B8G8R8A8_UNORM;
    require(word(32)==static_cast<UINT>(result.format),"Season DDS format differs from role");
    result.width=word(4);result.height=word(3);result.layers=climate?12:1;
    const UINT maxDimension=climate?1024:2048;
    require(result.width>=1&&result.height>=1&&result.width<=maxDimension&&result.height<=maxDimension&&
        !(result.width&(result.width-1))&&!(result.height&(result.height-1)),"Season DDS dimensions rejected");
    result.mips=1;if(!climate)for(UINT dimension=std::max(result.width,result.height);dimension>1;dimension>>=1)++result.mips;
    require(word(7)==result.mips&&word(35)==result.layers,"Season DDS mip/layer count rejected");
    const UINT pixelBytes=(climate||coverage)?8:4;require(word(5)==result.width*pixelBytes,"Season DDS row pitch rejected");
    size_t offset=148;
    for(UINT layer=0;layer<result.layers;++layer)for(UINT mip=0;mip<result.mips;++mip){
        const UINT width=std::max(1u,result.width>>mip),height=std::max(1u,result.height>>mip),pitch=width*pixelBytes,length=pitch*height;
        require(length<=data.size()-offset,"Truncated season DDS payload");
        result.parts.push_back({layer,mip,width,height,offset,pitch,length});offset+=length;
    }
    require(offset==data.size(),"Unexpected trailing season DDS payload");
    if(climate){
        for(size_t at=148;at<data.size();at+=8){
            float temperature,rain;memcpy(&temperature,data.data()+at,4);memcpy(&rain,data.data()+at+4,4);
            require(std::isfinite(temperature)&&temperature>=-60&&temperature<=60&&std::isfinite(rain)&&rain>=0&&rain<=3000,"Invalid season climate texel");
        }
    }else if(coverage){
        for(size_t at=148;at<data.size();at+=8){
            float leaf,branch;memcpy(&leaf,data.data()+at,4);memcpy(&branch,data.data()+at+4,4);
            require(std::isfinite(leaf)&&std::isfinite(branch)&&leaf>=0&&leaf<=1&&branch>=0&&branch<=1&&leaf+branch<=1+1e-6f,
                "Invalid leaf/branch coverage texel");
        }
    }else if(role!=TextureRole::TreeDiffuse)
        for(size_t at=151;at<data.size();at+=4)require(data[at]==255,"Non-opaque terrain texel");
    return result;
}

inline void ValidateCoverageSource(const std::vector<unsigned char>& coverage,const std::vector<unsigned char>& diffuse){
    const auto mask=ParseTexture(coverage,TextureRole::TreeCoverage),source=ParseTexture(diffuse,TextureRole::TreeDiffuse);
    if(mask.width!=source.width||mask.height!=source.height||mask.mips!=source.mips||mask.layers!=source.layers)
        throw std::runtime_error("Coverage and diffuse mip layouts differ");
    for(size_t index=0;index<mask.parts.size();++index){
        const auto& m=mask.parts[index];const auto& s=source.parts[index];
        for(size_t pixel=0;pixel<size_t(m.width)*m.height;++pixel){
            float leaf,branch;memcpy(&leaf,coverage.data()+m.offset+pixel*8,4);memcpy(&branch,coverage.data()+m.offset+pixel*8+4,4);
            const float alpha=diffuse[s.offset+pixel*4+3]/255.0f;
            if((alpha==0&&(leaf!=0||branch!=0))||std::abs(leaf+branch-alpha)>1e-6f)
                throw std::runtime_error("Coverage contributions differ from source alpha");
        }
    }
}
}
