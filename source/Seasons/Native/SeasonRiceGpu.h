#pragma once
#include "SeasonRiceRootsBytecode.h"
#include "SeasonRiceVertexBytecode.h"
#include "SeasonRicePlantBytecode.h"
#include "SeasonRiceColorBytecode.h"

// The admitted detailed land VS evaluates the game's height, instance transform
// and terrain seams. A second, isolated Draw streams roots from that exact VS.
// The roots are subsequently drawn as 3D stalk/leaf/grain geometry before UI.
class SeasonRiceGpu {
    static constexpr UINT Capacity=262144,QueryCount=64;
    ComPtr<ID3D11Buffer> roots;
    ComPtr<ID3D11GeometryShader> placement,plant;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> color;
    ComPtr<ID3D11InputLayout> layout;
    ComPtr<ID3DDeviceContextState> state;
    ComPtr<ID3D11BlendState> blend;
    std::array<ComPtr<ID3D11DepthStencilState>,2> depthStates;
    std::array<ComPtr<ID3D11Query>,QueryCount> queries;
    std::array<bool,QueryCount> busy{};
    UINT cursor=0;bool pending=false;
    uint64_t generated=0,written=0,draws=0,frames=0;
    HRESULT initialize(ID3D11DeviceContext* context){
        if(state)return S_OK;ComPtr<ID3D11Device> device;context->GetDevice(&device);HRESULT hr;
#define RICE_CREATE(expression) hr=(expression);if(FAILED(hr))return hr
        D3D11_BUFFER_DESC bd{Capacity*16,D3D11_USAGE_DEFAULT,D3D11_BIND_VERTEX_BUFFER|D3D11_BIND_STREAM_OUTPUT,0,0,0};
        RICE_CREATE(device->CreateBuffer(&bd,nullptr,&roots));
        D3D11_SO_DECLARATION_ENTRY entry{0,"POSITION",0,0,4,0};UINT stride=16;
        RICE_CREATE(device->CreateGeometryShaderWithStreamOutput(SeasonRiceRootsBytecode,sizeof(SeasonRiceRootsBytecode),&entry,1,&stride,1,D3D11_SO_NO_RASTERIZED_STREAM,nullptr,&placement));
        RICE_CREATE(device->CreateGeometryShader(SeasonRicePlantBytecode,sizeof(SeasonRicePlantBytecode),nullptr,&plant));
        RICE_CREATE(device->CreateVertexShader(SeasonRiceVertexBytecode,sizeof(SeasonRiceVertexBytecode),nullptr,&vertex));
        RICE_CREATE(device->CreatePixelShader(SeasonRiceColorBytecode,sizeof(SeasonRiceColorBytecode),nullptr,&color));
        D3D11_INPUT_ELEMENT_DESC element{"POSITION",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0};
        RICE_CREATE(device->CreateInputLayout(&element,1,SeasonRiceVertexBytecode,sizeof(SeasonRiceVertexBytecode),&layout));
        D3D11_BLEND_DESC b{};auto& rt=b.RenderTarget[0];rt.BlendEnable=TRUE;rt.SrcBlend=D3D11_BLEND_SRC_ALPHA;rt.DestBlend=D3D11_BLEND_INV_SRC_ALPHA;
        rt.BlendOp=D3D11_BLEND_OP_ADD;rt.SrcBlendAlpha=D3D11_BLEND_ZERO;rt.DestBlendAlpha=D3D11_BLEND_ONE;rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        RICE_CREATE(device->CreateBlendState(&b,&blend));
        for(UINT i=0;i<2;i++){D3D11_DEPTH_STENCIL_DESC d{};d.DepthEnable=TRUE;d.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL;
            d.DepthFunc=i?D3D11_COMPARISON_GREATER_EQUAL:D3D11_COMPARISON_LESS_EQUAL;RICE_CREATE(device->CreateDepthStencilState(&d,&depthStates[i]));}
        D3D11_QUERY_DESC q{D3D11_QUERY_SO_STATISTICS,0};for(auto& query:queries)RICE_CREATE(device->CreateQuery(&q,&query));
        state=seasons::CreatePipelineState(context);return S_OK;
#undef RICE_CREATE
    }
    HRESULT retire(ID3D11DeviceContext* context){
        for(UINT i=0;i<QueryCount;i++)if(busy[i]){
            D3D11_QUERY_DATA_SO_STATISTICS result{};auto hr=context->GetData(queries[i].Get(),&result,sizeof(result),D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if(FAILED(hr))return hr;if(hr==S_FALSE)continue;busy[i]=false;generated+=result.PrimitivesStorageNeeded;written+=result.NumPrimitivesWritten;
            if(result.PrimitivesStorageNeeded>result.NumPrimitivesWritten)return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
        }return S_OK;
    }
public:
    HRESULT poll(ID3D11DeviceContext* context){return retire(context);}
    bool settled()const{return std::none_of(busy.begin(),busy.end(),[](bool b){return b;});}
#ifdef SEASON_BRIDGE_FIXTURE
    ID3D11Buffer* fixtureRoots()const{return roots.Get();}
#endif
    uint64_t gpuBytes()const{return roots?Capacity*16:0;}
    uint64_t rootCount()const{return written;}
    uint64_t rootDraws()const{return draws;}
    uint64_t renderedFrames()const{return frames;}
    void saveStatistics(const std::wstring& path)const{
        std::string json="{\"placementDraws\":"+std::to_string(draws)+",\"renderedFrames\":"+std::to_string(frames)+
            ",\"completedQueryRoots\":"+std::to_string(written)+",\"generatedQueryRoots\":"+std::to_string(generated)+
            ",\"pendingQueries\":"+std::to_string(std::count(busy.begin(),busy.end(),true))+",\"capacityRoots\":"+std::to_string(Capacity)+"}\n";
        HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(file==INVALID_HANDLE_VALUE)return;
        DWORD writtenBytes{};WriteFile(file,json.data(),static_cast<DWORD>(json.size()),&writtenBytes,nullptr);CloseHandle(file);
    }
    void discardFrame(){pending=false;}
    HRESULT collect(ID3D11DeviceContext* context,const TreeColorParameters& p,UINT indexCount,UINT instances,
                    ID3D11Buffer* parameters,ID3D11ShaderResourceView* climate,ID3D11ShaderResourceView* matrix,ID3D11ShaderResourceView* authenticatedControl){
        if(p.weights[1]+p.weights[2]<=0||!indexCount||!instances||!matrix||!authenticatedControl)return S_FALSE;
        if(indexCount>65536||instances>4096)return E_INVALIDARG;
        auto hr=initialize(context);if(FAILED(hr))return hr;hr=retire(context);if(FAILED(hr))return hr;
        UINT queryIndex=QueryCount;for(UINT i=0;i<QueryCount;i++){UINT k=(cursor+i)%QueryCount;if(!busy[k]){queryIndex=k;break;}}
        if(queryIndex==QueryCount)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        ComPtr<ID3D11VertexShader> gameVertex;UINT classes=0;context->VSGetShader(&gameVertex,nullptr,&classes);
        ComPtr<ID3D11GeometryShader> gameGeometry;context->GSGetShader(&gameGeometry,nullptr,nullptr);
        ComPtr<ID3D11HullShader> hull;ComPtr<ID3D11DomainShader> domain;context->HSGetShader(&hull,nullptr,nullptr);context->DSGetShader(&domain,nullptr,nullptr);
        if(!gameVertex||classes||gameGeometry||hull||domain)return S_FALSE;
        ComPtr<ID3D11InputLayout> gameLayout;context->IAGetInputLayout(&gameLayout);
        ComPtr<ID3D11Buffer> indices,constants,gameTable;DXGI_FORMAT indexFormat{};UINT indexOffset{};
        context->IAGetIndexBuffer(&indices,&indexFormat,&indexOffset);context->VSGetConstantBuffers(0,1,&constants);
        context->PSGetConstantBuffers(0,1,&gameTable);D3D11_BUFFER_DESC tableDesc{};if(gameTable)gameTable->GetDesc(&tableDesc);
        ComPtr<ID3D11ShaderResourceView> height,control,detail;context->VSGetShaderResources(8,1,&height);
        context->PSGetShaderResources(3,1,&control);context->PSGetShaderResources(7,1,&detail);
        ComPtr<ID3D11SamplerState> heightSampler,controlSampler,detailSampler,climateSampler;
        context->VSGetSamplers(8,1,&heightSampler);context->PSGetSamplers(3,1,&controlSampler);context->PSGetSamplers(7,1,&detailSampler);context->PSGetSamplers(12,1,&climateSampler);
        D3D11_PRIMITIVE_TOPOLOGY topology{};context->IAGetPrimitiveTopology(&topology);
        if(!indices||!constants||!gameTable||tableDesc.ByteWidth!=1027*16||!height||!control||!detail||!gameLayout||!heightSampler||!controlSampler||!detailSampler||topology!=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST)return S_FALSE;
        std::array<ComPtr<ID3D11Buffer>,8> buffers;ID3D11Buffer* raw[8]{};UINT strides[8]{},offsets[8]{};
        context->IAGetVertexBuffers(0,8,raw,strides,offsets);for(UINT i=0;i<8;i++)buffers[i].Attach(raw[i]);
        {
            seasons::PipelineScope scope(context,GetCurrentThreadId(),state.Get());
            context->IASetInputLayout(gameLayout.Get());context->IASetVertexBuffers(0,8,raw,strides,offsets);context->IASetIndexBuffer(indices.Get(),indexFormat,indexOffset);
            context->IASetPrimitiveTopology(topology);context->VSSetShader(gameVertex.Get(),nullptr,0);
            auto* cb=constants.Get();context->VSSetConstantBuffers(0,1,&cb);auto* view=height.Get();context->VSSetShaderResources(8,1,&view);
            auto* sampler=heightSampler.Get();context->VSSetSamplers(8,1,&sampler);context->GSSetShader(placement.Get(),nullptr,0);context->PSSetShader(nullptr,nullptr,0);
            context->GSSetConstantBuffers(12,1,&parameters);view=control.Get();context->GSSetShaderResources(3,1,&view);view=detail.Get();context->GSSetShaderResources(7,1,&view);
            context->GSSetShaderResources(11,1,&authenticatedControl);
            ID3D11ShaderResourceView* privateViews[]={climate,matrix};context->GSSetShaderResources(100,2,privateViews);cb=gameTable.Get();context->GSSetConstantBuffers(0,1,&cb);
            sampler=controlSampler.Get();context->GSSetSamplers(3,1,&sampler);sampler=detailSampler.Get();context->GSSetSamplers(7,1,&sampler);
            sampler=climateSampler.Get();context->GSSetSamplers(12,1,&sampler);
            auto* output=roots.Get();UINT offset=pending?UINT(-1):0;context->SOSetTargets(1,&output,&offset);
            context->Begin(queries[queryIndex].Get());context->DrawIndexedInstanced(indexCount,instances,0,0,0);context->End(queries[queryIndex].Get());
            context->SOSetTargets(0,nullptr,nullptr);
        }
        busy[queryIndex]=true;cursor=(queryIndex+1)%QueryCount;pending=true;++draws;return S_OK;
    }
    // Invoked inside the weather's isolated pipeline. DSV belongs to our copy;
    // neither original depth nor original color alpha is written by rice.
    HRESULT draw(ID3D11DeviceContext* context,const TreeColorParameters& p,ID3D11RenderTargetView* target,ID3D11DepthStencilView* depth,
                 UINT direction,ID3D11Buffer* parameters,ID3D11ShaderResourceView* matrix){
        if(!pending)return S_FALSE;pending=false;auto hr=retire(context);if(FAILED(hr))return hr;
        if(p.weights[1]+p.weights[2]<=0||!depth||direction<1||direction>2)return S_FALSE;
        context->OMSetRenderTargets(1,&target,depth);context->OMSetDepthStencilState(depthStates[direction-1].Get(),0);context->OMSetBlendState(blend.Get(),nullptr,UINT_MAX);
        auto* buffer=roots.Get();UINT stride=16,offset=0;context->IASetInputLayout(layout.Get());context->IASetVertexBuffers(0,1,&buffer,&stride,&offset);
        context->IASetIndexBuffer(nullptr,DXGI_FORMAT_UNKNOWN,0);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_POINTLIST);
        context->VSSetShader(vertex.Get(),nullptr,0);context->GSSetShader(plant.Get(),nullptr,0);context->PSSetShader(color.Get(),nullptr,0);
        context->GSSetConstantBuffers(12,1,&parameters);context->PSSetConstantBuffers(12,1,&parameters);context->GSSetShaderResources(101,1,&matrix);
        context->DrawAuto();context->GSSetShader(nullptr,nullptr,0);context->OMSetRenderTargets(1,&target,nullptr);++frames;
        ComPtr<ID3D11Device> device;context->GetDevice(&device);return device->GetDeviceRemovedReason();
    }
};
