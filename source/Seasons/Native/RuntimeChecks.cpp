// Real D3D11 WARP validation in this separate process. Does not attach to SAN14.
#include "RuntimeGuard.h"
#include <d3dcompiler.h>
#include <stdio.h>
#include <string.h>
#include <thread>
#include <latch>
using namespace seasons;
static int checks=0;
static void require(bool value,const char* label){if(!value){fprintf(stderr,"FAIL: %s\n",label);exit(1);}++checks;}
static void hr(HRESULT value){require(SUCCEEDED(value),"D3D11 creation/API result");}

// The previous lease expressions, retained only as a pure regression fixture.
// This fixture has no context, GPU resources, process handle or Hook code.
struct LegacyLease {
    uint64_t heartbeat;bool stopped=false;
    void beat(uint64_t now){if(!stopped)heartbeat=now;}
    void expire(uint64_t now){if(now-heartbeat>=50)stopped=true;}
};
static void leaseRegressions(){
    LegacyLease priorTimer{100};priorTimer.beat(130);priorTimer.expire(129);
    require(priorTimer.stopped,"old timer/heartbeat ordering failure reproduced");
    LegacyLease priorRenewal{100};priorRenewal.beat(130);priorRenewal.beat(120);priorRenewal.expire(170);
    require(priorRenewal.stopped,"old heartbeat timestamp regression reproduced");
    LegacyLease priorLate{100};priorLate.beat(150);priorLate.expire(150);
    require(!priorLate.stopped,"old expired lease revival reproduced");

    StopGate timer(1,100,50);require(timer.beat(130),"timely renewal accepted");timer.expire(129);
    require(!timer.stopping(),"older timer sample cannot expire newer heartbeat");
    timer.expire(179);require(!timer.stopping(),"renewed lease remains valid below boundary");
    timer.expire(180);require(timer.drained(),"renewed lease expires at exact boundary");
    StopGate renewal(1,100,50);require(renewal.beat(130)&&renewal.beat(120),"out of order timely renewal does not fail");
    renewal.expire(170);require(!renewal.stopping(),"older heartbeat cannot replace newer timestamp");
    renewal.expire(180);require(renewal.drained(),"out of order renewal does not extend deadline");
    StopGate late(1,100,50);require(!late.beat(150)&&late.drained(),"exact-boundary renewal cannot revive expired lease");
    require(!late.beat(151),"stopped lease never renews");
    StopGate wrapped(1,UINT64_MAX-20,50);require(wrapped.beat(10),"renewal across tick wrap accepted");
    wrapped.expire(9);require(!wrapped.stopping(),"older sample across tick wrap ignored");
    wrapped.expire(59);require(!wrapped.stopping(),"wrapped renewed lease remains valid");
    wrapped.expire(60);require(wrapped.drained(),"wrapped renewed deadline expires exactly");
    // Two captured timestamps are delivered by different threads, in the
    // reverse order. Latches make the regression repeatable rather than timed.
    StopGate concurrent(1,100,50);std::latch renewed(1);
    std::thread newer([&]{concurrent.beat(130);renewed.count_down();});
    std::thread older([&]{renewed.wait();concurrent.beat(120);concurrent.expire(129);});
    newer.join();older.join();concurrent.expire(170);
    require(!concurrent.stopping(),"concurrent old renewal/timer cannot expire current lease");
    concurrent.stop();require(concurrent.drained(),"concurrent lease stops without leaked callback count");
}

static UINT counterValue(ID3D11Device* device,ID3D11DeviceContext* context,ID3D11UnorderedAccessView* uav,bool disablePredication=true){
    ComPtr<ID3D11Predicate> predicate;BOOL predicateValue=FALSE;
    if(disablePredication){context->GetPredication(&predicate,&predicateValue);context->SetPredication(nullptr,FALSE);}
    D3D11_BUFFER_DESC desc{4,D3D11_USAGE_DEFAULT,0,0,0,0};ComPtr<ID3D11Buffer> counter,readback;
    UINT zero=0;D3D11_SUBRESOURCE_DATA initial{&zero,0,0};
    hr(device->CreateBuffer(&desc,&initial,&counter));desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    hr(device->CreateBuffer(&desc,&initial,&readback));context->CopyStructureCount(counter.Get(),0,uav);context->CopyResource(readback.Get(),counter.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(readback.Get(),0,D3D11_MAP_READ,0,&mapped));
    UINT result;memcpy(&result,mapped.pData,sizeof(result));context->Unmap(readback.Get(),0);
    if(disablePredication)context->SetPredication(predicate.Get(),predicateValue);return result;
}

static void streamContinuation(ID3D11Device* device,ID3D11DeviceContext* context,DWORD renderThread){
    PipelineScope original(context,renderThread);context->ClearState();
    const char vertex[]="cbuffer Marker:register(b0){float4 marker;}float4 main():SV_Position{return marker;}";
    const char geometry[]="struct O{float4 value:TEXCOORD0;};[maxvertexcount(1)] void main(point float4 p[1]:SV_Position,inout PointStream<O> stream){O o;o.value=p[0];stream.Append(o);}";
    ComPtr<ID3DBlob> vsCode,gsCode,errors;ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11GeometryShader> gs;
    hr(D3DCompile(vertex,sizeof(vertex)-1,nullptr,nullptr,nullptr,"main","vs_5_0",D3DCOMPILE_WARNINGS_ARE_ERRORS,0,&vsCode,&errors));
    hr(D3DCompile(geometry,sizeof(geometry)-1,nullptr,nullptr,nullptr,"main","gs_5_0",D3DCOMPILE_WARNINGS_ARE_ERRORS,0,&gsCode,&errors));
    hr(device->CreateVertexShader(vsCode->GetBufferPointer(),vsCode->GetBufferSize(),nullptr,&vs));
    D3D11_SO_DECLARATION_ENTRY entry{0,"TEXCOORD",0,0,4,0};UINT stride=16;
    hr(device->CreateGeometryShaderWithStreamOutput(gsCode->GetBufferPointer(),gsCode->GetBufferSize(),&entry,1,&stride,1,D3D11_SO_NO_RASTERIZED_STREAM,nullptr,&gs));
    float zeros[32]{};D3D11_SUBRESOURCE_DATA initial{zeros,0,0};
    D3D11_BUFFER_DESC desc{sizeof(zeros),D3D11_USAGE_DEFAULT,D3D11_BIND_STREAM_OUTPUT,0,0,0};
    ComPtr<ID3D11Buffer> gameStream,effectStream,readback;
    hr(device->CreateBuffer(&desc,&initial,&gameStream));hr(device->CreateBuffer(&desc,&initial,&effectStream));
    desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;hr(device->CreateBuffer(&desc,nullptr,&readback));
    float marker[4]{1,0,0,1},ownMarker[4]{21,0,0,1};ComPtr<ID3D11Buffer> gameMarker,effectMarker;
    D3D11_BUFFER_DESC markerDesc{16,D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};
    D3D11_SUBRESOURCE_DATA markerInitial{marker,0,0},ownInitial{ownMarker,0,0};
    hr(device->CreateBuffer(&markerDesc,&markerInitial,&gameMarker));hr(device->CreateBuffer(&markerDesc,&ownInitial,&effectMarker));
    ID3D11Buffer* originalCb=gameMarker.Get();context->VSSetConstantBuffers(0,1,&originalCb);
    context->VSSetShader(vs.Get(),nullptr,0);context->GSSetShader(gs.Get(),nullptr,0);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_POINTLIST);
    ID3D11Buffer* target=gameStream.Get();UINT offset=16;context->SOSetTargets(1,&target,&offset);context->Draw(1,0);
    {
        PipelineScope effect(context,renderThread);context->ClearState();
        context->VSSetShader(vs.Get(),nullptr,0);context->GSSetShader(gs.Get(),nullptr,0);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_POINTLIST);
        ID3D11Buffer* effectCb=effectMarker.Get();context->VSSetConstantBuffers(0,1,&effectCb);
        ID3D11Buffer* ownTarget=effectStream.Get();UINT ownOffset=0;context->SOSetTargets(1,&ownTarget,&ownOffset);context->Draw(1,20);
    }
    // No SO rebind here: the second draw must continue from the first draw's
    // hidden position, rather than restart from its initial byte offset 16.
    ComPtr<ID3D11VertexShader> restoredVs;ComPtr<ID3D11GeometryShader> restoredGs;ComPtr<ID3D11Buffer> restoredCb,restoredSo;
    context->VSGetShader(&restoredVs,nullptr,nullptr);context->GSGetShader(&restoredGs,nullptr,nullptr);
    context->VSGetConstantBuffers(0,1,&restoredCb);context->SOGetTargets(1,&restoredSo);
    require(restoredVs==vs&&restoredGs==gs&&restoredCb==gameMarker&&restoredSo==gameStream,"stream fixture VS/GS/CB/SO bindings restored");
    marker[0]=11;context->UpdateSubresource(gameMarker.Get(),0,nullptr,marker,0,0);
    context->Draw(1,0);context->SOSetTargets(0,nullptr,nullptr);
    context->CopyResource(readback.Get(),gameStream.Get());D3D11_MAPPED_SUBRESOURCE mapped{};
    hr(context->Map(readback.Get(),0,D3D11_MAP_READ,0,&mapped));float data[32];memcpy(data,mapped.pData,sizeof(data));context->Unmap(readback.Get(),0);
    require(data[4]==1&&data[7]==1,"stream output first draw retains original offset");
    if(data[8]!=11||data[11]!=1)fprintf(stderr,"SO readback: byte16=(%g,%g,%g,%g), byte32=(%g,%g,%g,%g), byte48=(%g,%g,%g,%g)\n",data[4],data[5],data[6],data[7],data[8],data[9],data[10],data[11],data[12],data[13],data[14],data[15]);
    require(data[8]==11&&data[11]==1,"stream output hidden write position continues after state restore");
    require(data[0]==0&&data[12]==0,"effect draw does not alter original stream output buffer");
    context->CopyResource(readback.Get(),effectStream.Get());hr(context->Map(readback.Get(),0,D3D11_MAP_READ,0,&mapped));
    memcpy(data,mapped.pData,sizeof(data));context->Unmap(readback.Get(),0);
    require(data[0]==21&&data[3]==1&&data[4]==0,"isolated effect stream writes its own target only");
}

struct Observed {
    ComPtr<ID3D11Predicate> predicate;BOOL predicateValue=FALSE;
    ComPtr<ID3D11Buffer> streamOutput;ComPtr<ID3D11UnorderedAccessView> csUav,omUav;
    ComPtr<ID3D11PixelShader> ps;ComPtr<ID3D11Buffer> cb,vb,ib;ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11SamplerState> sampler;ComPtr<ID3D11RenderTargetView> rt;ComPtr<ID3D11DepthStencilView> dsv;
    ComPtr<ID3D11BlendState> blend;FLOAT factors[4];UINT mask;
    ComPtr<ID3D11DepthStencilState> ds;UINT ref;
    ComPtr<ID3D11RasterizerState> rs;UINT nv=1,nr=1;D3D11_VIEWPORT viewport;D3D11_RECT rect;
    UINT stride,offset,indexOffset;DXGI_FORMAT format;D3D11_PRIMITIVE_TOPOLOGY topology;
    explicit Observed(ID3D11DeviceContext* c){
        c->PSGetShader(&ps,nullptr,nullptr);c->PSGetConstantBuffers(3,1,&cb);c->IAGetVertexBuffers(2,1,&vb,&stride,&offset);
        c->IAGetIndexBuffer(&ib,&format,&indexOffset);c->PSGetShaderResources(7,1,&srv);c->PSGetSamplers(4,1,&sampler);
        c->OMGetRenderTargets(1,&rt,&dsv);c->OMGetBlendState(&blend,factors,&mask);c->OMGetDepthStencilState(&ds,&ref);
        c->RSGetState(&rs);c->RSGetViewports(&nv,&viewport);c->RSGetScissorRects(&nr,&rect);c->IAGetPrimitiveTopology(&topology);
        c->GetPredication(&predicate,&predicateValue);c->SOGetTargets(1,&streamOutput);
        c->CSGetUnorderedAccessViews(2,1,&csUav);c->OMGetRenderTargetsAndUnorderedAccessViews(0,nullptr,nullptr,6,1,&omUav);
    }
    void equals(const Observed& b)const{
        require(ps==b.ps,"PS restored");require(cb==b.cb,"constant buffer restored");require(vb==b.vb,"vertex buffer restored");
        require(ib==b.ib && format==b.format && indexOffset==b.indexOffset,"index buffer restored");
        require(stride==b.stride && offset==b.offset,"vertex stride/offset restored");require(srv==b.srv,"SRV restored");
        require(sampler==b.sampler,"sampler restored");require(rt==b.rt && dsv==b.dsv,"targets/depth restored");
        require(blend==b.blend && mask==b.mask && !memcmp(factors,b.factors,sizeof(factors)),"blend restored");
        require(ds==b.ds && ref==b.ref,"depth/stencil restored");require(rs==b.rs,"raster state restored");
        require(nv==b.nv && !memcmp(&viewport,&b.viewport,sizeof(viewport)),"viewport restored");
        require(nr==b.nr && !memcmp(&rect,&b.rect,sizeof(rect)),"scissors restored");require(topology==b.topology,"topology restored");
        require(predicate==b.predicate && predicateValue==b.predicateValue,"predication restored");
        require(streamOutput==b.streamOutput,"stream output restored");require(csUav==b.csUav,"compute UAV restored");
        require(omUav==b.omUav,"output-merger UAV restored");
    }
};
int main(){
    leaseRegressions();
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL level;
    hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context));
    ComPtr<ID3D11Buffer> buffer;D3D11_BUFFER_DESC bd{256,D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};hr(device->CreateBuffer(&bd,nullptr,&buffer));
    ComPtr<ID3D11Buffer> geometry;bd.BindFlags=D3D11_BIND_VERTEX_BUFFER|D3D11_BIND_INDEX_BUFFER;hr(device->CreateBuffer(&bd,nullptr,&geometry));
    ComPtr<ID3D11Texture2D> texture,target,depthTexture;
    D3D11_TEXTURE2D_DESC td{16,16,1,1,DXGI_FORMAT_R8G8B8A8_UNORM,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_SHADER_RESOURCE,0,0};
    hr(device->CreateTexture2D(&td,nullptr,&texture));td.BindFlags=D3D11_BIND_RENDER_TARGET;hr(device->CreateTexture2D(&td,nullptr,&target));
    td.Format=DXGI_FORMAT_D32_FLOAT;td.BindFlags=D3D11_BIND_DEPTH_STENCIL;hr(device->CreateTexture2D(&td,nullptr,&depthTexture));
    ComPtr<ID3D11ShaderResourceView> srv;ComPtr<ID3D11RenderTargetView> rtv;ComPtr<ID3D11DepthStencilView> depth;
    hr(device->CreateShaderResourceView(texture.Get(),nullptr,&srv));hr(device->CreateRenderTargetView(target.Get(),nullptr,&rtv));
    hr(device->CreateDepthStencilView(depthTexture.Get(),nullptr,&depth));
    ComPtr<ID3D11SamplerState> sampler;D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;sd.MaxLOD=D3D11_FLOAT32_MAX;hr(device->CreateSamplerState(&sd,&sampler));
    ComPtr<ID3D11BlendState> blend;D3D11_BLEND_DESC blendDesc{};blendDesc.RenderTarget[0].RenderTargetWriteMask=15;hr(device->CreateBlendState(&blendDesc,&blend));
    ComPtr<ID3D11DepthStencilState> ds;D3D11_DEPTH_STENCIL_DESC dsDesc{};dsDesc.DepthEnable=TRUE;dsDesc.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL;
    dsDesc.DepthFunc=D3D11_COMPARISON_LESS;hr(device->CreateDepthStencilState(&dsDesc,&ds));
    ComPtr<ID3D11RasterizerState> raster;D3D11_RASTERIZER_DESC rd{};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_BACK;
    rd.ScissorEnable=TRUE;rd.DepthClipEnable=TRUE;hr(device->CreateRasterizerState(&rd,&raster));
    const char source[]="float4 main():SV_Target{return float4(0.2,0.3,0.4,1);}";
    ComPtr<ID3DBlob> code,errors;hr(D3DCompile(source,sizeof(source)-1,nullptr,nullptr,nullptr,"main","ps_5_0",0,0,&code,&errors));
    ComPtr<ID3D11PixelShader> shader;hr(device->CreatePixelShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&shader));
    ID3D11Buffer* cb=buffer.Get();ID3D11Buffer* vb=geometry.Get();ID3D11ShaderResourceView* view=srv.Get();ID3D11SamplerState* smp=sampler.Get();
    ID3D11RenderTargetView* rt=rtv.Get();UINT stride=16,offset=8;FLOAT factors[4]{0.1f,0.2f,0.3f,0.4f};D3D11_VIEWPORT vp{1,2,12,10,0.1f,0.9f};D3D11_RECT rect{1,2,12,13};
    context->PSSetShader(shader.Get(),nullptr,0);context->PSSetConstantBuffers(3,1,&cb);context->PSSetShaderResources(7,1,&view);
    context->PSSetSamplers(4,1,&smp);context->OMSetRenderTargets(1,&rt,depth.Get());context->OMSetBlendState(blend.Get(),factors,0x12345678);
    context->OMSetDepthStencilState(ds.Get(),23);context->RSSetState(raster.Get());context->RSSetViewports(1,&vp);context->RSSetScissorRects(1,&rect);
    context->IASetVertexBuffers(2,1,&vb,&stride,&offset);context->IASetIndexBuffer(geometry.Get(),DXGI_FORMAT_R16_UINT,4);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    ComPtr<ID3D11Predicate> predicate;D3D11_QUERY_DESC query{D3D11_QUERY_OCCLUSION_PREDICATE,0};hr(device->CreatePredicate(&query,&predicate));
    context->Begin(predicate.Get());context->End(predicate.Get());context->Flush();BOOL predicateResult=TRUE;HRESULT queryResult=S_FALSE;
    for(int attempt=0;attempt<2000 && queryResult==S_FALSE;++attempt){
        queryResult=context->GetData(predicate.Get(),&predicateResult,sizeof(predicateResult),0);
        if(queryResult==S_FALSE)Sleep(1);
    }
    require(queryResult==S_OK&&predicateResult==FALSE,"empty occlusion predicate completed before state/readback checks");
    context->SetPredication(predicate.Get(),TRUE);
    ComPtr<ID3D11Buffer> streamOutput;bd.BindFlags=D3D11_BIND_STREAM_OUTPUT;hr(device->CreateBuffer(&bd,nullptr,&streamOutput));
    ID3D11Buffer* stream=streamOutput.Get();UINT soOffset=12;context->SOSetTargets(1,&stream,&soOffset);
    ComPtr<ID3D11Buffer> computeBuffer,outputBuffer;bd.BindFlags=D3D11_BIND_UNORDERED_ACCESS;bd.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;bd.StructureByteStride=4;
    hr(device->CreateBuffer(&bd,nullptr,&computeBuffer));hr(device->CreateBuffer(&bd,nullptr,&outputBuffer));
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud{};ud.Format=DXGI_FORMAT_UNKNOWN;ud.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;
    ud.Buffer.NumElements=64;ud.Buffer.Flags=D3D11_BUFFER_UAV_FLAG_COUNTER;
    ComPtr<ID3D11UnorderedAccessView> computeUav,outputUav;hr(device->CreateUnorderedAccessView(computeBuffer.Get(),&ud,&computeUav));
    hr(device->CreateUnorderedAccessView(outputBuffer.Get(),&ud,&outputUav));ID3D11UnorderedAccessView* cu=computeUav.Get();UINT computeCount=4;
    context->CSSetUnorderedAccessViews(2,1,&cu,&computeCount);ID3D11UnorderedAccessView* ou=outputUav.Get();UINT outputCount=7;
    context->OMSetRenderTargetsAndUnorderedAccessViews(1,&rt,depth.Get(),6,1,&ou,&outputCount);
    const UINT predicatedCs=counterValue(device.Get(),context.Get(),computeUav.Get(),false);
    const UINT predicatedOm=counterValue(device.Get(),context.Get(),outputUav.Get(),false);
    const UINT initialCs=counterValue(device.Get(),context.Get(),computeUav.Get());
    const UINT initialOm=counterValue(device.Get(),context.Get(),outputUav.Get());
    require(initialCs==4,"initial compute UAV counter read back without predication");
    require(initialOm==7,"initial output merger UAV counter read back without predication");
    Observed before(context.Get());
    const DWORD renderThread=GetCurrentThreadId();
    for(int i=0;i<100;++i){PipelineScope scope(context.Get(),renderThread);require(scope.restorable(),"complete prior context state captured");context->ClearState();}
    before.equals(Observed(context.Get()));
    try{PipelineScope scope(context.Get(),renderThread);context->ClearState();throw 1;}catch(int){}
    before.equals(Observed(context.Get()));
    StopGate gate(7,100,50);require(gate.enter(7,{194,12,30},101),"month boundary admitted");gate.leave();
    require(gate.enter(7,{195,1,1},102),"year boundary admitted");gate.leave();
    require(gate.enter(7,{193,3,1},103),"backward date admitted");gate.leave();
    require(!gate.enter(7,{194,13,1},104) && gate.drained(),"invalid date stops admission");
    StopGate changed(7,100,50);require(!changed.enter(8,{194,1,1},101)&&changed.drained(),"scene generation change stops admission");
    StopGate lease(7,100,50);lease.beat(130);require(lease.enter(7,{194,1,1},179),"heartbeat renewed");
    lease.stop();require(!lease.drained(),"OFF waits for active frame");require(!lease.enter(7,{194,1,1},179),"OFF prevents later frame");
    lease.leave();require(lease.drained(),"OFF drain verified");
    StopGate expiry(7,UINT64_MAX-20,50);expiry.expire(28);require(!expiry.stopping(),"lease wrap remains valid");
    expiry.expire(29);require(expiry.drained(),"lease wrap expires at exact boundary");
    auto session=std::make_shared<ContextSession>(context.Get(),7,5000);
    std::weak_ptr<ContextSession> weak=session;
    auto owner=session.get();
    const bool rendered=owner->render(context.Get(),7,{194,1,1},[&](ID3D11DeviceContext* active){
        session->stop();session.reset();require(!weak.expired(),"frame token retains context during OFF");
        require(!weak.lock()->drained(),"callback not drained before pipeline restoration");active->ClearState();
    });
    require(rendered,"owned context synchronous rendering admitted");before.equals(Observed(context.Get()));
    require(weak.expired(),"session released after restored frame drains");
    auto unattended=std::make_shared<ContextSession>(context.Get(),9,50);Sleep(200);
    require(unattended->drained(),"native watchdog stops even without render callback");
    bool invoked=false;
    require(!unattended->render(context.Get(),9,{194,1,1},[&](ID3D11DeviceContext*){invoked=true;}),"expired session cannot apply later frame");
    require(!invoked&&!unattended->heartbeat(),"expired session rejects both effect and renewal");
    auto wrongContext=std::make_shared<ContextSession>(context.Get(),10,5000);
    ComPtr<ID3D11Device> other;ComPtr<ID3D11DeviceContext> otherContext;
    hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&other,&level,&otherContext));
    require(!wrongContext->render(otherContext.Get(),10,{194,1,1},[](ID3D11DeviceContext*){})&&wrongContext->drained(),"different device/context rejected");
    auto wrongThread=std::make_shared<ContextSession>(context.Get(),12,5000);
    bool wrongThreadResult=true,wrongThreadInvoked=false;
    std::thread foreign([&]{wrongThreadResult=wrongThread->render(context.Get(),12,{194,1,1},[&](ID3D11DeviceContext*){wrongThreadInvoked=true;});});foreign.join();
    require(!wrongThreadResult&&!wrongThreadInvoked&&wrongThread->drained(),"wrong render thread rejected before effect runs");
    before.equals(Observed(context.Get()));
    bool rejectedScope=false;
    std::thread foreignScope([&]{try{PipelineScope scope(context.Get(),renderThread);}catch(const std::invalid_argument&){rejectedScope=true;}});foreignScope.join();
    require(rejectedScope,"standalone pipeline scope rejects foreign thread");before.equals(Observed(context.Get()));
    ComPtr<ID3D11DeviceContext> deferred;hr(device->CreateDeferredContext(0,&deferred));bool deferredRejected=false;
    try{auto invalid=std::make_shared<ContextSession>(deferred.Get(),13,5000);}catch(const std::invalid_argument&){deferredRejected=true;}
    require(deferredRejected,"deferred context rejected before watchdog creation");
    auto otherState=CreatePipelineState(otherContext.Get());bool otherStateRejected=false;
    try{PipelineScope invalid(context.Get(),renderThread,otherState.Get());}catch(const std::invalid_argument&){otherStateRejected=true;}
    require(otherStateRejected,"pipeline state from another device rejected before swap");before.equals(Observed(context.Get()));
    auto failure=std::make_shared<ContextSession>(context.Get(),14,5000);bool effectThrew=false;
    try{failure->render(context.Get(),14,{194,1,1},[](ID3D11DeviceContext* active){active->ClearState();throw std::runtime_error("Effect failure fixture");});}
    catch(const std::runtime_error&){effectThrew=true;}
    require(effectThrew&&failure->drained(),"effect exception restores before stopped callback drains");before.equals(Observed(context.Get()));
    auto interrupted=std::make_shared<ContextSession>(context.Get(),15,5000);
    std::latch inEffect(1),offRequested(1);bool offSawBusy=false;
    std::thread off([&]{inEffect.wait();interrupted->stop();offSawBusy=!interrupted->drained();offRequested.count_down();});
    const bool interruptedResult=interrupted->render(context.Get(),15,{194,1,1},[&](ID3D11DeviceContext* active){
        inEffect.count_down();offRequested.wait();active->ClearState();
    });off.join();
    require(interruptedResult&&offSawBusy&&interrupted->drained(),"concurrent OFF waits for restored synchronous frame");before.equals(Observed(context.Get()));
    auto timed=std::make_shared<ContextSession>(context.Get(),16,50);
    const bool timedResult=timed->render(context.Get(),16,{194,1,1},[&](ID3D11DeviceContext* active){
        active->ClearState();Sleep(200);require(timed->stopping()&&!timed->drained(),"watchdog closes admission while effect still owns state");
    });
    require(timedResult&&timed->drained(),"watchdog-expired frame restores before drain");before.equals(Observed(context.Get()));
    auto recursive=std::make_shared<ContextSession>(context.Get(),17,5000);bool nestedInvoked=false;
    require(recursive->render(context.Get(),17,{194,1,1},[&](ID3D11DeviceContext* active){
        require(!recursive->render(active,17,{194,1,1},[&](ID3D11DeviceContext*){nestedInvoked=true;}),"reentrant effect rejected before second state swap");active->ClearState();
    }),"outer effect retains original scope during reentry rejection");
    require(!nestedInvoked&&recursive->drained(),"reentry closes admission without leaking active frame");before.equals(Observed(context.Get()));
    auto repeatedFrames=std::make_shared<ContextSession>(context.Get(),18,5000);
    for(int i=0;i<100;++i)require(repeatedFrames->render(context.Get(),18,{194,1,1},[](ID3D11DeviceContext* active){active->ClearState();}),"reusable session state renders consecutive frames");
    repeatedFrames->stop();require(repeatedFrames->drained(),"reusable pipeline state drains after repeated frames");before.equals(Observed(context.Get()));
    const UINT finalCs=counterValue(device.Get(),context.Get(),computeUav.Get()),finalOm=counterValue(device.Get(),context.Get(),outputUav.Get());
    require(finalCs==initialCs,"compute UAV counter preserved after state isolation and restoration");
    require(finalOm==initialOm,"output merger UAV counter preserved after state isolation and restoration");
    streamContinuation(device.Get(),context.Get(),renderThread);before.equals(Observed(context.Get()));
    for(int i=0;i<1000;++i){auto repeated=std::make_shared<ContextSession>(context.Get(),11,5000);repeated->stop();require(repeated->drained(),"repeated session drained");}
    printf("{\"mode\":\"Separate D3D11 WARP process; no game attachment\",\"legacyLeaseFailuresReproduced\":3,\"synchronousFrameCycles\":100,\"hiddenStreamPositionVerified\":true,\"uavCounterValuesVerified\":true,\"predicatedCounterReadback\":[%u,%u],\"counterBefore\":[%u,%u],\"counterAfter\":[%u,%u],\"passed\":%d,\"failed\":0}\n",predicatedCs,predicatedOm,initialCs,initialOm,finalCs,finalOm,checks);
    return 0;
}
