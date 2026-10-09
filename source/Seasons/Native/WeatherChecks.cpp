// Owned single-sample scene, depth, particle and UI fixtures. Never attaches to a game.
#define NOMINMAX
#include "RuntimeGuard.h"
#include <d3dcompiler.h>
#include <filesystem>
#include <fstream>
#include <vector>
#include <cstdio>
#include <cmath>
#include <cstring>
using namespace seasons;
static UINT assertions=0;
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);++assertions;}
static void hr(HRESULT value){require(SUCCEEDED(value),"Owned weather WARP API failed");}
static ComPtr<ID3DBlob> compile(const wchar_t* path,const char* entry,const char* profile){
    ComPtr<ID3DBlob> code,errors;const HRESULT result=D3DCompileFromFile(path,nullptr,D3D_COMPILE_STANDARD_FILE_INCLUDE,entry,profile,
        D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
    if(errors)fprintf(stderr,"%.*s",int(errors->GetBufferSize()),static_cast<const char*>(errors->GetBufferPointer()));hr(result);return code;
}
struct Color{float r,g,b,a;};
struct Settings{float inverse[2],haze,amplitude,phase,frequency[2],padding,matrix[16];UINT direction,reserved[3];};
struct Particle{float position[3],radius,intensity,opacity,reserved[2];};
static_assert(sizeof(Settings)==112&&sizeof(Particle)==32&&sizeof(Color)==16);
static float smooth(float low,float high,float value){const float t=std::clamp((value-low)/(high-low),0.0f,1.0f);return t*t*(3-2*t);}
static void bitmap(const std::filesystem::path& path,const std::vector<Color>& pixels,UINT width,UINT height){
    const UINT length=54+width*height*4;std::vector<unsigned char> bytes(length,0);
    auto word=[&](size_t offset,UINT value){memcpy(bytes.data()+offset,&value,4);};
    bytes[0]='B';bytes[1]='M';word(2,length);word(10,54);word(14,40);word(18,width);word(22,height);bytes[26]=1;bytes[28]=32;
    auto srgb=[](float v){v=std::clamp(v,0.0f,1.0f);return static_cast<unsigned char>(std::round((v<=.0031308f?v*12.92f:1.055f*std::pow(v,1/2.4f)-.055f)*255));};
    for(UINT y=0;y<height;++y)for(UINT x=0;x<width;++x){const auto& p=pixels[y*width+x];const size_t at=54+size_t(height-1-y)*width*4+x*4;
        bytes[at]=srgb(p.b);bytes[at+1]=srgb(p.g);bytes[at+2]=srgb(p.r);bytes[at+3]=255;}
    std::ofstream file(path,std::ios::binary);file.write(reinterpret_cast<char*>(bytes.data()),bytes.size());require(bool(file),"Owned bitmap export failed");
}
int wmain(int argc,wchar_t** argv){
    bool deviceCreated=false;
    try{
        require(argc==3,"Usage: WeatherChecks.exe SeasonWeatherPasses.hlsl owned-output-directory");
        const std::filesystem::path output{argv[2]};std::filesystem::create_directories(output);
        const auto fullCode=compile(argv[1],"FullscreenVertex","vs_5_0"),snowVertexCode=compile(argv[1],"SnowVertex","vs_5_0"),
            hazeCode=compile(argv[1],"Haze","ps_5_0"),snowCode=compile(argv[1],"Snow","ps_5_0"),uiCode=compile(argv[1],"FixtureUi","ps_5_0");
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));deviceCreated=true;
        constexpr UINT width=32,height=16;std::vector<Color> scene(width*height);std::vector<float> depths[3];
        for(auto& depth:depths)depth.resize(width*height);
        for(UINT y=0;y<height;++y)for(UINT x=0;x<width;++x){
            const float u=(x+.5f)/width,v=(y+.5f)/height;scene[y*width+x]={u,v,.25f,.2f+.4f*u};
            depths[0][y*width+x]=x<width/2?.3f:.9f;depths[1][y*width+x]=x<width/2?.7f:.1f;depths[2][y*width+x]=std::nanf("");
        }
        D3D11_TEXTURE2D_DESC td{width,height,1,1,DXGI_FORMAT_R32G32B32A32_FLOAT,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
        D3D11_SUBRESOURCE_DATA initial{scene.data(),width*16,0};ComPtr<ID3D11Texture2D> source,target,staging,depthObjects[3];
        ComPtr<ID3D11ShaderResourceView> sourceView,depthViews[3],particleView;hr(device->CreateTexture2D(&td,&initial,&source));hr(device->CreateShaderResourceView(source.Get(),nullptr,&sourceView));
        td.Format=DXGI_FORMAT_R32_FLOAT;for(int mode=0;mode<3;++mode){initial={depths[mode].data(),width*4,0};hr(device->CreateTexture2D(&td,&initial,&depthObjects[mode]));hr(device->CreateShaderResourceView(depthObjects[mode].Get(),nullptr,&depthViews[mode]));}
        td.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_RENDER_TARGET;hr(device->CreateTexture2D(&td,nullptr,&target));
        ComPtr<ID3D11RenderTargetView> targetView;hr(device->CreateRenderTargetView(target.Get(),nullptr,&targetView));
        td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&td,nullptr,&staging));
        ComPtr<ID3D11VertexShader> fullVertex,snowVertex;ComPtr<ID3D11PixelShader> haze,snow,ui;
        hr(device->CreateVertexShader(fullCode->GetBufferPointer(),fullCode->GetBufferSize(),nullptr,&fullVertex));hr(device->CreateVertexShader(snowVertexCode->GetBufferPointer(),snowVertexCode->GetBufferSize(),nullptr,&snowVertex));
        hr(device->CreatePixelShader(hazeCode->GetBufferPointer(),hazeCode->GetBufferSize(),nullptr,&haze));hr(device->CreatePixelShader(snowCode->GetBufferPointer(),snowCode->GetBufferSize(),nullptr,&snow));hr(device->CreatePixelShader(uiCode->GetBufferPointer(),uiCode->GetBufferSize(),nullptr,&ui));
        D3D11_BUFFER_DESC bd{sizeof(Settings),D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};ComPtr<ID3D11Buffer> constants,particles;
        hr(device->CreateBuffer(&bd,nullptr,&constants));bd={sizeof(Particle),D3D11_USAGE_DEFAULT,D3D11_BIND_SHADER_RESOURCE,0,D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,sizeof(Particle)};
        hr(device->CreateBuffer(&bd,nullptr,&particles));hr(device->CreateShaderResourceView(particles.Get(),nullptr,&particleView));
        D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxAnisotropy=1;sd.ComparisonFunc=D3D11_COMPARISON_NEVER;sd.MaxLOD=D3D11_FLOAT32_MAX;
        ComPtr<ID3D11SamplerState> sampler;hr(device->CreateSamplerState(&sd,&sampler));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;ComPtr<ID3D11RasterizerState> raster;hr(device->CreateRasterizerState(&rs,&raster));
        D3D11_BLEND_DESC blend{};blend.RenderTarget[0].BlendEnable=TRUE;blend.RenderTarget[0].SrcBlend=D3D11_BLEND_ONE;blend.RenderTarget[0].DestBlend=D3D11_BLEND_INV_SRC_ALPHA;blend.RenderTarget[0].BlendOp=D3D11_BLEND_OP_ADD;
        blend.RenderTarget[0].SrcBlendAlpha=D3D11_BLEND_ZERO;blend.RenderTarget[0].DestBlendAlpha=D3D11_BLEND_ONE;blend.RenderTarget[0].BlendOpAlpha=D3D11_BLEND_OP_ADD;blend.RenderTarget[0].RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        ComPtr<ID3D11BlendState> snowBlend;hr(device->CreateBlendState(&blend,&snowBlend));
        // Deliberate prior state to verify the real frame scope restores its own caller.
        context->PSSetShader(ui.Get(),nullptr,0);ID3D11ShaderResourceView* sourceBinding=sourceView.Get();context->PSSetShaderResources(7,1,&sourceBinding);
        ID3D11Buffer* priorCb=constants.Get();context->PSSetConstantBuffers(5,1,&priorCb);ID3D11RenderTargetView* rt=targetView.Get();context->OMSetRenderTargets(1,&rt,nullptr);
        const D3D11_VIEWPORT priorViewport{3,2,11,7,0,1};context->RSSetViewports(1,&priorViewport);context->RSSetState(raster.Get());context->OMSetBlendState(snowBlend.Get(),nullptr,0xAAAAAAAA);
        auto session=std::make_shared<ContextSession>(context.Get(),42,5000);std::vector<Color> baseline;UINT checkedPixels=0,uiPixels=0,occluded=0,visible=0,changedHaze=0;
        float maxError=0;
        struct Case{const char* name;float haze,fall;UINT direction;float depth=.5f,clipW=1;bool nonfiniteCamera=false,nonfiniteDepth=false;float centerX=0,phase=0;};
        const Case cases[]={{"original",0,0,1},{"haze",.25f,0,1},{"haze-phase",.25f,0,1,.5f,1,false,false,0,1.5f},
            {"snow-forward",0,.35f,1},{"snow-reversed",0,.35f,2},{"homogeneous-w-two",0,.35f,1,1,2,false,false,.5f},{"combined",.25f,.35f,1},
            {"unverified-direction",0,.35f,0},{"clipped-near",0,.35f,1,-.1f},{"clipped-far",0,.35f,1,1.1f},
            {"zero-clip-w",0,.35f,1,.5f,0},{"nonfinite-camera",0,.35f,1,.5f,1,true},
            {"nonfinite-depth",0,.35f,1,.5f,1,false,true},{"restored",0,0,1}};
        printf("{\"backend\":\"D3D11 WARP\",\"gameAttached\":false,\"fixture\":\"owned 32x16 linear scene/depth/particle/UI\",\"cases\":[");bool first=true;
        for(const auto& test:cases){
            Settings parameters{{1.0f/width,1.0f/height},test.haze,2,test.phase,{4,3},0,{},test.direction,{}};for(int i=0;i<4;++i)parameters.matrix[i*4+i]=1;
            parameters.matrix[15]=test.clipW;if(test.nonfiniteCamera)parameters.matrix[0]=std::nanf("");
            Particle particle{{test.centerX,0,test.depth},6,test.fall,1,{}};
            context->UpdateSubresource(constants.Get(),0,nullptr,&parameters,0,0);context->UpdateSubresource(particles.Get(),0,nullptr,&particle,0,0);
            require(session->render(context.Get(),42,{194,1,1},[&](ID3D11DeviceContext* active){
                active->ClearState();ID3D11RenderTargetView* targetBinding=targetView.Get();active->OMSetRenderTargets(1,&targetBinding,nullptr);
                active->RSSetState(raster.Get());D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};active->RSSetViewports(1,&viewport);
                active->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);ID3D11Buffer* cb=constants.Get();active->VSSetConstantBuffers(0,1,&cb);active->PSSetConstantBuffers(0,1,&cb);
                active->VSSetShader(fullVertex.Get(),nullptr,0);active->PSSetShader(haze.Get(),nullptr,0);ID3D11ShaderResourceView* view=sourceView.Get();active->PSSetShaderResources(0,1,&view);
                ID3D11SamplerState* sample=sampler.Get();active->PSSetSamplers(0,1,&sample);active->Draw(3,0);
                active->VSSetShader(snowVertex.Get(),nullptr,0);view=particleView.Get();active->VSSetShaderResources(2,1,&view);
                active->PSSetShader(snow.Get(),nullptr,0);view=depthViews[test.nonfiniteDepth?2:test.direction==2?1:0].Get();active->PSSetShaderResources(1,1,&view);active->OMSetBlendState(snowBlend.Get(),nullptr,0xFFFFFFFF);active->DrawInstanced(6,1,0,0);
                active->OMSetBlendState(nullptr,nullptr,0xFFFFFFFF);active->VSSetShader(fullVertex.Get(),nullptr,0);active->PSSetShader(ui.Get(),nullptr,0);active->Draw(3,0);
            }),"Owned weather frame admitted");
            ComPtr<ID3D11PixelShader> restoredPs;ComPtr<ID3D11ShaderResourceView> restoredSrv;ComPtr<ID3D11Buffer> restoredCb;ComPtr<ID3D11RenderTargetView> restoredTarget;
            context->PSGetShader(&restoredPs,nullptr,nullptr);context->PSGetShaderResources(7,1,&restoredSrv);context->PSGetConstantBuffers(5,1,&restoredCb);context->OMGetRenderTargets(1,&restoredTarget,nullptr);
            ComPtr<ID3D11BlendState> restoredBlend;FLOAT factors[4];UINT mask;context->OMGetBlendState(&restoredBlend,factors,&mask);D3D11_VIEWPORT viewport{};UINT count=1;context->RSGetViewports(&count,&viewport);
            require(restoredPs==ui&&restoredSrv==sourceView&&restoredCb==constants&&restoredTarget==targetView&&restoredBlend==snowBlend&&mask==0xAAAAAAAA&&count==1&&!memcmp(&viewport,&priorViewport,sizeof(viewport)),"Owned weather frame state restored");
            context->CopyResource(staging.Get(),target.Get());D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));std::vector<Color> actual(width*height);
            for(UINT y=0;y<height;++y)memcpy(actual.data()+y*width,static_cast<unsigned char*>(mapped.pData)+y*mapped.RowPitch,width*16);context->Unmap(staging.Get(),0);
            for(UINT y=0;y<height;++y)for(UINT x=0;x<width;++x){
                const UINT index=y*width+x;Color expected=scene[index];const bool isUi=x<4&&y<4;
                if(isUi){expected={.1f,.8f,.2f,.65f};require(!memcmp(&actual[index],&expected,sizeof(Color)),"Late owned UI remains exact");++uiPixels;}
                else{
                    if(test.haze>0){expected.r=std::clamp(expected.r+std::sin(expected.g*4+test.phase)*test.haze*2/width,.5f/width,1-.5f/width);
                        expected.g=std::clamp(expected.g+std::sin(scene[index].r*3+test.phase)*test.haze*2/height,.5f/height,1-.5f/height);
                        if(std::abs(actual[index].r-scene[index].r)>1e-5)++changedHaze;}
                    const float centerX=test.clipW>1e-6f?width*.5f*(1+test.centerX/test.clipW):width*.5f;
                    const float dx=(x+.5f-centerX)/6,dy=(y+.5f-height*.5f)/6;
                    const bool validProjection=!test.nonfiniteCamera&&!test.nonfiniteDepth&&test.clipW>1e-6f&&test.depth>=0&&test.depth<=test.clipW&&(test.direction==1||test.direction==2);
                    if(std::abs(dx)<1&&std::abs(dy)<1&&test.fall>0&&validProjection){
                        const float sceneDepth=depths[test.direction==2?1:0][index],particleDepth=test.depth/test.clipW;
                        const bool particleVisible=test.direction==1?particleDepth<=sceneDepth:particleDepth>=sceneDepth;
                        if(particleVisible){const float alpha=(1-smooth(.25f,1,dx*dx+dy*dy))*test.fall;
                            expected.r=expected.r*(1-alpha)+alpha;expected.g=expected.g*(1-alpha)+alpha;expected.b=expected.b*(1-alpha)+alpha;if(alpha>0)++visible;}
                        else ++occluded;
                    }
                    require(actual[index].a==expected.a,"Weather preserves destination alpha");
                }
                const float differences[]{std::abs(actual[index].r-expected.r),std::abs(actual[index].g-expected.g),std::abs(actual[index].b-expected.b),std::abs(actual[index].a-expected.a)};
                for(float error:differences){maxError=std::max(maxError,error);require(std::isfinite(error)&&error<=.0003f,"Owned weather differs from analytic raster reference");}++checkedPixels;
            }
            if(first)baseline=actual;if(std::strcmp(test.name,"restored")==0)require(!memcmp(actual.data(),baseline.data(),actual.size()*sizeof(Color)),"Owned original/restored captures match exactly");
            bitmap(output/(std::string(test.name)+".bmp"),actual,width,height);
            std::ofstream raw(output/(std::string(test.name)+".rgba32f"),std::ios::binary);
            raw.write(reinterpret_cast<const char*>(actual.data()),actual.size()*sizeof(Color));require(bool(raw),"Owned float capture export failed");
            if(!first)printf(",");first=false;printf("\"%s\"",test.name);
        }
        session->stop();require(session->drained(),"Owned weather session stopped/drained");bool invoked=false;
        require(!session->render(context.Get(),42,{194,1,1},[&](ID3D11DeviceContext*){invoked=true;})&&!invoked,"Stopped owned weather cannot render");
        require(changedHaze>0&&visible>0&&occluded>0,"Owned distortion and visible/occluded snowfall exercised");
        printf("],\"pixelsCompared\":%u,\"uiPixelsBitExact\":%u,\"occludedSnowQuadPixels\":%u,\"visibleSnowPixels\":%u,\"changedHazePixels\":%u,\"maxError\":%.9g,\"stateRestored\":true,\"originalRestoredBitExact\":true,\"passed\":%u,\"failed\":0}\n",checkedPixels,uiPixels,occluded,visible,changedHaze,maxError,assertions);return 0;
    }catch(const std::exception& error){fprintf(stderr,"FAIL: %s\ngameAttached=false; deviceCreated=%s\n",error.what(),deviceCreated?"true":"false");return 1;}
}
