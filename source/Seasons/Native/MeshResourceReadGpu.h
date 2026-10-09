MeshRead* meshRead;
MeshReadBatch* meshBatch;
bool matchesMeshRead(ID3D11DeviceContext* context,uint32_t count,const MeshRead& r) noexcept {
    if(r.state||count!=r.indexCount||GetCurrentThreadId()!=r.renderThread)return false;
    ComPtr<ID3D11ShaderResourceView> view;context->PSGetShaderResources(0,1,&view);
    if(reinterpret_cast<uint64_t>(view.Get())!=r.view)return false;
    ComPtr<ID3D11Resource> resource;if(view)view->GetResource(&resource);
    if(reinterpret_cast<uint64_t>(resource.Get())!=r.resource)return false;
    if(r.requireColor){ComPtr<ID3D11RenderTargetView> color;context->OMGetRenderTargets(1,&color,nullptr);if(!color)return false;}
    return true;
}
bool selectMeshRead(ID3D11DeviceContext* context,uint32_t count) noexcept {
    if(!meshBatch)return matchesMeshRead(context,count,*meshRead);
    for(uint32_t i=0;i<meshBatch->count;i++)if(matchesMeshRead(context,count,meshBatch->items[i])){
        meshRead=&meshBatch->items[i];return true;
    }
    return false;
}
HRESULT appendBuffer(ID3D11DeviceContext* context,ID3D11Device* device,ID3D11Buffer* source,
                     uint32_t kind,uint32_t slot,uint32_t stride=0,uint32_t bindingOffset=0,uint32_t sourceOffset=0,uint32_t count=0) noexcept {
    if(!source)return S_OK;
    auto& r=*meshRead;r.failureStage=20;r.failureKind=kind;r.failureSlot=slot;if(r.blobCount>=MeshBlobCount)return E_INVALIDARG;
    D3D11_BUFFER_DESC d{};source->GetDesc(&d);
    auto& b=r.blobs[r.blobCount];b.resource=reinterpret_cast<uint64_t>(source);b.kind=kind;b.slot=slot;b.offset=r.bytes;
    b.width=d.ByteWidth;b.usage=d.Usage;b.bindFlags=d.BindFlags;b.miscFlags=d.MiscFlags;b.structureStride=d.StructureByteStride;
    b.stride=stride;b.bindingOffset=bindingOffset;b.sourceOffset=sourceOffset;
    if(!d.ByteWidth||sourceOffset>=d.ByteWidth)return E_INVALIDARG;
    const auto bytes=count?count:d.ByteWidth;
    if(!bytes||bytes>d.ByteWidth-sourceOffset||bytes>2*1024*1024||r.bytes>r.capacity||bytes>r.capacity-r.bytes)return E_INVALIDARG;
    D3D11_BUFFER_DESC stagingDesc{bytes,D3D11_USAGE_STAGING,0,D3D11_CPU_ACCESS_READ,0,0};
    r.failureStage=21;
    ComPtr<ID3D11Buffer> staging;auto hr=device->CreateBuffer(&stagingDesc,nullptr,&staging);if(FAILED(hr))return hr;
    D3D11_BOX box{sourceOffset,0,0,sourceOffset+bytes,1,1};context->CopySubresourceRegion(staging.Get(),0,0,0,0,source,0,&box);
    r.failureStage=22;D3D11_MAPPED_SUBRESOURCE mapped{};
    hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
    if(mapped.pData)std::memcpy(reinterpret_cast<BYTE*>(r.output)+r.bytes,mapped.pData,bytes);else hr=E_FAIL;
    context->Unmap(staging.Get(),0);
    if(SUCCEEDED(hr)){b.bytes=bytes;r.bytes+=b.bytes;++r.blobCount;r.failureStage=0;}return hr;
}
HRESULT readMeshResources(ID3D11DeviceContext* context) noexcept {
    auto& r=*meshRead;r.failureStage=10;ComPtr<ID3D11Device> device;context->GetDevice(&device);
    if(!device||reinterpret_cast<uint64_t>(device.Get())!=receipt->device||device->GetCreationFlags()&D3D11_CREATE_DEVICE_SINGLETHREADED)return E_INVALIDARG;
    ComPtr<ID3D11ShaderResourceView> view;context->PSGetShaderResources(0,1,&view);
    ComPtr<ID3D11Resource> resource;if(view)view->GetResource(&resource);
    if(reinterpret_cast<uint64_t>(view.Get())!=r.view||reinterpret_cast<uint64_t>(resource.Get())!=r.resource)return E_INVALIDARG;
    ComPtr<ID3D11Texture2D> source;auto hr=resource.As(&source);if(FAILED(hr))return hr;
    r.failureStage=11;D3D11_TEXTURE2D_DESC d{};source->GetDesc(&d);
    if(d.Format!=DXGI_FORMAT_BC7_TYPELESS||d.ArraySize!=1||d.SampleDesc.Count!=1||d.Width>2048||d.Height>2048||!d.MipLevels||d.MipLevels>12)return E_INVALIDARG;
    auto& texture=r.blobs[r.blobCount];texture.resource=r.resource;texture.kind=0;texture.format=d.Format;
    texture.width=d.Width;texture.height=d.Height;texture.mipLevels=d.MipLevels;texture.arraySize=d.ArraySize;
    texture.usage=d.Usage;texture.bindFlags=d.BindFlags;texture.miscFlags=d.MiscFlags;texture.offset=r.bytes;
    d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
    r.failureStage=12;ComPtr<ID3D11Texture2D> staging;hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr))return hr;
    context->CopyResource(staging.Get(),source.Get());
    for(UINT mip=0;mip<d.MipLevels;mip++){
        auto pitch=std::max(1u,((d.Width>>mip)+3)/4)*16,rows=std::max(1u,((d.Height>>mip)+3)/4),bytes=pitch*rows;
        if(r.bytes>r.capacity||bytes>r.capacity-r.bytes)return E_INVALIDARG;
        r.failureStage=13;r.failureSlot=mip;D3D11_MAPPED_SUBRESOURCE mapped{};hr=context->Map(staging.Get(),mip,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
        if(!mapped.pData||mapped.RowPitch<pitch)hr=E_FAIL;
        else for(UINT y=0;y<rows;y++)std::memcpy(reinterpret_cast<BYTE*>(r.output)+r.bytes+y*pitch,static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch,pitch);
        context->Unmap(staging.Get(),mip);if(FAILED(hr))return hr;r.bytes+=bytes;
    }
    texture.bytes=r.bytes-texture.offset;++r.blobCount;r.failureStage=0;
    ComPtr<ID3D11InputLayout> layout;context->IAGetInputLayout(&layout);r.inputLayout=reinterpret_cast<uint64_t>(layout.Get());
    D3D11_PRIMITIVE_TOPOLOGY topology{};context->IAGetPrimitiveTopology(&topology);r.topology=topology;
    ComPtr<ID3D11Buffer> indices;DXGI_FORMAT format{};context->IAGetIndexBuffer(&indices,&format,&r.indexOffset);r.indexFormat=format;
    r.failureStage=23;const auto elementBytes=format==DXGI_FORMAT_R16_UINT?2u:format==DXGI_FORMAT_R32_UINT?4u:0u;
    if(!indices||!elementBytes||!r.indexCount||r.indexCount>65536)return E_INVALIDARG;
    const uint64_t begin=uint64_t(r.indexOffset)+uint64_t(r.indexStart)*elementBytes,bytes=uint64_t(r.indexCount)*elementBytes;
    if(begin>UINT32_MAX||bytes>UINT32_MAX)return E_INVALIDARG;
    hr=appendBuffer(context,device.Get(),indices.Get(),2,0,0,r.indexOffset,static_cast<UINT>(begin),static_cast<UINT>(bytes));
    if(FAILED(hr))return hr;
    const auto& indexBlob=r.blobs[r.blobCount-1];const auto* data=reinterpret_cast<BYTE*>(r.output)+indexBlob.offset;
    for(UINT i=0;i<r.indexCount;i++){UINT index=0;if(elementBytes==2){uint16_t word{};std::memcpy(&word,data+i*2,2);index=word;}else std::memcpy(&index,data+i*4,4);r.maxIndex=std::max(r.maxIndex,index);}
    r.failureStage=24;if(r.maxIndex==UINT32_MAX)return E_INVALIDARG;
    const auto elements=std::max(r.maxIndex+1,r.instances);
    ID3D11Buffer* vertices[8]{};UINT strides[8]{},offsets[8]{};context->IAGetVertexBuffers(0,8,vertices,strides,offsets);
    for(UINT i=0;i<8;i++){
        ComPtr<ID3D11Buffer> buffer;buffer.Attach(vertices[i]);
        if(buffer&&SUCCEEDED(hr)){
            D3D11_BUFFER_DESC desc{};buffer->GetDesc(&desc);r.failureStage=25;r.failureKind=1;r.failureSlot=i;
            const auto needed=uint64_t(elements)*strides[i];
            if(!strides[i]||offsets[i]>=desc.ByteWidth||!needed||needed>2*1024*1024)hr=E_INVALIDARG;
            else hr=appendBuffer(context,device.Get(),buffer.Get(),1,i,strides[i],offsets[i],offsets[i],
                static_cast<UINT>(std::min<uint64_t>(needed,desc.ByteWidth-offsets[i])));
        }
    }
    ComPtr<ID3D11Buffer> vs,ps;context->VSGetConstantBuffers(0,1,&vs);context->PSGetConstantBuffers(0,1,&ps);
    if(SUCCEEDED(hr))hr=appendBuffer(context,device.Get(),vs.Get(),3,0);
    if(SUCCEEDED(hr))hr=appendBuffer(context,device.Get(),ps.Get(),4,0);
    return hr;
}
void captureMeshResources(ID3D11DeviceContext* context) noexcept {
    meshRead->state=1;meshRead->hresult=readMeshResources(context);meshRead->state=SUCCEEDED(meshRead->hresult)?2:4;
    bool pending=false;
    if(meshBatch&&meshRead->state==2)
        for(uint32_t i=0;i<meshBatch->count;i++)pending=pending||meshBatch->items[i].state==0;
    if(!pending)admission.store(false,std::memory_order_release);
}
