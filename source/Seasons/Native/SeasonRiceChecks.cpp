#define SEASON_BRIDGE_FIXTURE
#include "SeasonBridge.cpp"
#include <iostream>
#include <fstream>
#include <set>
#include <thread>
namespace {
void require(bool b,const char* text){if(!b)throw std::runtime_error(text);}
void checked(HRESULT hr){if(FAILED(hr)){std::cerr<<"HRESULT="<<std::hex<<hr<<std::dec<<"\n";throw std::runtime_error("Rice GPU check failed");}}
ComPtr<ID3DBlob> compile(const char* text,const char* entry,const char* target){ComPtr<ID3DBlob> code,errors;
    auto hr=D3DCompile(text,strlen(text),nullptr,nullptr,nullptr,entry,target,D3DCOMPILE_ENABLE_STRICTNESS,0,&code,&errors);
    if(errors)std::cerr<<static_cast<const char*>(errors->GetBufferPointer());checked(hr);return code;}
}
int main(int argc,char** argv){try{
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    bool hardware=false;const char* controlFile=nullptr;
    for(int i=1;i<argc;i++){std::string arg=argv[i];if(arg=="--hardware")hardware=true;
        else if(arg=="--control"&&i+1<argc)controlFile=argv[++i];else throw std::runtime_error("Unknown rice check argument");}
    checked(D3D11CreateDevice(nullptr,hardware?D3D_DRIVER_TYPE_HARDWARE:D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
    bool controlAuthenticated=false;
    if(controlFile){
        std::ifstream file(controlFile,std::ios::binary);std::vector<BYTE> data(std::istreambuf_iterator<char>(file),{});
        require(data.size()==16793604,"Canonical control payload size differs");
        auto controlTexture=[&]{D3D11_TEXTURE2D_DESC d{2049,2049,1,1,DXGI_FORMAT_R8G8B8A8_TYPELESS,{1,0},D3D11_USAGE_DEFAULT,
            D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET|D3D11_BIND_UNORDERED_ACCESS,0,0};
            D3D11_SUBRESOURCE_DATA init{data.data(),2049*4,0};ComPtr<ID3D11Texture2D> t;ComPtr<ID3D11ShaderResourceView> v;
            checked(device->CreateTexture2D(&d,&init,&t));D3D11_SHADER_RESOURCE_VIEW_DESC vd{};vd.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
            vd.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;vd.Texture2D.MipLevels=1;checked(device->CreateShaderResourceView(t.Get(),&vd,&v));return v;};
        SeasonGroundSources source;auto view=controlTexture();require(source.fixtureControl(context.Get(),view.Get())==S_OK&&
            source.riceControlReference()&&source.gpuBytes()==16793604,"Observed DEFAULT/SRV/RT/UAV control rejected");
        data[0]^=1;SeasonGroundSources changed;auto changedView=controlTexture();
        require(changed.fixtureControl(context.Get(),changedView.Get())==S_FALSE&&!changed.riceControlReference(),"Changed source hash was admitted");
        controlAuthenticated=true;
    }
    if(controlFile)std::cerr<<"CanonicalControlAuthenticated="<<(controlAuthenticated?"true":"false")<<"; mutatedHashRejected=true\n";
    auto buffer=[&](UINT bytes,UINT bind,const void* data,UINT misc=0,UINT stride=0){D3D11_BUFFER_DESC d{bytes,D3D11_USAGE_DEFAULT,bind,0,misc,stride};
        D3D11_SUBRESOURCE_DATA initial{data,0,0};ComPtr<ID3D11Buffer> b;checked(device->CreateBuffer(&d,data?&initial:nullptr,&b));return b;};
    auto texture=[&](DXGI_FORMAT format,UINT layers,const void* pixel,UINT pitch,D3D11_USAGE usage=D3D11_USAGE_IMMUTABLE){D3D11_TEXTURE2D_DESC d{1,1,1,layers,format,{1,0},usage,D3D11_BIND_SHADER_RESOURCE,0,0};
        D3D11_SUBRESOURCE_DATA init[19];for(auto& x:init)x={pixel,pitch,0};ComPtr<ID3D11Texture2D> t;ComPtr<ID3D11ShaderResourceView> v;
        checked(device->CreateTexture2D(&d,init,&t));checked(device->CreateShaderResourceView(t.Get(),nullptr,&v));return v;};
    constexpr UINT width=256,height=256;ComPtr<ID3D11Texture2D> image,staging,depth;ComPtr<ID3D11RenderTargetView> target;ComPtr<ID3D11DepthStencilView> dsv;
    D3D11_TEXTURE2D_DESC td{width,height,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_RENDER_TARGET,0,0};
    checked(device->CreateTexture2D(&td,nullptr,&image));checked(device->CreateRenderTargetView(image.Get(),nullptr,&target));td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    checked(device->CreateTexture2D(&td,nullptr,&staging));td.Usage=D3D11_USAGE_DEFAULT;td.CPUAccessFlags=0;td.BindFlags=D3D11_BIND_DEPTH_STENCIL;td.Format=DXGI_FORMAT_D32_FLOAT;
    checked(device->CreateTexture2D(&td,nullptr,&depth));checked(device->CreateDepthStencilView(depth.Get(),nullptr,&dsv));
    D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;ComPtr<ID3D11RasterizerState> raster;checked(device->CreateRasterizerState(&rd,&raster));
    D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);context->RSSetState(raster.Get());
    // An independent sloping plane exercises the exact original-VS output
    // contract. This fixture is not presented as proof of the game screen.
    const char* shader=R"(
        cbuffer C:register(b0){row_major float4x4 cameraMatrix;float4 settings;};
        struct O{float4 p:SV_Position;float4 a:TEXCOORD0;float4 b:TEXCOORD1;float4 c:TEXCOORD2;float4 world:TEXCOORD3;float3 tangent:TEXCOORD4;float3 normal:TEXCOORD5;float2 uv:TEXCOORD6;};
        O vs(float2 p:POSITION){O o;o.world=float4(p.x,20+p.x*.04+p.y*.02,p.y,1);o.p=mul(o.world,cameraMatrix);o.a=0;o.b=1;o.c=0;o.tangent=float3(1,0,0);o.normal=float3(0,settings.x,0);o.uv=p/800+.5;return o;}
        float4 ps():SV_Target{return float4(.1,.2,.3,.3);})";
    auto vsCode=compile(shader,"vs","vs_5_0"),psCode=compile(shader,"ps","ps_5_0");ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;
    checked(device->CreateVertexShader(vsCode->GetBufferPointer(),vsCode->GetBufferSize(),nullptr,&vs));checked(device->CreatePixelShader(psCode->GetBufferPointer(),psCode->GetBufferSize(),nullptr,&ps));
    D3D11_INPUT_ELEMENT_DESC element{"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0};ComPtr<ID3D11InputLayout> layout;
    checked(device->CreateInputLayout(&element,1,vsCode->GetBufferPointer(),vsCode->GetBufferSize(),&layout));
    float points[]={-400,-400,400,-400,400,400,-400,400};uint16_t indices[]={0,1,2,0,2,3};
    auto vertices=buffer(sizeof(points),D3D11_BIND_VERTEX_BUFFER,points),index=buffer(sizeof(indices),D3D11_BIND_INDEX_BUFFER,indices);
    float camera[]={1.f/600,0,0,0,0,.004f,-.0005f,0,0,-.0015f,.0001f,0,0,0,.5f,1,1,0,0,0};
    auto constants=buffer(sizeof(camera),D3D11_BIND_CONSTANT_BUFFER,camera);float season[16]{};season[2]=1;season[8]=9;season[9]=10;season[14]=season[15]=1.f/width;
    auto parameters=buffer(sizeof(season),D3D11_BIND_CONSTANT_BUFFER,season);
    std::array<uint32_t,1027*4> table{};table[7*4]=5;float scale=1000;memcpy(&table[517*4],&scale,4);memcpy(&table[772*4],&scale,4);
    auto originalTable=buffer(sizeof(table),D3D11_BIND_CONSTANT_BUFFER,table.data());
    float controlPixel[]={0,0,0,0},detailPixel[]={.48f,.53f,.15f,1},heightPixel=0,climatePixel[]={20,50};
    auto control=texture(DXGI_FORMAT_R32G32B32A32_FLOAT,1,controlPixel,16,D3D11_USAGE_DEFAULT),detail=texture(DXGI_FORMAT_R32G32B32A32_FLOAT,19,detailPixel,16);
    auto controlReference=texture(DXGI_FORMAT_R32G32B32A32_FLOAT,1,controlPixel,16);
    auto heightView=texture(DXGI_FORMAT_R32_FLOAT,1,&heightPixel,4),climate=texture(DXGI_FORMAT_R32G32_FLOAT,12,climatePixel,8);
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;sd.MaxLOD=D3D11_FLOAT32_MAX;
    ComPtr<ID3D11SamplerState> sampler;checked(device->CreateSamplerState(&sd,&sampler));
    auto prepare=[&]{context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);context->GSSetShader(nullptr,nullptr,0);context->IASetInputLayout(layout.Get());
        auto* raw=vertices.Get();UINT stride=8,offset=0;context->IASetVertexBuffers(0,1,&raw,&stride,&offset);context->IASetIndexBuffer(index.Get(),DXGI_FORMAT_R16_UINT,0);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        raw=constants.Get();context->VSSetConstantBuffers(0,1,&raw);raw=originalTable.Get();context->PSSetConstantBuffers(0,1,&raw);auto* v=heightView.Get();context->VSSetShaderResources(8,1,&v);v=control.Get();context->PSSetShaderResources(3,1,&v);v=detail.Get();context->PSSetShaderResources(7,1,&v);
        auto* s=sampler.Get();context->VSSetSamplers(8,1,&s);context->PSSetSamplers(3,1,&s);context->PSSetSamplers(7,1,&s);context->PSSetSamplers(12,1,&s);};
    SeasonWorldGpu world;SeasonRiceGpu rice;TreeColorParameters fall{1,197,10,1,{0,0,1,0},true};
    auto settle=[&]{context->Flush();for(UINT i=0;i<5000&&!rice.settled();i++){checked(rice.poll(context.Get()));std::this_thread::sleep_for(std::chrono::milliseconds(1));}require(rice.settled(),"Rice query timed out");};
    auto collect=[&]{prepare();checked(world.update(context.Get(),constants.Get()));auto hr=rice.collect(context.Get(),fall,6,1,parameters.Get(),climate.Get(),world.get(),controlReference.Get());require(hr==S_OK,"Fixture placement rejected");settle();
        ComPtr<ID3D11VertexShader> saved;context->VSGetShader(&saved,nullptr,nullptr);require(saved==vs,"Original VS was not restored");ComPtr<ID3D11Buffer> savedCb;context->VSGetConstantBuffers(0,1,&savedCb);require(savedCb==constants,"Original constant buffer was not restored");};
    auto roots=[&](UINT count){D3D11_BUFFER_DESC d{};rice.fixtureRoots()->GetDesc(&d);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Buffer> copy;checked(device->CreateBuffer(&d,nullptr,&copy));context->CopyResource(copy.Get(),rice.fixtureRoots());D3D11_MAPPED_SUBRESOURCE m{};checked(context->Map(copy.Get(),0,D3D11_MAP_READ,0,&m));
        std::vector<float> result(count*4);memcpy(result.data(),m.pData,result.size()*4);context->Unmap(copy.Get(),0);return result;};
    collect();std::cerr<<"Placed roots="<<rice.rootCount()<<"\n";
    require(rice.rootCount()==100,"Shared terrain edges duplicated/missed roots");auto originalRoots=roots(100);std::set<std::pair<int,int>> cells;
    for(UINT i=0;i<100;i++){const auto* r=originalRoots.data()+i*4;require(std::abs(r[1]-(20+r[0]*.04f+r[2]*.02f))<.001,"Rice root floats above/below original VS plane");cells.emplace(int(r[0]),int(r[2]));}
    require(cells.size()==100,"Duplicate world anchors");
    auto pixels=[&]{context->CopyResource(staging.Get(),image.Get());D3D11_MAPPED_SUBRESOURCE m{};checked(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&m));std::vector<BYTE> b(width*height*4);
        for(UINT y=0;y<height;y++)memcpy(b.data()+y*width*4,static_cast<BYTE*>(m.pData)+y*m.RowPitch,width*4);context->Unmap(staging.Get(),0);return b;};
    auto draw=[&](float seconds,float foreground=1.f){season[11]=seconds;context->UpdateSubresource(parameters.Get(),0,nullptr,season,0,0);float clear[]={.1f,.2f,.3f,.3f};context->ClearRenderTargetView(target.Get(),clear);context->ClearDepthStencilView(dsv.Get(),D3D11_CLEAR_DEPTH,foreground,0);
        {seasons::PipelineScope scope(context.Get(),GetCurrentThreadId());context->RSSetState(raster.Get());context->RSSetViewports(1,&viewport);require(rice.draw(context.Get(),fall,target.Get(),dsv.Get(),1,parameters.Get(),world.get())==S_OK,"Rice draw rejected");}return pixels();};
    float untouchedColor[]={.1f,.2f,.3f,.3f};context->ClearRenderTargetView(target.Get(),untouchedColor);auto untouched=pixels();
    auto wind0=draw(0);collect();auto wind1=draw(1.5);UINT changed=0,moved=0;for(UINT i=0;i<width*height;i++){require(wind0[i*4+3]==untouched[i*4+3]&&wind1[i*4+3]==untouched[i*4+3],"Rice altered original color alpha");
        changed+=memcmp(wind0.data()+i*4,untouched.data()+i*4,3)!=0;moved+=memcmp(wind0.data()+i*4,wind1.data()+i*4,3)!=0;}
    require(changed>500&&moved>100,"3D rice/wind has no visible GPU output");collect();auto blocked=draw(0,.05f);
    require(blocked==untouched,"Rice draws through foreground depth");
    // Ordinary overview: each 80-unit cell spans about two screen pixels,
    // and a 140-unit stalk is about two pixels high. Test the clump LOD here.
    float overviewCamera[20];memcpy(overviewCamera,camera,sizeof(camera));
    overviewCamera[0]=1.f/5000;overviewCamera[5]=.00012f;overviewCamera[9]=-.00012f;
    context->UpdateSubresource(constants.Get(),0,nullptr,overviewCamera,0,0);collect();auto overview0=draw(0);collect();auto overview1=draw(1.5f);
    UINT overviewChanged=0,overviewMoved=0;
    for(UINT i=0;i<width*height;i++){require(overview0[i*4+3]==untouched[i*4+3]&&overview1[i*4+3]==untouched[i*4+3],"Overview rice changed alpha");
        overviewChanged+=memcmp(overview0.data()+i*4,untouched.data()+i*4,3)!=0;overviewMoved+=memcmp(overview0.data()+i*4,overview1.data()+i*4,3)!=0;}
    require(overviewChanged>100&&overviewMoved>20,"Ordinary zoom clumps/wind have insufficient GPU coverage");
    std::cerr<<"OverviewRicePixels="<<overviewChanged<<"; overviewWindPixels="<<overviewMoved<<"; stalkHeightPixels=2.15\n";
    context->UpdateSubresource(constants.Get(),0,nullptr,camera,0,0);
    camera[12]=.08f;context->UpdateSubresource(constants.Get(),0,nullptr,camera,0,0);auto before=rice.rootCount();collect();require(rice.rootCount()-before==100&&roots(100)==originalRoots,"Camera movement displaced world anchors");rice.discardFrame();
    for(UINT layer:{4u,0u,18u}){table[7*4]=layer;context->UpdateSubresource(originalTable.Get(),0,nullptr,table.data(),0,0);before=rice.rootCount();collect();require(rice.rootCount()==before,"Rice leaked into another terrain layer");rice.discardFrame();}
    table[7*4]=17;context->UpdateSubresource(originalTable.Get(),0,nullptr,table.data(),0,0);before=rice.rootCount();collect();require(rice.rootCount()-before==100,"Second farmland layer missing");rice.discardFrame();
    {ComPtr<ID3D11Resource> mutableControl;control->GetResource(&mutableControl);controlPixel[0]=1.f/255;
        table[8*4]=17;memcpy(&table[518*4],&scale,4);memcpy(&table[773*4],&scale,4);context->UpdateSubresource(originalTable.Get(),0,nullptr,table.data(),0,0);
        context->UpdateSubresource(mutableControl.Get(),0,nullptr,controlPixel,16,0);before=rice.rootCount();collect();require(rice.rootCount()==before,"Changed DEFAULT control bypassed the authenticated reference");rice.discardFrame();}
    // Summer is growing foliage without autumn grain artwork. Inspect actual
    // GPU output and source exclusion, rather than only testing date constants.
    {ComPtr<ID3D11Resource> mutableControl;control->GetResource(&mutableControl);controlPixel[0]=0;
        context->UpdateSubresource(mutableControl.Get(),0,nullptr,controlPixel,16,0);}
    fall.month=7;fall.weights[1]=1;fall.weights[2]=0;season[1]=1;season[2]=0;
    camera[12]=0;context->UpdateSubresource(constants.Get(),0,nullptr,camera,0,0);
    context->UpdateSubresource(parameters.Get(),0,nullptr,season,0,0);before=rice.rootCount();collect();
    require(rice.rootCount()-before==100,"Summer crop roots missing");auto summer0=draw(0);collect();auto summer1=draw(1.5f);
    UINT summerPixels=0,summerWind=0,goldPixels=0;
    for(UINT i=0;i<width*height;i++){
        const auto* px=summer0.data()+i*4;require(px[3]==untouched[i*4+3],"Summer foliage changed alpha");
        bool altered=memcmp(px,untouched.data()+i*4,3)!=0;summerPixels+=altered;
        goldPixels+=altered&&px[0]>px[1];summerWind+=memcmp(px,summer1.data()+i*4,3)!=0;
    }
    require(summerPixels>500&&summerWind>100&&goldPixels==0,"Summer foliage/grain separation failed");
    collect();require(draw(0,.05f)==untouched,"Summer foliage draws through foreground");
    for(UINT layer:{0u,7u,9u,12u}){
        table[7*4]=layer;context->UpdateSubresource(originalTable.Get(),0,nullptr,table.data(),0,0);before=rice.rootCount();collect();
        require(rice.rootCount()-before==50,"Summer grass layer or sparse cells differ");
        auto grassRoots=roots(50);for(UINT i=0;i<50;i++)require(grassRoots[i*4+3]>=2,"Grass kind lost in stream output");rice.discardFrame();
    }
    for(UINT layer:{1u,4u,6u,18u}){table[7*4]=layer;context->UpdateSubresource(originalTable.Get(),0,nullptr,table.data(),0,0);before=rice.rootCount();collect();
        require(rice.rootCount()==before,"Summer vegetation leaked into excluded terrain");rice.discardFrame();}
    for(UINT s:{0u,3u}){auto p=fall;std::fill(std::begin(p.weights),std::end(p.weights),0.f);p.weights[s]=1;
        require(rice.collect(context.Get(),p,6,1,parameters.Get(),climate.Get(),world.get(),controlReference.Get())==S_FALSE,"Plants appeared in spring/winter");}
    std::cerr<<"SummerFoliagePixels="<<summerPixels<<"; summerWindPixels="<<summerWind<<"; summerGoldPixels="<<goldPixels<<"; grassRootsPerLayer=50\n";
    require(rice.gpuBytes()==262144*16,"Rice buffer is not bounded");
    std::cout<<"{\"rootsPerPlane\":100,\"worldHeightVerified\":true,\"worldAnchorsStable\":true,\"sharedEdgesUnique\":true,\"windChangedPixels\":"<<moved<<",\"ricePixels\":"<<changed<<",\"summerFoliagePixels\":"<<summerPixels<<",\"summerWindPixels\":"<<summerWind<<",\"summerGrainPixels\":"<<goldPixels<<",\"foregroundDepthOcclusion\":true,\"alphaPreserved\":true,\"otherTerrainExcluded\":true,\"mutableControlComparedEveryDraw\":true,\"springWinterExcluded\":true,\"originalBindingsRestored\":true,\"gpuPayloadBytes\":"<<rice.gpuBytes()<<",\"gameAccess\":false,\"gameScreenVerified\":false,\"passed\":true}\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
