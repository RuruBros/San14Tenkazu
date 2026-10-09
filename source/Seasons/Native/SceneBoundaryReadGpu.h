// Included inside the authenticated engine observer's namespace.
season::boundary::Data* boundary;
struct BoundaryTicket {void* driver;bool admitted;uint32_t start,count;bool captured=false;};
thread_local BoundaryTicket* boundaryTicket=nullptr;
uint64_t boundaryQpc(){LARGE_INTEGER t{};QueryPerformanceCounter(&t);return t.QuadPart;}
void boundaryFail(HRESULT hr){boundary->hresult=hr;boundary->state=4;admission=false;}
bool boundaryIdentity(void* driver,bool ui){
    auto& b=*boundary;
    if(GetCurrentThreadId()!=request->renderThread||reinterpret_cast<uint64_t>(driver)!=b.driver||
       read<uint64_t>(driver,0x10)!=request->context)return false;
    return read<uint64_t>(driver,0xb8)==(ui?b.uiPsOwner:b.scenePsOwner)&&
           read<uint64_t>(driver,0xa8)==(ui?b.uiVsOwner:b.sceneVsOwner);
}
HRESULT boundaryCapture(void* driver,bool ui){
    auto& b=*boundary;b.failureStage=1;
    if(b.images>=3)return E_INVALIDARG;
    ComPtr<ID3D11DeviceContext> context=read<ID3D11DeviceContext*>(driver,0x10);
    if(!context||context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE)return E_INVALIDARG;
    ComPtr<ID3D11Device> device;context->GetDevice(&device);
    if(reinterpret_cast<uint64_t>(device.Get())!=request->device||FAILED(device->GetDeviceRemovedReason()))return E_INVALIDARG;
    ComPtr<ID3D11PixelShader> ps;ComPtr<ID3D11VertexShader> vs;
    context->PSGetShader(&ps,nullptr,nullptr);context->VSGetShader(&vs,nullptr,nullptr);
    if(reinterpret_cast<uint64_t>(ps.Get())!=(ui?b.uiPs:b.scenePs)||
       reinterpret_cast<uint64_t>(vs.Get())!=(ui?b.uiVs:b.sceneVs))return E_INVALIDARG;
    if(ui){ComPtr<ID3D11InputLayout> layout;context->IAGetInputLayout(&layout);
        if(reinterpret_cast<uint64_t>(layout.Get())!=b.inputLayout)return E_INVALIDARG;}
    ComPtr<ID3D11RenderTargetView> rt;context->OMGetRenderTargets(1,&rt,nullptr);
    if(reinterpret_cast<uint64_t>(rt.Get())!=b.target)return E_INVALIDARG;
    ComPtr<ID3D11Resource> resource;rt->GetResource(&resource);
    ComPtr<ID3D11Texture2D> source;if(FAILED(resource.As(&source)))return E_INVALIDARG;
    // GetBuffer and every context/device API execute on the already verified
    // render thread. Temporary COM ownership never escapes this callback.
    b.failureStage=2;ComPtr<IDXGISwapChain> chain=reinterpret_cast<IDXGISwapChain*>(request->swapchain);
    ComPtr<ID3D11Texture2D> backbuffer;auto hr=chain->GetBuffer(0,IID_PPV_ARGS(&backbuffer));
    if(FAILED(hr)||source.Get()!=backbuffer.Get())return FAILED(hr)?hr:E_INVALIDARG;
    D3D11_TEXTURE2D_DESC desc{};source->GetDesc(&desc);
    if(desc.Format!=DXGI_FORMAT_R8G8B8A8_UNORM&&desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM&&
       desc.Format!=DXGI_FORMAT_R8G8B8A8_UNORM_SRGB&&desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)return E_INVALIDARG;
    if(!desc.Width||!desc.Height||desc.Width>4096||desc.Height>2160||desc.ArraySize!=1||desc.MipLevels!=1||desc.SampleDesc.Count!=1)return E_INVALIDARG;
    const uint64_t bytes=uint64_t(desc.Width)*desc.Height*4;
    if(b.bytes>b.capacity||bytes>b.capacity-b.bytes)return E_INVALIDARG;
    auto& image=b.image[b.images];image.resource=reinterpret_cast<uint64_t>(source.Get());image.view=b.target;
    image.backbuffer=reinterpret_cast<uint64_t>(backbuffer.Get());image.width=desc.Width;image.height=desc.Height;
    image.format=desc.Format;image.offset=b.bytes;image.qpc=boundaryQpc();
    desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;
    b.failureStage=3;ComPtr<ID3D11Texture2D> staging;hr=device->CreateTexture2D(&desc,nullptr,&staging);if(FAILED(hr))return hr;
    // Copy to an owned staging resource only. No binding or original contents
    // are modified, and no device/context API is called by the remote worker.
    context->CopyResource(staging.Get(),source.Get());D3D11_MAPPED_SUBRESOURCE mapped{};
    b.failureStage=4;hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
    if(!mapped.pData||mapped.RowPitch<image.width*4)hr=E_INVALIDARG;
    else for(UINT y=0;y<image.height;y++)std::memcpy(reinterpret_cast<BYTE*>(b.output)+b.bytes+y*image.width*4,
        static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch,image.width*4);
    context->Unmap(staging.Get(),0);
    if(SUCCEEDED(hr)){image.bytes=static_cast<uint32_t>(bytes);b.bytes+=image.bytes;++b.images;b.thread=GetCurrentThreadId();b.failureStage=0;}
    return hr;
}
void boundaryBeforeIndexed(void* driver){
    auto& b=*boundary;
    if(b.state!=1||b.images!=1||!boundaryTicket||boundaryTicket->count!=6||!boundaryIdentity(driver,true))return;
    const auto hr=boundaryCapture(driver,true);if(FAILED(hr)){boundaryFail(hr);return;}
    boundaryTicket->captured=true;b.counterBefore=read<uint32_t>(driver,0x2ad0);
}
bool boundaryAfterDraw(void* driver,uint32_t kind,uint32_t start,uint32_t count,uint32_t before,uint32_t after){
    auto& b=*boundary;
    if(kind==3&&!b.state&&start==0&&count==3&&boundaryIdentity(driver,false)){
        b.state=1;const auto hr=boundaryCapture(driver,false);if(FAILED(hr))boundaryFail(hr);
        return true;
    }else if(kind==0&&boundaryTicket&&boundaryTicket->captured){
        if(before!=b.counterBefore||after!=before+1||!boundaryIdentity(driver,true)){boundaryFail(E_INVALIDARG);return true;}
        b.counterAfter=after;const auto hr=boundaryCapture(driver,true);
        if(FAILED(hr))boundaryFail(hr);else{b.state=2;admission=false;}
        return true;
    }
    return false;
}
