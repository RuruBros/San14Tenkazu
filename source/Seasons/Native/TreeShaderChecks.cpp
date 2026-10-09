// Synthetic leaf/branch fixture on an owned WARP device. No game process access.
#define NOMINMAX
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <stdexcept>
#include <cstdio>
#include <vector>
#include <cmath>
using Microsoft::WRL::ComPtr;
static void require(bool ok,const char* why){if(!ok)throw std::runtime_error(why);}
static void hr(HRESULT v){require(SUCCEEDED(v),"D3D11 fixture API failed");}
static ComPtr<ID3DBlob> compile(const wchar_t* path,const char* entry,const char* profile)
{
    ComPtr<ID3DBlob> code,errors;HRESULT result=D3DCompileFromFile(path,nullptr,D3D_COMPILE_STANDARD_FILE_INCLUDE,entry,profile,
        D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_WARNINGS_ARE_ERRORS|D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors);
    if(errors)fprintf(stderr,"%.*s",int(errors->GetBufferSize()),static_cast<const char*>(errors->GetBufferPointer()));hr(result);return code;
}
int wmain(int argc,wchar_t** argv)
{
    try
    {
        require(argc==2,"Usage: TreeShaderChecks.exe SeasonTreeCheck.hlsl");
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        constexpr UINT width=8,height=4;
        unsigned char pixels[width*height*4]{};float weights[width*height*2]{},coverage[width*height*2]{};
        for(UINT i=0;i<width*height;i++)
        {
            UINT kind=i%4;pixels[i*4]=kind==1?128:30;pixels[i*4+1]=kind==1?60:190;pixels[i*4+2]=20;
            pixels[i*4+3]=kind==2?0:kind==3?128:255;weights[i*2]=kind==1?0.0f:1.0f;weights[i*2+1]=kind==1?1.0f:0.0f;
            coverage[i*2]=weights[i*2]*(pixels[i*4+3]/255.0f);coverage[i*2+1]=weights[i*2+1]*(pixels[i*4+3]/255.0f);
        }
        D3D11_TEXTURE2D_DESC td{width,height,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
        D3D11_SUBRESOURCE_DATA initial{pixels,width*4,0};ComPtr<ID3D11Texture2D> source,mask,coverageMask,color,depth,colorRead,depthRead;
        hr(device->CreateTexture2D(&td,&initial,&source));td.Format=DXGI_FORMAT_R32G32_FLOAT;initial={weights,width*8,0};hr(device->CreateTexture2D(&td,&initial,&mask));
        initial={coverage,width*8,0};hr(device->CreateTexture2D(&td,&initial,&coverageMask));
        ComPtr<ID3D11ShaderResourceView> sourceView,maskView,coverageView;hr(device->CreateShaderResourceView(source.Get(),nullptr,&sourceView));hr(device->CreateShaderResourceView(mask.Get(),nullptr,&maskView));hr(device->CreateShaderResourceView(coverageMask.Get(),nullptr,&coverageView));
        td.Usage=D3D11_USAGE_DEFAULT;td.Format=DXGI_FORMAT_R8G8B8A8_UNORM;td.BindFlags=D3D11_BIND_RENDER_TARGET;hr(device->CreateTexture2D(&td,nullptr,&color));
        td.Format=DXGI_FORMAT_D32_FLOAT;td.BindFlags=D3D11_BIND_DEPTH_STENCIL;hr(device->CreateTexture2D(&td,nullptr,&depth));
        ComPtr<ID3D11RenderTargetView> rtv;ComPtr<ID3D11DepthStencilView> dsv;hr(device->CreateRenderTargetView(color.Get(),nullptr,&rtv));hr(device->CreateDepthStencilView(depth.Get(),nullptr,&dsv));
        td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateTexture2D(&td,nullptr,&depthRead));
        td.Format=DXGI_FORMAT_R8G8B8A8_UNORM;hr(device->CreateTexture2D(&td,nullptr,&colorRead));
        D3D11_DEPTH_STENCIL_DESC ds{};ds.DepthEnable=TRUE;ds.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL;ds.DepthFunc=D3D11_COMPARISON_LESS;
        ComPtr<ID3D11DepthStencilState> depthState;hr(device->CreateDepthStencilState(&ds,&depthState));
        D3D11_RASTERIZER_DESC rs{};rs.FillMode=D3D11_FILL_SOLID;rs.CullMode=D3D11_CULL_NONE;rs.DepthClipEnable=TRUE;
        ComPtr<ID3D11RasterizerState> raster;hr(device->CreateRasterizerState(&rs,&raster));
        auto vertexCode=compile(argv[1],"Vertex","vs_5_0"),colorCode=compile(argv[1],"Color","ps_5_0"),
            depthCode=compile(argv[1],"Depth","ps_5_0"),shadowCode=compile(argv[1],"Shadow","ps_5_0");
        ComPtr<ID3D11VertexShader> vertex;ComPtr<ID3D11PixelShader> colorShader,depthShader,shadowShader;
        hr(device->CreateVertexShader(vertexCode->GetBufferPointer(),vertexCode->GetBufferSize(),nullptr,&vertex));
        hr(device->CreatePixelShader(colorCode->GetBufferPointer(),colorCode->GetBufferSize(),nullptr,&colorShader));
        hr(device->CreatePixelShader(depthCode->GetBufferPointer(),depthCode->GetBufferSize(),nullptr,&depthShader));
        hr(device->CreatePixelShader(shadowCode->GetBufferPointer(),shadowCode->GetBufferSize(),nullptr,&shadowShader));
        auto coverageColorCode=compile(argv[1],"ColorCoverage","ps_5_0"),coverageDepthCode=compile(argv[1],"DepthCoverage","ps_5_0"),coverageShadowCode=compile(argv[1],"ShadowCoverage","ps_5_0");
        ComPtr<ID3D11PixelShader> coverageColor,coverageDepth,coverageShadow;
        hr(device->CreatePixelShader(coverageColorCode->GetBufferPointer(),coverageColorCode->GetBufferSize(),nullptr,&coverageColor));
        hr(device->CreatePixelShader(coverageDepthCode->GetBufferPointer(),coverageDepthCode->GetBufferSize(),nullptr,&coverageDepth));
        hr(device->CreatePixelShader(coverageShadowCode->GetBufferPointer(),coverageShadowCode->GetBufferSize(),nullptr,&coverageShadow));
        struct Parameters { float retention;UINT policy;float cutoff;UINT pad; };
        D3D11_BUFFER_DESC bd{sizeof(Parameters),D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};ComPtr<ID3D11Buffer> constants;hr(device->CreateBuffer(&bd,nullptr,&constants));
        struct Case { const char* name;float retention;UINT policy,expected; } cases[]{
            {"deciduous-leaf-on",1,1,24},{"deciduous-winter",0,1,8},{"deciduous-transition",0.75f,1,16},
            {"deciduous-low-retention",0.25f,1,8},{"evergreen-winter",0,2,24},{"evergreen-transition",0.5f,2,24}};
        printf("{\"backend\":\"D3D11 WARP\",\"gameAttached\":false,\"fixture\":\"synthetic 8x4 alpha and leaf/branch weights\",\"cases\":[");
        bool first=true;UINT compared=0;
        for(int representation=0;representation<2;++representation)for(const auto& test:cases)
        {
            Parameters params{test.retention,test.policy,0.5f,0};context->UpdateSubresource(constants.Get(),0,nullptr,&params,0,0);
            std::vector<unsigned char> masks[3];
            for(int pass=0;pass<3;pass++)
            {
                context->ClearState();context->ClearDepthStencilView(dsv.Get(),D3D11_CLEAR_DEPTH,1,0);
                FLOAT clear[4]{};context->ClearRenderTargetView(rtv.Get(),clear);
                ID3D11RenderTargetView* target=rtv.Get();context->OMSetRenderTargets(pass==0?1:0,pass==0?&target:nullptr,dsv.Get());
                context->OMSetDepthStencilState(depthState.Get(),0);context->RSSetState(raster.Get());D3D11_VIEWPORT vp{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&vp);
                context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vertex.Get(),nullptr,0);
                ID3D11PixelShader* selected=representation==0?(pass==0?colorShader.Get():pass==1?depthShader.Get():shadowShader.Get()):
                    (pass==0?coverageColor.Get():pass==1?coverageDepth.Get():coverageShadow.Get());context->PSSetShader(selected,nullptr,0);
                ID3D11ShaderResourceView* views[]{sourceView.Get(),representation==0?maskView.Get():coverageView.Get()};context->PSSetShaderResources(0,2,views);
                ID3D11Buffer* cb=constants.Get();context->PSSetConstantBuffers(0,1,&cb);context->Draw(3,0);context->ClearState();
                context->CopyResource(pass==0?colorRead.Get():depthRead.Get(),pass==0?color.Get():depth.Get());
                D3D11_MAPPED_SUBRESOURCE mapped{};auto staging=pass==0?colorRead.Get():depthRead.Get();hr(context->Map(staging,0,D3D11_MAP_READ,0,&mapped));
                for(UINT y=0;y<height;y++)for(UINT x=0;x<width;x++)
                {
                    auto row=static_cast<const unsigned char*>(mapped.pData)+y*mapped.RowPitch;
                    bool covered=pass==0?row[x*4+3]!=0:reinterpret_cast<const float*>(row)[x]<1;
                    masks[pass].push_back(covered?1:0);
                    if(pass!=0 && covered)require(std::abs(reinterpret_cast<const float*>(row)[x]-0.5f)<0.000001,"Unexpected fixture depth");
                }
                context->Unmap(staging,0);
            }
            require(masks[0]==masks[1] && masks[0]==masks[2],"Leaf silhouette differs in Color/Depth/Shadow");
            UINT count=0;for(auto hit:masks[0])count+=hit;require(count==test.expected,"Unexpected leaf/branch/evergreen coverage");
            for(UINT i=0;i<width*height;i++)
            {
                if(i%4==1)require(masks[0][i]==1,"Branch removed");if(i%4==2)require(masks[0][i]==0,"Transparent source casts shadow");
                if(test.policy==1 && test.retention==0 && i%4!=1)require(masks[0][i]==0,"Winter leaf remains");
            }
            compared+=width*height*3;
            printf("%s{\"name\":\"%s\",\"representation\":\"%s\",\"coveredPixels\":%u,\"colorDepthShadowEqual\":true}",first?"":",",test.name,representation==0?"normalized-weights":"premultiplied-coverage",count);first=false;
        }
        context->ClearState();printf("],\"passPixelsCompared\":%u,\"failures\":0}\n",compared);return 0;
    }
    catch(const std::exception& error){fprintf(stderr,"FAIL: %s\n",error.what());return 1;}
}
