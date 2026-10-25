#pragma once
#include "../../Environment/Native/EnvironmentNoticeTrace.h"
// Diagnostic records never authorize a replacement. All GPU operations are
// called by the bridge only after its render ownership/context/scene checks.
class SeasonNoticeCausality {
public:
    static constexpr uint32_t Magic=0x4e434231,Version=2,Capacity=65536,Descriptors=4096;
    enum Phase:uint32_t {Prepare=1,Returned,Restored,PresentEnter,PresentReturn,Rejected};
    struct Row {
        uint64_t id=0,parent=0,qpc=0,endQpc=0,caller=0,driver=0,context=0;
        notice_trace::State environment;
        uint32_t path=0,phase=0,thread=0,owner=0,mode=0,result=0,before=0,after=0,gate=0,kind=0,date=0,changed=0,complete=0;
        uint16_t bindings[128]{};
        uint32_t safety=0; // 0 CPU-only, 1 owned GPU observation, 2 rejected GPU ownership
        uint32_t boundary=0,reserved=0;
    };
    struct Descriptor {
        uint64_t view=0,resource=0;D3D11_SHADER_RESOURCE_VIEW_DESC srv{};D3D11_TEXTURE2D_DESC texture{};
        uint32_t hashState=0,error=0; // 0 ineligible, 1 pending, 2 authenticated, 3 wrong hash, 4 failed, 5 budget
        BYTE sha256[32]{};
    };
    struct Header {
        uint32_t magic=Magic,version=Version,size=sizeof(Header),rowSize=sizeof(Row),descriptorSize=sizeof(Descriptor),eventSize=sizeof(notice_trace::Entry);
        uint64_t season=0,environment=0,test=0,frequency=0;
        uint32_t rows=0,descriptors=0,events=0,rowDropped=0,descriptorDropped=0,snapshotMissed=0,excluded=0,pending=0,deadline=0,environmentMissing=0;
        uint64_t firstQpc=0,lastQpc=0,maxCallbackQpc=0,totalCallbackQpc=0;
        uint64_t requestedMs=0,captureStartedMs=0,captureStartedQpc=0;
        uint32_t stopReason=0,gateCount=notice_trace::GateCount,gateSize=sizeof(notice_trace::GateSummary),reserved=0;
    };
private:
    Header header;
    std::unique_ptr<Row[]> rows=std::make_unique<Row[]>(Capacity);
    std::array<Descriptor,Descriptors> descriptors{};
    std::array<ComPtr<ID3D11ShaderResourceView>,Descriptors> heldViews;
    std::array<uint16_t,8192> lookup{};
    struct Readback {uint32_t descriptor=0;ComPtr<ID3D11Texture2D> staging;};
    std::array<Readback,8> readbacks;uint32_t readbackCount=0,descriptorCount=0;
    std::unique_ptr<BYTE[]> hashBytes=std::make_unique<BYTE[]>(1048576);
    std::array<notice_trace::Entry,notice_trace::EventCapacity> events{};
    std::array<notice_trace::GateSummary,notice_trace::GateCount> gates{};
    std::atomic<uint32_t> count{0},dropped{0},snapshotMissed{0},excluded{0},deadline{0},descriptorDropped{0};
    std::atomic<uint64_t> nextId{0},maxCost{0},totalCost{0};
    notice_trace::Exchange exchange=nullptr;std::wstring path;
    static bool digest(const BYTE* bytes,BYTE* output){
        BCRYPT_ALG_HANDLE algorithm{};BCRYPT_HASH_HANDLE hash{};
        if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return false;
        bool ok=BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0&&BCryptHashData(hash,const_cast<BYTE*>(bytes),1048576,0)>=0&&BCryptFinishHash(hash,output,32,0)>=0;
        if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);return ok;
    }
    uint16_t descriptor(ID3D11DeviceContext* context,ID3D11ShaderResourceView* view){
        if(!view)return 0;auto key=reinterpret_cast<uint64_t>(view);size_t slot=(key>>4)%lookup.size();
        for(size_t n=0;n<lookup.size();n++,slot=(slot+1)%lookup.size()){
            if(auto id=lookup[slot]){if(descriptors[id-1].view==key)return id;continue;}
            if(descriptorCount==Descriptors){++descriptorDropped;return UINT16_MAX;}
            auto index=descriptorCount++;auto& out=descriptors[index];out.view=key;heldViews[index]=view;view->GetDesc(&out.srv);
            ComPtr<ID3D11Resource> resource;view->GetResource(&resource);out.resource=reinterpret_cast<uint64_t>(resource.Get());
            ComPtr<ID3D11Texture2D> texture;auto hr=resource.As(&texture);if(SUCCEEDED(hr))texture->GetDesc(&out.texture);else out.error=hr;
            lookup[slot]=static_cast<uint16_t>(index+1);
            auto& v=out.srv;auto& d=out.texture;
            bool eligible=v.ViewDimension==D3D11_SRV_DIMENSION_TEXTURE2D&&(v.Format==DXGI_FORMAT_BC3_UNORM||v.Format==DXGI_FORMAT_BC3_UNORM_SRGB)&&
                !v.Texture2D.MostDetailedMip&&v.Texture2D.MipLevels&&v.Texture2D.MipLevels<=d.MipLevels&&d.MipLevels&&d.MipLevels<=11&&
                d.Width==1024&&d.Height==1024&&d.ArraySize==1&&d.SampleDesc.Count==1&&(d.Format==DXGI_FORMAT_BC3_TYPELESS||d.Format==DXGI_FORMAT_BC3_UNORM||d.Format==DXGI_FORMAT_BC3_UNORM_SRGB);
            if(eligible){
                // One level-zero copy per resource, even when UNORM/sRGB views alias it.
                for(uint32_t i=0;i<index;i++)if(descriptors[i].resource==out.resource&&descriptors[i].hashState){out.hashState=descriptors[i].hashState;out.error=descriptors[i].error;std::memcpy(out.sha256,descriptors[i].sha256,32);return static_cast<uint16_t>(index+1);}
                if(readbackCount==readbacks.size())out.hashState=5;
                else {auto& copy=readbacks[readbackCount++];copy.descriptor=index;D3D11_TEXTURE2D_DESC staging=d;staging.MipLevels=1;staging.Usage=D3D11_USAGE_STAGING;staging.BindFlags=0;staging.MiscFlags=0;staging.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                    ComPtr<ID3D11Device> device;context->GetDevice(&device);hr=device->CreateTexture2D(&staging,nullptr,&copy.staging);
                    if(FAILED(hr)){out.hashState=4;out.error=hr;}else {context->CopySubresourceRegion(copy.staging.Get(),0,0,0,0,texture.Get(),0,nullptr);out.hashState=1;}}
            }
            return static_cast<uint16_t>(index+1);
        }++descriptorDropped;return UINT16_MAX;
    }
    void poll(ID3D11DeviceContext* context){
        for(auto& copy:readbacks){if(!copy.staging)continue;auto& out=descriptors[copy.descriptor];if(out.hashState!=1)continue;
            D3D11_MAPPED_SUBRESOURCE mapped{};auto hr=context->Map(copy.staging.Get(),0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&mapped);
            if(hr==DXGI_ERROR_WAS_STILL_DRAWING)continue;
            if(FAILED(hr)){out.hashState=4;out.error=hr;continue;}
            bool valid=mapped.pData&&mapped.RowPitch>=4096;
            if(valid)for(size_t y=0;y<256;y++)std::memcpy(hashBytes.get()+y*4096,static_cast<const BYTE*>(mapped.pData)+y*mapped.RowPitch,4096);
            context->Unmap(copy.staging.Get(),0);
            if(!valid||!digest(hashBytes.get(),out.sha256)){out.hashState=4;out.error=ERROR_INVALID_DATA;}
            else out.hashState=materialSha(hashBytes.get(),1048576,"3bd4b1d2d18b50ad64613ca80dc06102b52f56877788767d10a618dfa9147bac")?2:3;
            for(uint32_t i=0;i<descriptorCount;i++)if(descriptors[i].resource==out.resource){descriptors[i].hashState=out.hashState;descriptors[i].error=out.error;std::memcpy(descriptors[i].sha256,out.sha256,32);}
        }
    }
public:
    SeasonNoticeCausality(uint64_t generation,std::wstring file):path(std::move(file)){header.season=generation;}
    bool arm(uint32_t controller,uint64_t start){
        auto env=GetModuleHandleW(L"EnvironmentBridge.dll");exchange=env?reinterpret_cast<notice_trace::Exchange>(GetProcAddress(env,"EnvironmentNoticeTraceExchange")):nullptr;
        if(!exchange)return false;notice_trace::Query query;query.command=1;query.season=header.season;query.controllerPid=controller;query.controllerStart=start;
        if(!exchange(&query))return false;header.environment=query.state.environment;header.frequency=query.state.frequency;return true;
    }
    uint64_t id(){return ++nextId;}
    bool active(notice_trace::State& state){
        notice_trace::Query query;query.season=header.season;query.environment=header.environment;
        if(dropped||descriptorDropped){query.command=4;query.reserved=dropped?notice_trace::RowCapacity:notice_trace::DescriptorCapacity;if(exchange)exchange(&query);return false;}
        if(!exchange||!exchange(&query)){++snapshotMissed;return false;}
        state=query.state;if(!state.test||!state.startedMs||!state.captureStartedMs||state.ended||state.stopReason)return false;
        if(notice_trace::milliseconds()-state.captureStartedMs>=5000){deadline=1;query.command=4;query.reserved=notice_trace::CaptureDeadline;exchange(&query);return false;}return true;
    }
    Row* begin(uint64_t id,uint64_t parent,uint32_t pathKind,uint32_t phase,void* driver,uint64_t context,uint32_t owner,uint64_t caller=0,uint64_t enteredQpc=0){
        notice_trace::State state;if(!active(state))return nullptr;auto index=count.fetch_add(1);
        if(index>=Capacity){++dropped;return nullptr;}auto& r=rows[index];r.id=id;r.parent=parent;r.path=pathKind;r.phase=phase;r.qpc=notice_trace::qpc();
        r.environment=state;r.driver=reinterpret_cast<uint64_t>(driver);r.context=context;r.thread=GetCurrentThreadId();r.owner=owner;r.caller=caller;
        r.boundary=enteredQpc&&enteredQpc<state.captureStartedQpc;return &r;
    }
    void bindings(Row* row,ID3D11DeviceContext* context,bool safe){
        if(!row)return;if(!safe){++excluded;row->safety=2;return;}row->safety=1;
        poll(context);ID3D11ShaderResourceView* raw[128]{};context->PSGetShaderResources(0,128,raw);
        for(uint32_t slot=0;slot<128;slot++){row->bindings[slot]=descriptor(context,raw[slot]);if(raw[slot])raw[slot]->Release();}
    }
    void complete(Row* row){if(!row)return;row->endQpc=notice_trace::qpc();auto cost=row->endQpc-row->qpc;totalCost+=cost;
        auto previous=maxCost.load();while(previous<cost&&!maxCost.compare_exchange_weak(previous,cost)){}row->complete=1;}
    void saveAfterDrain(){
        notice_trace::Query query;query.command=2;query.season=header.season;query.environment=header.environment;
        if(exchange&&exchange(&query)){
            header.test=query.state.test;header.events=query.state.records;
            header.requestedMs=query.state.startedMs;header.captureStartedMs=query.state.captureStartedMs;header.captureStartedQpc=query.state.captureStartedQpc;
            header.stopReason=query.state.stopReason;std::copy(std::begin(query.gates),std::end(query.gates),gates.begin());
            if(header.events>events.size()){header.environmentMissing=1;header.events=0;}
            while(query.first<header.events){
                if(!exchange(&query)||!query.count){header.environmentMissing=1;break;}
                for(uint32_t i=0;i<query.count&&query.first+i<header.events;i++)events[query.first+i]=query.entries[i];query.first+=query.count;
            }
            if(query.state.dropped)header.environmentMissing=1;
            if(query.state.ended){query.command=3;exchange(&query);}
        }else header.environmentMissing=1;
        header.rows=std::min(count.load(),Capacity);header.descriptors=descriptorCount;header.rowDropped=dropped;header.descriptorDropped=descriptorDropped;header.snapshotMissed=snapshotMissed;header.excluded=excluded;header.deadline=deadline;
        header.maxCallbackQpc=maxCost;header.totalCallbackQpc=totalCost;
        for(uint32_t i=0;i<descriptorCount;i++)if(descriptors[i].hashState==1||descriptors[i].hashState==5)++header.pending;
        if(header.rows){header.firstQpc=rows[0].qpc;header.lastQpc=rows[header.rows-1].endQpc;}
        std::ofstream out(std::filesystem::path(path),std::ios::binary);out.write(reinterpret_cast<const char*>(&header),sizeof(header));
        out.write(reinterpret_cast<const char*>(rows.get()),sizeof(Row)*header.rows);out.write(reinterpret_cast<const char*>(descriptors.data()),sizeof(Descriptor)*header.descriptors);
        out.write(reinterpret_cast<const char*>(events.data()),sizeof(notice_trace::Entry)*header.events);
        out.write(reinterpret_cast<const char*>(gates.data()),sizeof(gates));if(!out)throw std::runtime_error("Causal notice evidence write failed");
    }
#ifdef SEASON_BRIDGE_FIXTURE
    void fixtureExchange(notice_trace::Exchange fn,uint64_t environment){exchange=fn;header.environment=environment;LARGE_INTEGER f{};QueryPerformanceFrequency(&f);header.frequency=f.QuadPart;}
    const Row* fixtureRows()const{return rows.get();}uint32_t fixtureCount()const{return std::min(count.load(),Capacity);}
    const auto& fixtureDescriptors()const{return descriptors;}
#endif
};
static_assert(sizeof(SeasonNoticeCausality::Row)*SeasonNoticeCausality::Capacity+sizeof(SeasonNoticeCausality)<64*1024*1024);
static_assert(sizeof(SeasonNoticeCausality::Header)==168&&sizeof(SeasonNoticeCausality::Row)==536&&sizeof(SeasonNoticeCausality::Descriptor)==128&&sizeof(notice_trace::Entry)==168&&sizeof(notice_trace::GateSummary)==56);
