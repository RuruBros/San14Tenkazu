// Paired synthetic mip textures and sampling cases on our owned WARP device.
#define NOMINMAX
#include "TextureData.h"
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <filesystem>
#include <fstream>
#include <cstdio>
using Microsoft::WRL::ComPtr;
using namespace seasons;
static void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
static void hr(HRESULT value){require(SUCCEEDED(value),"WARP mask fixture API failed");}
static std::vector<unsigned char> read(const std::filesystem::path& path){
    std::ifstream file(path,std::ios::binary|std::ios::ate);require(bool(file),"Mask fixture missing");
    const auto size=file.tellg();require(size>=16&&size<=128*1024*1024,"Mask fixture size rejected");
    std::vector<unsigned char> data(static_cast<size_t>(size));file.seekg(0);file.read(reinterpret_cast<char*>(data.data()),size);
    require(bool(file)&&file.peek()==std::char_traits<char>::eof(),"Mask fixture changed while reading");return data;
}
struct SampleCase {float u,v,lod,retention;UINT policy,padding[3];float expected[4];};
static_assert(sizeof(SampleCase)==48);
int wmain(int argc,wchar_t** argv){
    bool deviceCreated=false;
    try{
        require(argc==3,"Usage: TreeMaskChecks.exe SeasonTreeMipCheck.hlsl fixture-directory");
        const std::filesystem::path directory{argv[2]};
        const auto sourceBytes=read(directory/L"fixture-source.dds"),maskBytes=read(directory/L"fixture-coverage.dds"),casesBytes=read(directory/L"sampling.bin");
        ValidateCoverageSource(maskBytes,sourceBytes);const auto sourceLayout=ParseTexture(sourceBytes,TextureRole::TreeDiffuse),maskLayout=ParseTexture(maskBytes,TextureRole::TreeCoverage);
        UINT header[4];memcpy(header,casesBytes.data(),16);
        require(header[0]==0x534D4950&&header[1]==1&&header[2]>0&&header[2]<=4096&&header[3]==0&&casesBytes.size()==16+size_t(header[2])*sizeof(SampleCase),"Sampling fixture header rejected");
        const UINT count=header[2];std::vector<SampleCase> cases(count);memcpy(cases.data(),casesBytes.data()+16,count*sizeof(SampleCase));
        for(const auto& test:cases){
            require(std::isfinite(test.u)&&std::isfinite(test.v)&&test.u>=-1&&test.u<=2&&test.v>=-1&&test.v<=2&&
                std::isfinite(test.lod)&&test.lod>=0&&test.lod<=float(sourceLayout.mips-1)&&std::isfinite(test.retention)&&test.retention>=0&&test.retention<=1&&
                (test.policy==1||test.policy==2)&&test.padding[0]==0&&test.padding[1]==0&&test.padding[2]==0,"Sampling fixture parameters rejected");
            for(float value:test.expected)require(std::isfinite(value)&&value>=0&&value<=1,"Sampling fixture expectation rejected");
        }
        ComPtr<ID3DBlob> code,errors;
        const HRESULT compilation=D3DCompileFromFile(argv[1],nullptr,D3D_COMPILE_STANDARD_FILE_INCLUDE,"Check","cs_5_0",
            D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
        if(errors)fprintf(stderr,"%.*s",int(errors->GetBufferSize()),static_cast<const char*>(errors->GetBufferPointer()));hr(compilation);
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));deviceCreated=true;
        ComPtr<ID3D11ShaderResourceView> sourceView,maskView,casesView;
        auto texture=[&](const auto& bytes,const TextureLayout& layout,ComPtr<ID3D11ShaderResourceView>& view){
            std::vector<D3D11_SUBRESOURCE_DATA> initial;for(const auto& part:layout.parts)initial.push_back({bytes.data()+part.offset,part.rowPitch,part.length});
            D3D11_TEXTURE2D_DESC desc{layout.width,layout.height,layout.mips,layout.layers,layout.format,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
            ComPtr<ID3D11Texture2D> object;hr(device->CreateTexture2D(&desc,initial.data(),&object));hr(device->CreateShaderResourceView(object.Get(),nullptr,&view));
        };
        texture(sourceBytes,sourceLayout,sourceView);texture(maskBytes,maskLayout,maskView);
        D3D11_BUFFER_DESC bd{count*UINT(sizeof(SampleCase)),D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,UINT(sizeof(SampleCase))};
        D3D11_SUBRESOURCE_DATA initial{cases.data(),0,0};ComPtr<ID3D11Buffer> casesBuffer,output,staging,constants;
        hr(device->CreateBuffer(&bd,&initial,&casesBuffer));hr(device->CreateShaderResourceView(casesBuffer.Get(),nullptr,&casesView));
        bd={count*16,D3D11_USAGE_DEFAULT,D3D11_BIND_UNORDERED_ACCESS,0,D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,16};hr(device->CreateBuffer(&bd,nullptr,&output));
        ComPtr<ID3D11UnorderedAccessView> resultView;hr(device->CreateUnorderedAccessView(output.Get(),nullptr,&resultView));
        bd.Usage=D3D11_USAGE_STAGING;bd.BindFlags=0;bd.CPUAccessFlags=D3D11_CPU_ACCESS_READ;bd.MiscFlags=0;bd.StructureByteStride=0;hr(device->CreateBuffer(&bd,nullptr,&staging));
        UINT settings[4]{count,0,0,0};bd={16,D3D11_USAGE_IMMUTABLE,D3D11_BIND_CONSTANT_BUFFER,0,0,0};initial={settings,0,0};hr(device->CreateBuffer(&bd,&initial,&constants));
        D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxAnisotropy=1;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MaxLOD=D3D11_FLOAT32_MAX;ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));
        ComPtr<ID3D11ComputeShader> shader;hr(device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&shader));
        context->CSSetShader(shader.Get(),nullptr,0);ID3D11ShaderResourceView* views[]{sourceView.Get(),maskView.Get(),casesView.Get()};context->CSSetShaderResources(0,3,views);
        ID3D11Buffer* cb=constants.Get();context->CSSetConstantBuffers(0,1,&cb);ID3D11SamplerState* ss=sampler.Get();context->CSSetSamplers(0,1,&ss);
        ID3D11UnorderedAccessView* uav=resultView.Get();context->CSSetUnorderedAccessViews(0,1,&uav,nullptr);context->Dispatch((count+63)/64,1,1);context->ClearState();context->CopyResource(staging.Get(),output.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        float maxError=0;bool equal=true;const auto actual=static_cast<const float*>(mapped.pData);
        for(UINT index=0;index<count;++index)for(UINT channel=0;channel<4;++channel){
            const float value=actual[index*4+channel],error=std::abs(value-cases[index].expected[channel]);
            equal=equal&&std::isfinite(value)&&error<=0.000002f;maxError=std::max(maxError,error);
        }
        context->Unmap(staging.Get(),0);require(equal,"WARP sampled leaf/branch coverage differs from analytic reference");context->Flush();
        printf("{\"backend\":\"D3D11 WARP\",\"gameAttached\":false,\"samples\":%u,\"channelsCompared\":%u,\"mips\":%u,\"maxError\":%.9g,\"failures\":0}\n",count,count*4,maskLayout.mips,maxError);return 0;
    }catch(const std::exception& error){fprintf(stderr,"FAIL: %s\ngameAttached=false; deviceCreated=%s\n",error.what(),deviceCreated?"true":"false");return 1;}
}
