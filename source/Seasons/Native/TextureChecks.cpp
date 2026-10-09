// Filesystem inputs and a device owned by this WARP process only. No game access.
#define NOMINMAX
#include "TextureData.h"
#include <wrl/client.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <cstdio>
using Microsoft::WRL::ComPtr;
using namespace seasons;
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static void hr(HRESULT value){require(SUCCEEDED(value),"WARP texture API failed");}
static std::vector<unsigned char> read(const std::filesystem::path& path){
    std::ifstream file(path,std::ios::binary|std::ios::ate);require(bool(file),"Texture input cannot be read");
    const auto length=file.tellg();require(length>=148&&length<=128*1024*1024,"Season texture file size rejected");
    std::vector<unsigned char> bytes(static_cast<size_t>(length));file.seekg(0);file.read(reinterpret_cast<char*>(bytes.data()),length);
    require(bool(file)&&file.peek()==std::char_traits<char>::eof(),"Texture changed/truncated while reading");return bytes;
}
static std::string sha256(const std::vector<unsigned char>& data){
    struct Provider {BCRYPT_ALG_HANDLE handle=nullptr;~Provider(){if(handle)BCryptCloseAlgorithmProvider(handle,0);}} provider;
    require(BCryptOpenAlgorithmProvider(&provider.handle,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0,"SHA256 provider unavailable");
    unsigned char digest[32];require(BCryptHash(provider.handle,nullptr,0,const_cast<PUCHAR>(data.data()),static_cast<ULONG>(data.size()),digest,32)>=0,"Texture SHA256 failed");
    constexpr char hex[]="0123456789abcdef";std::string result;
    for(unsigned char value:digest){result+=hex[value>>4];result+=hex[value&15];}return result;
}
int wmain(int argc,wchar_t** argv){
    bool deviceCreated=false;
    try{
        require(argc==2,"Usage: TextureChecks.exe verified-textures.tsv");
        std::ifstream list{std::filesystem::path(argv[1])};require(bool(list),"Texture list unavailable");std::string line;
        require(bool(std::getline(list,line))&&line=="SeasonTextureList\t1","Texture list contract rejected");
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL level;
        // Create lazily, only after the first input passes hash and DDS checks.
        size_t resources=0,parts=0,payload=0,peakResidentPayload=0;
        while(std::getline(list,line)){
            require(!line.empty()&&resources<64&&line.size()<4096,"Texture list entry rejected");
            const auto first=line.find('\t'),second=first==std::string::npos?first:line.find('\t',first+1);
            require(first==1&&second==66&&line.find('\t',second+1)==std::string::npos&&line[0]>='1'&&line[0]<='6',"Texture list role/hash/path rejected");
            const auto role=static_cast<TextureRole>(line[0]-'0');std::u8string utf8Path;
            for(unsigned char value:line.substr(second+1))utf8Path.push_back(static_cast<char8_t>(value));
            auto data=read(std::filesystem::path(utf8Path));
            require(sha256(data)==line.substr(first+1,64),"Season texture SHA256 mismatch");const auto layout=ParseTexture(data,role);
            if(!device){hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context));deviceCreated=true;}
            std::vector<D3D11_SUBRESOURCE_DATA> initial;
            for(const auto& part:layout.parts)initial.push_back({data.data()+part.offset,part.rowPitch,part.length});
            D3D11_TEXTURE2D_DESC desc{layout.width,layout.height,layout.mips,layout.layers,layout.format,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
            ComPtr<ID3D11Texture2D> texture,staging;ComPtr<ID3D11ShaderResourceView> view;
            hr(device->CreateTexture2D(&desc,initial.data(),&texture));hr(device->CreateShaderResourceView(texture.Get(),nullptr,&view));
            desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&desc,nullptr,&staging));
            context->CopyResource(staging.Get(),texture.Get());
            for(const auto& part:layout.parts){
                const UINT index=D3D11CalcSubresource(part.mip,part.layer,layout.mips);D3D11_MAPPED_SUBRESOURCE mapped{};
                hr(context->Map(staging.Get(),index,D3D11_MAP_READ,0,&mapped));require(mapped.RowPitch>=part.rowPitch,"GPU readback pitch rejected");
                bool equal=true;for(UINT row=0;row<part.height;++row)
                    equal=equal&&!memcmp(static_cast<const unsigned char*>(mapped.pData)+size_t(row)*mapped.RowPitch,data.data()+part.offset+size_t(row)*part.rowPitch,part.rowPitch);
                context->Unmap(staging.Get(),index);require(equal,"GPU texture readback differs from validated bytes");++parts;
            }
            peakResidentPayload=std::max(peakResidentPayload,(data.size()-148)*2);payload+=data.size()-148;++resources;
            // No bindings retain these views/textures. Each iteration releases
            // its resources; the counter below is payload, not measured GPU RAM.
        }
        require(resources>0,"Empty texture list rejected");
        context->ClearState();context->Flush();
        printf("{\"backend\":\"D3D11 WARP\",\"gameAttached\":false,\"resources\":%zu,\"subresources\":%zu,\"payloadBytesChecked\":%zu,\"maxSimultaneousTexturePayloadBytes\":%zu,\"gpuMemoryMeasured\":false,\"bitExactReadback\":true,\"failures\":0}\n",resources,parts,payload,peakResidentPayload);return 0;
    }catch(const std::exception& exception){fprintf(stderr,"FAIL: %s\ngameAttached=false; deviceCreated=%s\n",exception.what(),deviceCreated?"true":"false");return 1;}
}
