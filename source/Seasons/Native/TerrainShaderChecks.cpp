// Owned texture arrays and WARP compute fixture only. No game/process/Hook access.
#define NOMINMAX
#include "TextureData.h"
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <array>
#include <cstdio>
using Microsoft::WRL::ComPtr;
using namespace seasons;
static void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
static void hr(HRESULT value){require(SUCCEEDED(value),"WARP terrain fixture API failed");}
static std::vector<unsigned char> read(const std::filesystem::path& path){
    std::ifstream file(path,std::ios::binary|std::ios::ate);require(bool(file),"Terrain input missing");
    const auto size=file.tellg();require(size>=16&&size<=128*1024*1024,"Terrain input size rejected");
    std::vector<unsigned char> bytes(static_cast<size_t>(size));file.seekg(0);file.read(reinterpret_cast<char*>(bytes.data()),size);
    require(bool(file)&&file.peek()==std::char_traits<char>::eof(),"Terrain input changed while reading");return bytes;
}
static std::string hash(const std::vector<unsigned char>& data){
    struct Provider {BCRYPT_ALG_HANDLE handle=nullptr;~Provider(){if(handle)BCryptCloseAlgorithmProvider(handle,0);}} provider;
    require(BCryptOpenAlgorithmProvider(&provider.handle,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0,"SHA256 provider unavailable");
    unsigned char digest[32];require(BCryptHash(provider.handle,nullptr,0,const_cast<PUCHAR>(data.data()),static_cast<ULONG>(data.size()),digest,32)>=0,"Terrain SHA256 failed");
    constexpr char hex[]="0123456789abcdef";std::string result;for(unsigned char v:digest){result+=hex[v>>4];result+=hex[v&15];}return result;
}
struct TerrainCase {
    float u,v,lod,upFacing,ground[4],groundCoverage,snowEligibility,snow,padding,
        groundStrength,snowStrength,slopeMin,slopeMax,original[3],roughness,normal[3],reserved,expected[12];
};
static_assert(sizeof(TerrainCase)==144);
static bool unit(float value){return std::isfinite(value)&&value>=0&&value<=1;}
int wmain(int argc,wchar_t** argv){
    bool deviceCreated=false;
    try{
        require(argc==4,"Usage: TerrainShaderChecks.exe SeasonTerrainCheck.hlsl terrain-textures.tsv cases.bin");
        std::ifstream list{std::filesystem::path(argv[2])};std::string line;
        require(bool(std::getline(list,line))&&line=="SeasonTerrainTextures\t1","Terrain texture list rejected");
        std::array<std::vector<unsigned char>,15> bytes;std::array<TextureLayout,15> layouts;
        for(size_t index=0;index<15;++index){
            require(bool(std::getline(list,line))&&line.size()<4096,"Terrain texture count rejected");
            const auto first=line.find('\t'),second=first==std::string::npos?first:line.find('\t',first+1);
            require(first==1&&second==66&&line.find('\t',second+1)==std::string::npos&&line[0]=='1'+int(index/5),"Terrain texture role/hash/path rejected");
            std::u8string path;for(unsigned char v:line.substr(second+1))path+=static_cast<char8_t>(v);
            bytes[index]=read(std::filesystem::path(path));require(hash(bytes[index])==line.substr(2,64),"Terrain texture SHA256 mismatch");
            layouts[index]=ParseTexture(bytes[index],static_cast<TextureRole>(1+index/5));
            const auto& firstLayout=layouts[0];const auto& layout=layouts[index];
            require(layout.width==firstLayout.width&&layout.height==firstLayout.height&&layout.mips==firstLayout.mips&&layout.layers==1,"Terrain texture layouts differ");
        }
        require(!std::getline(list,line),"Trailing terrain texture list entry rejected");
        const auto casesBytes=read(argv[3]);UINT header[4];memcpy(header,casesBytes.data(),16);
        require(header[0]==0x53475443&&header[1]==1&&header[2]>0&&header[2]<=16384&&header[3]==0&&casesBytes.size()==16+size_t(header[2])*sizeof(TerrainCase),"Terrain cases header rejected");
        const UINT count=header[2];std::vector<TerrainCase> cases(count);memcpy(cases.data(),casesBytes.data()+16,count*sizeof(TerrainCase));
        for(const auto& test:cases){
            require(std::isfinite(test.u)&&std::isfinite(test.v)&&test.u>=-2&&test.u<=2&&test.v>=-2&&test.v<=2&&
                std::isfinite(test.lod)&&test.lod>=0&&test.lod<=float(layouts[0].mips-1)&&test.padding==0&&test.reserved==0,"Terrain sampling parameters rejected");
            require(unit(test.upFacing)&&unit(test.groundCoverage)&&unit(test.snowEligibility)&&unit(test.snow)&&unit(test.groundStrength)&&unit(test.snowStrength)&&
                unit(test.slopeMin)&&unit(test.slopeMax)&&test.slopeMax-test.slopeMin>=0.0001f,"Terrain artwork parameters rejected");
            float total=0;for(float value:test.ground){require(unit(value),"Terrain contribution rejected");total+=value;}
            require(total<=1+1e-6f,"Terrain contribution sum rejected");
            for(float value:test.original)require(unit(value),"Terrain original color rejected");
            require(unit(test.roughness),"Terrain original roughness rejected");float length=0;
            for(float value:test.normal){require(std::isfinite(value),"Terrain original normal rejected");length+=value*value;}
            require(std::isfinite(length)&&std::abs(length-1)<=0.0001f,"Terrain original normal rejected");
            for(size_t channel=0;channel<12;++channel)require(std::isfinite(test.expected[channel])&&test.expected[channel]>=((channel>=4&&channel<7)?-1.0f:0.0f)&&test.expected[channel]<=1.000001f,"Terrain expectation rejected");
        }
        ComPtr<ID3DBlob> code,errors;const HRESULT compilation=D3DCompileFromFile(argv[1],nullptr,D3D_COMPILE_STANDARD_FILE_INCLUDE,"Check","cs_5_0",
            D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
        if(errors)fprintf(stderr,"%.*s",int(errors->GetBufferSize()),static_cast<const char*>(errors->GetBufferPointer()));hr(compilation);
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));deviceCreated=true;
        std::array<ComPtr<ID3D11ShaderResourceView>,4> views;
        for(size_t kind=0;kind<3;++kind){
            std::vector<D3D11_SUBRESOURCE_DATA> initial;
            for(size_t layer=0;layer<5;++layer)for(const auto& part:layouts[kind*5+layer].parts)initial.push_back({bytes[kind*5+layer].data()+part.offset,part.rowPitch,part.length});
            const auto& layout=layouts[kind*5];D3D11_TEXTURE2D_DESC desc{layout.width,layout.height,layout.mips,5,layout.format,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
            ComPtr<ID3D11Texture2D> texture;hr(device->CreateTexture2D(&desc,initial.data(),&texture));hr(device->CreateShaderResourceView(texture.Get(),nullptr,&views[kind]));
        }
        D3D11_BUFFER_DESC desc{count*UINT(sizeof(TerrainCase)),D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,UINT(sizeof(TerrainCase))};
        D3D11_SUBRESOURCE_DATA initial{cases.data(),0,0};ComPtr<ID3D11Buffer> input,output,staging,constants;
        hr(device->CreateBuffer(&desc,&initial,&input));hr(device->CreateShaderResourceView(input.Get(),nullptr,&views[3]));
        desc={count*48,D3D11_USAGE_DEFAULT,D3D11_BIND_UNORDERED_ACCESS,0,D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,48};hr(device->CreateBuffer(&desc,nullptr,&output));
        ComPtr<ID3D11UnorderedAccessView> result;hr(device->CreateUnorderedAccessView(output.Get(),nullptr,&result));
        desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;desc.StructureByteStride=0;hr(device->CreateBuffer(&desc,nullptr,&staging));
        UINT settings[4]{count,0,0,0};desc={16,D3D11_USAGE_IMMUTABLE,D3D11_BIND_CONSTANT_BUFFER,0,0,0};initial={settings,0,0};hr(device->CreateBuffer(&desc,&initial,&constants));
        D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;
        sd.MaxAnisotropy=1;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MaxLOD=D3D11_FLOAT32_MAX;ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));
        ComPtr<ID3D11ComputeShader> shader;hr(device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&shader));
        context->CSSetShader(shader.Get(),nullptr,0);ID3D11ShaderResourceView* resources[]{views[0].Get(),views[1].Get(),views[2].Get(),views[3].Get()};context->CSSetShaderResources(0,4,resources);
        ID3D11Buffer* cb=constants.Get();context->CSSetConstantBuffers(0,1,&cb);ID3D11SamplerState* ss=sampler.Get();context->CSSetSamplers(0,1,&ss);
        ID3D11UnorderedAccessView* uav=result.Get();context->CSSetUnorderedAccessViews(0,1,&uav,nullptr);context->Dispatch((count+63)/64,1,1);context->ClearState();context->CopyResource(staging.Get(),output.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        const auto actual=static_cast<const float*>(mapped.pData);float maxError[3]{};UINT failures=0,firstFailure=0,passthroughCases=0;
        for(UINT index=0;index<count;++index)for(UINT channel=0;channel<12;++channel){
            const float value=actual[index*12+channel],error=std::abs(value-cases[index].expected[channel]);
            const UINT group=channel<3?0:(channel>=4&&channel<=6)?1:2;
            const float tolerance=0.000002f;
            maxError[group]=std::max(maxError[group],error);if(!std::isfinite(value)||error>tolerance){if(!failures)firstFailure=index;++failures;}
        }
        bool passthroughEqual=true;
        for(UINT index=0;index<count;++index){
            const auto& test=cases[index];const float total=test.ground[0]+test.ground[1]+test.ground[2]+test.ground[3];
            const bool groundZero=test.groundCoverage==0||test.groundStrength==0||total==0;
            const bool snowZero=test.snow==0||test.snowEligibility==0||test.snowStrength==0||test.upFacing<=test.slopeMin;
            if(groundZero&&snowZero){
                ++passthroughCases;passthroughEqual=passthroughEqual&&!memcmp(actual+index*12,test.original,3*sizeof(float))&&
                    !memcmp(actual+index*12+3,&test.roughness,sizeof(float))&&!memcmp(actual+index*12+4,test.normal,3*sizeof(float));
            }
        }
        context->Unmap(staging.Get(),0);context->Flush();
        printf("{\"backend\":\"D3D11 WARP\",\"gameAttached\":false,\"cases\":%u,\"channelsCompared\":%u,\"mips\":%u,\"materialLayers\":5,\"maxColorError\":%.9g,\"maxNormalError\":%.9g,\"maxOtherError\":%.9g,\"passthroughCases\":%u,\"passthroughBitExact\":%s,\"failures\":%u,\"firstFailure\":%u}\n",count,count*12,layouts[0].mips,maxError[0],maxError[1],maxError[2],passthroughCases,passthroughEqual?"true":"false",failures,firstFailure);
        require(passthroughCases>0&&passthroughEqual,"Terrain passthrough differs from original input bits");
        require(failures==0,"Terrain shader differs from independent material reference");return 0;
    }catch(const std::exception& error){fprintf(stderr,"FAIL: %s\ngameAttached=false; deviceCreated=%s\n",error.what(),deviceCreated?"true":"false");return 1;}
}
