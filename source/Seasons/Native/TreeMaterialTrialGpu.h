#pragma once
#include "RuntimeGuard.h"
#include "TreeMaterialTrialShaderBytecode.h"

HRESULT CreateSpringTree(ID3D11DeviceContext* context,ID3D11ShaderResourceView* originalView,
    TreeMaterialTrial& p,ComPtr<ID3D11Texture2D>& texture,ComPtr<ID3D11ShaderResourceView>& view) noexcept {
    try {
        ComPtr<ID3D11Device> device;context->GetDevice(&device);
        if(!device||(device->GetCreationFlags()&D3D11_CREATE_DEVICE_SINGLETHREADED))return E_INVALIDARG;
        if(FAILED(device->GetDeviceRemovedReason()))return DXGI_ERROR_DEVICE_REMOVED;
        p.failureStage=10;
        ComPtr<ID3D11Resource> originalResource;originalView->GetResource(&originalResource);
        ComPtr<ID3D11Texture2D> originalTexture;if(FAILED(originalResource.As(&originalTexture)))return E_INVALIDARG;
        D3D11_TEXTURE2D_DESC d{};originalTexture->GetDesc(&d);
        D3D11_SHADER_RESOURCE_VIEW_DESC vd{};originalView->GetDesc(&vd);
        p.width=d.Width;p.height=d.Height;p.mips=d.MipLevels;p.viewFormat=vd.Format;
        if(d.Width!=512||d.Height!=256||d.MipLevels!=7||d.ArraySize!=1||d.SampleDesc.Count!=1||
           d.Format!=DXGI_FORMAT_BC7_TYPELESS||vd.ViewDimension!=D3D11_SRV_DIMENSION_TEXTURE2D||
           vd.Texture2D.MostDetailedMip!=0||vd.Texture2D.MipLevels!=7||
           (vd.Format!=DXGI_FORMAT_BC7_UNORM&&vd.Format!=DXGI_FORMAT_BC7_UNORM_SRGB)||
           p.bytes!=TreeTrialOutputBytes||!p.output)return E_INVALIDARG;
        p.newViewFormat=vd.Format==DXGI_FORMAT_BC7_UNORM_SRGB?DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:DXGI_FORMAT_R8G8B8A8_UNORM;
        d.Format=DXGI_FORMAT_R8G8B8A8_TYPELESS;d.Usage=D3D11_USAGE_DEFAULT;d.CPUAccessFlags=0;d.MiscFlags=0;
        d.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;
        ComPtr<ID3D11Texture2D> baseline,baselineRead,resultRead;
        p.failureStage=11;auto hr=device->CreateTexture2D(&d,nullptr,&texture);if(FAILED(hr))return hr;
        hr=device->CreateTexture2D(&d,nullptr,&baseline);if(FAILED(hr))return hr;
        d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        hr=device->CreateTexture2D(&d,nullptr,&baselineRead);if(FAILED(hr))return hr;
        hr=device->CreateTexture2D(&d,nullptr,&resultRead);if(FAILED(hr))return hr;
        ComPtr<ID3D11ComputeShader> shader;
        p.failureStage=12;hr=device->CreateComputeShader(SeasonTreeMaterialSourceShader,sizeof(SeasonTreeMaterialSourceShader),nullptr,&shader);if(FAILED(hr))return hr;
        {
            seasons::PipelineScope scope(context,GetCurrentThreadId());
            context->CSSetShader(shader.Get(),nullptr,0);auto* raw=originalView;context->CSSetShaderResources(0,1,&raw);
            for(UINT mip=0;mip<7;mip++)for(UINT variant=0;variant<2;variant++){
                p.failureStage=13;
                D3D11_UNORDERED_ACCESS_VIEW_DESC u{};u.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
                u.ViewDimension=D3D11_UAV_DIMENSION_TEXTURE2D;u.Texture2D.MipSlice=mip;
                ComPtr<ID3D11UnorderedAccessView> uav;hr=device->CreateUnorderedAccessView(variant?texture.Get():baseline.Get(),&u,&uav);if(FAILED(hr))return hr;
                struct Parameters {uint32_t mip,encode;float spring;uint32_t padding;} parameters{mip,vd.Format==DXGI_FORMAT_BC7_UNORM_SRGB,static_cast<float>(variant),0};
                D3D11_BUFFER_DESC b{};b.ByteWidth=16;b.Usage=D3D11_USAGE_IMMUTABLE;b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
                D3D11_SUBRESOURCE_DATA data{&parameters,0,0};ComPtr<ID3D11Buffer> constants;
                hr=device->CreateBuffer(&b,&data,&constants);if(FAILED(hr))return hr;
                auto* ru=uav.Get();auto* rc=constants.Get();context->CSSetUnorderedAccessViews(0,1,&ru,nullptr);context->CSSetConstantBuffers(0,1,&rc);
                context->Dispatch((std::max(1u,512u>>mip)+7)/8,(std::max(1u,256u>>mip)+7)/8,1);
            }
            ID3D11UnorderedAccessView* unbound{};context->CSSetUnorderedAccessViews(0,1,&unbound,nullptr);
            context->CopyResource(baselineRead.Get(),baseline.Get());context->CopyResource(resultRead.Get(),texture.Get());
        }
        // Read both decoded mip chains before publishing the replacement. The
        // same shader/output encoding makes RGB comparison valid for sRGB too.
        p.failureStage=14;auto* output=reinterpret_cast<BYTE*>(p.output);constexpr UINT half=TreeTrialOutputBytes/2;
        UINT at=0;p.changedPixels=0;p.alphaPixels=0;
        for(UINT mip=0;mip<7;mip++){
            UINT pitch=std::max(1u,512u>>mip)*4,rows=std::max(1u,256u>>mip);
            for(UINT variant=0;variant<2;variant++){
                auto* staging=variant?resultRead.Get():baselineRead.Get();D3D11_MAPPED_SUBRESOURCE m{};
                hr=context->Map(staging,mip,D3D11_MAP_READ,0,&m);if(FAILED(hr))return hr;
                bool valid=m.pData&&m.RowPitch>=pitch&&at+pitch*rows<=half;
                if(valid)for(UINT y=0;y<rows;y++)std::memcpy(output+variant*half+at+y*pitch,static_cast<BYTE*>(m.pData)+y*m.RowPitch,pitch);
                context->Unmap(staging,mip);if(!valid)return E_INVALIDARG;
            }
            at+=pitch*rows;
        }
        if(at!=half)return E_INVALIDARG;
        for(UINT i=0;i<half;i+=4){
            if(output[i+3]!=output[half+i+3])return E_INVALIDARG;
            ++p.alphaPixels;p.changedPixels+=std::memcmp(output+i,output+half+i,3)!=0;
        }
        if(!p.changedPixels)return E_INVALIDARG;
        vd.Format=static_cast<DXGI_FORMAT>(p.newViewFormat);p.failureStage=15;
        hr=device->CreateShaderResourceView(texture.Get(),&vd,&view);
        if(SUCCEEDED(hr)){p.outputBytes=TreeTrialOutputBytes;p.gpuReadbackComplete=1;p.failureStage=0;}
        return hr;
    }catch(...){return E_FAIL;}
}
