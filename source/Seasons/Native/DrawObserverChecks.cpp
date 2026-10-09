// Real WARP Draw calls in this authored process; never a SAN14 success receipt.
#define SEASON_OBSERVER_FIXTURE
#include "DrawObserver.cpp"
#include <d3dcompiler.h>
#include <thread>
#include <memory>
#include <iostream>

std::atomic<bool> hold{false},inside{false};
__declspec(noinline) void __fastcall mockDraw(void* driver,uint32_t mode,uint32_t start,uint32_t count
#if defined(SEASON_OBSERVER_INDEXED) || defined(SEASON_OBSERVER_MESH_INSTANCED)
    ,uint32_t indexStart,uint32_t indexCount
#endif
#ifdef SEASON_OBSERVER_MESH_INSTANCED
    ,uint32_t instanceCount
#endif
    ){
    if(mode==99)return;
    auto* context=read<ID3D11DeviceContext*>(driver,0x10);
#ifdef SEASON_OBSERVER_COLOR_ONLY
    ID3D11RenderTargetView* bound{};context->OMGetRenderTargets(1,&bound,nullptr);
    const bool blockHere=bound!=nullptr;if(bound)bound->Release();
    if(blockHere){
#endif
    inside.store(true);
    while(hold.load())Sleep(1);
#ifdef SEASON_OBSERVER_COLOR_ONLY
    }
#endif
#ifdef SEASON_OBSERVER_MESH_INSTANCED
    (void)start;(void)count;context->DrawIndexedInstanced(indexCount,instanceCount,indexStart,0,0);
#elif defined(SEASON_OBSERVER_INDEXED)
    (void)start;(void)count;
    context->DrawIndexed(indexCount,indexStart,0);
#elif defined(SEASON_OBSERVER_INSTANCED)
    (void)count;
    context->DrawIndexedInstanced(read<uint32_t>(read<void*>(driver,0x90),0x1c),start,0,0,0);
#else
    context->Draw(count,start);
#endif
    auto* counter=reinterpret_cast<uint32_t*>(static_cast<BYTE*>(driver)+0x2ad0);++*counter;
}
void issue(void* driver,uint32_t mode,uint32_t start,uint32_t count){
#ifdef SEASON_OBSERVER_MESH_INSTANCED
    mockDraw(driver,mode,37,41,start,count,2);
#elif defined(SEASON_OBSERVER_INDEXED)
    mockDraw(driver,mode,37,41,start,count);
#elif defined(SEASON_OBSERVER_INSTANCED)
    (void)start;mockDraw(driver,mode,1,count);
#else
    mockDraw(driver,mode,start,count);
#endif
}
int main(int argc,char** argv){
    bool blocked=argc>1&&std::string(argv[1])=="hold";
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
    bool limited=argc>1&&std::string(argv[1])=="limit";
    bool batched=argc>1&&(std::string(argv[1])=="batch"||std::string(argv[1])=="batch-limit");
    limited=limited||(argc>1&&std::string(argv[1])=="batch-limit");
#endif
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
    struct FixtureRequest {Request base;MeshReadBatch mesh;};
    static_assert(offsetof(FixtureRequest,mesh)==sizeof(Request));
    auto storage=std::make_unique<FixtureRequest>();auto* request=&storage->base;meshRead=&storage->mesh.items[0];
    meshBatch=batched?&storage->mesh:nullptr;
#elif defined(SEASON_OBSERVER_TEXTURE_READ)
    struct FixtureRequest {Request base;TextureRead texture;};
    static_assert(offsetof(FixtureRequest,texture)==sizeof(Request));
    auto storage=std::make_unique<FixtureRequest>();auto* request=&storage->base;
    textureRead=&storage->texture;
#else
    auto storage=std::make_unique<Request>();auto* request=storage.get();
#endif
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    auto hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);
    if(FAILED(hr))return 1;
    const char shader[]=R"(
    float4 vs(uint id:SV_VertexID):SV_Position{
        float2 uv=float2((id<<1)&2,id&2);return float4(uv*float2(2,-2)+float2(-1,1),0,1);}
    float4 ps():SV_Target{return float4(0.2,0.4,0.6,1);})";
    ComPtr<ID3DBlob> v,p,e;
    if(FAILED(D3DCompile(shader,sizeof(shader),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&v,&e))||
       FAILED(D3DCompile(shader,sizeof(shader),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&p,&e)))return 2;
    ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;
    if(FAILED(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,&vs))||
       FAILED(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,&ps)))return 3;
    D3D11_TEXTURE2D_DESC desc{};desc.Width=desc.Height=16;desc.MipLevels=desc.ArraySize=1;
    desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11RenderTargetView> rt;
    if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture))||FAILED(device->CreateRenderTargetView(texture.Get(),nullptr,&rt)))return 4;
    context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);
    auto* rawRt=rt.Get();context->OMSetRenderTargets(1,&rawRt,nullptr);
    D3D11_VIEWPORT viewport{0,0,16,16,0,1};context->RSSetViewports(1,&viewport);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
#if defined(SEASON_OBSERVER_INDEXED) || defined(SEASON_OBSERVER_INSTANCED) || defined(SEASON_OBSERVER_MESH_INSTANCED)
    const uint32_t indices[]={0,1,2};
    D3D11_BUFFER_DESC ibDesc{};ibDesc.ByteWidth=sizeof(indices);
    ibDesc.Usage=D3D11_USAGE_IMMUTABLE;ibDesc.BindFlags=D3D11_BIND_INDEX_BUFFER;
    D3D11_SUBRESOURCE_DATA ibData{indices,0,0};ComPtr<ID3D11Buffer> ib;
    if(FAILED(device->CreateBuffer(&ibDesc,&ibData,&ib)))return 8;
    context->IASetIndexBuffer(ib.Get(),DXGI_FORMAT_R32_UINT,0);
#endif
    alignas(16) BYTE driver[0x3100]{};auto* raw=context.Get();std::memcpy(driver+0x10,&raw,8);
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
    // BC7 blocks and distinct IA/constant bytes must survive exact GPU readback.
    std::vector<BYTE> payload(32),output(MeshReadCapacity,0),vertexBytes(96),constantBytes(32),sharedBytes(3*1024*1024,0x59);
    for(size_t i=0;i<payload.size();i++)payload[i]=static_cast<BYTE>((i*17+23)%251);
    for(size_t i=0;i<vertexBytes.size();i++)vertexBytes[i]=static_cast<BYTE>(i+7);
    for(size_t i=0;i<constantBytes.size();i++)constantBytes[i]=static_cast<BYTE>(i+101);
    D3D11_TEXTURE2D_DESC inputDesc{};inputDesc.Width=8;inputDesc.Height=4;inputDesc.MipLevels=inputDesc.ArraySize=inputDesc.SampleDesc.Count=1;
    inputDesc.Format=DXGI_FORMAT_BC7_TYPELESS;inputDesc.Usage=D3D11_USAGE_IMMUTABLE;inputDesc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA textureBytes{payload.data(),32,32};ComPtr<ID3D11Texture2D> input;ComPtr<ID3D11ShaderResourceView> inputView;
    D3D11_SHADER_RESOURCE_VIEW_DESC viewDesc{};viewDesc.Format=DXGI_FORMAT_BC7_UNORM;viewDesc.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;viewDesc.Texture2D.MipLevels=1;
    if(FAILED(device->CreateTexture2D(&inputDesc,&textureBytes,&input))||FAILED(device->CreateShaderResourceView(input.Get(),&viewDesc,&inputView)))return 9;
    auto* rawView=inputView.Get();context->PSSetShaderResources(0,1,&rawView);
    D3D11_BUFFER_DESC vbDesc{96,D3D11_USAGE_IMMUTABLE,D3D11_BIND_VERTEX_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA vbData{vertexBytes.data(),0,0};
    ComPtr<ID3D11Buffer> vb,cb;
    if(FAILED(device->CreateBuffer(&vbDesc,&vbData,&vb)))return 9;
    UINT stride=32,offset=0;auto* rawVb=vb.Get();context->IASetVertexBuffers(0,1,&rawVb,&stride,&offset);
    // A shared source larger than the readback limit, with a nonzero IA offset.
    const UINT sharedOffset=2*1024*1024;for(UINT i=0;i<48;i++)sharedBytes[sharedOffset+i]=static_cast<BYTE>(i+151);
    D3D11_BUFFER_DESC sharedDesc{static_cast<UINT>(sharedBytes.size()),D3D11_USAGE_IMMUTABLE,D3D11_BIND_VERTEX_BUFFER,0,0,0};
    D3D11_SUBRESOURCE_DATA sharedData{sharedBytes.data(),0,0};ComPtr<ID3D11Buffer> shared;
    if(FAILED(device->CreateBuffer(&sharedDesc,&sharedData,&shared)))return 9;
    UINT sharedStride=16;auto* sharedRaw=shared.Get();context->IASetVertexBuffers(1,1,&sharedRaw,&sharedStride,&sharedOffset);
    D3D11_BUFFER_DESC cbDesc{32,D3D11_USAGE_IMMUTABLE,D3D11_BIND_CONSTANT_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA cbData{constantBytes.data(),0,0};
    if(FAILED(device->CreateBuffer(&cbDesc,&cbData,&cb)))return 9;
    auto* rawCb=cb.Get();context->VSSetConstantBuffers(0,1,&rawCb);context->PSSetConstantBuffers(0,1,&rawCb);
    meshRead->magic=MeshReadMagic;meshRead->version=MeshReadVersion;meshRead->size=sizeof(MeshRead);meshRead->indexCount=3;
    meshRead->renderThread=GetCurrentThreadId();meshRead->requireColor=1;meshRead->view=reinterpret_cast<uint64_t>(rawView);
    meshRead->resource=reinterpret_cast<uint64_t>(input.Get());meshRead->output=reinterpret_cast<uint64_t>(output.data());meshRead->capacity=limited?190:MeshReadCapacity;
    std::vector<BYTE> output2(MeshReadCapacity),output3(MeshReadCapacity);
    ComPtr<ID3D11ShaderResourceView> secondView,thirdView;
    if(batched){
        if(FAILED(device->CreateShaderResourceView(input.Get(),&viewDesc,&secondView))||
           FAILED(device->CreateShaderResourceView(input.Get(),&viewDesc,&thirdView)))return 9;
        meshBatch->magic=MeshBatchMagic;meshBatch->version=MeshBatchVersion;meshBatch->size=sizeof(MeshReadBatch);meshBatch->count=3;
        meshBatch->items[1]=*meshRead;meshBatch->items[2]=*meshRead;
        meshBatch->items[1].view=reinterpret_cast<uint64_t>(secondView.Get());meshBatch->items[1].output=reinterpret_cast<uint64_t>(output2.data());
        meshBatch->items[2].view=reinterpret_cast<uint64_t>(thirdView.Get());meshBatch->items[2].output=reinterpret_cast<uint64_t>(output3.data());
    }
#endif
#ifdef SEASON_OBSERVER_TEXTURE_READ
    std::vector<BYTE> payload(22369616),output(payload.size(),0);
    for(size_t i=0;i<payload.size();i++)payload[i]=static_cast<BYTE>((i*17+23)%251);
    D3D11_SUBRESOURCE_DATA mips[11]{};size_t at=0;
    for(UINT mip=0;mip<11;mip++){
        UINT pitch=std::max(1u,((4096u>>mip)+3)/4)*16,rows=std::max(1u,((4096u>>mip)+3)/4);
        mips[mip]={payload.data()+at,pitch,pitch*rows};at+=pitch*rows;
    }
    D3D11_TEXTURE2D_DESC inputDesc{};inputDesc.Width=inputDesc.Height=4096;inputDesc.MipLevels=11;
    inputDesc.ArraySize=inputDesc.SampleDesc.Count=1;inputDesc.Format=DXGI_FORMAT_BC3_TYPELESS;
    inputDesc.Usage=D3D11_USAGE_IMMUTABLE;inputDesc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> input;ComPtr<ID3D11ShaderResourceView> inputView;
    D3D11_SHADER_RESOURCE_VIEW_DESC viewDesc{};viewDesc.Format=DXGI_FORMAT_BC3_UNORM;
    viewDesc.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;viewDesc.Texture2D.MipLevels=11;
    if(FAILED(device->CreateTexture2D(&inputDesc,mips,&input))||
       FAILED(device->CreateShaderResourceView(input.Get(),&viewDesc,&inputView)))return 9;
    auto* rawView=inputView.Get();context->PSSetShaderResources(4,1,&rawView);
    alignas(8) BYTE psOwner[64]{};auto* owner=psOwner;std::memcpy(driver+0xb8,&owner,8);
    textureRead->magic=TextureMagic;textureRead->version=TextureVersion;textureRead->size=sizeof(TextureRead);
    textureRead->slot=4;textureRead->psOwner=reinterpret_cast<uint64_t>(owner);
    textureRead->view=reinterpret_cast<uint64_t>(rawView);textureRead->resource=reinterpret_cast<uint64_t>(input.Get());
    textureRead->output=reinterpret_cast<uint64_t>(output.data());textureRead->capacity=static_cast<uint32_t>(output.size());
#endif
#ifdef SEASON_OBSERVER_INSTANCED
    alignas(8) BYTE indexOwner[32]{};uint32_t indexCount=3;std::memcpy(indexOwner+0x1c,&indexCount,4);
    auto* index=indexOwner;std::memcpy(driver+0x90,&index,8);
#endif
    request->magic=Magic;request->version=Version;request->size=sizeof(Request);request->durationMs=blocked?50:150;
    request->context=reinterpret_cast<uint64_t>(raw);request->device=reinterpret_cast<uint64_t>(device.Get());
    DWORD result=0;bool earlyRecovery=false;
    if(blocked)hold=true;
    std::thread observer([&]{result=run(request,mockDraw,true);request->error=result;
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&request->status),result?4:2);});
    std::thread release;
    auto status=[&]{return InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&request->status),0,0);};
    while(!status()&&observer.joinable())Sleep(1);
    if(blocked)release=std::thread([&]{while(!inside.load())Sleep(1);Sleep(100);
        earlyRecovery=request->callbacksDrained||request->referencesReleased||status()==2;hold=false;});
    while(status()==1){
        issue(driver,99,0,3);
#ifdef SEASON_OBSERVER_COLOR_ONLY
        context->OMSetRenderTargets(0,nullptr,nullptr);issue(driver,0,0,3);
        context->OMSetRenderTargets(1,&rawRt,nullptr);
#endif
        issue(driver,0,0,3);
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
        if(batched){
            auto* second=secondView.Get();context->PSSetShaderResources(0,1,&second);issue(driver,0,0,3);
            auto* third=thirdView.Get();context->PSSetShaderResources(0,1,&third);issue(driver,0,0,3);
            context->PSSetShaderResources(0,1,&rawView);
        }
#endif
        Sleep(1);
    }
    observer.join();if(release.joinable())release.join();
    bool valid=result==0&&request->status==2&&request->records>0&&request->skipped>0&&!earlyRecovery&&
        request->codeRestored&&request->protectionRestored&&request->callbacksDrained&&request->referencesReleased;
    for(int i=0;i<request->records;i++){auto& r=request->rows[i];valid=valid&&r.complete==1&&r.context==request->context&&
        r.device==request->device&&r.contextType==D3D11_DEVICE_CONTEXT_IMMEDIATE&&r.ps==reinterpret_cast<uint64_t>(ps.Get())&&
        r.vs==reinterpret_cast<uint64_t>(vs.Get())&&r.targets[0].resource==reinterpret_cast<uint64_t>(texture.Get())&&
        r.counterAfter==r.counterBefore+1&&r.thread==GetCurrentThreadId()&&r.count==3&&r.start==0;
#ifdef SEASON_OBSERVER_MESH_INSTANCED
        valid=valid&&r.instances==2&&r.drawKind==3;
#endif
    }
    // Execute the restored original once more, then independently read the RT.
    issue(driver,0,0,3);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;if(FAILED(device->CreateTexture2D(&desc,nullptr,&staging)))return 5;
    context->CopyResource(staging.Get(),texture.Get());D3D11_MAPPED_SUBRESOURCE mapped{};
    if(FAILED(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped)))return 6;
    for(unsigned y=0;y<16;y++)for(unsigned x=0;x<16;x++){
        auto* pixel=static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch+x*4;
        valid=valid&&pixel[0]==51&&pixel[1]==102&&pixel[2]==153&&pixel[3]==255;}
    context->Unmap(staging.Get(),0);
    ComPtr<ID3D11PixelShader> after;context->PSGetShader(&after,nullptr,nullptr);valid=valid&&after.Get()==ps.Get();
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
    meshRead=&storage->mesh.items[0];
    ComPtr<ID3D11ShaderResourceView> finalView;context->PSGetShaderResources(0,1,&finalView);
    valid=valid&&finalView.Get()==inputView.Get()&&meshRead->maxIndex==2;
    if(limited)valid=valid&&meshRead->state==4&&meshRead->hresult==static_cast<uint32_t>(E_INVALIDARG)&&meshRead->blobCount==4&&
        meshRead->failureStage==20&&meshRead->failureKind==3&&meshRead->failureSlot==0;
    else valid=valid&&meshRead->state==2&&!meshRead->hresult&&meshRead->blobCount==6;
    for(UINT i=0;i<meshRead->blobCount;i++){
        auto& b=meshRead->blobs[i];const BYTE* expected=b.kind==0?payload.data():b.kind==1?(b.slot?sharedBytes.data()+sharedOffset:vertexBytes.data()):b.kind==2?reinterpret_cast<const BYTE*>(indices):constantBytes.data();
        auto length=b.kind==0?payload.size():b.kind==1?(b.slot?48:vertexBytes.size()):b.kind==2?sizeof(indices):constantBytes.size();
        valid=valid&&b.bytes==length&&std::memcmp(output.data()+b.offset,expected,length)==0;
        if(b.kind==1&&b.slot==1)valid=valid&&b.width==sharedBytes.size()&&b.sourceOffset==sharedOffset;
    }
    if(batched){
        valid=valid&&request->records==(limited?1:3);
        for(UINT i=1;i<3;i++){
            const auto& item=meshBatch->items[i];const auto& bytes=i==1?output2:output3;
            valid=valid&&(limited?(item.state==0&&item.bytes==0):
                (item.state==2&&!item.hresult&&item.bytes==meshRead->bytes&&item.blobCount==meshRead->blobCount&&
                 std::memcmp(bytes.data(),output.data(),item.bytes)==0));
        }
    }
#endif
#ifdef SEASON_OBSERVER_TEXTURE_READ
    ComPtr<ID3D11ShaderResourceView> finalView;context->PSGetShaderResources(4,1,&finalView);
    valid=valid&&textureRead->state==2&&textureRead->hresult==0&&textureRead->bytes==payload.size()&&
        output==payload&&finalView.Get()==inputView.Get();
    valid=valid&&textureRead->usage==D3D11_USAGE_IMMUTABLE&&textureRead->bindFlags==D3D11_BIND_SHADER_RESOURCE&&
        textureRead->viewFormat==DXGI_FORMAT_BC3_UNORM&&textureRead->viewDimension==D3D11_SRV_DIMENSION_TEXTURE2D;
    std::vector<BYTE> controlPayload(16793604),controlOutput(controlPayload.size());
    for(size_t i=0;i<controlPayload.size();i++)controlPayload[i]=static_cast<BYTE>((i*11+7)%251);
    D3D11_TEXTURE2D_DESC controlDesc{2049,2049,1,1,DXGI_FORMAT_R8G8B8A8_TYPELESS,{1,0},D3D11_USAGE_DEFAULT,
        D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET,0,0};
    D3D11_SUBRESOURCE_DATA controlInit{controlPayload.data(),2049*4,0};ComPtr<ID3D11Texture2D> controlTexture;
    ComPtr<ID3D11ShaderResourceView> controlView;D3D11_SHADER_RESOURCE_VIEW_DESC controlViewDesc{};
    controlViewDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;controlViewDesc.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;controlViewDesc.Texture2D.MipLevels=1;
    if(FAILED(device->CreateTexture2D(&controlDesc,&controlInit,&controlTexture))||
       FAILED(device->CreateShaderResourceView(controlTexture.Get(),&controlViewDesc,&controlView)))return 10;
    auto* controlRaw=controlView.Get();context->PSSetShaderResources(3,1,&controlRaw);
    *textureRead={};textureRead->slot=3;textureRead->psOwner=reinterpret_cast<uint64_t>(owner);
    textureRead->view=reinterpret_cast<uint64_t>(controlRaw);textureRead->resource=reinterpret_cast<uint64_t>(controlTexture.Get());
    textureRead->output=reinterpret_cast<uint64_t>(controlOutput.data());textureRead->capacity=static_cast<UINT>(controlOutput.size());
    Record controlRecord{};controlRecord.psOwner=textureRead->psOwner;controlRecord.views[3].object=textureRead->view;
    controlRecord.views[3].resource=textureRead->resource;captureTexture(context.Get(),controlRecord);
    valid=valid&&textureRead->state==2&&!textureRead->hresult&&controlOutput==controlPayload&&
        textureRead->usage==D3D11_USAGE_DEFAULT&&textureRead->bindFlags==controlDesc.BindFlags&&
        textureRead->viewFormat==controlViewDesc.Format&&textureRead->viewMipLevels==1&&textureRead->sampleCount==1;
#endif
    std::cout<<"{\"ownWarpProcess\":true,\"gameAccess\":false,\"records\":"<<request->records<<
        ",\"skipped\":"<<request->skipped<<",\"originalPixels\":256,\"error\":"<<result<<
        ",\"failedStage\":"<<request->failedStage<<",\"failedThread\":"<<request->failedThread<<
        ",\"failedThreadExitRead\":"<<request->failedThreadExitRead<<",\"failedThreadExit\":"<<request->failedThreadExit<<
        ",\"blockedCallback\":"<<(blocked?"true":"false")<<",\"earlyRecovery\":"<<(earlyRecovery?"true":"false")<<
#ifdef SEASON_OBSERVER_MESH_INSTANCED
        ",\"gpuMethod\":\"DrawIndexedInstanced\",\"engineArguments\":7"<<
#elif defined(SEASON_OBSERVER_INDEXED)
        ",\"gpuMethod\":\"DrawIndexed\""<<
#elif defined(SEASON_OBSERVER_INSTANCED)
        ",\"gpuMethod\":\"DrawIndexedInstanced\""<<
#else
        ",\"gpuMethod\":\"Draw\""<<
#endif
#ifdef SEASON_OBSERVER_TEXTURE_READ
        ",\"gpuTextureMipReadbackBytes\":"<<textureRead->bytes<<
#endif
#ifdef SEASON_OBSERVER_MESH_RESOURCE_READ
        ",\"batchCount\":"<<(batched?3:1)<<
        ",\"gpuReadbackBytes\":"<<meshRead->bytes<<",\"gpuReadbackBlobs\":"<<meshRead->blobCount<<
        ",\"limitedOutputFixture\":"<<(limited?"true":"false")<<",\"readbackState\":"<<meshRead->state<<",\"failureStage\":"<<meshRead->failureStage<<
#endif
        ",\"passed\":"<<(valid?"true":"false")<<"}\n";
    return valid?0:7;
}
