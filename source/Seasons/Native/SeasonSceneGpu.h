#pragma once
#include "SeasonWorldGpu.h"
#include "SeasonShaderTranslation.h"
#include "SeasonTerrainShaderText.h"
#include "SeasonTreeColorBytecode.h"
#include "SeasonTreeDepthBytecode.h"
#include "SeasonWeatherSnowVertexBytecode.h"
#include "SeasonWeatherSnowBytecode.h"
#include "SeasonWeatherCapture.h"
#include "SeasonRiceGpu.h"

struct SeasonSceneData {
    std::vector<BYTE> bytes;ComPtr<ID3DBlob> terrain,distantTerrain;std::string compileDiagnostic;
    static constexpr uint32_t MaskBytes=TreeMaskBytes,ClimateSize=256,ClimateBytes=ClimateSize*ClimateSize*12*8;
    const uint32_t* header()const{return reinterpret_cast<const uint32_t*>(bytes.data());}
    uint32_t detailedShaderBytes()const{uint32_t n{};std::memcpy(&n,bytes.data()+header()[11]+24,4);return n;}
    bool valid()const{
        if(bytes.size()<64||bytes.size()>64+MaskBytes+ClimateBytes+263168)return false;const auto* h=header();
        bool capture=h[1]==4&&h[13]>=1&&h[13]<=2&&h[14]==uint64_t(h[11])+h[12]&&h[15]>=8&&h[15]<=1024&&h[15]%2==0;
        if(h[0]!=0x53475331||(h[1]!=3&&!capture)||h[2]!=bytes.size()||h[3]!=64||h[4]!=MaskBytes||h[5]!=64+MaskBytes||h[6]!=ClimateBytes||
           h[7]!=ClimateSize||h[8]!=ClimateSize||h[9]!=12||h[10]!=std::size(TreeAssets)||h[11]!=64+MaskBytes+ClimateBytes||h[12]<32||h[12]>262144||
           (!capture&&(h[13]||h[14]||h[15]))||uint64_t(h[11])+h[12]+(capture?h[15]:0)!=bytes.size())return false;
        if(capture){auto path=reinterpret_cast<const wchar_t*>(bytes.data()+h[14]);size_t length=h[15]/2;
            if(path[1]!=L':'||path[2]!=L'\\'||path[length-1]||std::wcslen(path)!=length-1)return false;}
        const auto* coverage=reinterpret_cast<const float*>(bytes.data()+64);
        for(size_t i=0;i<MaskBytes/4;i+=2)if(!std::isfinite(coverage[i])||!std::isfinite(coverage[i+1])||coverage[i]<0||coverage[i+1]<0||coverage[i]+coverage[i+1]>1.000001f)return false;
        const auto* climate=reinterpret_cast<const float*>(bytes.data()+h[5]);
        for(size_t i=0;i<ClimateBytes/4;i+=2)if(!std::isfinite(climate[i])||!std::isfinite(climate[i+1])||climate[i]<-60||climate[i]>60||climate[i+1]<0||climate[i+1]>3000)return false;
        auto detailed=detailedShaderBytes();if(detailed<32||h[12]<64||detailed>h[12]-32)return false;
        return materialSha(bytes.data()+h[11],detailed,"08f898d88d527a70b8a4f0a8bf283e5c4abffcf7cc53d7992400005878a7d381")&&
            materialSha(bytes.data()+h[11]+detailed,h[12]-detailed,"7a99093d74cea74d44fccde0d69c8a9d7af4b1300a3a7f24b7195c784d9d93f9");
    }
    HRESULT prepare(){
        if(!valid())return E_INVALIDARG;
        HRESULT result=S_OK;uint32_t detailed=detailedShaderBytes();
        for(bool distant:{false,true}){
        auto source=seasons::shader::translate(bytes.data()+header()[11]+(distant?detailed:0),distant?header()[12]-detailed:detailed,SeasonTerrainShaderText,distant);
        ComPtr<ID3DBlob> errors;result=D3DCompile(source.data(),source.size(),nullptr,nullptr,nullptr,"TerrainColor","ps_5_0",
            D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,distant?distantTerrain.ReleaseAndGetAddressOf():terrain.ReleaseAndGetAddressOf(),&errors);
        if(errors)compileDiagnostic.append(static_cast<const char*>(errors->GetBufferPointer()),errors->GetBufferSize());if(FAILED(result))return result;
        }return result;
    }
};
struct SeasonSceneTicket {
    bool changed=false;uint32_t kind=0;
    ComPtr<ID3D11PixelShader> shader;std::array<ComPtr<ID3D11Buffer>,2> constants;
    std::array<ComPtr<ID3D11ShaderResourceView>,6> resources;
    std::array<ComPtr<ID3D11SamplerState>,2> samplers;
};
class SeasonSceneGpu {
    const SeasonSceneData& source;
    ComPtr<ID3D11PixelShader> treeColor,treeDepth,land,distantLand,snow;
    ComPtr<ID3D11VertexShader> snowVertex;
    ComPtr<ID3D11Buffer> parameters,material;
    ComPtr<ID3D11SamplerState> clamp,plantWrap;
    ComPtr<ID3D11ShaderResourceView> climate;
    ComPtr<ID3D11Buffer> terrainTable;
    ComPtr<ID3D11ShaderResourceView> terrainTableView;
    std::array<ComPtr<ID3D11ShaderResourceView>,std::size(TreeAssets)> masks;
    SeasonWorldGpu world,colorWorld;
    ComPtr<ID3D11DepthStencilView> sceneDepth;
    D3D11_VIEWPORT colorViewport{};uint32_t depthDirection=0;
    ComPtr<ID3DDeviceContextState> weatherState;
    ComPtr<ID3D11Texture2D> sceneCopy,depthCopy;
    ComPtr<ID3D11ShaderResourceView> sceneView,depthView;
    ComPtr<ID3D11DepthStencilView> riceDepth;
    SeasonRiceGpu rice;
    ComPtr<ID3D11BlendState> snowBlend;
    ComPtr<ID3D11RasterizerState> weatherRaster;
    ComPtr<ID3D11DepthStencilState> noDepth;
    SeasonWeatherCapture capture,riceCapture;std::array<BYTE,64> captureParameters{};
    std::wstring riceStatisticsPath;
    uint64_t start=GetTickCount64();
#ifdef SEASON_BRIDGE_FIXTURE
    float fixedSeconds=0;
#endif
    HRESULT initialize(ID3D11DeviceContext* context){
        if(parameters)return S_OK;ComPtr<ID3D11Device> device;context->GetDevice(&device);HRESULT result;
#define SEASON_CREATE(expression) result=(expression);if(FAILED(result))return result
        SEASON_CREATE(device->CreatePixelShader(SeasonTreeColorBytecode,sizeof(SeasonTreeColorBytecode),nullptr,&treeColor));
        SEASON_CREATE(device->CreatePixelShader(SeasonTreeDepthBytecode,sizeof(SeasonTreeDepthBytecode),nullptr,&treeDepth));
        SEASON_CREATE(device->CreatePixelShader(source.terrain->GetBufferPointer(),source.terrain->GetBufferSize(),nullptr,&land));
        SEASON_CREATE(device->CreatePixelShader(source.distantTerrain->GetBufferPointer(),source.distantTerrain->GetBufferSize(),nullptr,&distantLand));
        SEASON_CREATE(device->CreatePixelShader(SeasonWeatherSnowBytecode,sizeof(SeasonWeatherSnowBytecode),nullptr,&snow));
        SEASON_CREATE(device->CreateVertexShader(SeasonWeatherSnowVertexBytecode,sizeof(SeasonWeatherSnowVertexBytecode),nullptr,&snowVertex));
        D3D11_SAMPLER_DESC sampler{};sampler.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sampler.AddressU=sampler.AddressV=sampler.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.ComparisonFunc=D3D11_COMPARISON_NEVER;sampler.MaxLOD=D3D11_FLOAT32_MAX;SEASON_CREATE(device->CreateSamplerState(&sampler,&clamp));
        sampler.AddressU=sampler.AddressV=sampler.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;
        SEASON_CREATE(device->CreateSamplerState(&sampler,&plantWrap));
        D3D11_TEXTURE2D_DESC d{256,256,1,12,DXGI_FORMAT_R32G32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
        D3D11_SUBRESOURCE_DATA data[12]{};for(UINT i=0;i<12;i++)data[i]={source.bytes.data()+source.header()[5]+i*256*256*8,256*8,0};
        ComPtr<ID3D11Texture2D> texture;SEASON_CREATE(device->CreateTexture2D(&d,data,&texture));SEASON_CREATE(device->CreateShaderResourceView(texture.Get(),nullptr,&climate));
        size_t at=64;for(UINT asset=0;asset<std::size(TreeAssets);asset++){
            const auto& a=TreeAssets[asset];d={a.width,a.height,a.mips,1,DXGI_FORMAT_R32G32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
            for(UINT mip=0;mip<a.mips;mip++){auto pitch=(a.width>>mip)*8;data[mip]={source.bytes.data()+at,pitch,0};at+=pitch*(a.height>>mip);}
            texture.Reset();SEASON_CREATE(device->CreateTexture2D(&d,data,&texture));SEASON_CREATE(device->CreateShaderResourceView(texture.Get(),nullptr,&masks[asset]));
        }
        D3D11_BUFFER_DESC cb{16,D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};SEASON_CREATE(device->CreateBuffer(&cb,nullptr,&material));
        D3D11_BLEND_DESC blend{};auto& b=blend.RenderTarget[0];b.BlendEnable=TRUE;b.SrcBlend=D3D11_BLEND_ONE;b.DestBlend=D3D11_BLEND_INV_SRC_ALPHA;b.BlendOp=D3D11_BLEND_OP_ADD;
        b.SrcBlendAlpha=D3D11_BLEND_ZERO;b.DestBlendAlpha=D3D11_BLEND_ONE;b.BlendOpAlpha=D3D11_BLEND_OP_ADD;b.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        SEASON_CREATE(device->CreateBlendState(&blend,&snowBlend));D3D11_RASTERIZER_DESC raster{};raster.FillMode=D3D11_FILL_SOLID;raster.CullMode=D3D11_CULL_NONE;raster.DepthClipEnable=TRUE;
        SEASON_CREATE(device->CreateRasterizerState(&raster,&weatherRaster));D3D11_DEPTH_STENCIL_DESC depth{};depth.DepthFunc=D3D11_COMPARISON_ALWAYS;
        SEASON_CREATE(device->CreateDepthStencilState(&depth,&noDepth));weatherState=seasons::CreatePipelineState(context);
        cb.ByteWidth=64;SEASON_CREATE(device->CreateBuffer(&cb,nullptr,&parameters));return S_OK;
    }
    void updateParameters(ID3D11DeviceContext* context,const TreeColorParameters& p,const D3D11_VIEWPORT& vp){
        struct Data {float weights[4],extent[4];uint32_t months[2];float fraction,seconds,viewport[4];} data{};
        static_assert(sizeof(Data)==64);std::memcpy(data.weights,p.weights,16);data.extent[2]=data.extent[3]=1.f/204800;
        data.months[0]=p.month-1;data.months[1]=p.month%12;data.fraction=float(p.day-1)/30;data.seconds=float((GetTickCount64()-start)%3600000)/1000;
#ifdef SEASON_BRIDGE_FIXTURE
        data.seconds=fixedSeconds;
#endif
        data.viewport[0]=vp.TopLeftX;data.viewport[1]=vp.TopLeftY;data.viewport[2]=1/vp.Width;data.viewport[3]=1/vp.Height;
        context->UpdateSubresource(parameters.Get(),0,nullptr,&data,0,0);
        if(capture.requested())std::memcpy(captureParameters.data(),&data,64);
    }
public:
#ifdef SEASON_BRIDGE_FIXTURE
    void fixtureTime(float seconds){fixedSeconds=seconds;}
#endif
    explicit SeasonSceneGpu(const SeasonSceneData& data):source(data){
        if(data.header()[1]==4){auto path=reinterpret_cast<const wchar_t*>(data.bytes.data()+data.header()[14]);
            capture.configure(path,data.header()[13]);riceCapture.configure(std::wstring(path)+L".rice",data.header()[13],true);riceStatisticsPath=std::wstring(path)+L".rice.json";}
    }
    void saveCaptureAfterDrain()const{capture.save();riceCapture.save();if(!riceStatisticsPath.empty())rice.saveStatistics(riceStatisticsPath);}
    void discardRiceFrame(){rice.discardFrame();}
    HRESULT collectRice(ID3D11DeviceContext* context,const TreeColorParameters& p,UINT indices,UINT instances,ID3D11ShaderResourceView* authenticatedControl){
        return rice.collect(context,p,indices,instances,parameters.Get(),climate.Get(),colorWorld.get(),authenticatedControl);
    }
    // kind 1=color tree, 2=depth tree, 3=detailed land, 5=distant land. Admission/authentication belongs
    // to the engine callback; this object never scans unrelated game resources.
    HRESULT enter(ID3D11DeviceContext* context,const TreeColorParameters& p,uint32_t kind,uint32_t asset,SeasonSceneTicket& ticket,
                  ID3D11ShaderResourceView* plantControl=nullptr,ID3D11ShaderResourceView* plantDetail=nullptr){
        HRESULT result;
        if(ticket.changed||kind<1||(kind>3&&kind!=5)||asset>=std::size(TreeAssets))return E_INVALIDARG;
        UINT count=1;D3D11_VIEWPORT vp{};context->RSGetViewports(&count,&vp);
        if(count!=1||vp.Width<=0||vp.Height<=0||vp.Width>4096||vp.Height>4096||vp.MaxDepth<=vp.MinDepth)return S_FALSE;
        SEASON_CREATE(initialize(context));
        if(kind==3){
            ComPtr<ID3D11Buffer> original;context->PSGetConstantBuffers(0,1,&original);if(!original)return S_FALSE;
            D3D11_BUFFER_DESC d{};original->GetDesc(&d);if(d.ByteWidth!=1027*16)return S_FALSE;
            if(!terrainTable){
                ComPtr<ID3D11Device> device;context->GetDevice(&device);
                d.Usage=D3D11_USAGE_DEFAULT;d.BindFlags=D3D11_BIND_SHADER_RESOURCE;d.CPUAccessFlags=0;
                d.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;d.StructureByteStride=16;
                SEASON_CREATE(device->CreateBuffer(&d,nullptr,&terrainTable));
                SEASON_CREATE(device->CreateShaderResourceView(terrainTable.Get(),nullptr,&terrainTableView));
            }
            // Capture the verified detailed material table on the render thread.
            // The distant b0 contains scale values, not detail-layer indices.
            context->CopyResource(terrainTable.Get(),original.Get());
        }else if(kind==5&&!terrainTableView)return S_FALSE;
        ComPtr<ID3D11Buffer> vsConstants;context->VSGetConstantBuffers(0,1,&vsConstants);if(!vsConstants)return S_FALSE;
        SEASON_CREATE(world.update(context,vsConstants.Get()));
        if(kind!=2){
            SEASON_CREATE(colorWorld.update(context,vsConstants.Get()));context->OMGetRenderTargets(0,nullptr,sceneDepth.ReleaseAndGetAddressOf());colorViewport=vp;
            ComPtr<ID3D11DepthStencilState> state;UINT stencil{};context->OMGetDepthStencilState(&state,&stencil);D3D11_DEPTH_STENCIL_DESC d{};
            if(state)state->GetDesc(&d);else d.DepthFunc=D3D11_COMPARISON_LESS;
            if(d.DepthFunc==D3D11_COMPARISON_LESS||d.DepthFunc==D3D11_COMPARISON_LESS_EQUAL)depthDirection=1;
            else if(d.DepthFunc==D3D11_COMPARISON_GREATER||d.DepthFunc==D3D11_COMPARISON_GREATER_EQUAL)depthDirection=2;
            else sceneDepth.Reset();
        }
        updateParameters(context,p,vp);
        struct Material {uint32_t policy;float cutoff,minDepth,inverseSpan;} m{TreeAssets[asset].policy|(TreeAssets[asset].green!=TreeAssets[asset].autumn?4u:0u),.49843138456344604f,vp.MinDepth,1/(vp.MaxDepth-vp.MinDepth)};
        if((kind==3||kind==5)&&plantControl&&plantDetail)m.policy|=256u;
        context->UpdateSubresource(material.Get(),0,nullptr,&m,0,0);
        UINT classes=0;context->PSGetShader(&ticket.shader,nullptr,&classes);if(classes)return S_FALSE;
        ID3D11Buffer* savedBuffers[2]{};context->PSGetConstantBuffers(12,2,savedBuffers);for(UINT i=0;i<2;i++)ticket.constants[i].Attach(savedBuffers[i]);
        ID3D11ShaderResourceView* savedViews[6]{};context->PSGetShaderResources(100,6,savedViews);for(UINT i=0;i<6;i++)ticket.resources[i].Attach(savedViews[i]);
        ID3D11SamplerState* savedSamplers[2]{};context->PSGetSamplers(12,2,savedSamplers);for(UINT i=0;i<2;i++)ticket.samplers[i].Attach(savedSamplers[i]);
        ID3D11Buffer* buffers[]={parameters.Get(),material.Get()};context->PSSetConstantBuffers(12,2,buffers);
        ID3D11ShaderResourceView* views[]={climate.Get(),world.get(),masks[asset].Get(),terrainTableView.Get(),plantControl,plantDetail};context->PSSetShaderResources(100,6,views);
        ID3D11SamplerState* samplers[]={clamp.Get(),plantWrap.Get()};context->PSSetSamplers(12,2,samplers);context->PSSetShader(kind==1?treeColor.Get():kind==2?treeDepth.Get():kind==3?land.Get():distantLand.Get(),nullptr,0);
        ticket.changed=true;ticket.kind=kind;return S_OK;
    }
    bool leave(ID3D11DeviceContext* context,SeasonSceneTicket& ticket){
        if(!ticket.changed)return true;
        ID3D11Buffer* buffers[2];ID3D11ShaderResourceView* views[6];for(UINT i=0;i<2;i++)buffers[i]=ticket.constants[i].Get();for(UINT i=0;i<6;i++)views[i]=ticket.resources[i].Get();
        ID3D11SamplerState* samplers[]={ticket.samplers[0].Get(),ticket.samplers[1].Get()};context->PSSetShader(ticket.shader.Get(),nullptr,0);context->PSSetConstantBuffers(12,2,buffers);
        context->PSSetShaderResources(100,6,views);context->PSSetSamplers(12,2,samplers);
        ComPtr<ID3D11PixelShader> ps;context->PSGetShader(&ps,nullptr,nullptr);bool valid=ps==ticket.shader;
        ID3D11Buffer* checkBuffers[2]{};ID3D11ShaderResourceView* checkViews[6]{};context->PSGetConstantBuffers(12,2,checkBuffers);context->PSGetShaderResources(100,6,checkViews);
        for(UINT i=0;i<2;i++){valid=valid&&buffers[i]==checkBuffers[i];if(checkBuffers[i])checkBuffers[i]->Release();}
        for(UINT i=0;i<6;i++){valid=valid&&views[i]==checkViews[i];if(checkViews[i])checkViews[i]->Release();}
        ID3D11SamplerState* checkSamplers[2]{};context->PSGetSamplers(12,2,checkSamplers);
        for(UINT i=0;i<2;i++){valid=valid&&samplers[i]==checkSamplers[i];if(checkSamplers[i])checkSamplers[i]->Release();}return valid;
    }
    uint64_t gpuBytes()const{
        uint64_t total=parameters?SeasonSceneData::MaskBytes+SeasonSceneData::ClimateBytes+64+16+2*144:0;
        if(terrainTable){D3D11_BUFFER_DESC b{};terrainTable->GetDesc(&b);total+=b.ByteWidth;}
        D3D11_TEXTURE2D_DESC d{};if(sceneCopy){sceneCopy->GetDesc(&d);total+=uint64_t(d.Width)*d.Height*4;}
        if(depthCopy){depthCopy->GetDesc(&d);total+=uint64_t(d.Width)*d.Height*(d.Format==DXGI_FORMAT_R32G8X24_TYPELESS?8:4);}return total+rice.gpuBytes();
    }
    HRESULT weather(ID3D11DeviceContext* context,const TreeColorParameters& p,ID3D11RenderTargetView* target);
#undef SEASON_CREATE
};

inline HRESULT SeasonSceneGpu::weather(ID3D11DeviceContext* context,const TreeColorParameters& p,ID3D11RenderTargetView* target){
    if(!parameters||!sceneDepth||!target){rice.discardFrame();return S_FALSE;}
    ComPtr<ID3D11Device> device;context->GetDevice(&device);
    ComPtr<ID3D11Resource> colorResource,depthResource;target->GetResource(&colorResource);sceneDepth->GetResource(&depthResource);
    ComPtr<ID3D11Texture2D> originalColor,originalDepth;
    if(FAILED(colorResource.As(&originalColor))||FAILED(depthResource.As(&originalDepth)))return S_FALSE;
    D3D11_TEXTURE2D_DESC cd{},dd{};originalColor->GetDesc(&cd);originalDepth->GetDesc(&dd);
    if(cd.Width!=dd.Width||cd.Height!=dd.Height||cd.Width!=colorViewport.Width||cd.Height!=colorViewport.Height||
       cd.ArraySize!=1||dd.ArraySize!=1||cd.MipLevels!=1||dd.MipLevels!=1||cd.SampleDesc.Count!=1||dd.SampleDesc.Count!=1)return S_FALSE;
    if(cd.Format!=DXGI_FORMAT_R8G8B8A8_UNORM&&cd.Format!=DXGI_FORMAT_B8G8R8A8_UNORM&&
       cd.Format!=DXGI_FORMAT_R8G8B8A8_UNORM_SRGB&&cd.Format!=DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)return S_FALSE;
    DXGI_FORMAT depthFormat{},depthViewFormat{};
    if(dd.Format==DXGI_FORMAT_R24G8_TYPELESS||dd.Format==DXGI_FORMAT_D24_UNORM_S8_UINT){depthFormat=DXGI_FORMAT_R24G8_TYPELESS;depthViewFormat=DXGI_FORMAT_R24_UNORM_X8_TYPELESS;}
    else if(dd.Format==DXGI_FORMAT_R32_TYPELESS||dd.Format==DXGI_FORMAT_D32_FLOAT){depthFormat=DXGI_FORMAT_R32_TYPELESS;depthViewFormat=DXGI_FORMAT_R32_FLOAT;}
    else if(dd.Format==DXGI_FORMAT_R32G8X24_TYPELESS||dd.Format==DXGI_FORMAT_D32_FLOAT_S8X24_UINT){depthFormat=DXGI_FORMAT_R32G8X24_TYPELESS;depthViewFormat=DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;}
    else return S_FALSE;
    D3D11_TEXTURE2D_DESC previous{};if(sceneCopy)sceneCopy->GetDesc(&previous);
    D3D11_TEXTURE2D_DESC previousDepth{};if(depthCopy)depthCopy->GetDesc(&previousDepth);
    if(previous.Width!=cd.Width||previous.Height!=cd.Height||previous.Format!=cd.Format||previousDepth.Format!=depthFormat){
        sceneView.Reset();depthView.Reset();riceDepth.Reset();sceneCopy.Reset();depthCopy.Reset();
        auto owned=cd;owned.Usage=D3D11_USAGE_DEFAULT;owned.BindFlags=D3D11_BIND_SHADER_RESOURCE;owned.CPUAccessFlags=0;owned.MiscFlags=0;
        auto hr=device->CreateTexture2D(&owned,nullptr,&sceneCopy);if(FAILED(hr))return hr;
        hr=device->CreateShaderResourceView(sceneCopy.Get(),nullptr,&sceneView);if(FAILED(hr))return hr;
        owned=dd;owned.Format=depthFormat;owned.Usage=D3D11_USAGE_DEFAULT;owned.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_DEPTH_STENCIL;owned.CPUAccessFlags=0;owned.MiscFlags=0;
        hr=device->CreateTexture2D(&owned,nullptr,&depthCopy);if(FAILED(hr))return hr;
        D3D11_SHADER_RESOURCE_VIEW_DESC vd{};vd.Format=depthViewFormat;vd.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;vd.Texture2D.MipLevels=1;
        hr=device->CreateShaderResourceView(depthCopy.Get(),&vd,&depthView);if(FAILED(hr))return hr;
        D3D11_DEPTH_STENCIL_VIEW_DESC dv{};dv.ViewDimension=D3D11_DSV_DIMENSION_TEXTURE2D;
        dv.Format=depthFormat==DXGI_FORMAT_R24G8_TYPELESS?DXGI_FORMAT_D24_UNORM_S8_UINT:
            depthFormat==DXGI_FORMAT_R32_TYPELESS?DXGI_FORMAT_D32_FLOAT:DXGI_FORMAT_D32_FLOAT_S8X24_UINT;
        hr=device->CreateDepthStencilView(depthCopy.Get(),&dv,&riceDepth);if(FAILED(hr))return hr;
    }
    updateParameters(context,p,colorViewport);
    struct Weather {float minDepth,inverseSpan;uint32_t direction;float padding;} weather{colorViewport.MinDepth,1/(colorViewport.MaxDepth-colorViewport.MinDepth),depthDirection,0};
    context->UpdateSubresource(material.Get(),0,nullptr,&weather,0,0);
    {
        seasons::PipelineScope scope(context,GetCurrentThreadId(),weatherState.Get());
        context->CopyResource(sceneCopy.Get(),originalColor.Get());context->CopyResource(depthCopy.Get(),originalDepth.Get());
        context->RSSetState(weatherRaster.Get());context->RSSetViewports(1,&colorViewport);
        riceCapture.begin(context,sceneCopy.Get(),depthCopy.Get(),colorWorld.get(),captureParameters.data(),&weather,p.year,p.month,p.day);
        auto riceResult=rice.draw(context,p,target,riceDepth.Get(),depthDirection,parameters.Get(),colorWorld.get());if(FAILED(riceResult))return riceResult;
        riceCapture.afterPass(context,originalColor.Get());
        // Weather capture excludes the vegetation pass. Heat distortion has
        // been removed; WCAP v2 retains an unchanged historical haze stage.
        context->CopyResource(sceneCopy.Get(),originalColor.Get());
        capture.begin(context,sceneCopy.Get(),depthCopy.Get(),colorWorld.get(),captureParameters.data(),&weather,p.year,p.month,p.day);
        context->OMSetRenderTargets(1,&target,nullptr);context->OMSetDepthStencilState(noDepth.Get(),0);context->OMSetBlendState(nullptr,nullptr,UINT_MAX);
        context->RSSetState(weatherRaster.Get());context->RSSetViewports(1,&colorViewport);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ID3D11Buffer* buffers[]={parameters.Get(),material.Get()};context->PSSetConstantBuffers(12,2,buffers);context->VSSetConstantBuffers(12,2,buffers);
        ID3D11ShaderResourceView* views[]={sceneView.Get(),depthView.Get()};context->PSSetShaderResources(0,2,views);context->VSSetShaderResources(1,1,&views[1]);
        ID3D11ShaderResourceView* privateViews[]={climate.Get(),colorWorld.get()};context->PSSetShaderResources(100,2,privateViews);context->VSSetShaderResources(100,2,privateViews);
        auto* sampler=clamp.Get();context->PSSetSamplers(12,1,&sampler);context->VSSetSamplers(12,1,&sampler);
        capture.afterHaze(context,originalColor.Get());
        context->OMSetBlendState(snowBlend.Get(),nullptr,UINT_MAX);context->VSSetShader(snowVertex.Get(),nullptr,0);context->PSSetShader(snow.Get(),nullptr,0);context->DrawInstanced(6,4096,0,0);
        capture.afterSnow(context,originalColor.Get());
        ID3D11ShaderResourceView* empty[2]{};context->PSSetShaderResources(0,2,empty);context->VSSetShaderResources(1,1,empty);
    }
    // The complete D3D11.1 pipeline is restored by the scope, including OM,
    // predication, tessellation, UAVs and private bindings.
    return device->GetDeviceRemovedReason();
}
