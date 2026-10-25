#pragma once
#include <fstream>
#include <filesystem>
#include <stdexcept>
// The game's original still layout supplies geometry, animation, tint and clipping.
// The installed stl_event_017 is G1T 0x5B (BC3), not BC7.
// Only its authenticated flood sprite's t0 binding is replaced for our owned report.
inline constexpr size_t NoticeArtBytes=32+4*1024*1024*4;
inline bool validNoticeArt(const BYTE* bytes,size_t count){
    const uint32_t expected[]{0x31415244,2,4,1024,1024,864,648,0};
    return count==NoticeArtBytes&&!std::memcmp(bytes,expected,sizeof(expected))&&
        materialSha(bytes,count,"a70e5ae892d6ed89e8e72f1698f37cdf6ed1421ebe05622e97da8b7dc1977430");
}
// Preserve the authenticated original sprite's irregular/feathered silhouette.
// BC3's first 8 bytes in each block contain its independent alpha palette/indices.
inline std::vector<BYTE> noticeBC3Alpha(const std::vector<BYTE>& source){
    if(source.size()!=1048576)throw std::invalid_argument("Original BC3 alpha size differs");
    std::vector<BYTE> alpha(1024*1024);
    for(UINT by=0;by<256;by++)for(UINT bx=0;bx<256;bx++){
        auto* block=source.data()+(size_t(by)*256+bx)*16;uint32_t a[8]{block[0],block[1]};
        if(a[0]>a[1])for(UINT i=1;i<=6;i++)a[i+1]=((7-i)*a[0]+i*a[1])/7;
        else {for(UINT i=1;i<=4;i++)a[i+1]=((5-i)*a[0]+i*a[1])/5;a[6]=0;a[7]=255;}
        uint64_t indices=0;for(UINT i=0;i<6;i++)indices|=uint64_t(block[2+i])<<(i*8);
        for(UINT y=0;y<4;y++)for(UINT x=0;x<4;x++)alpha[(size_t(by)*4+y)*1024+bx*4+x]=static_cast<BYTE>(a[(indices>>(3*(y*4+x)))&7]);
    }
    return alpha;
}
struct NoticeArtTicket {ComPtr<ID3D11ShaderResourceView> original;bool changed=false;uint32_t kind=0;};
enum class NoticeGate:uint32_t {Prepared,PrepareFailed,Admission,Count,Thread,Context,Scene,Request,Getter,WeatherRead,WeatherHeader,Kind,Driver,Bindings,Exception};
struct NoticeObservation {
    uint64_t id=0,tick=0,caller=0,driver=0,driverContext=0,sessionContext=0,generation=0,sequence=0;
    uint32_t thread=0,ownerThread=0,path=0,mode=0,result=0,kind=0,date=0;
    bool nestedMismatch=false;NoticeGate gate=NoticeGate::Prepared;
};
struct NoticeBindingEvidence {
    uint64_t view=0,resource=0;D3D11_SHADER_RESOURCE_VIEW_DESC srv{};D3D11_TEXTURE2D_DESC texture{};
    bool accepted=false,hashAttempted=false;std::string hash;HRESULT error=S_OK;
};
class SeasonNoticeArt {
    const BYTE* pixels;
    std::wstring tracePath;
    std::array<std::atomic<uint64_t>,16> stages{};
    std::array<std::atomic<uint64_t>,6> kindCalls{},kindSwaps{},kindRestores{};
    std::array<std::atomic<uint64_t>,6> pathCalls{};
    std::array<std::atomic<uint64_t>,6> matchingDriverCalls{},otherDriverCalls{};
    bool traceLimitReached=false,bc3TraceLimitReached=false,otherTraceLimitReached=false;
    uint32_t bc3TraceCount=0,otherTraceCount=0;
    struct TraceCaller{uint64_t rva,calls;};std::array<TraceCaller,16> unscopedCallers{};size_t unscopedCallerCount=0;
    // CPU-only records may arrive from rejected threads. GPU evidence remains
    // confined to the admitted render thread; never hold this mutex across D3D.
    std::mutex observationMutex;std::atomic<uint64_t> observationSequence{0};
    struct GateRecord{NoticeObservation first,last;uint64_t calls=0;};
    std::array<GateRecord,64> gateRecords{};size_t gateCount=0;uint64_t gateDropped=0,callerDropped=0;
    struct BindingRecord{NoticeObservation first,last;uint32_t slot=0;NoticeBindingEvidence evidence;uint64_t calls=0;};
    std::array<BindingRecord,128> bindingRecords{};size_t bindingCount=0;
    size_t candidateBindings=0,otherBindings=0;uint64_t candidateDropped=0,otherDropped=0;
    struct TraceTexture {uint64_t view;uint32_t viewFormat,dimension,firstMip,viewMips,width,height,textureFormat,mips,array,samples;bool accepted;std::string hash;bool hashAttempted=false;uint32_t readError=0,paths=0;std::array<uint64_t,2> slots{};};
    std::vector<TraceTexture> traceTextures;
    std::vector<BYTE> sourceAlpha;
    std::string digest(const std::vector<BYTE>& bytes){
        BCRYPT_ALG_HANDLE algorithm{};BCRYPT_HASH_HANDLE hash{};std::array<BYTE,32> value{};
        if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
        bool ok=BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0;
        if(ok)ok=BCryptHashData(hash,const_cast<BYTE*>(bytes.data()),static_cast<ULONG>(bytes.size()),0)>=0&&BCryptFinishHash(hash,value.data(),32,0)>=0;
        if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);if(!ok)return {};
        constexpr char hex[]="0123456789abcdef";std::string text(64,'0');for(size_t i=0;i<32;i++){text[i*2]=hex[value[i]>>4];text[i*2+1]=hex[value[i]&15];}return text;
    }
    struct Candidate{ComPtr<ID3D11ShaderResourceView> view;bool accepted;std::string hash;};std::vector<Candidate> candidates;
    std::array<std::array<std::array<ComPtr<ID3D11ShaderResourceView>,11>,2>,4> views;
    // Diagnostic evidence only: a rejected descriptor never becomes an admitted sprite.
    // Read each bounded BC3 view at most once; unrelated GUI draws must not repeat GPU readback.
    void traceRejected(ID3D11DeviceContext* context,ID3D11Texture2D* texture,D3D11_TEXTURE2D_DESC d,TraceTexture* observed){
        if(!observed||observed->hashAttempted||!d.Width||!d.Height||d.Width>2048||d.Height>2048)return;
        observed->hashAttempted=true;
        ComPtr<ID3D11Device> device;context->GetDevice(&device);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
        ComPtr<ID3D11Texture2D> staging;auto hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr)){observed->readError=static_cast<uint32_t>(hr);return;}
        context->CopyResource(staging.Get(),texture);D3D11_MAPPED_SUBRESOURCE mapped{};hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);
        if(FAILED(hr)){observed->readError=static_cast<uint32_t>(hr);return;}
        UINT rowBytes=((d.Width+3)/4)*16,rows=(d.Height+3)/4;std::vector<BYTE> original;
        try{if(mapped.pData&&mapped.RowPitch>=rowBytes){original.resize(size_t(rowBytes)*rows);for(UINT row=0;row<rows;row++)std::memcpy(original.data()+size_t(row)*rowBytes,static_cast<const BYTE*>(mapped.pData)+size_t(row)*mapped.RowPitch,rowBytes);}}
        catch(...){context->Unmap(staging.Get(),0);throw;}context->Unmap(staging.Get(),0);
        if(original.empty())observed->readError=ERROR_INVALID_DATA;else observed->hash=digest(original);
    }
    HRESULT recognize(ID3D11DeviceContext* context,ID3D11ShaderResourceView* incoming,bool& accepted,uint32_t path=0,uint32_t slot=0,bool observationOnly=false,NoticeBindingEvidence* evidence=nullptr){
        accepted=false;D3D11_SHADER_RESOURCE_VIEW_DESC v{};incoming->GetDesc(&v);
        if(evidence){
            evidence->view=reinterpret_cast<uint64_t>(incoming);evidence->srv=v;
            ComPtr<ID3D11Resource> resource;incoming->GetResource(&resource);evidence->resource=reinterpret_cast<uint64_t>(resource.Get());
            ComPtr<ID3D11Texture2D> texture;auto hr=resource.As(&texture);
            if(SUCCEEDED(hr))texture->GetDesc(&evidence->texture);else evidence->error=hr;
        }
        TraceTexture* observed=nullptr;
        if(!tracePath.empty()){
            for(auto& item:traceTextures)if(item.view==reinterpret_cast<uint64_t>(incoming)){observed=&item;break;}
            bool bc3=v.Format==DXGI_FORMAT_BC3_UNORM||v.Format==DXGI_FORMAT_BC3_UNORM_SRGB;
            auto& count=bc3?bc3TraceCount:otherTraceCount;
            if(!observed&&count<(bc3?64u:128u)){traceTextures.push_back({reinterpret_cast<uint64_t>(incoming),static_cast<uint32_t>(v.Format),static_cast<uint32_t>(v.ViewDimension),v.Texture2D.MostDetailedMip,v.Texture2D.MipLevels,0,0,0,0,0,0,false,{}});observed=&traceTextures.back();++count;}
            if(!observed){traceLimitReached=true;(bc3?bc3TraceLimitReached:otherTraceLimitReached)=true;}
            else {
                observed->paths|=1u<<path;observed->slots[slot/64]|=uint64_t(1)<<(slot%64);
                // Descriptor evidence only. Non-BC3 and array resources remain
                // rejected below, without readback, binding changes or admission.
                if(!observed->width){
                    ComPtr<ID3D11Resource> resource;incoming->GetResource(&resource);ComPtr<ID3D11Texture2D> texture;
                    if(SUCCEEDED(resource.As(&texture))){D3D11_TEXTURE2D_DESC d{};texture->GetDesc(&d);
                        observed->width=d.Width;observed->height=d.Height;observed->textureFormat=d.Format;observed->mips=d.MipLevels;observed->array=d.ArraySize;observed->samples=d.SampleDesc.Count;}
                }
            }
        }
        if(v.ViewDimension!=D3D11_SRV_DIMENSION_TEXTURE2D||(v.Format!=DXGI_FORMAT_BC3_UNORM&&v.Format!=DXGI_FORMAT_BC3_UNORM_SRGB)||v.Texture2D.MostDetailedMip)return S_OK;
        record(9);
        for(const auto& candidate:candidates)if(candidate.view.Get()==incoming){accepted=candidate.accepted;
            if(evidence){evidence->accepted=accepted;evidence->hashAttempted=true;evidence->hash=candidate.hash;}return S_OK;}
        ComPtr<ID3D11Resource> resource;incoming->GetResource(&resource);ComPtr<ID3D11Texture2D> texture;if(FAILED(resource.As(&texture)))return S_OK;
        D3D11_TEXTURE2D_DESC d{};texture->GetDesc(&d);if(observed){observed->width=d.Width;observed->height=d.Height;observed->textureFormat=d.Format;observed->mips=d.MipLevels;observed->array=d.ArraySize;observed->samples=d.SampleDesc.Count;}
        if(!d.MipLevels||d.MipLevels>11||!v.Texture2D.MipLevels||v.Texture2D.MipLevels>d.MipLevels||d.ArraySize!=1||d.SampleDesc.Count!=1||
            (d.Format!=DXGI_FORMAT_BC3_TYPELESS&&d.Format!=DXGI_FORMAT_BC3_UNORM&&d.Format!=DXGI_FORMAT_BC3_UNORM_SRGB))return S_OK;
        if(d.Width!=1024||d.Height!=1024){if(!observationOnly)traceRejected(context,texture.Get(),d,observed);return S_OK;}
        record(10);if(evidence)evidence->hashAttempted=true;
#ifdef SEASON_BRIDGE_FIXTURE
        if(FAILED(fixtureReadbackError))return fixtureReadbackError;
#endif
        ComPtr<ID3D11Device> device;context->GetDevice(&device);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
        ComPtr<ID3D11Texture2D> staging;auto hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr))return hr;
        context->CopyResource(staging.Get(),texture.Get());D3D11_MAPPED_SUBRESOURCE mapped{};hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
        std::vector<BYTE> original;
        try{if(mapped.pData&&mapped.RowPitch>=4096){original.resize(1048576);for(UINT row=0;row<256;row++)std::memcpy(original.data()+row*4096,static_cast<const BYTE*>(mapped.pData)+row*mapped.RowPitch,4096);}}
        catch(...){context->Unmap(staging.Get(),0);throw;}context->Unmap(staging.Get(),0);
        accepted=original.size()==1048576&&materialSha(original.data(),original.size(),"3bd4b1d2d18b50ad64613ca80dc06102b52f56877788767d10a618dfa9147bac");
        if(accepted&&sourceAlpha.empty())sourceAlpha=noticeBC3Alpha(original);
        auto hash=tracing()?digest(original):std::string{};
        record(11);if(observed){observed->hashAttempted=true;observed->hash=hash;observed->accepted=accepted;}if(accepted)record(12);
        if(evidence){evidence->accepted=accepted;evidence->hash=hash;if(original.empty())evidence->error=HRESULT_FROM_WIN32(ERROR_INVALID_DATA);}
        if(candidates.size()>=32)candidates.erase(candidates.begin());candidates.push_back({incoming,accepted,std::move(hash)});return S_OK;
    }
public:
#ifdef SEASON_BRIDGE_FIXTURE
    HRESULT fixtureReadbackError=S_OK;
    const auto& fixtureGates()const{return gateRecords;}
    const auto& fixtureBindings()const{return bindingRecords;}
    std::array<uint64_t,4> fixtureDropped()const{return {gateDropped,callerDropped,candidateDropped,otherDropped};}
#endif
    explicit SeasonNoticeArt(const BYTE* data,std::wstring trace={}):pixels(data+32),tracePath(std::move(trace)){}
    bool tracing()const noexcept{return !tracePath.empty();}
    uint64_t nextObservation()noexcept{return ++observationSequence;}
    void observeDriver(uint32_t path,bool sameContext){if(tracing()&&path>=1&&path<=5)++(sameContext?matchingDriverCalls:otherDriverCalls)[path];}
    void observeCaller(uint64_t rva)noexcept{
        // Called only while observationMutex is held.
        if(!tracing())return;for(size_t i=0;i<unscopedCallerCount;i++)if(unscopedCallers[i].rva==rva){++unscopedCallers[i].calls;return;}
        if(unscopedCallerCount<unscopedCallers.size())unscopedCallers[unscopedCallerCount++]={rva,1};else ++callerDropped;
    }
    static bool sameObservation(const NoticeObservation& a,const NoticeObservation& b){
        return a.caller==b.caller&&a.driver==b.driver&&a.driverContext==b.driverContext&&a.sessionContext==b.sessionContext&&
            a.thread==b.thread&&a.ownerThread==b.ownerThread&&a.path==b.path&&a.mode==b.mode&&a.result==b.result&&
            a.kind==b.kind&&a.date==b.date&&a.generation==b.generation&&a.gate==b.gate&&a.nestedMismatch==b.nestedMismatch;
    }
    void observe(const NoticeObservation& event){
        if(!tracing())return;std::lock_guard lock(observationMutex);
        if(event.path==5&&event.result)observeCaller(event.caller);
        for(size_t i=0;i<gateCount;i++)if(sameObservation(gateRecords[i].first,event)){gateRecords[i].last=event;++gateRecords[i].calls;return;}
        if(gateCount<gateRecords.size())gateRecords[gateCount++]={event,event,1};else ++gateDropped;
    }
    void observeBinding(const NoticeObservation& event,uint32_t slot,const NoticeBindingEvidence& evidence){
        std::lock_guard lock(observationMutex);
        for(size_t i=0;i<bindingCount;i++){auto& item=bindingRecords[i];const auto& e=item.evidence;
            if(item.slot==slot&&sameObservation(item.first,event)&&e.view==evidence.view&&e.resource==evidence.resource&&
               !std::memcmp(&e.srv,&evidence.srv,sizeof(e.srv))&&!std::memcmp(&e.texture,&evidence.texture,sizeof(e.texture))&&
               e.accepted==evidence.accepted&&e.hash==evidence.hash&&e.hashAttempted==evidence.hashAttempted&&e.error==evidence.error){item.last=event;++item.calls;return;}}
        bool candidate=(evidence.srv.Format==DXGI_FORMAT_BC3_UNORM||evidence.srv.Format==DXGI_FORMAT_BC3_UNORM_SRGB)&&evidence.texture.Width==1024&&evidence.texture.Height==1024;
        auto& count=candidate?candidateBindings:otherBindings;
        if(count==64){++(candidate?candidateDropped:otherDropped);return;}
        bindingRecords[bindingCount++]={event,event,slot,evidence,1};++count;
    }
    void observeBindings(ID3D11DeviceContext* context,uint32_t path,NoticeObservation* event=nullptr){
        if(!tracing()||path<1||path>5)return;++pathCalls[path];
        ID3D11ShaderResourceView* raw[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT]{};
        context->PSGetShaderResources(0,D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT,raw);
        std::array<ComPtr<ID3D11ShaderResourceView>,D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT> held;
        for(size_t i=0;i<held.size();i++)held[i].Attach(raw[i]);
        for(uint32_t slot=0;slot<held.size();slot++)if(held[slot]){bool accepted=false;NoticeBindingEvidence evidence;
            auto hr=recognize(context,held[slot].Get(),accepted,path,slot,true,event?&evidence:nullptr);
            if(event){if(FAILED(hr))evidence.error=hr;observeBinding(*event,slot,evidence);}}
    }
    void record(size_t stage)noexcept{if(!tracePath.empty())++stages[stage];}
    void saveTraceAfterDrain()const{
        if(tracePath.empty())return;std::ofstream out{std::filesystem::path(tracePath)};
        out<<"{\"formatVersion\":2,\"stages\":[";for(size_t i=0;i<stages.size();i++){if(i)out<<',';out<<stages[i].load();}
        auto kinds=[&](const char* name,const auto& counts){out<<"],\""<<name<<"\":[";for(size_t i=0;i<counts.size();i++){if(i)out<<',';out<<counts[i].load();}};
        kinds("kindCalls",kindCalls);kinds("kindSwaps",kindSwaps);kinds("kindRestores",kindRestores);
        kinds("pathCalls",pathCalls);
        kinds("matchingDriverCalls",matchingDriverCalls);kinds("otherDriverCalls",otherDriverCalls);
        out<<"],\"traceLimitReached\":"<<(traceLimitReached?"true":"false")<<",\"bc3TraceLimitReached\":"<<(bc3TraceLimitReached?"true":"false")<<",\"otherTraceLimitReached\":"<<(otherTraceLimitReached?"true":"false")<<",\"unscopedCallers\":[";
        bool first=true;for(size_t i=0;i<unscopedCallerCount;i++){const auto& c=unscopedCallers[i];if(!first)out<<',';first=false;out<<"{\"rva\":"<<c.rva<<",\"calls\":"<<c.calls<<'}';}
        constexpr const char* gates[]{"prepared","prepare-failed","admission","count","thread","context","scene","request","getter","weather-read","weather-header","kind","driver","bindings","exception"};
        auto event=[&](const NoticeObservation& e){
            out<<"\"id\":"<<e.id<<",\"tickMs\":"<<e.tick<<",\"callerRva\":"<<e.caller<<",\"driver\":"<<e.driver<<",\"driverContext\":"<<e.driverContext<<",\"sessionContext\":"<<e.sessionContext
                <<",\"thread\":"<<e.thread<<",\"ownerThread\":"<<e.ownerThread<<",\"path\":"<<e.path<<",\"mode\":"<<e.mode<<",\"prepareResult\":"<<e.result<<",\"nestedMismatch\":"<<(e.nestedMismatch?"true":"false")
                <<",\"gate\":\""<<gates[static_cast<size_t>(e.gate)]<<"\",\"generation\":"<<e.generation<<",\"sequence\":"<<e.sequence<<",\"kind\":"<<e.kind<<",\"date\":"<<e.date;};
        auto last=[&](const NoticeObservation& e,uint64_t calls){out<<",\"lastId\":"<<e.id<<",\"lastTickMs\":"<<e.tick<<",\"lastSequence\":"<<e.sequence<<",\"calls\":"<<calls;};
        out<<"],\"observationCount\":"<<observationSequence.load()<<",\"gateDropped\":"<<gateDropped<<",\"callerDropped\":"<<callerDropped
            <<",\"candidateBindingDropped\":"<<candidateDropped<<",\"otherBindingDropped\":"<<otherDropped<<",\"observations\":[";
        first=true;for(size_t i=0;i<gateCount;i++){if(!first)out<<',';first=false;const auto& r=gateRecords[i];out<<'{';event(r.first);last(r.last,r.calls);out<<'}';}
        out<<"],\"bindings\":[";first=true;for(size_t i=0;i<bindingCount;i++){
            if(!first)out<<',';first=false;const auto& r=bindingRecords[i];const auto& e=r.evidence;out<<'{';event(r.first);last(r.last,r.calls);
            out<<",\"slot\":"<<r.slot<<",\"view\":"<<e.view<<",\"resource\":"<<e.resource<<",\"viewFormat\":"<<e.srv.Format<<",\"dimension\":"<<e.srv.ViewDimension
                <<",\"firstMip\":"<<e.srv.Texture2D.MostDetailedMip<<",\"viewMips\":"<<e.srv.Texture2D.MipLevels<<",\"width\":"<<e.texture.Width<<",\"height\":"<<e.texture.Height
                <<",\"textureFormat\":"<<e.texture.Format<<",\"mips\":"<<e.texture.MipLevels<<",\"array\":"<<e.texture.ArraySize<<",\"samples\":"<<e.texture.SampleDesc.Count
                <<",\"accepted\":"<<(e.accepted?"true":"false")<<",\"hashAttempted\":"<<(e.hashAttempted?"true":"false")<<",\"sha256\":\""<<e.hash<<"\",\"readError\":"<<static_cast<uint32_t>(e.error)<<'}';}
        out<<"],\"textures\":[";first=true;for(const auto& item:traceTextures){if(!first)out<<',';first=false;
            out<<"{\"view\":"<<item.view<<",\"viewFormat\":"<<item.viewFormat<<",\"dimension\":"<<item.dimension<<",\"firstMip\":"<<item.firstMip<<",\"viewMips\":"<<item.viewMips<<",\"width\":"<<item.width<<",\"height\":"<<item.height
                <<",\"textureFormat\":"<<item.textureFormat<<",\"mips\":"<<item.mips<<",\"array\":"<<item.array<<",\"samples\":"<<item.samples<<",\"accepted\":"<<(item.accepted?"true":"false")<<",\"sha256\":\""<<item.hash<<"\",\"hashAttempted\":"<<(item.hashAttempted?"true":"false")<<",\"readError\":"<<item.readError<<",\"paths\":"<<item.paths<<",\"slots\":["<<item.slots[0]<<','<<item.slots[1]<<"]}";}
        out<<"]}";
    }
    uint64_t gpuBytes()const{uint64_t count=0;for(const auto& kinds:views)for(const auto& variants:kinds)for(size_t level=0;level<variants.size();level++)if(variants[level])for(size_t mip=0;mip<=level;mip++)count+=uint64_t(1024>>mip)*(1024>>mip)*4;return count;}
    HRESULT enter(ID3D11DeviceContext* context,uint32_t kind,NoticeArtTicket& ticket,uint32_t path=0){
        if(kind<2||kind>5)return S_FALSE;
        if(!tracePath.empty())++kindCalls[kind];
        ComPtr<ID3D11ShaderResourceView> incoming;context->PSGetShaderResources(0,1,&incoming);if(!incoming)return S_FALSE;
        record(8);
        bool accepted=false;auto hr=recognize(context,incoming.Get(),accepted,path);if(FAILED(hr)||!accepted)return FAILED(hr)?hr:S_FALSE;
        if(sourceAlpha.size()!=1024*1024)return E_UNEXPECTED;
        D3D11_SHADER_RESOURCE_VIEW_DESC original{};incoming->GetDesc(&original);uint32_t variant=original.Format==DXGI_FORMAT_BC3_UNORM_SRGB?1:0,index=kind-2;
        UINT levels=original.Texture2D.MipLevels;
        auto& replacement=views[index][variant][levels-1];if(!replacement){
            ComPtr<ID3D11Device> device;context->GetDevice(&device);
            D3D11_TEXTURE2D_DESC d{1024,1024,levels,1,DXGI_FORMAT_B8G8R8A8_TYPELESS,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
            std::array<std::vector<BYTE>,11> mips;std::array<D3D11_SUBRESOURCE_DATA,11> data{};
            auto* painting=pixels+size_t(index)*1024*1024*4;
            mips[0].assign(painting,painting+1024*1024*4);
            for(size_t pixel=0;pixel<sourceAlpha.size();pixel++)mips[0][pixel*4+3]=static_cast<BYTE>((uint32_t(painting[pixel*4+3])*sourceAlpha[pixel]+127)/255);
            data[0]={mips[0].data(),4096,1024*1024*4};
            for(UINT mip=1;mip<levels;mip++){
                UINT side=1024>>mip,previous=side*2;mips[mip].resize(size_t(side)*side*4);auto* source=static_cast<const BYTE*>(data[mip-1].pSysMem);
                for(UINT y=0;y<side;y++)for(UINT x=0;x<side;x++)for(UINT c=0;c<4;c++){
                    size_t at=(size_t(y)*2*previous+x*2)*4+c;
                    mips[mip][(size_t(y)*side+x)*4+c]=static_cast<BYTE>((uint32_t(source[at])+source[at+4]+source[at+previous*4]+source[at+previous*4+4]+2)/4);
                }
                data[mip]={mips[mip].data(),side*4,side*side*4};
            }
            ComPtr<ID3D11Texture2D> texture;
            hr=device->CreateTexture2D(&d,data.data(),&texture);if(FAILED(hr))return hr;
            D3D11_SHADER_RESOURCE_VIEW_DESC v{};v.Format=variant?DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:DXGI_FORMAT_B8G8R8A8_UNORM;v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;v.Texture2D.MipLevels=levels;
            hr=device->CreateShaderResourceView(texture.Get(),&v,&replacement);if(FAILED(hr))return hr;
        }
        ticket.original=incoming;auto* next=replacement.Get();context->PSSetShaderResources(0,1,&next);ticket.changed=true;ticket.kind=kind;
        if(!tracePath.empty())++kindSwaps[kind];record(13);return S_OK;
    }
    bool leave(ID3D11DeviceContext* context,NoticeArtTicket& ticket){
        if(!ticket.changed)return true;auto* original=ticket.original.Get();context->PSSetShaderResources(0,1,&original);
        ComPtr<ID3D11ShaderResourceView> restored;context->PSGetShaderResources(0,1,&restored);ticket.changed=false;bool same=restored.Get()==original;
        if(same){record(14);if(!tracePath.empty()&&ticket.kind>=2&&ticket.kind<=5)++kindRestores[ticket.kind];}return same;
    }
};
