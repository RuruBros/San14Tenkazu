#define SEASON_BRIDGE_FIXTURE
#include "SeasonBridge.cpp"
#include <fstream>
#include <iostream>
#include <chrono>
namespace {
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void hr(HRESULT value){if(FAILED(value)){std::cerr<<"HRESULT="<<std::hex<<value<<std::dec<<"\n";throw std::runtime_error("Scene fixture GPU API failed");}}
ComPtr<ID3DBlob> compile(const char* source,const char* entry,const char* profile){
    ComPtr<ID3DBlob> code,errors;auto result=D3DCompile(source,strlen(source),nullptr,nullptr,nullptr,entry,profile,D3DCOMPILE_ENABLE_STRICTNESS,0,&code,&errors);
    if(errors)std::cerr<<static_cast<const char*>(errors->GetBufferPointer());hr(result);return code;
}
}
int main(int argc,char** argv){try{
    require(argc==2||argc==3,"Usage: SeasonSceneChecks scene-data.bin [managed-calendar-vectors.txt]");SeasonSceneData data;
    std::ifstream input(argv[1],std::ios::binary);data.bytes.assign(std::istreambuf_iterator<char>(input),std::istreambuf_iterator<char>());
    auto started=std::chrono::steady_clock::now();auto prepared=data.prepare();if(!data.compileDiagnostic.empty())std::cerr<<data.compileDiagnostic;hr(prepared);
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    constexpr UINT width=128,height=64;SeasonSceneGpu scene(data);
    BYTE gameState[0x80]{};Bootstrap calendarInput{};calendarInput.state=reinterpret_cast<uint64_t>(gameState);
    Session calendarSession(calendarInput,true);
    auto request=std::make_shared<TreeColorParameters>(TreeColorParameters{17,195,2,11,{1.f/3,0,0,2.f/3},true});
    calendarSession.pending.store(request);
    auto setDate=[&](uint32_t y,uint32_t m,uint32_t d){uint32_t v=y|(m<<16)|(d<<24);std::memcpy(gameState+0x34,&v,4);};
    size_t calendarCases=0;
    if(argc==3){
        std::ifstream vectors(argv[2]);require(bool(vectors),"Managed calendar vectors missing");uint32_t y,m,d;double expected[4];
        while(vectors>>y>>m>>d>>expected[0]>>expected[1]>>expected[2]>>expected[3]){
            setDate(y,m,d);TreeColorParameters rendered{};require(renderParameters(calendarSession,request,rendered),"Full effect dropped between controller updates");
            require(rendered.year==y&&rendered.month==m&&rendered.day==d&&rendered.epoch==request->epoch,"Actual calendar or request identity changed");
            for(UINT i=0;i<4;i++)require(std::abs(rendered.weights[i]-expected[i])<1e-6,"Native weights differ from managed calendar");++calendarCases;
        }
        require(vectors.eof()&&calendarCases==720,"Managed calendar vector count differs");
        for(auto invalid:std::array<std::array<uint32_t,3>,6>{{{100,2,1},{400,2,1},{195,0,1},{195,13,1},{195,2,0},{195,2,31}}}){
            setDate(invalid[0],invalid[1],invalid[2]);TreeColorParameters p{};require(!renderParameters(calendarSession,request,p),"Invalid live date was accepted");
        }
        setDate(195,3,1);auto scoped=std::make_shared<TreeColorParameters>(*request);scoped->full=false;TreeColorParameters p{};
        require(!renderParameters(calendarSession,scoped,p),"Scoped date guard was weakened");
        require(!renderParameters(calendarSession,{},p),"Unrequested rendering was admitted");
        calendarSession.admission=false;require(!renderRequestActive(calendarSession,request),"Stopped session admitted rendering");calendarSession.admission=true;
    }
    const float* climate=reinterpret_cast<const float*>(data.bytes.data()+data.header()[5]);
    UINT chosen=0;float best=-1;
    for(UINT i=0;i<256*256;i++){
        const float* c=climate+(256*256+i)*2;float snow=(c[0]<0?1.f:0.f)*std::min(c[1]/40,1.f);
        if(snow>best){best=snow;chosen=i;}
    }
    require(best>0,"The authentic atlas has no cold winter fixture");float x=(chosen%256+.5f)*800,z=(chosen/256+.5f)*800;
    // A wide top-down view covers the production 1600-world-unit snow cells.
    // Positive height is closer under LESS depth; all 800..7800 heights fit.
    float matrix[16]={1.f/16000,0,0,0,0,0,-.00003f,0,0,-1.f/8000,0,0,-x/16000-1,z/8000+1,.5f,1};
    auto buffer=[&](UINT size,const void* contents){D3D11_BUFFER_DESC bd{size,D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA initial{contents,0,0};ComPtr<ID3D11Buffer> b;hr(device->CreateBuffer(&bd,contents?&initial:nullptr,&b));return b;};
    auto camera=buffer(64,matrix),sentinel=buffer(64,matrix);auto* raw=camera.Get();context->VSSetConstantBuffers(0,1,&raw);
    raw=sentinel.Get();context->PSSetConstantBuffers(12,1,&raw);
    std::array<ComPtr<ID3D11Texture2D>,5> colors;std::array<ComPtr<ID3D11RenderTargetView>,5> targets;ID3D11RenderTargetView* rtvs[5];
    D3D11_TEXTURE2D_DESC td{width,height,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    for(UINT i=0;i<5;i++){hr(device->CreateTexture2D(&td,nullptr,&colors[i]));hr(device->CreateRenderTargetView(colors[i].Get(),nullptr,&targets[i]));rtvs[i]=targets[i].Get();}
    ComPtr<ID3D11Texture2D> staging,depth;ComPtr<ID3D11DepthStencilView> dsv;
    td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&td,nullptr,&staging));
    td.Format=DXGI_FORMAT_D32_FLOAT;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_DEPTH_STENCIL;td.CPUAccessFlags=0;hr(device->CreateTexture2D(&td,nullptr,&depth));hr(device->CreateDepthStencilView(depth.Get(),nullptr,&dsv));
    D3D11_DEPTH_STENCIL_DESC dd{};dd.DepthEnable=TRUE;dd.DepthFunc=D3D11_COMPARISON_LESS;dd.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL;
    ComPtr<ID3D11DepthStencilState> depthState;hr(device->CreateDepthStencilState(&dd,&depthState));context->OMSetDepthStencilState(depthState.Get(),23);
    D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;ComPtr<ID3D11RasterizerState> raster;hr(device->CreateRasterizerState(&rd,&raster));context->RSSetState(raster.Get());
    D3D11_VIEWPORT vp{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&vp);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    const char* vertex=R"(cbuffer Fixture:register(b1){float4 Origin;};struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float4 b:TEXCOORD1;float4 c:TEXCOORD2;float4 w:TEXCOORD3;float3 t:TEXCOORD4;float3 n:TEXCOORD5;float2 uv:TEXCOORD6;};O main(uint id:SV_VertexID){float2 v=float2((id<<1)&2,id&2);O o;o.p=float4(v*float2(2,-2)+float2(-1,1),.5,1);o.a=float4(0,0,4,16);o.b=float4(1000,0,0,1);o.c=float4(v.x,0,v.y,1);o.w=float4(v.x*Origin.y+Origin.x,0,v.y*Origin.w+Origin.z,1);o.t=float3(1,0,0);o.n=float3(0,1,0);o.uv=v;return o;})";
    auto vsCode=compile(vertex,"main","vs_5_0");ComPtr<ID3D11VertexShader> vs;hr(device->CreateVertexShader(vsCode->GetBufferPointer(),vsCode->GetBufferSize(),nullptr,&vs));context->VSSetShader(vs.Get(),nullptr,0);
    float origin[4]={x,32000,z,16000};auto originCb=buffer(16,origin);raw=originCb.Get();context->VSSetConstantBuffers(1,1,&raw);
    ComPtr<ID3D11PixelShader> baseline;hr(device->CreatePixelShader(data.bytes.data()+data.header()[11],data.detailedShaderBytes(),nullptr,&baseline));context->PSSetShader(baseline.Get(),nullptr,0);
    std::array<float,1027*4> cb{};cb[4]=cb[5]=cb[6]=1;cb[7]=2;cb[8]=cb[13]=cb[18]=1;cb[21]=4096;cb[22]=50;cb[24]=.6f;cb[26]=1000;cb[27]=4000;
    for(UINT i=0;i<255;i++){cb[(262+i)*4]=.5f;cb[(517+i)*4]=100;cb[(772+i)*4]=120;}
    auto constants=buffer(sizeof(cb),cb.data());raw=constants.Get();context->PSSetConstantBuffers(0,1,&raw);
    std::array<ComPtr<ID3D11ShaderResourceView>,8> inputs;
    for(UINT slot=3;slot<=10;slot++){
        UINT layers=slot>=7&&slot<=9?19:1;float pixel[4]={.2f,.4f,.1f,.6f};if(slot==3)pixel[0]=pixel[1]=pixel[2]=0;if(slot==8){pixel[0]=pixel[1]=.5f;pixel[2]=1;}if(slot==10)pixel[0]=pixel[1]=pixel[2]=1;
        D3D11_SUBRESOURCE_DATA init[19];for(auto& d:init)d={pixel,16,0};D3D11_TEXTURE2D_DESC d{1,1,1,layers,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};ComPtr<ID3D11Texture2D> t;hr(device->CreateTexture2D(&d,init,&t));hr(device->CreateShaderResourceView(t.Get(),nullptr,&inputs[slot-3]));auto* v=inputs[slot-3].Get();context->PSSetShaderResources(slot,1,&v);
    }
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MaxLOD=D3D11_FLOAT32_MAX;
    ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));auto* ss=sampler.Get();for(UINT slot=3;slot<=12;slot++)context->PSSetSamplers(slot,1,&ss);
    auto pixels=[&]{context->CopyResource(staging.Get(),colors[0].Get());D3D11_MAPPED_SUBRESOURCE m{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&m));std::vector<BYTE> result(width*height*4);for(UINT y=0;y<height;y++)memcpy(result.data()+y*width*4,static_cast<BYTE*>(m.pData)+y*m.RowPitch,width*4);context->Unmap(staging.Get(),0);return result;};
    auto setup=[&]{float clear[4]={0,0,0,0};for(auto& r:targets)context->ClearRenderTargetView(r.Get(),clear);context->ClearDepthStencilView(dsv.Get(),D3D11_CLEAR_DEPTH,1,0);context->OMSetRenderTargets(5,rtvs,dsv.Get());};
    setup();context->Draw(3,0);auto original=pixels();std::vector<std::vector<BYTE>> seasons;UINT weatherChanged=0;
    {TreeColorParameters p{1,195,2,1,{0,0,0,1}};p.full=true;SeasonSceneTicket ticket;
        require(scene.enter(context.Get(),p,5,0,ticket)==S_FALSE&&!ticket.changed,"Distant terrain used an unavailable material table");}
    for(UINT season=0;season<4;season++){
        TreeColorParameters p{season+1,195,season==0?4u:season==1?7u:season==2?10u:2u,1,{}};p.weights[season]=1;p.full=true;
        setup();SeasonSceneTicket ticket;require(scene.enter(context.Get(),p,3,0,ticket)==S_OK,"Terrain entry rejected");context->Draw(3,0);require(scene.leave(context.Get(),ticket),"Terrain shader/binding restoration failed");seasons.push_back(pixels());
        require(seasons.back()!=original,"Terrain season has no effect in the independent fixture");
        ComPtr<ID3D11Buffer> saved;context->PSGetConstantBuffers(12,1,&saved);require(saved==sentinel,"Private binding sentinel changed");
        for(UINT kind:{1u,2u}){SeasonSceneTicket tree;require(scene.enter(context.Get(),p,kind,0,tree)==S_OK,"Tree pipeline entry rejected");require(scene.leave(context.Get(),tree),"Tree pipeline restoration failed");}
        context->OMSetRenderTargets(1,rtvs,nullptr);auto before=pixels();require(scene.weather(context.Get(),p,targets[0].Get())==S_OK,"Weather pipeline rejected");auto after=pixels();
        if(before!=after)weatherChanged++;if(season==3)require(before!=after,"Winter particles did not draw");
        ComPtr<ID3D11PixelShader> restored;context->PSGetShader(&restored,nullptr,nullptr);require(restored==baseline,"Weather shader restoration failed");
        ComPtr<ID3D11VertexShader> restoredVs;context->VSGetShader(&restoredVs,nullptr,nullptr);require(restoredVs==vs,"Weather vertex shader restoration failed");
        ComPtr<ID3D11DepthStencilState> restoredDepth;UINT stencil{};context->OMGetDepthStencilState(&restoredDepth,&stencil);require(restoredDepth==depthState&&stencil==23,"Weather depth state restoration failed");
        setup();context->Draw(3,0);require(pixels()==original,"OFF original pixels differ");
    }
    for(UINT i=0;i<4;i++)for(UINT j=0;j<i;j++)require(seasons[i]!=seasons[j],"Four-season terrain outputs are not distinct");
    // The distant vertex contract has no world-position interpolator. Its
    // pixel depth must reconstruct the same world coordinates as detailed land.
    const char* distantVertex=R"(struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float3 t:TEXCOORD1;float3 n:TEXCOORD2;float2 uv:TEXCOORD3;};
O main(uint id:SV_VertexID){float2 v=float2((id<<1)&2,id&2);O o;o.p=float4(v*float2(2,-2)+float2(-1,1),.5,1);
o.a=float4(1000,0,0,1);o.t=float3(1,0,0);o.n=float3(0,1,0);o.uv=v;return o;})";
    auto farCode=compile(distantVertex,"main","vs_5_0");ComPtr<ID3D11VertexShader> farVs;hr(device->CreateVertexShader(farCode->GetBufferPointer(),farCode->GetBufferSize(),nullptr,&farVs));
    ComPtr<ID3D11PixelShader> farBaseline;auto detailed=data.detailedShaderBytes();hr(device->CreatePixelShader(data.bytes.data()+data.header()[11]+detailed,data.header()[12]-detailed,nullptr,&farBaseline));
    std::array<float,261*4> farCb{};farCb[0]=farCb[1]=farCb[2]=1;farCb[3]=2;farCb[4]=farCb[9]=farCb[14]=1;farCb[17]=4096;farCb[18]=50;farCb[20]=.6f;
    for(UINT i=0;i<255;i++)farCb[(6+i)*4]=100;
    auto farConstants=buffer(sizeof(farCb),farCb.data());raw=farConstants.Get();context->PSSetConstantBuffers(0,1,&raw);
    context->VSSetShader(farVs.Get(),nullptr,0);context->PSSetShader(farBaseline.Get(),nullptr,0);
    setup();context->Draw(3,0);auto farOriginal=pixels();
    std::vector<std::vector<BYTE>> farSeasons;
    for(UINT season=0;season<4;season++){
        TreeColorParameters p{season+5,195,season==0?4u:season==1?7u:season==2?10u:2u,1,{}};p.weights[season]=1;p.full=true;
        SeasonSceneTicket farTicket;setup();require(scene.enter(context.Get(),p,5,0,farTicket)==S_OK,"Distant terrain entry rejected");context->Draw(3,0);
        require(scene.leave(context.Get(),farTicket),"Distant terrain bindings not restored");farSeasons.push_back(pixels());
        require(farSeasons.back()!=farOriginal,"Distant terrain season has no effect");
        setup();context->Draw(3,0);require(pixels()==farOriginal,"Distant terrain OFF did not restore original pixels");
    }
    for(UINT i=0;i<4;i++)for(UINT j=0;j<i;j++)require(farSeasons[i]!=farSeasons[j],"Four-season distant outputs are not distinct");
    UINT minimumSummerWind=width*height,summerZoomCases=0,maximumSummerLodError=0;
    TreeColorParameters summer{19,195,7,1,{0,1,0,0},true};
    auto summerFrame=[&](UINT kind,float seconds,bool foliage=true,ID3D11ShaderResourceView* detail=nullptr){
        auto* currentCb=kind==3?constants.Get():farConstants.Get();context->PSSetConstantBuffers(0,1,&currentCb);
        context->VSSetShader(kind==3?vs.Get():farVs.Get(),nullptr,0);context->PSSetShader(kind==3?baseline.Get():farBaseline.Get(),nullptr,0);
        // Sentinels exercise restoration of the two new private source slots.
        ID3D11ShaderResourceView* sentinels[]={inputs[1].Get(),inputs[2].Get()};context->PSSetShaderResources(104,2,sentinels);
        setup();scene.fixtureTime(seconds);SeasonSceneTicket ticket;
        require(scene.enter(context.Get(),summer,kind,0,ticket,foliage?inputs[0].Get():nullptr,
            foliage?(detail?detail:inputs[4].Get()):nullptr)==S_OK,"Summer terrain entry failed");
        context->Draw(3,0);require(scene.leave(context.Get(),ticket),"Summer private source/sampler bindings not restored");return pixels();
    };
    for(float span:{10000.f,32000.f,96000.f}){
        float zoomMatrix[16];memcpy(zoomMatrix,matrix,sizeof(matrix));zoomMatrix[0]=2/span;zoomMatrix[9]=-4/span;
        zoomMatrix[12]=-2*x/span-1;zoomMatrix[13]=4*z/span+1;
        context->UpdateSubresource(camera.Get(),0,nullptr,zoomMatrix,0,0);
        float zoomOrigin[]={x,span,z,span*.5f};context->UpdateSubresource(originCb.Get(),0,nullptr,zoomOrigin,0,0);
        auto near0=summerFrame(3,0),far0=summerFrame(5,0),near1=summerFrame(3,2),far1=summerFrame(5,2);
        for(UINT kind=0;kind<2;kind++){
            const auto& first=kind?far0:near0;const auto& second=kind?far1:near1;UINT moving=0;
            for(UINT i=0;i<width*height;i++){
                require(first[i*4+3]==second[i*4+3],"Summer canopy changes alpha");moving+=memcmp(first.data()+i*4,second.data()+i*4,3)!=0;
                for(UINT c=0;c<3;c++)maximumSummerLodError=std::max(maximumSummerLodError,UINT(abs(int(near0[i*4+c])-int(far0[i*4+c]))));
            }
            require(moving>width*height/4,"Summer wind disappears at the tested zoom");minimumSummerWind=std::min(minimumSummerWind,moving);++summerZoomCases;
            std::cerr<<"SummerWorldSpan="<<span<<"; terrainKind="<<(kind?5:3)<<"; windPixels="<<moving<<"\n";
        }
        // Summer composition must not warp any original scene pixels. There
        // are no collected plants here, and summer has no snowfall.
        context->OMSetRenderTargets(1,rtvs,nullptr);auto beforeWeather=pixels();hr(scene.weather(context.Get(),summer,targets[0].Get()));
        require(pixels()==beforeWeather,"Summer weather distorts the scene after haze removal");
    }
    require(maximumSummerLodError<=2,"Summer canopy has a detailed/distant seam");
    context->UpdateSubresource(camera.Get(),0,nullptr,matrix,0,0);context->UpdateSubresource(originCb.Get(),0,nullptr,origin,0,0);
    scene.fixtureTime(0);
    for(uint32_t layer:{1u,4u,6u,18u}){
        memcpy(&cb[7*4],&layer,4);context->UpdateSubresource(constants.Get(),0,nullptr,cb.data(),0,0);
        require(summerFrame(3,0)==summerFrame(3,0,false),"Summer canopy leaked into excluded terrain");
    }
    uint32_t farmLayer=5;memcpy(&cb[7*4],&farmLayer,4);context->UpdateSubresource(constants.Get(),0,nullptr,cb.data(),0,0);
    float bankPixel[]={.8f,.8f,.75f,1};D3D11_SUBRESOURCE_DATA bankData[19];for(auto& initial:bankData)initial={bankPixel,16,0};
    D3D11_TEXTURE2D_DESC bankDesc{1,1,1,19,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
    ComPtr<ID3D11Texture2D> bank;ComPtr<ID3D11ShaderResourceView> bankView;hr(device->CreateTexture2D(&bankDesc,bankData,&bank));hr(device->CreateShaderResourceView(bank.Get(),nullptr,&bankView));
    require(summerFrame(3,0,true,bankView.Get())==summerFrame(3,0,false),"Summer canopy colored pale farm banks");
    uint32_t restoredLayer=0;memcpy(&cb[7*4],&restoredLayer,4);context->UpdateSubresource(constants.Get(),0,nullptr,cb.data(),0,0);
    std::cerr<<"SummerZoomCases="<<summerZoomCases<<"; minimumWindPixels="<<minimumSummerWind<<"; maximumLodError="<<maximumSummerLodError<<"; summerWeatherUnchanged=true\n";
    const auto& farWinter=farSeasons[3];
    // Keep one stale controller request while the actual clock advances across
    // winter/spring, a year boundary, and a backward load. Compare GPU pixels to
    // an independently requested current-date draw for both terrain contracts.
    constexpr uint32_t dateSteps[][3]={{195,2,11},{195,2,12},{195,2,16},{195,2,29},{195,2,30},{195,3,1},{195,12,30},{196,1,1},{195,2,11}};
    size_t continuousDraws=0;
    for(const auto& date:dateSteps){
        setDate(date[0],date[1],date[2]);TreeColorParameters actual{};
        require(renderParameters(calendarSession,request,actual),"Calendar gap rejected a terrain frame");
        auto fresh=std::make_shared<TreeColorParameters>(actual);fresh->epoch=100+continuousDraws;
        TreeColorParameters acknowledged{};require(renderParameters(calendarSession,fresh,acknowledged),"Current-date fixture failed");
        for(UINT kind:{3u,5u}){
            raw=kind==3?constants.Get():farConstants.Get();context->PSSetConstantBuffers(0,1,&raw);
            context->VSSetShader(kind==3?vs.Get():farVs.Get(),nullptr,0);context->PSSetShader(kind==3?baseline.Get():farBaseline.Get(),nullptr,0);
            auto drawDate=[&](const TreeColorParameters& p){setup();SeasonSceneTicket t;require(scene.enter(context.Get(),p,kind,0,t)==S_OK,"Calendar terrain entry failed");context->Draw(3,0);require(scene.leave(context.Get(),t),"Calendar GPU state not restored");return pixels();};
            auto expected=drawDate(acknowledged),observed=drawDate(actual);
            require(observed==expected,"Stale controller changed actual-date terrain pixels");
            require(observed!=(kind==3?original:farOriginal),"Terrain reverted to OFF while advancing dates");++continuousDraws;
        }
        completedMaterialPass(calendarSession,actual,4);
        require(calendarSession.fullClock==packedDate(actual)&&calendarSession.fullEpoch==request->epoch,"Frame pass calendar was not updated");
        require(calendarSession.completedEpoch==0,"Unacknowledged request manufactured a completion");
    }
    raw=farConstants.Get();context->PSSetConstantBuffers(0,1,&raw);context->VSSetShader(farVs.Get(),nullptr,0);context->PSSetShader(farBaseline.Get(),nullptr,0);
    auto climateAt=[&](double wx,double wz,UINT channel){
        double px=wx/800-.5,pz=wz/800-.5;int ix=int(std::floor(px)),iz=int(std::floor(pz));double fx=px-ix,fz=pz-iz;
        auto texel=[&](int xx,int zz){return climate[(256*256+std::clamp(zz,0,255)*256+std::clamp(xx,0,255))*2+channel];};
        return (texel(ix,iz)*(1-fx)+texel(ix+1,iz)*fx)*(1-fz)+(texel(ix,iz+1)*(1-fx)+texel(ix+1,iz+1)*fx)*fz;};
    double maxTransitionError=0;
    for(UINT yy=0;yy<height;yy++)for(UINT xx=0;xx<width;xx++){
        double wx=x+(xx+.5)*32000/width,wz=z+(yy+.5)*16000/height;
        double t=std::clamp(climateAt(wx,wz,0)/8,0.0,1.0),snow=(1-t*t*(3-2*t))*(.3+.7*std::clamp(climateAt(wx,wz,1)/40,0.0,1.0));
        for(UINT c=0;c<3;c++){size_t at=(yy*width+xx)*4+c;
            double expected=(int(farOriginal[at])-int(original[at]))*(1-snow),actual=int(farWinter[at])-int(seasons[3][at]);
            maxTransitionError=std::max(maxTransitionError,std::abs(actual-expected));}
    }
    require(maxTransitionError<=2,"Detailed/distant snow differs at the same world position");
    setup();context->Draw(3,0);require(pixels()==farOriginal,"Distant terrain OFF did not restore original pixels");
    scene.saveCaptureAfterDrain();
    if(data.header()[1]==4){
        std::ifstream captured(reinterpret_cast<const wchar_t*>(data.bytes.data()+data.header()[14]),std::ios::binary);
        std::vector<BYTE> bytes{std::istreambuf_iterator<char>(captured),std::istreambuf_iterator<char>()};
        require(bytes.size()>=16,"Capture output is missing");auto* h=reinterpret_cast<const uint32_t*>(bytes.data());
        constexpr size_t frameBytes=32+80+144+width*height*16;
        require(h[0]==0x57434150&&h[1]==2&&h[2]>=1&&h[2]<=2&&h[3]==0&&bytes.size()==16+h[2]*frameBytes,"Capture framing/error/count differs");
        // Spring has no haze/snow; compare all three actual capture stages with
        // the separately read terrain fixture, not just a draw receipt.
        size_t at=16+32+80+144+width*height*4;
        for(UINT stage=0;stage<3;stage++)require(std::memcmp(bytes.data()+at+stage*width*height*4,seasons[0].data(),width*height*4)==0,"Captured GPU pixels differ");
    }
    std::cout<<"{\"seasons\":4,\"calendarCases\":"<<calendarCases<<",\"continuousDateDraws\":"<<continuousDraws<<",\"weatherChangedCases\":"<<weatherChanged<<",\"distantSnowPixels\":"<<width*height<<",\"maxLodSnowErrorByte\":"<<maxTransitionError<<",\"gpuPayloadBytes\":"<<scene.gpuBytes()<<",\"restoredOriginalPixels\":"<<2*width*height<<",\"elapsedSeconds\":"<<std::chrono::duration<double>(std::chrono::steady_clock::now()-started).count()<<",\"gameAccess\":false,\"gameScreenVerified\":false,\"passed\":true}\n";return 0;
}catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}}
