// Own WARP device/texture/compute shader. Never opens SAN14 or installs a callback.
#define NOMINMAX
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <filesystem>
#include <fstream>
#include <vector>
#include <stdexcept>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <algorithm>
#include "TextureData.h"
using Microsoft::WRL::ComPtr;
static void require(bool ok,const char* reason){if(!ok)throw std::runtime_error(reason);}
static void hr(HRESULT v){if(FAILED(v)){char message[64];sprintf_s(message,"D3D HRESULT 0x%08X",unsigned(v));throw std::runtime_error(message);}}
static std::vector<unsigned char> read(const wchar_t* path)
{
    std::ifstream file(std::filesystem::path(path),std::ios::binary|std::ios::ate);
    require(bool(file),"Input file cannot be read");auto n=file.tellg();require(n>0 && n<128*1024*1024,"Input size rejected");
    std::vector<unsigned char> data(static_cast<size_t>(n));file.seekg(0);file.read(reinterpret_cast<char*>(data.data()),n);
    require(bool(file),"Truncated input file");return data;
}
static UINT word(const std::vector<unsigned char>& bytes,size_t offset)
{
    require(offset+4<=bytes.size(),"Truncated input header");UINT value;memcpy(&value,bytes.data()+offset,4);return value;
}
struct Input { float x,z;UINT month,next;float fraction,pad[3],weights[4]; };
struct Output { float values[12]; };
static_assert(sizeof(Input)==48 && sizeof(Output)==48);
static double climateBound(const std::vector<unsigned char>& dds,UINT w,UINT h,const Input& input,UINT channel)
{
    // D3D11 guarantees at least 8 fractional subtexel bits. The saved WARP readback
    // agrees with 8-bit floor weights. Compare to the full-precision CPU reference
    // using a local gradient bound, plus float-coordinate/arithmetic allowance.
    double px=std::clamp(double(input.x),0.0,204800.0)/204800*w-0.5;
    double pz=std::clamp(double(input.z),0.0,204800.0)/204800*h-0.5;
    int ix=int(std::floor(px)),iz=int(std::floor(pz));
    auto layer=[&](UINT month)
    {
        auto value=[&](int x,int z)
        {
            size_t at=((size_t(month)*h+std::clamp(z,0,int(h)-1))*w+std::clamp(x,0,int(w)-1))*2+channel;
            float result;memcpy(&result,dds.data()+148+at*4,4);return double(result);
        };
        double a=value(ix,iz),b=value(ix+1,iz),c=value(ix,iz+1),d=value(ix+1,iz+1);
        double gx=std::max(std::abs(a-b),std::abs(c-d)),gz=std::max(std::abs(a-c),std::abs(b-d));
        return (gx+gz)*(1.0/256+1.0/4096);
    };
    return layer(input.month)*(1-input.fraction)+layer(input.next)*input.fraction+(channel==0?0.00001:0.0001);
}
int wmain(int argc,wchar_t** argv)
{
    try
    {
        require(argc==4,"Usage: ShaderChecks.exe climate.dds vectors.bin check.hlsl");
        auto dds=read(argv[1]),vectors=read(argv[2]);
        const auto climateLayout=seasons::ParseTexture(dds,seasons::TextureRole::Climate);
        UINT w=climateLayout.width,h=climateLayout.height;
        require(word(vectors,0)==0x53434154 && word(vectors,4)==1,"Unexpected vector contract");
        UINT count=word(vectors,8);require(count>0 && count<=65536 && vectors.size()==16+size_t(count)*84,"Vector size/count mismatch");
        std::vector<Input> inputs(count);std::vector<float> expected(size_t(count)*9);
        for(UINT i=0;i<count;i++)
        {
            memcpy(&inputs[i],vectors.data()+16+size_t(i)*84,48);memcpy(expected.data()+size_t(i)*9,vectors.data()+64+size_t(i)*84,36);
            require(std::isfinite(inputs[i].x) && std::isfinite(inputs[i].z) && std::isfinite(inputs[i].fraction) &&
                inputs[i].month<12 && inputs[i].next==(inputs[i].month+1)%12 && inputs[i].fraction>=0 && inputs[i].fraction<=1,"Malformed shader input");
            float sum=0;for(float v:inputs[i].weights){require(std::isfinite(v) && v>=0 && v<=1,"Invalid season weight");sum+=v;}
            require(std::abs(sum-1)<0.00001f,"Non-normalized season weights");
        }
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL level;
        hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context));
        require(level>=D3D_FEATURE_LEVEL_11_0,"Shader model 5 WARP required");
        D3D11_TEXTURE2D_DESC td{w,h,1,12,DXGI_FORMAT_R32G32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
        D3D11_SUBRESOURCE_DATA parts[12]{};
        for(UINT month=0;month<12;month++){parts[month].pSysMem=dds.data()+148+size_t(month)*w*h*8;parts[month].SysMemPitch=w*8;}
        ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11ShaderResourceView> climateView;
        hr(device->CreateTexture2D(&td,parts,&texture));hr(device->CreateShaderResourceView(texture.Get(),nullptr,&climateView));
        D3D11_BUFFER_DESC bd{count*UINT(sizeof(Input)),D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,UINT(sizeof(Input))};
        D3D11_SUBRESOURCE_DATA initial{inputs.data(),0,0};ComPtr<ID3D11Buffer> input,output,staging,constants;
        hr(device->CreateBuffer(&bd,&initial,&input));ComPtr<ID3D11ShaderResourceView> inputView;hr(device->CreateShaderResourceView(input.Get(),nullptr,&inputView));
        bd.Usage=D3D11_USAGE_DEFAULT;bd.BindFlags=D3D11_BIND_UNORDERED_ACCESS;bd.ByteWidth=count*UINT(sizeof(Output));bd.StructureByteStride=UINT(sizeof(Output));
        hr(device->CreateBuffer(&bd,nullptr,&output));ComPtr<ID3D11UnorderedAccessView> outputView;hr(device->CreateUnorderedAccessView(output.Get(),nullptr,&outputView));
        bd.Usage=D3D11_USAGE_STAGING;bd.BindFlags=0;bd.CPUAccessFlags=D3D11_CPU_ACCESS_READ;bd.MiscFlags=0;bd.StructureByteStride=0;hr(device->CreateBuffer(&bd,nullptr,&staging));
        struct Constants { float extent[4];UINT count,padding[3]; } values{{0,0,1.0f/204800,1.0f/204800},count,{}};
        bd={sizeof(Constants),D3D11_USAGE_IMMUTABLE,D3D11_BIND_CONSTANT_BUFFER,0,0,0};initial.pSysMem=&values;hr(device->CreateBuffer(&bd,&initial,&constants));
        D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD=0;ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));
        ComPtr<ID3DBlob> code,errors;HRESULT compiled=D3DCompileFromFile(argv[3],nullptr,D3D_COMPILE_STANDARD_FILE_INCLUDE,"main","cs_5_0",
            D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
        if(errors)fprintf(stderr,"%.*s",int(errors->GetBufferSize()),static_cast<const char*>(errors->GetBufferPointer()));hr(compiled);
        ComPtr<ID3D11ComputeShader> shader;hr(device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&shader));
        ID3D11Buffer* cb=constants.Get();ID3D11ShaderResourceView* views[]{climateView.Get(),inputView.Get()};ID3D11UnorderedAccessView* uav=outputView.Get();ID3D11SamplerState* smp=sampler.Get();
        context->CSSetShader(shader.Get(),nullptr,0);context->CSSetConstantBuffers(0,1,&cb);context->CSSetShaderResources(0,2,views);
        context->CSSetSamplers(0,1,&smp);context->CSSetUnorderedAccessViews(0,1,&uav,nullptr);context->Dispatch((count+63)/64,1,1);
        context->ClearState();context->CopyResource(staging.Get(),output.Get());
        D3D11_MAPPED_SUBRESOURCE mapped;hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        double maxErrors[9]{},maxBounds[9]{};UINT failed=0;auto results=static_cast<const Output*>(mapped.pData);
        for(UINT i=0;i<count;i++)
        {
            double t=climateBound(dds,w,h,inputs[i],0),p=climateBound(dds,w,h,inputs[i],1),winter=inputs[i].weights[3];
            constexpr double epsilon=0.0000003;
            // Smoothstep's maximum slope is 1.5/range; each product factor lies in [0,1].
            double bounds[]{t,p,inputs[i].weights[0]*1.5/14*t+epsilon,epsilon,epsilon,
                winter*1.5/10*t+epsilon,winter*(1.5/8*t+0.7/40*p)+epsilon,
                winter*0.35*(1.5/8*t+p/40)+epsilon,inputs[i].weights[1]*0.25*1.5/10*t+epsilon};
            for(UINT channel=0;channel<9;channel++)
            {
                float actual=results[i].values[channel],want=expected[size_t(i)*9+channel];double error=std::abs(double(actual)-want);
                // Independent physical quality caps also reject an unexpectedly steep/broken field.
                double limit=std::min(bounds[channel],channel==0?0.005:channel==1?0.05:0.001);
                if(!std::isfinite(actual) || !std::isfinite(want) || !std::isfinite(limit) || error>limit)
                {if(failed<8)fprintf(stderr,"Mismatch vector %u channel %u actual %.9g expected %.9g bound %.9g\n",i,channel,actual,want,limit);failed++;}
                maxErrors[channel]=std::max(maxErrors[channel],error);maxBounds[channel]=std::max(maxBounds[channel],limit);
            }
        }
        std::ofstream raw(std::filesystem::path(argv[2]).concat(L".gpu.bin"),std::ios::binary);
        raw.write(static_cast<const char*>(mapped.pData),std::streamsize(count)*sizeof(Output));require(bool(raw),"Readback output failed");
        context->Unmap(staging.Get(),0);
        printf("{\"backend\":\"D3D11 WARP\",\"gameAttached\":false,\"vectors\":%u,\"channelsPerVector\":9,\"failures\":%u,\"maxErrors\":[",count,failed);
        for(int i=0;i<9;i++)printf("%s%.9g",i?",":"",maxErrors[i]);printf("],\"maxLocalBounds\":[");
        for(int i=0;i<9;i++)printf("%s%.9g",i?",":"",maxBounds[i]);printf("]}\n");
        require(failed==0,"CPU/GPU climate/season comparison failed");return 0;
    }
    catch(const std::exception& error){fprintf(stderr,"FAIL: %s\n",error.what());return 1;}
}
