#pragma once
#include "RuntimeGuard.h"
#include "SeasonWorldMatrixBytecode.h"

// Owned by one admitted render session. No CPU readback, game writes, per-date
// allocation, worker GPU calls or cached assumptions about the current camera.
class SeasonWorldGpu {
    Microsoft::WRL::ComPtr<ID3D11Buffer> buffer;
    Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView> write;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view;
    Microsoft::WRL::ComPtr<ID3D11ComputeShader> shader;
    Microsoft::WRL::ComPtr<ID3DDeviceContextState> effect;
public:
    HRESULT update(ID3D11DeviceContext* context,ID3D11Buffer* actualVsConstants) {
        if(!context||!actualVsConstants)return E_INVALIDARG;
        D3D11_BUFFER_DESC source{};actualVsConstants->GetDesc(&source);
        if(source.ByteWidth<64||!(source.BindFlags&D3D11_BIND_CONSTANT_BUFFER))return E_INVALIDARG;
        Microsoft::WRL::ComPtr<ID3D11Device> device;context->GetDevice(&device);
        if(!shader){
            auto hr=device->CreateComputeShader(SeasonWorldMatrixBytecode,sizeof(SeasonWorldMatrixBytecode),nullptr,&shader);
            if(FAILED(hr))return hr;
            D3D11_BUFFER_DESC d{9*16,D3D11_USAGE_DEFAULT,D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS,
                0,D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,16};
            hr=device->CreateBuffer(&d,nullptr,&buffer);if(FAILED(hr))return hr;
            hr=device->CreateShaderResourceView(buffer.Get(),nullptr,&view);if(FAILED(hr))return hr;
            hr=device->CreateUnorderedAccessView(buffer.Get(),nullptr,&write);if(FAILED(hr))return hr;
            effect=seasons::CreatePipelineState(context);
        }
        seasons::PipelineScope scope(context,GetCurrentThreadId(),effect.Get());
        auto* uav=write.Get();context->CSSetShader(shader.Get(),nullptr,0);
        context->CSSetConstantBuffers(0,1,&actualVsConstants);context->CSSetUnorderedAccessViews(0,1,&uav,nullptr);
        context->Dispatch(1,1,1);
        return device->GetDeviceRemovedReason();
    }
    ID3D11ShaderResourceView* get()const{return view.Get();}
    ID3D11Buffer* data()const{return buffer.Get();}
};
