#pragma once
#include "RuntimeGuard.h"
#include "SeasonTreeColorShaderBytecode.h"
#include <bcrypt.h>
#include <array>
#include <algorithm>
#include <cmath>

// SHA256 of the GPU bytes, not shader metadata or texture dimensions alone.
inline bool materialSha(const void* bytes,size_t count,const char* expected) {
    if(count>UINT32_MAX)return false;
    BCRYPT_ALG_HANDLE algorithm{};BCRYPT_HASH_HANDLE hash{};
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return false;
    std::array<BYTE,32> digest{};
    bool ok=BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0;
    if(ok)ok=BCryptHashData(hash,static_cast<BYTE*>(const_cast<void*>(bytes)),static_cast<ULONG>(count),0)>=0&&
        BCryptFinishHash(hash,digest.data(),static_cast<ULONG>(digest.size()),0)>=0;
    if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);
    constexpr char hex[]="0123456789abcdef";
    if(ok)for(size_t i=0;i<digest.size();i++)ok=ok&&expected[i*2]==hex[digest[i]>>4]&&expected[i*2+1]==hex[digest[i]&15];
    return ok;
}
struct TreeColorParameters {
    uint64_t epoch;uint32_t year,month,day;float weights[4];
    bool full=false,seasonal=true;
};
inline void calendarWeights(TreeColorParameters& p) {
    const auto season=[](uint32_t month){return month>=3&&month<=5?0:month>=6&&month<=8?1:month>=9&&month<=11?2:3;};
    std::fill(std::begin(p.weights),std::end(p.weights),0.f);
    const float t=static_cast<float>(p.day-1)/30;
    p.weights[season(p.month)]+=1-t;p.weights[season(p.month==12?1:p.month+1)]+=t;
}
#include "SeasonTreeCatalog.h"
struct TreeSources {
    std::vector<BYTE> bytes;
    bool valid()const{
        if(bytes.size()!=TreeSourceBytes)return false;size_t at=0;
        for(const auto& asset:TreeAssets){if(!materialSha(bytes.data()+at,asset.bytes,asset.hash))return false;at+=asset.bytes;}
        return at==bytes.size();
    }
    const BYTE* data(uint32_t asset)const{
        size_t at=0;for(uint32_t i=0;i<asset;i++)at+=TreeAssets[i].bytes;return bytes.data()+at;
    }
};
inline bool validTreeColor(const TreeColorParameters& p) {
    if(!p.epoch||p.year<=100||p.year>=400||p.month<1||p.month>12||p.day<1||p.day>30)return false;
    TreeColorParameters expected=p;calendarWeights(expected);
    for(unsigned i=0;i<4;i++)if(!std::isfinite(p.weights[i])||std::abs(p.weights[i]-expected.weights[i])>0.00001f)return false;
    return true;
}
// All context operations run inside the verified engine Draw/Prepare thread.
// The cache has one source and one current date. Changing dates replaces owned
// GPU resources; it does not accumulate one texture per heartbeat/date.
class TreeColorTexture {
    ComPtr<ID3D11ShaderResourceView> source,view;
    ComPtr<ID3D11ShaderResourceView> green,autumn;
    const TreeSources& sources;
    uint32_t asset;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ComputeShader> shader;
    ComPtr<ID3DDeviceContextState> effect;
    std::vector<BYTE> baseline;
    uint64_t epoch=0;
    std::array<float,4> weights{};
    uint32_t format=0;
    HRESULT decode(ID3D11DeviceContext* context,const TreeColorParameters& p,bool original,
        ComPtr<ID3D11Texture2D>& output,std::vector<BYTE>& pixels) {
        ComPtr<ID3D11Device> device;context->GetDevice(&device);
        const auto& material=TreeAssets[asset];
        D3D11_TEXTURE2D_DESC d{material.width,material.height,material.mips,1,DXGI_FORMAT_R8G8B8A8_TYPELESS,{1,0},D3D11_USAGE_DEFAULT,
            D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS,0,0};
        auto hr=device->CreateTexture2D(&d,nullptr,&output);if(FAILED(hr))return hr;
        d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr))return hr;
        {
            seasons::PipelineScope scope(context,GetCurrentThreadId(),effect.Get());
            context->CSSetShader(shader.Get(),nullptr,0);
            ID3D11ShaderResourceView* inputs[]={source.Get(),green.Get(),autumn.Get()};context->CSSetShaderResources(0,3,inputs);
            for(UINT mip=0;mip<material.mips;mip++){
                D3D11_UNORDERED_ACCESS_VIEW_DESC u{};u.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
                u.ViewDimension=D3D11_UAV_DIMENSION_TEXTURE2D;u.Texture2D.MipSlice=mip;
                ComPtr<ID3D11UnorderedAccessView> uav;hr=device->CreateUnorderedAccessView(output.Get(),&u,&uav);if(FAILED(hr))return hr;
                struct Constants {uint32_t mip,encode,baseline,evergreen;float weights[4];} constants{mip,(format==99||format==72),original?1u:0u,material.policy==2?1u:0u};
                std::memcpy(constants.weights,p.weights,16);
                D3D11_BUFFER_DESC b{};b.ByteWidth=32;b.Usage=D3D11_USAGE_IMMUTABLE;b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
                D3D11_SUBRESOURCE_DATA data{&constants,0,0};ComPtr<ID3D11Buffer> buffer;
                hr=device->CreateBuffer(&b,&data,&buffer);if(FAILED(hr))return hr;
                auto* rawUav=uav.Get();auto* rawBuffer=buffer.Get();context->CSSetUnorderedAccessViews(0,1,&rawUav,nullptr);
                context->CSSetConstantBuffers(0,1,&rawBuffer);context->Dispatch((std::max(1u,material.width>>mip)+7)/8,(std::max(1u,material.height>>mip)+7)/8,1);
            }
            ID3D11UnorderedAccessView* unbound{};context->CSSetUnorderedAccessViews(0,1,&unbound,nullptr);
            context->CopyResource(staging.Get(),output.Get());
        }
        pixels.resize(material.pixels*4);size_t at=0;
        for(UINT mip=0;mip<material.mips;mip++){
            UINT pitch=std::max(1u,material.width>>mip)*4,rows=std::max(1u,material.height>>mip);D3D11_MAPPED_SUBRESOURCE mapped{};
            hr=context->Map(staging.Get(),mip,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
            bool valid=mapped.pData&&mapped.RowPitch>=pitch&&at+size_t(pitch)*rows<=pixels.size();
            if(valid)for(UINT y=0;y<rows;y++)std::memcpy(pixels.data()+at+y*pitch,static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch,pitch);
            context->Unmap(staging.Get(),mip);if(!valid)return E_INVALIDARG;at+=size_t(pitch)*rows;
        }
        return at==pixels.size()?S_OK:E_INVALIDARG;
    }
public:
    static HRESULT verifySource(ID3D11DeviceContext* context,ID3D11ShaderResourceView* incoming,uint32_t& asset,uint32_t& format) {
        ComPtr<ID3D11Resource> resource;incoming->GetResource(&resource);ComPtr<ID3D11Texture2D> sourceTexture;
        auto hr=resource.As(&sourceTexture);if(FAILED(hr))return hr;
        D3D11_TEXTURE2D_DESC d{};sourceTexture->GetDesc(&d);D3D11_SHADER_RESOURCE_VIEW_DESC v{};incoming->GetDesc(&v);
        const bool bc1=d.Format==DXGI_FORMAT_BC1_TYPELESS;
        if((d.Width!=256&&d.Width!=512)||(d.Height!=128&&d.Height!=256&&d.Height!=512)||d.MipLevels<6||d.MipLevels>8||
            d.ArraySize!=1||d.SampleDesc.Count!=1||(d.Format!=DXGI_FORMAT_BC7_TYPELESS&&!bc1)||
            d.Usage!=D3D11_USAGE_IMMUTABLE||d.BindFlags!=D3D11_BIND_SHADER_RESOURCE||d.MiscFlags!=0||
            v.ViewDimension!=D3D11_SRV_DIMENSION_TEXTURE2D||v.Texture2D.MostDetailedMip!=0||v.Texture2D.MipLevels!=d.MipLevels||
            (bc1?(v.Format!=DXGI_FORMAT_BC1_UNORM&&v.Format!=DXGI_FORMAT_BC1_UNORM_SRGB):
                (v.Format!=DXGI_FORMAT_BC7_UNORM&&v.Format!=DXGI_FORMAT_BC7_UNORM_SRGB)))return S_FALSE;
        ComPtr<ID3D11Device> device;context->GetDevice(&device);
        d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
        ComPtr<ID3D11Texture2D> staging;hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr))return hr;
        context->CopyResource(staging.Get(),sourceTexture.Get());std::vector<BYTE> bytes;
        bytes.reserve(350000);
        for(UINT mip=0;mip<d.MipLevels;mip++){
            UINT pitch=std::max(1u,((d.Width>>mip)+3)/4)*(bc1?8u:16u),rows=std::max(1u,((d.Height>>mip)+3)/4);
            const auto at=bytes.size();bytes.resize(at+size_t(pitch)*rows);D3D11_MAPPED_SUBRESOURCE mapped{};
            hr=context->Map(staging.Get(),mip,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
            bool valid=mapped.pData&&mapped.RowPitch>=pitch;
            if(valid)for(UINT y=0;y<rows;y++)std::memcpy(bytes.data()+at+y*pitch,static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch,pitch);
            context->Unmap(staging.Get(),mip);if(!valid)return E_INVALIDARG;
        }
        // Both payloads and their identical leaf geometry were compared with
        // original G1T/G1M files and actual game GPU readback. July 3 uses
        // D9268C48; dimensions or the month alone never authorize a source.
        for(asset=0;asset<std::size(TreeAssets);asset++)if(d.Width==TreeAssets[asset].width&&d.Height==TreeAssets[asset].height&&d.MipLevels==TreeAssets[asset].mips&&
            (bc1?71u:98u)==TreeAssets[asset].format&&bytes.size()==TreeAssets[asset].bytes&&materialSha(bytes.data(),bytes.size(),TreeAssets[asset].hash))break;
        if(asset==std::size(TreeAssets))return S_FALSE;
        format=v.Format;return S_OK;
    }
    TreeColorTexture(ID3D11ShaderResourceView* incoming,uint32_t sourceFormat,uint32_t assetIndex,const TreeSources& data):
        source(incoming),sources(data),asset(assetIndex),format(sourceFormat){}
    HRESULT reference(ID3D11Device* device,uint32_t index,ComPtr<ID3D11ShaderResourceView>& result){
        const auto& m=TreeAssets[index];D3D11_SUBRESOURCE_DATA mips[8]{};size_t at=0;
        for(UINT mip=0;mip<m.mips;mip++){UINT pitch=std::max(1u,((m.width>>mip)+3)/4)*(m.format==71?8u:16u),rows=std::max(1u,((m.height>>mip)+3)/4);
            mips[mip]={sources.data(index)+at,pitch,pitch*rows};at+=size_t(pitch)*rows;}
        D3D11_TEXTURE2D_DESC d{m.width,m.height,m.mips,1,static_cast<DXGI_FORMAT>(m.format-1),{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
        ComPtr<ID3D11Texture2D> t;auto hr=device->CreateTexture2D(&d,mips,&t);if(FAILED(hr))return hr;
        D3D11_SHADER_RESOURCE_VIEW_DESC v{};v.Format=static_cast<DXGI_FORMAT>(m.format+((format==99||format==72)?1:0));v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;v.Texture2D.MipLevels=m.mips;
        return device->CreateShaderResourceView(t.Get(),&v,&result);
    }
    HRESULT get(ID3D11DeviceContext* context,ID3D11ShaderResourceView* incoming,const TreeColorParameters& p,
        ID3D11ShaderResourceView** replacement,uint32_t& alphaPixels,bool& generated) noexcept {
        try {
            if(!incoming||!validTreeColor(p))return E_INVALIDARG;
            auto hr=S_OK;
            if(!shader){ComPtr<ID3D11Device> device;context->GetDevice(&device);
                hr=device->CreateComputeShader(SeasonTreeColorShader,sizeof(SeasonTreeColorShader),nullptr,&shader);if(FAILED(hr))return hr;
                effect=seasons::CreatePipelineState(context);
                hr=reference(device.Get(),TreeAssets[asset].green,green);if(FAILED(hr))return hr;
                hr=reference(device.Get(),TreeAssets[asset].autumn,autumn);if(FAILED(hr))return hr;}
            // Full rendering follows the live calendar between controller
            // acknowledgements, so weights can change within one request epoch.
            if(!view||epoch!=p.epoch||std::memcmp(weights.data(),p.weights,16)!=0){
                if(view&&std::memcmp(weights.data(),p.weights,16)==0){epoch=p.epoch;alphaPixels=TreeAssets[asset].pixels;*replacement=view.Get();return S_OK;}
                if(baseline.empty()){ComPtr<ID3D11Texture2D> original;hr=decode(context,p,true,original,baseline);if(FAILED(hr))return hr;}
                ComPtr<ID3D11Texture2D> next;std::vector<BYTE> pixels;hr=decode(context,p,false,next,pixels);if(FAILED(hr))return hr;
                if(pixels.size()!=baseline.size())return E_INVALIDARG;
                for(size_t i=3;i<pixels.size();i+=4)if(pixels[i]!=baseline[i])return E_INVALIDARG;
                ComPtr<ID3D11Device> device;context->GetDevice(&device);
                D3D11_SHADER_RESOURCE_VIEW_DESC v{};v.Format=(format==99||format==72)?DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:DXGI_FORMAT_R8G8B8A8_UNORM;
                v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;v.Texture2D.MipLevels=TreeAssets[asset].mips;ComPtr<ID3D11ShaderResourceView> nextView;
                hr=device->CreateShaderResourceView(next.Get(),&v,&nextView);if(FAILED(hr))return hr;
                view=nextView;texture=next;epoch=p.epoch;std::memcpy(weights.data(),p.weights,16);generated=true;
            }
            alphaPixels=TreeAssets[asset].pixels;*replacement=view.Get();return S_OK;
        }catch(...){return E_FAIL;}
    }
    uint64_t gpuBytes()const{const auto& a=TreeAssets[asset];return (texture?uint64_t(a.pixels)*4:0)+(green?TreeAssets[a.green].bytes:0)+(autumn?TreeAssets[a.autumn].bytes:0);}
};

// A calendar/scene refresh can recreate an SRV for the same immutable asset.
// Authenticate its GPU payload instead of pinning the first COM address.
// Linear and sRGB outputs stay separate; duplicate SRVs share the corresponding
// asset/date/weight cache. The two verified assets each have separate linear
// and sRGB outputs, bounding owned GPU allocation by four textures.
class TreeColorCache {
    struct Binding {ComPtr<ID3D11ShaderResourceView> view;int texture=-1;};
    std::array<Binding,32> bindings;
    size_t nextBinding=0;
    std::array<std::unique_ptr<TreeColorTexture>,std::size(TreeAssets)*2> textures;
    const TreeSources& sources;
public:
    explicit TreeColorCache(const TreeSources& value):sources(value){}
    HRESULT get(ID3D11DeviceContext* context,ID3D11ShaderResourceView* incoming,const TreeColorParameters& p,
        ID3D11ShaderResourceView** replacement,uint32_t& alphaPixels,bool& generated,uint32_t indexCount=918,uint32_t* selectedAsset=nullptr) noexcept {
        try {
            if(!incoming||!validTreeColor(p))return E_INVALIDARG;
            int index=-1;bool known=false;
            for(const auto& binding:bindings)if(binding.view.Get()==incoming){index=binding.texture;known=true;break;}
            if(!known){
                uint32_t asset{},format{};auto hr=TreeColorTexture::verifySource(context,incoming,asset,format);
                if(FAILED(hr))return hr;
                if(hr==S_OK){index=asset*2+((format==DXGI_FORMAT_BC7_UNORM_SRGB||format==DXGI_FORMAT_BC1_UNORM_SRGB)?1:0);
                    if(!textures[index])textures[index]=std::make_unique<TreeColorTexture>(incoming,format,asset,sources);}
                // Strong refs prevent address reuse while an identity is cached.
                // Evicted entries are authenticated again, never trusted by address.
                bindings[nextBinding]={incoming,index};nextBinding=(nextBinding+1)%bindings.size();
            }
            if(index<0||!treeAssetCount(static_cast<uint32_t>(index/2),indexCount))return S_FALSE;
            if(selectedAsset)*selectedAsset=static_cast<uint32_t>(index/2);
            return textures[index]->get(context,incoming,p,replacement,alphaPixels,generated);
        }catch(...){return E_FAIL;}
    }
    uint64_t gpuBytes()const{uint64_t bytes=0;for(const auto& item:textures)if(item)bytes+=item->gpuBytes();return bytes;}
};
