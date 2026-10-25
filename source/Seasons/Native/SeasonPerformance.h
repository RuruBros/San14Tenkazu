#pragma once
#include <array>
#include <atomic>
#include <sstream>
#include <iomanip>
#include <chrono>
#include <optional>

// Bounded diagnostics; not a rendering admission/ownership mechanism.
namespace season_perf {
inline uint64_t tick(){LARGE_INTEGER v{};QueryPerformanceCounter(&v);return v.QuadPart;}
enum Stage:uint32_t {DispatchWait,OriginalPrepare,Notice,WeatherRead,EnvironmentWait,EnvironmentGuard,EnvironmentCopy,
    TreeEnter,TreeLeave,LandEnter,LandLeave,OriginalDraw,RegionalCells,RegionalUpload,WeatherResources,
    WeatherCopy,WeatherBounds,Vegetation,WeatherRecopy,Particles,Marks,PipelineRestore,QueryWork,Count};
inline constexpr const char* Names[]={"dispatchWait","originalPrepare","notice","weatherRead","environmentWait","environmentGuard","environmentCopy",
    "treeEnter","treeLeave","landEnter","landLeave","originalDraw","regionalCells","regionalUpload","weatherResources",
    "weatherCopy","weatherBounds","vegetation","weatherRecopy","particles","marks","pipelineRestore","queryWork"};
struct Stat {uint64_t calls=0,inclusive=0,exclusive=0,maximum=0;};
struct Frame {uint64_t id=0,begin=0,end=0;uint32_t thread=0,sampled=0;std::array<Stat,Count> cpu{};};
struct Present {uint64_t begin=0,end=0;uint32_t thread=0,depth=0;};
struct GpuResult {uint64_t frame=0,frequency=0;uint32_t count=0,status=0;std::array<uint64_t,32> ticks{};std::array<uint32_t,32> stages{};};
class Trace;
inline thread_local Trace* local=nullptr;
struct Cpu;
inline thread_local Cpu* parent=nullptr;
class Trace {
public:
    static constexpr uint32_t Frames=4096,Samples=512,Slots=16;
    std::array<Frame,Frames> frames{};std::array<Present,Frames> presents{};std::array<GpuResult,Samples> results{};
    std::atomic<uint32_t> presentCount{0},reason{0};
    uint32_t frameCount=0,sampleCount=0,skipped=0,overflow=0,geometryOmitted=0,owner=0,date=0,flags=0;uint64_t environment=0,environmentSequence=0;std::array<uint64_t,4> environmentFrameFirst{},environmentFrameLast{};
    uint64_t lastPresent=UINT64_MAX,frequency=0,started=0,ended=0,gpuFrame=UINT64_MAX,queryCalls=0,queryTicks=0;uint32_t width=0,height=0,seasonal=0;std::atomic<uint64_t> deadline{0};std::atomic<bool> collecting{false};
    struct QueryCost {Trace& trace;uint64_t begin=tick();~QueryCost(){++trace.queryCalls;trace.queryTicks+=tick()-begin;}};
    std::atomic<uint64_t> stopAt{0};bool gpuEnabled=true;std::atomic<bool> prepared{false};HRESULT queryError=S_OK;
    struct Slot {Microsoft::WRL::ComPtr<ID3D11Query> disjoint;std::array<Microsoft::WRL::ComPtr<ID3D11Query>,32> stamps;int result=-1;bool open=false;};
    std::array<Slot,Slots> slots;int currentSlot=-1;ID3D11DeviceContext* ownedContext=nullptr;
    explicit Trace(bool gpu=true):gpuEnabled(gpu){LARGE_INTEGER f{};QueryPerformanceFrequency(&f);frequency=f.QuadPart;started=tick();deadline=started+25*frequency;collecting=true;}
    void stop(uint32_t why){uint32_t zero=0;reason.compare_exchange_strong(zero,why);collecting=false;uint64_t none=0;stopAt.compare_exchange_strong(none,tick());}
    bool active(){if(collecting&&tick()>=deadline)stop(prepared?1:4);return collecting.load();}
    HRESULT prepare(ID3D11DeviceContext* context,uint32_t thread){
        if(prepared)return context==ownedContext&&thread==owner?S_OK:E_ACCESSDENIED;
        owner=thread;ownedContext=context;Microsoft::WRL::ComPtr<ID3D11Device> device;context->GetDevice(&device);
        if(gpuEnabled)for(auto& slot:slots){D3D11_QUERY_DESC d{D3D11_QUERY_TIMESTAMP_DISJOINT,0};auto hr=device->CreateQuery(&d,&slot.disjoint);if(FAILED(hr))return queryError=hr;
            d.Query=D3D11_QUERY_TIMESTAMP;for(auto& stamp:slot.stamps)if(FAILED(hr=device->CreateQuery(&d,&stamp)))return queryError=hr;}
        prepared=true;deadline=tick()+20*frequency;return S_OK;
    }
    Frame* frame(){return frameCount?&frames[frameCount-1]:nullptr;}
    void boundary(ID3D11DeviceContext* context,uint32_t thread){
        // Present count is a coarse boundary only, never a replacement for a game guard.
        if(prepared&&uint64_t(presentCount.load())==lastPresent)return;
        if(!active())return;
        if(!prepared){if(FAILED(prepare(context,thread)))stop(5);return;}
        if(owner!=thread||ownedContext!=context){stop(6);return;}
        auto n=uint64_t(presentCount.load());if(n==lastPresent)return;
        close(context);poll(context);if(auto f=frame())f->end=tick();
        if(frameCount==Frames){stop(2);return;}lastPresent=n;auto& f=frames[frameCount++];f.id=n;f.begin=tick();f.thread=thread;f.sampled=(frameCount%8)==1;
    }
    void add(Stage stage,uint64_t elapsed,uint64_t own){auto f=frame();if(!f)return;auto& s=f->cpu[stage];++s.calls;s.inclusive+=elapsed;s.exclusive+=own;s.maximum=std::max(s.maximum,elapsed);}
    bool detailed()const{return frameCount&&frames[frameCount-1].sampled;}
    void stamp(ID3D11DeviceContext* context,uint32_t stage){
        if(!collecting.load()||!gpuEnabled||!detailed()||!prepared||GetCurrentThreadId()!=owner||context!=ownedContext)return;
        QueryCost measured{*this};
        if(currentSlot<0){
            if(gpuFrame==frame()->id)return;gpuFrame=frame()->id;
            if(sampleCount==Samples){++skipped;stop(2);return;}
            for(unsigned i=0;i<Slots;i++)if(slots[i].result<0){currentSlot=int(i);break;}
            if(currentSlot<0){++skipped;return;}
            auto& slot=slots[currentSlot];slot.result=int(sampleCount++);auto& r=results[slot.result];r.frame=frame()->id;r.status=1;
            context->Begin(slot.disjoint.Get());slot.open=true;
        }
        auto& slot=slots[currentSlot];auto& r=results[slot.result];if(r.count==32){++overflow;return;}
        r.stages[r.count]=stage;context->End(slot.stamps[r.count++].Get());
    }
    void close(ID3D11DeviceContext* context){if(currentSlot<0)return;auto& slot=slots[currentSlot];
        if(context!=ownedContext||GetCurrentThreadId()!=owner)return;
        if(slot.open){context->End(slot.disjoint.Get());slot.open=false;}currentSlot=-1;}
    bool geometryBegin(ID3D11DeviceContext* context,uint32_t kind){
        if(!collecting||!detailed())return false;
        if((currentSlot<0&&gpuFrame==frame()->id)||(currentSlot>=0&&results[slots[currentSlot].result].count>=16)){++geometryOmitted;return false;}
        stamp(context,100+kind*2);return currentSlot>=0;
    }
    void poll(ID3D11DeviceContext* context){
        if(!prepared||!gpuEnabled||context!=ownedContext||GetCurrentThreadId()!=owner)return;
        QueryCost measured{*this};
        auto begin=tick();unsigned checked=0;for(auto& slot:slots){if(slot.result<0||slot.open)continue;if(checked++==2||tick()-begin>frequency/10000)break;
            auto& r=results[slot.result];D3D11_QUERY_DATA_TIMESTAMP_DISJOINT d{};auto hr=context->GetData(slot.disjoint.Get(),&d,sizeof(d),D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if(hr==S_FALSE)continue;if(FAILED(hr)){queryError=hr;r.status=4;slot.result=-1;stop(5);continue;}
            if(d.Disjoint||!d.Frequency){r.status=3;slot.result=-1;continue;}bool ready=true;
            for(unsigned i=0;i<r.count;i++){hr=context->GetData(slot.stamps[i].Get(),&r.ticks[i],sizeof(uint64_t),D3D11_ASYNC_GETDATA_DONOTFLUSH);if(hr!=S_OK){ready=false;break;}}
            if(FAILED(hr)){queryError=hr;r.status=4;slot.result=-1;stop(5);}else if(ready){r.frequency=d.Frequency;r.status=2;slot.result=-1;}
        }
    }
    // Called under the existing owner render callback, even during bounded retirement.
    void visit(ID3D11DeviceContext* context,uint32_t thread){if(collecting)boundary(context,thread);else if(prepared&&thread==owner){close(context);poll(context);}}
    std::string json()const{
        std::ostringstream o;o<<"{\"version\":1,\"qpcFrequency\":"<<frequency<<",\"started\":"<<started<<",\"reason\":"<<reason.load()
         <<",\"queryError\":"<<uint32_t(queryError)<<",\"prepared\":"<<(prepared?"true":"false")<<",\"skipped\":"<<skipped<<",\"overflow\":"<<overflow
         <<",\"queryCalls\":"<<queryCalls<<",\"queryTicks\":"<<queryTicks<<",\"width\":"<<width<<",\"height\":"<<height<<",\"seasonal\":"<<seasonal<<",\"geometryOmitted\":"<<geometryOmitted<<",\"date\":"<<date<<",\"flags\":"<<flags<<",\"environment\":"<<environment<<",\"environmentSequence\":"<<environmentSequence<<",\"cpuStages\":[";
        for(unsigned i=0;i<Count;i++){if(i)o<<',';o<<'"'<<Names[i]<<'"';}o<<"],\"environmentFrames\":{\"first\":[";for(int i=0;i<4;i++){if(i)o<<',';o<<environmentFrameFirst[i];}o<<"],\"last\":[";for(int i=0;i<4;i++){if(i)o<<',';o<<environmentFrameLast[i];}o<<"]},\"frames\":[";
        for(unsigned i=0;i<frameCount;i++){if(i)o<<',';auto& f=frames[i];o<<"{\"id\":"<<f.id<<",\"begin\":"<<f.begin<<",\"end\":"<<f.end<<",\"thread\":"<<f.thread<<",\"sampled\":"<<f.sampled<<",\"cpu\":[";
            for(unsigned j=0;j<Count;j++){if(j)o<<',';auto& s=f.cpu[j];o<<'['<<s.calls<<','<<s.inclusive<<','<<s.exclusive<<','<<s.maximum<<']';}o<<"]}";}
        o<<"],\"presents\":[";for(unsigned i=0;i<std::min(presentCount.load(),Frames);i++){if(i)o<<',';auto& p=presents[i];o<<'['<<p.begin<<','<<p.end<<','<<p.thread<<','<<p.depth<<']';}
        o<<"],\"gpu\":[";for(unsigned i=0;i<sampleCount;i++){if(i)o<<',';auto& r=results[i];o<<"{\"frame\":"<<r.frame<<",\"status\":"<<r.status<<",\"frequency\":"<<r.frequency<<",\"points\":[";
            for(unsigned j=0;j<r.count;j++){if(j)o<<',';o<<'['<<r.stages[j]<<','<<r.ticks[j]<<']';}o<<"]}";}o<<"]}";return o.str();
    }
};
static_assert(sizeof(Trace)<8*1024*1024);
struct Cpu {
    Trace* trace;Cpu* previous=nullptr;Stage stage;uint64_t begin=0,children=0;
    explicit Cpu(Stage value):trace(local),stage(value){auto error=GetLastError();if(trace&&trace->collecting.load()){
        if(trace->detailed()){previous=parent;parent=this;begin=tick();}else{trace->add(stage,0,0);trace=nullptr;}
        }else trace=nullptr;SetLastError(error);}
    ~Cpu(){if(!trace)return;auto error=GetLastError();auto elapsed=tick()-begin;trace->add(stage,elapsed,elapsed>=children?elapsed-children:0);parent=previous;if(previous)previous->children+=elapsed;SetLastError(error);}
};
inline void gpu(ID3D11DeviceContext* context,uint32_t stage){if(local)local->stamp(context,stage);}
}
