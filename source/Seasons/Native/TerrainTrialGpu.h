#pragma once
#include "RuntimeGuard.h"
#include "TerrainTrialShaderBytecode.h"

// Requires the already owned render callback. GPU generation temporarily swaps
// a complete D3D11.1 pipeline state; the original state returns before the Draw.
HRESULT CreateSpringLand(ID3D11DeviceContext* context,ID3D11ShaderResourceView* originalView,
    const TerrainTrial& parameters,ComPtr<ID3D11Texture2D>& texture,ComPtr<ID3D11ShaderResourceView>& view,
    uint32_t& changedBlocks) noexcept {
    try {
        ComPtr<ID3D11Device> device;context->GetDevice(&device);
        if(device->GetCreationFlags()&D3D11_CREATE_DEVICE_SINGLETHREADED)return E_INVALIDARG;
        if(FAILED(device->GetDeviceRemovedReason()))return DXGI_ERROR_DEVICE_REMOVED;
        ComPtr<ID3D11Resource> originalResource;originalView->GetResource(&originalResource);
        ComPtr<ID3D11Texture2D> originalTexture;if(FAILED(originalResource.As(&originalTexture)))return E_INVALIDARG;
        D3D11_TEXTURE2D_DESC d{};originalTexture->GetDesc(&d);
        if(d.Width!=4096||d.Height!=4096||d.MipLevels!=11||d.ArraySize!=1||d.Format!=DXGI_FORMAT_BC3_TYPELESS||
           d.SampleDesc.Count!=1||parameters.bytes!=22369616)return E_INVALIDARG;
        D3D11_SHADER_RESOURCE_VIEW_DESC vd{};originalView->GetDesc(&vd);
        if(vd.ViewDimension!=D3D11_SRV_DIMENSION_TEXTURE2D||vd.Texture2D.MostDetailedMip!=0||vd.Texture2D.MipLevels!=11)return E_INVALIDARG;
        const auto* input=reinterpret_cast<const uint32_t*>(parameters.input);
        const uint32_t blocks=parameters.bytes/16;
        D3D11_BUFFER_DESC buffer{};buffer.ByteWidth=parameters.bytes;buffer.StructureByteStride=16;
        buffer.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;buffer.Usage=D3D11_USAGE_IMMUTABLE;
        buffer.BindFlags=D3D11_BIND_SHADER_RESOURCE;D3D11_SUBRESOURCE_DATA data{input,0,0};
        ComPtr<ID3D11Buffer> source,result,staging,constants;
        auto hr=device->CreateBuffer(&buffer,&data,&source);if(FAILED(hr))return hr;
        buffer.Usage=D3D11_USAGE_DEFAULT;buffer.BindFlags=D3D11_BIND_UNORDERED_ACCESS;
        hr=device->CreateBuffer(&buffer,nullptr,&result);if(FAILED(hr))return hr;
        buffer.Usage=D3D11_USAGE_STAGING;buffer.BindFlags=0;buffer.MiscFlags=0;buffer.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        hr=device->CreateBuffer(&buffer,nullptr,&staging);if(FAILED(hr))return hr;
        ComPtr<ID3D11ShaderResourceView> srv;ComPtr<ID3D11UnorderedAccessView> uav;
        D3D11_SHADER_RESOURCE_VIEW_DESC sv{};sv.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;sv.Buffer.NumElements=blocks;
        hr=device->CreateShaderResourceView(source.Get(),&sv,&srv);if(FAILED(hr))return hr;
        D3D11_UNORDERED_ACCESS_VIEW_DESC uv{};uv.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;uv.Buffer.NumElements=blocks;
        hr=device->CreateUnorderedAccessView(result.Get(),&uv,&uav);if(FAILED(hr))return hr;
        struct Parameters {uint32_t count;float spring,pad[2];} p{blocks,1,{0,0}};
        // Trial admission accepts only the currently verified April date. Both
        // current/next monthly anchors have Spring weight 1 in SeasonTimeline.
        buffer={};buffer.ByteWidth=16;buffer.Usage=D3D11_USAGE_IMMUTABLE;buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        data={&p,0,0};hr=device->CreateBuffer(&buffer,&data,&constants);if(FAILED(hr))return hr;
        ComPtr<ID3D11ComputeShader> shader;
        hr=device->CreateComputeShader(SeasonTerrainSourceShader,sizeof(SeasonTerrainSourceShader),nullptr,&shader);if(FAILED(hr))return hr;
        {
            seasons::PipelineScope scope(context,GetCurrentThreadId());
            auto* rawSrv=srv.Get();auto* rawUav=uav.Get();auto* rawConstants=constants.Get();
            context->CSSetShader(shader.Get(),nullptr,0);context->CSSetShaderResources(0,1,&rawSrv);
            context->CSSetUnorderedAccessViews(0,1,&rawUav,nullptr);context->CSSetConstantBuffers(0,1,&rawConstants);
            context->Dispatch((blocks+63)/64,1,1);context->CopyResource(staging.Get(),result.Get());
        }
        D3D11_MAPPED_SUBRESOURCE mapped{};hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
        const auto* generated=static_cast<const uint32_t*>(mapped.pData);
        bool preserved=generated!=nullptr;changedBlocks=0;
        for(uint32_t i=0;i<blocks&&preserved;i++){
            preserved=generated[i*4]==input[i*4]&&generated[i*4+1]==input[i*4+1]&&generated[i*4+3]==input[i*4+3];
            changedBlocks+=generated[i*4+2]!=input[i*4+2];
        }
        if(preserved&&changedBlocks){
            D3D11_SUBRESOURCE_DATA mips[11]{};uint32_t at=0;
            for(UINT mip=0;mip<11;mip++){
                UINT pitch=std::max(1u,((4096u>>mip)+3)/4)*16,rows=std::max(1u,((4096u>>mip)+3)/4);
                mips[mip]={static_cast<const BYTE*>(mapped.pData)+at,pitch,pitch*rows};at+=pitch*rows;
            }
            d.Usage=D3D11_USAGE_IMMUTABLE;d.BindFlags=D3D11_BIND_SHADER_RESOURCE;d.CPUAccessFlags=0;d.MiscFlags=0;
            hr=device->CreateTexture2D(&d,mips,&texture);
        }else hr=E_INVALIDARG;
        context->Unmap(staging.Get(),0);
        if(SUCCEEDED(hr))hr=device->CreateShaderResourceView(texture.Get(),&vd,&view);
        return hr;
    }catch(...){return E_FAIL;}
}
