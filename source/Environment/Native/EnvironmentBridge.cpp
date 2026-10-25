#include "GameBuildFacts.g.h"
extern "C" __declspec(dllexport) const char San14BuildProfileIdentity[]=SAN14_BUILD_PROFILE_IDENTITY;
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <sddl.h>
#include <intrin.h>
#include <cstring>
#include <atomic>
#include <memory>
#include <mutex>
#include <vector>
#include <string>
#include "detours.h"
#include "EnvironmentProtocol.h"
#include "EnvironmentPolicy.h"
#include "EnvironmentSignatures.h"
#include "EnvironmentWeather.h"
#include "EnvironmentNoticeTraceStore.h"
using namespace environment;
namespace {
struct Handle {HANDLE value{};~Handle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}};
struct Local {void* value{};~Local(){if(value)LocalFree(value);}};
template<class T> bool copy(T& value,uint64_t address) noexcept {
    if(!address)return false;__try{std::memcpy(&value,reinterpret_cast<void*>(address),sizeof(value));return true;}
    __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
template<class T> T read(uint64_t address,size_t offset=0) noexcept {T value{};copy(value,address+offset);return value;}
template<class T> bool write(uint64_t address,T value) noexcept {
    __try{std::memcpy(reinterpret_cast<void*>(address),&value,sizeof(value));return true;}
    __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
#include "EnvironmentNotification.h"
using Update=gamebuild::Abi::Update;
using Speed=gamebuild::Abi::Speed;
Update originalUpdate{};Speed originalSpeed{};
SRWLOCK dispatch=SRWLOCK_INIT;
std::mutex connection;
std::atomic<long> callbacks{0};
struct World {
    uint64_t root{},state{},tiles{},units{};uint32_t date{};
    uint64_t cities[CitySlots+1]{},counties[501]{};
    uint16_t cityTiles[CitySlots+1]{};wchar_t cityNames[CitySlots+1][5]{};
    uint64_t availableCities{},unavailableCities{};
};
// Compare the exact inputs consumed by region()/topology(), not a sampled hash
// or just root/state pointers. SSE2 is part of the supported Windows x64 ABI.
// Other tile fields can change without invalidating geography.
bool tileInputs(uint64_t address,uint8_t* saved,bool capture) noexcept {
    static_assert(gamebuild::Memory::TileStride==0x20&&gamebuild::Memory::TileBaseOffset==0x1a);
    const auto klassMask=_mm_set_epi64x(0,-1);
    const auto geographyMask=_mm_set_epi64x(0x00000000ffff0000ll,0x00000000ffff00ffll);
    __try {
        for(size_t offset=0;offset<TileCount*0x20;offset+=0x20){
            auto a=_mm_and_si128(_mm_loadu_si128(reinterpret_cast<const __m128i*>(address+offset)),klassMask);
            auto b=_mm_and_si128(_mm_loadu_si128(reinterpret_cast<const __m128i*>(address+offset+16)),geographyMask);
            if(capture){_mm_storeu_si128(reinterpret_cast<__m128i*>(saved+offset),a);_mm_storeu_si128(reinterpret_cast<__m128i*>(saved+offset+16),b);}
            else if(_mm_movemask_epi8(_mm_cmpeq_epi8(a,_mm_loadu_si128(reinterpret_cast<const __m128i*>(saved+offset))))!=0xffff||
                    _mm_movemask_epi8(_mm_cmpeq_epi8(b,_mm_loadu_si128(reinterpret_cast<const __m128i*>(saved+offset+16))))!=0xffff)return false;
        }
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
bool sameGeographyWorld(const World& a,const World& b) {
    return a.root==b.root&&a.state==b.state&&a.tiles==b.tiles&&a.units==b.units&&
        a.availableCities==b.availableCities&&a.unavailableCities==b.unavailableCities&&
        !std::memcmp(a.cities,b.cities,sizeof(a.cities))&&!std::memcmp(a.counties,b.counties,sizeof(a.counties))&&
        !std::memcmp(a.cityTiles,b.cityTiles,sizeof(a.cityTiles))&&!std::memcmp(a.cityNames,b.cityNames,sizeof(a.cityNames));
}
struct Session {
    Bootstrap input;std::atomic<bool> admission{true};
    std::atomic<uint32_t> failure{0},thread{0};std::atomic<uint64_t> frames{0},speedCalls{0},slowed{0};
    std::atomic<std::shared_ptr<const Message>> pending;
    std::atomic<std::shared_ptr<const Message>> testRequest;
    uint64_t testRoot{},testWorld{},testConfig{},testDeadline{},testStillSince{},testForegroundSince{};
    uint32_t testHour{},testNoticeFlags{};
    std::recursive_mutex stateLock;Reply state;World previous{};
    uint8_t regionHasRiver[CitySlots+1]{},regionHasForest[CitySlots+1]{},regionByTile[TileCount]{};uint64_t topologyRoot{},topologyState{},topologyFingerprint{},lastTopologyFingerprint{};
    std::vector<uint8_t> geographyTiles=std::vector<uint8_t>(TileCount*0x20);
    uint64_t countyClasses[501]{};uint8_t countyHomes[501]{};bool geographyReady=false;
#ifdef ENVIRONMENT_FIXTURE
    uint64_t topologyBuilds=0;
#endif
    uint32_t lastDate{},lastHour{};uint64_t lastState{},lastRoot{};
    struct Notice{uint32_t date,city,kind,units=0;int64_t troops=0,morale=0,food=0;};std::vector<Notice> notices;
    uint64_t noticeQueued=0;uint32_t noticeDate=0,noticeCity=0,noticeKind=0;
    std::wstring testNotice;
    uint32_t testNoticeKind=0;NoticePresentation presentation;std::atomic<bool> stopPresentation{false},presentationBusy{false};
    BYTE speedBefore[sizeof(MoveSpeedCode)]{},speedInstalled[sizeof(MoveSpeedCode)]{};
    DWORD speedProtect{},slotProtect{};bool prepared{},speedHooked{},slotHooked{};
    std::shared_ptr<notice_trace::Store> noticeTrace=std::make_shared<notice_trace::Store>();
    explicit Session(const Bootstrap& value):input(value){state.generation=value.generation;presentation.trace=noticeTrace.get();}
    bool world(World& result) {
        result.root=read<uint64_t>(input.module+gamebuild::Memory::BasePointerRva)+gamebuild::Memory::BaseAdjust;
        result.state=read<uint64_t>(result.root,gamebuild::Memory::StatePointer);
        if(!result.state||read<uint64_t>(result.state)!=input.worldClass)return false;
        auto manager=read<uint64_t>(input.module+gamebuild::Scene::ManagerRva);
        if(!manager||read<uint32_t>(manager,gamebuild::Scene::ManagerStateOffset)!=0||!read<uint64_t>(manager,gamebuild::Scene::SceneOffset))return false;
        result.date=read<uint32_t>(result.state,0x34);if(!validDate(result.date))return false;
        result.tiles=read<uint64_t>(result.root,gamebuild::Memory::TileTable);result.units=read<uint64_t>(result.root,gamebuild::Memory::UnitTable);
        if(!result.tiles||!result.units||read<uint64_t>(result.tiles)!=input.tileClass||read<uint64_t>(result.units)!=input.unitClass)return false;
        // A building's table index need not be its city ID. Resolve type-1 castles
        // through the loaded hex's base ID, as the editor's snapshot reader does.
        uint64_t castles[CitySlots+1]{};bool ambiguous[CitySlots+1]{};
        for(uint32_t index=0;index<gamebuild::Memory::MaxBuildings;index++) {
            auto building=read<uint64_t>(result.root,gamebuild::Memory::BuildingTable+uint64_t(index)*8);
            uint64_t klass{};uint8_t type{};uint16_t tile{},base{};
            if(!copy(klass,building)||klass!=input.buildingClass||!copy(type,building+0x12)||type!=1||!copy(tile,building+0x10)||tile>=TileCount)continue;
            auto hex=result.tiles+uint64_t(tile)*gamebuild::Memory::TileStride;
            if(!copy(klass,hex)||klass!=input.tileClass||!copy(base,hex+gamebuild::Memory::TileBaseOffset)||base<1||base>CitySlots)continue;
            if(castles[base])ambiguous[base]=true;else castles[base]=building;
        }
        for(uint32_t city=1;city<=CitySlots;city++) {
            result.cities[city]=read<uint64_t>(result.root,gamebuild::Memory::CityTable+city*8);
            auto place=result.cities[city];uint64_t bit=uint64_t(1)<<(city-1),klass{};uint16_t object{};uint8_t province{};
            if(!copy(klass,place)||klass!=input.cityClass||!copy(object,place+0x4e)||object!=city||!copy(province,place+0xce))
                {result.unavailableCities|=bit;continue;}
            if(province>=18&&province<=22)continue;
            if(province<1||province>17){result.unavailableCities|=bit;continue;}
            auto building=ambiguous[city]?0:castles[city];
            uint16_t tile{};uint8_t type{};
            if(!copy(klass,building)||klass!=input.buildingClass||!copy(type,building+0x12)||type!=1||!copy(tile,building+0x10)||tile>=TileCount)
                {result.unavailableCities|=bit;continue;}
            auto hex=result.tiles+uint64_t(tile)*0x20;uint16_t county{};
            if(!copy(klass,hex)||klass!=input.tileClass||!copy(county,hex+0x12)||county<1||county>500)
                {result.unavailableCities|=bit;continue;}
            auto record=read<uint64_t>(result.root,gamebuild::Memory::CountyTable+uint64_t(county)*8);uint8_t home{};
            // The castle's validated hex base names this city. A loaded data patch can
            // place it in a county assigned to another valid city; those are separate facts.
            if(!copy(klass,record)||klass!=input.countyClass||!copy(home,record+0x34)||home<1||home>CitySlots)
                {result.unavailableCities|=bit;continue;}
            result.availableCities|=bit;result.cityTiles[city]=tile;
            struct Name{wchar_t text[4];}name{};
            if(copy(name,place+0x12))std::memcpy(result.cityNames[city],name.text,sizeof(name));
        }
        for(uint32_t county=1;county<=500;county++)result.counties[county]=read<uint64_t>(result.root,gamebuild::Memory::CountyTable+county*8);
        return result.date==read<uint32_t>(result.state,0x34)&&result.root==read<uint64_t>(input.module+gamebuild::Memory::BasePointerRva)+gamebuild::Memory::BaseAdjust;
    }
    uint32_t region(const World& world,uint32_t tile) const {
        if(tile>=48400)return 0;
        auto hex=world.tiles+tile*0x20;
        if(read<uint64_t>(hex)!=input.tileClass)return 0;
        auto base=read<uint16_t>(hex,gamebuild::Memory::TileBaseOffset);
        if(base>=1&&base<=CitySlots&&(world.availableCities&(uint64_t(1)<<(base-1)))&&world.cityTiles[base]==tile)return base;
        auto county=read<uint16_t>(hex,0x12);if(county==0||county>500)return 0;
        auto record=world.counties[county];if(!record||read<uint64_t>(record)!=input.countyClass)return 0;
        auto city=read<uint8_t>(record,0x34);return city>=1&&city<=CitySlots&&(world.availableCities&(uint64_t(1)<<(city-1)))?city:0;
    }
    void topology(const World& world) {
#ifdef ENVIRONMENT_FIXTURE
        ++topologyBuilds;
#endif
        // Observe decoded geography even when a reload reuses the same root/state addresses.
        uint64_t fingerprint=1469598103934665603ull;
        auto mix=[&](uint64_t value){fingerprint^=value;fingerprint*=1099511628211ull;};
        mix(world.availableCities);mix(world.unavailableCities);
        for(uint32_t city=1;city<=CitySlots;city++){
            mix(world.cities[city]);mix(world.cityTiles[city]);
            for(auto ch:world.cityNames[city])mix(ch);
        }
        for(uint32_t county=1;county<=500;county++)mix(world.counties[county]);
        std::memset(regionHasRiver,0,sizeof(regionHasRiver));std::memset(regionHasForest,0,sizeof(regionHasForest));
        for(uint32_t tile=0;tile<48400;tile++) {
            uint32_t city=region(world,tile);regionByTile[tile]=static_cast<uint8_t>(city);
            auto terrain=read<uint8_t>(world.tiles+tile*0x20,0x10);mix((uint64_t(city)<<16)|terrain);if(!city)continue;
            if(terrain==13||terrain==14)regionHasRiver[city]=1;
            if(forest(terrain))regionHasForest[city]=1;
        }
        topologyRoot=world.root;topologyState=world.state;topologyFingerprint=fingerprint;
    }
    bool captureGeographyInputs(const World& world) {
        for(uint32_t i=1;i<=500;i++){
            countyClasses[i]=read<uint64_t>(world.counties[i]);countyHomes[i]=read<uint8_t>(world.counties[i],0x34);
        }
        return tileInputs(world.tiles,geographyTiles.data(),true);
    }
    bool sameGeographyInputs(const World& world) {
        for(uint32_t i=1;i<=500;i++)if(countyClasses[i]!=read<uint64_t>(world.counties[i])||countyHomes[i]!=read<uint8_t>(world.counties[i],0x34))return false;
        return tileInputs(world.tiles,geographyTiles.data(),false);
    }
    bool riverside(const World& world,uint32_t tile) const {
        uint32_t x=tile%220,y=tile/220;
        auto river=[&](int column,int row) {
            if(column<0||column>=220||row<0||row>=220)return false;
            auto terrain=read<uint8_t>(world.tiles+uint64_t(row*220+column)*0x20,0x10);return terrain==13||terrain==14;
        };
        int step=(x&1)?-1:1;
        return river(int(x),int(y))||river(int(x),int(y)-1)||river(int(x),int(y)+1)||
            river(int(x)-1,int(y))||river(int(x)+1,int(y))||river(int(x)-1,int(y)+step)||river(int(x)+1,int(y)+step);
    }
    void climate(const Message& config,const World& world,uint32_t city,uint32_t date,int& t,int& p) const {
        uint32_t month=((date>>16)&255)-1,day=(date>>24)-1,next=(month+1)%12;
        auto center=[](uint32_t tile){return std::pair{1.5*double(tile%220),std::sqrt(3.)*(double(tile/220)+((tile%220&1)?0.:.5))};};
        auto point=center(world.cityTiles[city]);
        auto sample=[&](uint32_t m,int channel){
            double total=0,weight=0;
            for(uint32_t source=0;source<ClimateSourceCount;source++){
                auto row=config.climate+source*ClimateRowBytes;uint32_t tile{};std::memcpy(&tile,row,4);
                double value{};std::memcpy(&value,row+4+m*16+channel*8,8);
                auto anchor=center(tile);double dx=anchor.first-point.first,dy=anchor.second-point.second,distance=dx*dx+dy*dy;
                if(distance<1e-12)return int(std::nearbyint(value*10));
                double w=1/distance;total+=value*w;weight+=w;
            }
            return int(std::nearbyint(total/weight*10));
        };
        int a=sample(month,0),b=sample(next,0),c=sample(month,1),d=sample(next,1);
        t=a+(b-a)*int(day)/30;p=c+(d-c)*int(day)/30;
    }
    void append(Event event) {
        event.id=++state.lastEvent;state.events[(event.id-1)%EventCount]=event;
        if(event.city>=1&&event.city<=CitySlots&&event.kind>=2&&event.kind<=11&&event.kind!=7){
            uint32_t kind=event.kind<=3?0:event.kind<=5?1:event.kind==6?2:event.kind<=9?3:4;state.recentDates[(event.city-1)*DisasterCount+kind]=event.date;
            auto found=std::find_if(notices.begin(),notices.end(),[&](const Notice& n){return n.date==event.date&&n.city==event.city&&n.kind==kind;});
            if(found==notices.end()){notices.push_back({event.date,event.city,kind});found=notices.end()-1;}
            found->units+=event.unit?1:0;found->troops+=std::max(0,event.troopsBefore-event.troopsAfter);found->morale+=std::max(0,event.moraleBefore-event.moraleAfter);found->food+=std::max(0,event.foodBefore-event.foodAfter);
        }
    }
    uint32_t testFlags() const {return testNoticeFlags|(state.testCity&&state.testKind<DisasterCount&&(state.testState==2||state.testState==4)?DisasterFlags[state.testKind]:0);}
    void cancelTest(uint32_t reason=0,uint32_t cause=notice_trace::ManualCancel) {
        if(state.testMode==1&&(state.testState==1||state.testState==2||state.testState==7))noticeTrace->stop(cause);
        presentation.traceEvent(input.module,notice_trace::Cancel,reason);
        if(presentation.diagnostic)presentation.poll(input.module,true);
        if(presentation.lastReceipt)state.reserved=presentation.diagnosticReceipt();
        testNotice.clear();state.testState=presentation.diagnostic&&presentation.busy()?7:5;state.testError=reason;
    }
#ifdef ENVIRONMENT_FIXTURE
    bool fixtureForegroundPid=true,fixtureEngineForeground=true;
#endif
    bool testForeground() const {
#ifdef ENVIRONMENT_FIXTURE
        auto samePid=fixtureForegroundPid;
#else
        DWORD pid{};GetWindowThreadProcessId(GetForegroundWindow(),&pid);auto samePid=pid==GetCurrentProcessId();
#endif
        if(!noticeGate(noticeTrace.get(),notice_trace::ForegroundPid,samePid,samePid))return false;
#ifdef ENVIRONMENT_FIXTURE
        auto foreground=uint8_t(fixtureEngineForeground);
#else
        auto foreground=read<uint8_t>(input.module+gamebuild::Engine::ForegroundRva);
#endif
        return noticeGate(noticeTrace.get(),notice_trace::EngineForeground,foreground==1,foreground);
    }
    void testFrame(const World& w,const Message& config,uint32_t hour,bool continuous,bool changed) {
        auto request=testRequest.exchange(nullptr);auto now=notice_trace::milliseconds();
        if(request) {
            auto mode=request->reserved&255,city=(request->reserved>>8)&255,kind=(request->reserved>>16)&255;
            cancelTest();state.testId=request->sequence;state.testMode=mode;state.testCity=city;state.testKind=kind;state.testDate=w.date;state.testError=0;
            if(mode){state.reserved=0;presentation.lastReceipt=0;}
            if(!mode)return;
            if(mode>2||!effectCity(city)||kind>=DisasterCount||!(w.availableCities&(uint64_t(1)<<(city-1)))||presentation.busy()) {state.testState=6;state.testError=ERROR_INVALID_STATE;return;}
            testRoot=w.root;testWorld=w.state;testConfig=config.sequence;testHour=hour;testDeadline=now+120000;testStillSince=now;testForegroundSince=0;
            if(mode==1&&effectCity(city)){noticeTrace->start(request->sequence,kind);presentation.traceEvent(input.module,notice_trace::Requested);}
            state.testState=mode==1?1:3;
            // A request received on a boundary never authorizes damage on that same frame.
            return;
        }
        if(state.testState!=1&&state.testState!=2&&state.testState!=3)return;
        if(noticeTrace->enabled&&noticeTrace->expired()){cancelTest();return;}
        if(w.root!=testRoot||w.state!=testWorld||config.sequence!=testConfig||changed||
            (lastDate&&w.date!=lastDate&&!continuous)||(lastDate==w.date&&hour<lastHour)) {cancelTest(ERROR_INVALID_STATE,notice_trace::StateChanged);return;}
        if(state.testMode==1) {
            if(w.date!=state.testDate||hour!=testHour){cancelTest(ERROR_RETRY,notice_trace::StateChanged);return;}
            if(now>=testDeadline){cancelTest(WAIT_TIMEOUT,notice_trace::PreparationDeadline);return;}
            if(!testForeground()){testForegroundSince=0;return;}
            if(!testForegroundSince)testForegroundSince=now;
            if(state.testState==1&&noticeGate(noticeTrace.get(),notice_trace::RequestAge,now-testStillSince>=800,now-testStillSince)&&
                noticeGate(noticeTrace.get(),notice_trace::ForegroundAge,now-testForegroundSince>=800,now-testForegroundSince)&&
                noticeGate(noticeTrace.get(),notice_trace::NotBusy,!presentation.busy(),presentation.busy())&&noticeReady(input.module,noticeTrace.get())) {
                wchar_t fallback[24]{};swprintf_s(fallback,L"도시 %u",state.testCity);
                const auto cityName=w.cityNames[state.testCity][0]?w.cityNames[state.testCity]:fallback;
                wchar_t text[201]{};const wchar_t* names[]{L"홍수",L"산불",L"가뭄",L"폭설",L"폭염"};
                swprintf_s(text,L"[개발자 · 표시 전용] %s · %s\f지도 표식과 그림·원본 효과음 확인\f병력·사기·병량 피해 없음.\f종료 시 테스트 표식을 지웁니다.",cityName,names[state.testKind]);
                if(presentation.begin(input.module,text,state.testKind,true)){state.testState=2;state.reserved=1;presentationBusy=presentation.busy();}
                else {noticeTrace->stop(notice_trace::QueueFailure);state.testState=6;state.testError=ERROR_INVALID_STATE;}
            }
        }
    }
    void frame() {
        // The authenticated AppUpdate dispatch can migrate between game workers.
        // Serialize the whole admitted frame, including its baseline and notice
        // ownership; a thread ID is a receipt, not the frame's authorization.
        std::lock_guard lock(stateLock);
        presentation.poll(input.module,stopPresentation);presentationBusy=presentation.busy();
        if(state.testState==7&&!presentation.busy())state.testState=5;
        if(notices.empty()&&!presentation.busy())testNoticeFlags=0;
        if(presentation.lastReceipt)state.reserved=presentation.diagnosticReceipt();
        if(!admission)return;
        geographyReady=false;
        auto id=GetCurrentThreadId();thread.store(id);
        ++frames;auto config=pending.load();if(!config)return;
        World worldValue{};if(!world(worldValue)){if(state.testState>=1&&state.testState<=3)cancelTest(ERROR_INVALID_STATE,notice_trace::StateChanged);testRequest.store(nullptr);lastDate=0;return;}
        state.thread=id;state.date=worldValue.date;
        if(!captureGeographyInputs(worldValue)){lastDate=0;return;}
        topology(worldValue);
        World verified{};
        if(!world(verified)||verified.date!=worldValue.date||!sameGeographyWorld(verified,worldValue)||!sameGeographyInputs(verified)){lastDate=0;return;}
        bool geographyChanged=lastTopologyFingerprint&&lastTopologyFingerprint!=topologyFingerprint;
        state.availableCities=worldValue.availableCities;state.unavailableCities=worldValue.unavailableCities;
        uint32_t today=serial(worldValue.date);
        uint32_t hour=read<uint8_t>(worldValue.state,0x38);
        // Only continuous forward game time authorizes damage. Load/rewind/jump establishes a new baseline.
        bool continuous=lastDate&&!geographyChanged&&lastRoot==worldValue.root&&lastState==worldValue.state&&
            today==serial(lastDate)+1&&lastHour>hour;
        if(lastDate&&(geographyChanged||lastRoot!=worldValue.root||lastState!=worldValue.state||(worldValue.date!=lastDate&&!continuous))){
            std::memset(state.recentDates,0,sizeof(state.recentDates));notices.clear();}
        std::fill(state.recentDates+46*DisasterCount,state.recentDates+CitySlots*DisasterCount,0u);
        for(auto& date:state.recentDates)if(date&&(today<serial(date)||today-serial(date)>=10))date=0;
        bool changed=state.applied!=config->sequence;
        auto requestThisFrame=testRequest.load()!=nullptr;
        testFrame(worldValue,*config,hour,continuous,changed);
        state.applied=config->sequence;state.flags=config->flags;state.values=config->values;
        if(presentation.busy()&&!presentation.diagnostic&&!((config->flags|testFlags())&DisasterFlags[presentation.kind]))presentation.poll(input.module,true);
        for(uint32_t city=1;city<=CitySlots;city++) {
            if(!effectCity(city)||!(worldValue.availableCities&(uint64_t(1)<<(city-1)))){state.weather[city-1]=0;continue;}
            int t{},p{};climate(*config,worldValue,city,worldValue.date,t,p);
            state.weather[city-1]=static_cast<uint8_t>(weather(config->seed,today,city,t,p));
        }
        previous=worldValue;
        lastTopologyFingerprint=topologyFingerprint;lastDate=worldValue.date;lastRoot=worldValue.root;lastState=worldValue.state;lastHour=hour;
        if(!testNotice.empty()&&!presentation.busy()&&noticeReady(input.module)){state.reserved=presentation.begin(input.module,testNotice.c_str(),testNoticeKind,true)?1u:2u;presentationBusy=presentation.busy();testNotice.clear();}
        if(continuous&&!changed&&((worldValue.date>>24)-1)%10==0){
            bool forced=state.testState==3&&!requestThisFrame;
            effects(worldValue,*config,today,forced?state.testCity:0,state.testKind);
            if(forced){state.testState=failure?6:4;state.testError=failure;state.testDate=worldValue.date;testNoticeFlags|=DisasterFlags[state.testKind];}
        }
        while(!notices.empty()&&!((config->flags|testFlags())&DisasterFlags[notices.front().kind]))notices.erase(notices.begin());
        if(!notices.empty()&&!presentation.busy()&&noticeReady(input.module)){
            auto& n=notices.front();wchar_t text[201]{};const wchar_t* names[]{L"홍수",L"산불",L"가뭄",L"폭설",L"폭염"};
            wchar_t place[32]{};if(worldValue.cityNames[n.city][0])wcscpy_s(place,worldValue.cityNames[n.city]);else swprintf_s(place,L"거점 %u",n.city);
            swprintf_s(text,L"%u년 %u월 %u일 · %s %s 피해\f피해 부대 %u개 · 병력 -%lld\f사기 합계 -%lld · 도시 병량 -%lld",n.date&65535,(n.date>>16)&255,n.date>>24,place,names[n.kind],n.units,n.troops,n.morale,n.food);
            if(presentation.begin(input.module,text,n.kind)){presentationBusy=presentation.busy();++noticeQueued;noticeDate=n.date;noticeCity=n.city;noticeKind=n.kind;notices.erase(notices.begin());}
            else fail(ERROR_INVALID_DATA);
        }
        state.noticesQueued=noticeQueued;state.noticesPending=static_cast<uint32_t>(notices.size());state.noticeDate=noticeDate;state.noticeCity=noticeCity;state.noticeKind=noticeKind;
        geographyReady=true;
    }
    void effects(const World& world,const Message& config,uint32_t today,uint32_t testCity=0,uint32_t testKind=0) {
        uint32_t disasters[CitySlots+1]{};
        for(uint32_t city=1;city<=CitySlots;city++) {
            if(!effectCity(city)||!(world.availableCities&(uint64_t(1)<<(city-1))))continue;
            int t{},p{};climate(config,world,city,world.date,t,p);
            disasters[city]=environment::disasters(config.seed,today,city,config.flags,config.values,state.weather[city-1],t,p,regionHasRiver[city],regionHasForest[city]);
        }
        if(effectCity(testCity)&&testKind<DisasterCount)disasters[testCity]=
            (testKind==0&&!regionHasRiver[testCity])||(testKind==1&&!regionHasForest[testCity])?0:DisasterFlags[testKind];
        // Verified plain fields, on the engine frame after its update. Fractional losses retain positive units.
        // Native morale mitigation and zero-morale destruction queues remain the game's responsibility.
        for(uint32_t slot=1;slot<=500&&admission;slot++) {
            uint64_t unit=world.units+uint64_t(slot)*0x200;
            if(read<uint64_t>(unit)!=input.unitClass)return fail(ERROR_INVALID_DATA);
            uint32_t kind=read<uint8_t>(unit,0x10),commander=read<uint16_t>(unit,0x12);
            if((kind!=1&&kind!=2)||!commander)continue;
            uint32_t tile=read<uint16_t>(unit,0x2a),city=region(world,tile);if(!effectCity(city))continue;
            uint32_t terrain=read<uint8_t>(world.tiles+tile*0x20,0x10),eventKind=0;
            int troops=read<uint16_t>(unit,0x16),morale=read<uint8_t>(unit,0x1a),newTroops=troops,newMorale=morale;
            if(troops<=0||morale<=0)continue;
            if((config.flags&WeatherMorale)&&state.weather[city-1]&&!water(terrain)) {
                newMorale=morale-config.values.weatherMoraleLoss;eventKind=1;
            }
            if((disasters[city]&Flood)&&riverside(world,tile))eventKind=3;
            else if((disasters[city]&Wildfire)&&forest(terrain))eventKind=5;
            else if((disasters[city]&Blizzard)&&!water(terrain))eventKind=9;
            else if((disasters[city]&Heatwave)&&!water(terrain))eventKind=11;
            if(eventKind==3||eventKind==5||eventKind==9||eventKind==11) {
                int percent=eventKind==3?config.values.floodTroopsPercent:eventKind==5?config.values.wildfireTroopsPercent:eventKind==9?config.values.blizzardTroopsPercent:config.values.heatwaveTroopsPercent;
                newTroops=troops-troops*percent/100;
                newMorale-=eventKind==3?config.values.floodMoraleLoss:eventKind==5?config.values.wildfireMoraleLoss:eventKind==9?config.values.blizzardMoraleLoss:config.values.heatwaveMoraleLoss;
            }
            if(troops==newTroops&&morale==newMorale)continue;
            if(newTroops<=0||read<uint16_t>(unit,0x16)!=troops||read<uint8_t>(unit,0x1a)!=morale)return fail(ERROR_RETRY);
            if(newTroops!=troops&&!write<uint16_t>(unit+0x16,static_cast<uint16_t>(newTroops)))return fail(ERROR_WRITE_FAULT);
            if(newMorale!=morale) {
                using AddMorale=gamebuild::Abi::AddMorale;
                reinterpret_cast<AddMorale>(input.module+gamebuild::Engine::AddMoraleRva)(reinterpret_cast<void*>(unit),newMorale-morale,nullptr,0,1);
                newMorale=read<uint8_t>(unit,0x1a);
            }
            if(read<uint16_t>(unit,0x16)!=newTroops)return fail(ERROR_WRITE_FAULT);
            append({0,world.date,eventKind,city,tile,slot,commander,troops,newTroops,morale,newMorale,0,0});
        }
        for(uint32_t city=1;city<=CitySlots&&admission;city++)if(disasters[city]) {
            auto place=world.cities[city];if(!read<uint16_t>(place,0x30))continue;
            int food=read<int32_t>(place,0x38);if(food<0)return fail(ERROR_INVALID_DATA);
            uint32_t kind=0;int percent=-1;
            for(auto loss:{std::pair{Flood,std::pair{config.values.floodFoodPercent,2u}},
                std::pair{Wildfire,std::pair{config.values.wildfireFoodPercent,4u}},std::pair{Drought,std::pair{config.values.droughtFoodPercent,6u}},
                std::pair{Blizzard,std::pair{config.values.blizzardFoodPercent,8u}},std::pair{Heatwave,std::pair{config.values.heatwaveFoodPercent,10u}}})
                if((disasters[city]&loss.first)&&loss.second.first>percent){percent=loss.second.first;kind=loss.second.second;}
            int next=food-static_cast<int>(int64_t(food)*percent/100);
            if(next==food)continue;
            if(!write<int32_t>(place+0x38,next)||read<int32_t>(place,0x38)!=next)return fail(ERROR_WRITE_FAULT);
            append({0,world.date,kind,city,48400,0,0,0,0,0,0,food,next});
        }
    }
    void fail(uint32_t error){failure=error;admission=false;}
    bool weatherSnapshot(WeatherSnapshot& result) {
        std::lock_guard lock(stateLock);auto config=pending.load();World currentWorld{};
        if((!admission&&!presentation.visual())||!config||state.applied!=config->sequence||!geographyReady||!world(currentWorld)||!sameGeographyWorld(currentWorld,previous))return false;
        // A frame publishes geography only after a full rebuild and input check.
        // Every reader still validates ALL consumed raw fields, including same-
        // address reloads; it never rebuilds regions/fingerprint on the Draw path.
        if(lastTopologyFingerprint!=topologyFingerprint||!sameGeographyInputs(currentWorld))return false;
        result.generation=state.generation;result.sequence=state.applied;result.date=currentWorld.date;result.flags=admission?config->flags:0;
        result.reserved=presentation.visual();
        result.flags|=testFlags();
        std::memcpy(result.regions,regionByTile,sizeof(regionByTile));
        std::memcpy(result.recentDates,state.recentDates,sizeof(result.recentDates));
        std::fill(result.recentDates+46*DisasterCount,result.recentDates+CitySlots*DisasterCount,0u);
        if(state.testState==2&&effectCity(state.testCity))result.recentDates[(state.testCity-1)*DisasterCount+state.testKind]=currentWorld.date;
        for(auto& date:result.recentDates)if(date&&(serial(currentWorld.date)<serial(date)||serial(currentWorld.date)-serial(date)>=10))date=0;
        if(currentWorld.date==lastDate)std::memcpy(result.weather,state.weather,sizeof(result.weather));
        else for(uint32_t city=1;city<=CitySlots;city++){if(!effectCity(city)||!(currentWorld.availableCities&(uint64_t(1)<<(city-1)))){result.weather[city-1]=0;continue;}int t{},p{};climate(*config,currentWorld,city,currentWorld.date,t,p);
            result.weather[city-1]=static_cast<uint8_t>(weather(config->seed,serial(currentWorld.date),city,t,p));}
        for(uint32_t city=1;city<=CitySlots;city++)if(!effectCity(city)||!(currentWorld.availableCities&(uint64_t(1)<<(city-1))))result.weather[city-1]=0;
        return currentWorld.date==read<uint32_t>(currentWorld.state,0x34)&&currentWorld.root==read<uint64_t>(input.module+gamebuild::Memory::BasePointerRva)+gamebuild::Memory::BaseAdjust;
    }
    int speed(void* value,int original) {
        if(!admission||original<=0||thread!=GetCurrentThreadId())return original;
        ++speedCalls;auto config=pending.load();if(!config||!(config->flags&(RainMovement|SnowMovement)))return original;
        std::lock_guard lock(stateLock);if(state.applied!=config->sequence||!previous.root)return original;
        auto unit=reinterpret_cast<uint64_t>(value);
        if(read<uint64_t>(input.module+gamebuild::Memory::BasePointerRva)+gamebuild::Memory::BaseAdjust!=previous.root||read<uint64_t>(previous.root,gamebuild::Memory::StatePointer)!=previous.state)return original;
        if(unit<previous.units||unit>=previous.units+501*0x200||((unit-previous.units)%0x200)!=0)return original;
        if(read<uint64_t>(unit)!=input.unitClass)return original;
        auto tile=read<uint16_t>(unit,0x2a);uint32_t city=region(previous,tile);if(!effectCity(city))return original;
        if(water(read<uint8_t>(previous.tiles+tile*0x20,0x10)))return original;
        uint32_t date=read<uint32_t>(previous.state,0x34);if(!validDate(date))return original;
        int t{},p{};climate(*config,previous,city,date,t,p);
        int percent=movement(weather(config->seed,serial(date),city,t,p),config->flags,config->values);
        int next=std::max(1,int(int64_t(original)*percent/100));if(next<original)++slowed;return next;
    }
};
std::atomic<std::shared_ptr<Session>> current;
std::atomic<std::shared_ptr<notice_trace::Store>> lastNoticeTrace;
std::atomic<bool> noticeTraceUsed{false};
BOOL readWeather(WeatherSnapshot* output) {
    WeatherSnapshot header;if(!output||!copy(header,reinterpret_cast<uint64_t>(output))||header.magic!=WeatherMagic||header.version!=WeatherVersion||header.size!=sizeof(header))return FALSE;
    auto session=current.load();WeatherSnapshot result;
    if(!session||!session->weatherSnapshot(result))return FALSE;
    return write(reinterpret_cast<uint64_t>(output),result)?TRUE:FALSE;
}
thread_local unsigned callbackDepth=0;
struct Scope {
    DWORD error=GetLastError();unsigned csr=_mm_getcsr();std::shared_ptr<Session> session;
    Scope(){++callbacks;if(callbackDepth++==0)AcquireSRWLockShared(&dispatch);session=current.load();SetLastError(error);_mm_setcsr(csr);}
    void preserve(){error=GetLastError();csr=_mm_getcsr();}
    ~Scope(){session.reset();if(--callbackDepth==0)ReleaseSRWLockShared(&dispatch);--callbacks;SetLastError(error);_mm_setcsr(csr);}
};
int __fastcall speed(void* unit) {Scope scope;int result=originalSpeed(unit);scope.preserve();try{return scope.session?scope.session->speed(unit,result):result;}
    catch(...){if(scope.session)scope.session->fail(ERROR_NOT_ENOUGH_MEMORY);return result;}}
LONG transaction(bool attach) {
    struct Exclusive {Exclusive(){AcquireSRWLockExclusive(&dispatch);}~Exclusive(){ReleaseSRWLockExclusive(&dispatch);}} exclusive;
    struct Threads {std::vector<HANDLE> values;~Threads(){for(auto h:values)CloseHandle(h);}} threads;
    LONG result=NO_ERROR;Handle snap{CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0)};
    if(snap.value==INVALID_HANDLE_VALUE)result=GetLastError();
    else {
        THREADENTRY32 e{sizeof(e)};
        if(!Thread32First(snap.value,&e))result=GetLastError();
        else do {
            if(e.th32OwnerProcessID!=GetCurrentProcessId()||e.th32ThreadID==GetCurrentThreadId())continue;
            HANDLE h=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_SET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,e.th32ThreadID);
            if(!h){result=GetLastError();break;}try{threads.values.push_back(h);}catch(...){CloseHandle(h);throw;}
        } while(Thread32Next(snap.value,&e));
        if(!result&&GetLastError()!=ERROR_NO_MORE_FILES)result=GetLastError();
    }
    if(!result) {
        result=DetourTransactionBegin();if(!result) {
            for(HANDLE h:threads.values){DWORD exit{};if(GetExitCodeThread(h,&exit)&&exit!=STILL_ACTIVE)continue;result=DetourUpdateThread(h);if(result)break;}
            if(!result)result=attach?DetourAttach(reinterpret_cast<PVOID*>(&originalSpeed),reinterpret_cast<void*>(speed)):
                DetourDetach(reinterpret_cast<PVOID*>(&originalSpeed),reinterpret_cast<void*>(speed));
            if(!result)result=DetourTransactionCommit();else DetourTransactionAbort();
        }
    }
    return result;
}
bool protection(void* address,size_t size,DWORD expected) {
    MEMORY_BASIC_INFORMATION info{};if(!VirtualQuery(address,&info,sizeof(info)))return false;
    if(info.Protect!=expected){DWORD before{};if(!VirtualProtect(address,size,expected,&before))return false;}
    return VirtualQuery(address,&info,sizeof(info))&&info.Protect==expected;
}
}
extern "C" __declspec(dllexport) void __fastcall EnvironmentFrame(void* app,void* value,void* third) {
    Scope scope;originalUpdate(app,value,third);scope.preserve();
    if(scope.session)try{scope.session->frame();}catch(...){scope.session->fail(ERROR_NOT_ENOUGH_MEMORY);}
}
// Evaluate the exact movement callback on the authenticated game frame, without moving a unit.
extern "C" __declspec(dllexport) int __fastcall EnvironmentMovement(void* unit) {return originalSpeed?speed(unit):0;}
extern "C" __declspec(dllexport) DWORD WINAPI EnvironmentRestoreDispatch(uint64_t* request) {
    uint64_t args[2]{};if(!request||!copy(args,reinterpret_cast<uint64_t>(request))||!args[0]||!args[1]||reinterpret_cast<uint64_t>(originalUpdate)!=args[0]+gamebuild::Engine::UpdateRva)return ERROR_INVALID_PARAMETER;
    AcquireSRWLockShared(&dispatch);auto s=current.load();auto next=s&&s->admission&&s->slotHooked?reinterpret_cast<void*>(EnvironmentFrame):reinterpret_cast<void*>(originalUpdate);
    auto before=InterlockedCompareExchangePointer(reinterpret_cast<void**>(args[0]+gamebuild::Engine::UpdateSlotRva),next,reinterpret_cast<void*>(args[1]));
    ReleaseSRWLockShared(&dispatch);return before==reinterpret_cast<void*>(args[1])||before==reinterpret_cast<void*>(originalUpdate)?NO_ERROR:ERROR_INVALID_STATE;
}
namespace {
Recovery finish(const std::shared_ptr<Session>& s) {
    Recovery result;result.generation=s->input.generation;result.reserved=s->failure;s->admission=false;
    if(!s->prepared){result.complete=1;return result;}
    s->noticeTrace->stop(notice_trace::OwnerStop);s->stopPresentation=true;auto uiDeadline=GetTickCount64()+2000;
    while(s->presentationBusy&&GetTickCount64()<uiDeadline)Sleep(1);
    if(s->presentationBusy){result.error=ERROR_BUSY;return result;}
    auto speedTarget=reinterpret_cast<BYTE*>(s->input.module+gamebuild::Engine::MoveSpeedRva);
    if(s->speedHooked) {
        if(std::memcmp(speedTarget,s->speedInstalled,sizeof(MoveSpeedCode))){result.error=ERROR_INVALID_DATA;return result;}
        result.error=transaction(false);if(result.error)return result;s->speedHooked=false;
    }
    auto slot=reinterpret_cast<void**>(s->input.module+gamebuild::Engine::UpdateSlotRva);
    if(s->slotHooked) {
        DWORD old{};if(!VirtualProtect(slot,8,PAGE_READWRITE,&old)){result.error=GetLastError();return result;}
        bool restored=false;auto deadline=GetTickCount64()+2000;
        do {
            auto before=InterlockedCompareExchangePointer(slot,reinterpret_cast<void*>(originalUpdate),reinterpret_cast<void*>(EnvironmentFrame));
            if(before==reinterpret_cast<void*>(EnvironmentFrame)||before==reinterpret_cast<void*>(originalUpdate)){restored=true;break;}
            Sleep(5); // A verified one-shot RunOnFrame may temporarily own this slot.
        }while(GetTickCount64()<deadline);
        if(!protection(slot,8,s->slotProtect)||!restored){result.error=ERROR_BUSY;return result;}s->slotHooked=false;
    }
    auto deadline=GetTickCount64()+2000;while(callbacks&&GetTickCount64()<deadline)Sleep(1);
    if(callbacks||std::memcmp(speedTarget,s->speedBefore,sizeof(MoveSpeedCode))||!protection(speedTarget,sizeof(MoveSpeedCode),s->speedProtect)) {
        result.error=ERROR_INVALID_STATE;return result;
    }
    s->presentation.traceEvent(s->input.module,notice_trace::Finished);
    if(s->noticeTrace->enabled)lastNoticeTrace.store(s->noticeTrace);
    result.frames=s->frames;result.thread=s->thread;result.complete=1;current.store(nullptr);s->pending.store(nullptr);return result;
}
bool attach(const std::shared_ptr<Session>& s) {
    auto speedTarget=reinterpret_cast<BYTE*>(s->input.module+gamebuild::Engine::MoveSpeedRva),app=reinterpret_cast<BYTE*>(s->input.module+gamebuild::Engine::UpdateRva);
    auto slot=reinterpret_cast<void**>(s->input.module+gamebuild::Engine::UpdateSlotRva);MEMORY_BASIC_INFORMATION info{},slotInfo{};
    if(std::memcmp(speedTarget,MoveSpeedCode,sizeof(MoveSpeedCode))||std::memcmp(app,AppUpdateCode,sizeof(AppUpdateCode))||
        std::memcmp(reinterpret_cast<void*>(s->input.module+gamebuild::Engine::AddMoraleRva),AddMoraleCode,sizeof(AddMoraleCode))||!noticeCode(s->input.module)||
        *slot!=app||!VirtualQuery(speedTarget,&info,sizeof(info))||info.Protect!=PAGE_EXECUTE_READ||
        !VirtualQuery(slot,&slotInfo,sizeof(slotInfo)))return false;
    s->speedProtect=info.Protect;s->slotProtect=slotInfo.Protect;std::memcpy(s->speedBefore,speedTarget,sizeof(MoveSpeedCode));s->prepared=true;
    originalSpeed=reinterpret_cast<Speed>(speedTarget);originalUpdate=reinterpret_cast<Update>(app);current.store(s);
    auto error=transaction(true);if(error){s->failure=error;return false;}s->speedHooked=true;
    std::memcpy(s->speedInstalled,speedTarget,sizeof(MoveSpeedCode));
    if(!protection(speedTarget,sizeof(MoveSpeedCode),s->speedProtect))return false;
    DWORD before{};if(!VirtualProtect(slot,8,PAGE_READWRITE,&before))return false;
    s->slotHooked=InterlockedCompareExchangePointer(slot,reinterpret_cast<void*>(EnvironmentFrame),app)==app;
    return protection(slot,8,s->slotProtect)&&s->slotHooked;
}
struct Io {
    HANDLE pipe,owner;std::shared_ptr<Session> session;uint64_t beat=GetTickCount64();
    bool alive(){return session->admission&&!session->failure&&GetTickCount64()-beat<5000&&WaitForSingleObject(owner,0)==WAIT_TIMEOUT;}
    bool wait(OVERLAPPED& ov,DWORD& count) {
        bool live=true;while(WaitForSingleObject(ov.hEvent,50)==WAIT_TIMEOUT)if(!alive()){CancelIoEx(pipe,&ov);live=false;break;}
        bool completed=GetOverlappedResult(pipe,&ov,&count,TRUE)!=FALSE;return completed&&live;
    }
    bool exchange(void* value,DWORD size,bool send) {
        Handle event{CreateEventW(nullptr,TRUE,FALSE,nullptr)};if(!event.value)return false;
        OVERLAPPED ov{};ov.hEvent=event.value;DWORD count{};
        bool ok=send?WriteFile(pipe,value,size,&count,&ov)!=FALSE:ReadFile(pipe,value,size,&count,&ov)!=FALSE;
        if(!ok&&GetLastError()==ERROR_IO_PENDING)ok=wait(ov,count);
        return ok&&count==size;
    }
    bool connect() {
        Handle event{CreateEventW(nullptr,TRUE,FALSE,nullptr)};if(!event.value)return false;
        OVERLAPPED ov{};ov.hEvent=event.value;DWORD count{};
        bool ok=ConnectNamedPipe(pipe,&ov)!=FALSE;auto error=ok?NO_ERROR:GetLastError();
        if(error==ERROR_PIPE_CONNECTED)ok=true;else if(error==ERROR_IO_PENDING)ok=wait(ov,count);
        ULONG pid{};return ok&&GetNamedPipeClientProcessId(pipe,&pid)&&pid==session->input.controllerPid;
    }
};
Recovery serve(const Bootstrap& input) {
    Recovery failed;failed.generation=input.generation;failed.complete=1;
    std::unique_lock guard(connection,std::try_to_lock);
    if(!guard.owns_lock()||current.load()){failed.error=ERROR_BUSY;failed.complete=0;return failed;}
    auto session=std::make_shared<Session>(input);
    Handle owner{OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,input.controllerPid)};
    FILETIME created{},exit{},kernel{},user{};
    if(!owner.value||!GetProcessTimes(owner.value,&created,&exit,&kernel,&user)||
        (uint64_t(created.dwHighDateTime)<<32|created.dwLowDateTime)!=input.controllerStartFileTime){failed.error=ERROR_ACCESS_DENIED;return failed;}
    Handle token;DWORD count{};std::vector<BYTE> tokenUser;Local sid,descriptor;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token.value)){failed.error=GetLastError();return failed;}
    GetTokenInformation(token.value,TokenUser,nullptr,0,&count);tokenUser.resize(count);
    if(!count||!GetTokenInformation(token.value,TokenUser,tokenUser.data(),count,&count)||
        !ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(tokenUser.data())->User.Sid,reinterpret_cast<LPWSTR*>(&sid.value))) {
        failed.error=ERROR_ACCESS_DENIED;return failed;
    }
    auto dacl=std::wstring(L"D:P(A;;GA;;;SY)(A;;GA;;;")+reinterpret_cast<LPWSTR>(sid.value)+L")";
    if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(dacl.c_str(),SDDL_REVISION_1,reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value),nullptr)) {
        failed.error=GetLastError();return failed;
    }
    SECURITY_ATTRIBUTES attributes{sizeof(attributes),descriptor.value,FALSE};
    Handle pipe{CreateNamedPipeW(input.pipe,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_MESSAGE|PIPE_READMODE_MESSAGE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,sizeof(Reply),sizeof(Message),0,&attributes)};
    if(pipe.value==INVALID_HANDLE_VALUE){failed.error=GetLastError();return failed;}
    if(!attach(session)){auto result=finish(session);if(!result.error)result.error=ERROR_REVISION_MISMATCH;return result;}
    Io io{pipe.value,owner.value,session};uint64_t sequence=0;
    if(io.connect())while(io.alive()) {
        Message message{};if(!io.exchange(&message,sizeof(message),false))break;
        if(message.magic!=Magic||message.version!=Version||message.size!=sizeof(Message)||message.generation!=input.generation||message.sequence<=sequence)break;
        sequence=message.sequence;io.beat=GetTickCount64();uint32_t error=0;
        if(message.command==1) {
            if(!valid(message))error=ERROR_INVALID_DATA;
            else session->pending.store(std::make_shared<Message>(message));
        }else if(message.command==4){
            auto text=reinterpret_cast<const wchar_t*>(message.climate);size_t length=0;while(length<=200&&text[length])++length;
            if(!length||length>200||message.reserved>=DisasterCount)error=ERROR_INVALID_DATA;
            else{std::lock_guard lock(session->stateLock);session->testNotice.assign(text,length);session->testNoticeKind=message.reserved;session->state.reserved=0;}
        }else if(message.command==5){
            auto mode=message.reserved&255,city=(message.reserved>>8)&255,kind=(message.reserved>>16)&255;
            if(message.reserved>>24||mode>2||(mode&&(city<1||city>CitySlots||kind>=DisasterCount)))error=ERROR_INVALID_DATA;
            else session->testRequest.store(std::make_shared<Message>(message));
        }else if(message.command!=2&&message.command!=3)error=ERROR_INVALID_FUNCTION;
        Reply reply;
        {std::lock_guard lock(session->stateLock);reply=session->state;}
        reply.sequence=sequence;reply.error=error?error:session->failure.load();reply.frames=session->frames;
        reply.speedCalls=session->speedCalls;reply.slowed=session->slowed;reply.thread=session->thread;
        if(!io.exchange(&reply,sizeof(reply),true)||message.command==3||error)break;
    }
    auto result=finish(session);DisconnectNamedPipe(pipe.value);return result;
}
bool bootstrap(Bootstrap* request,Bootstrap& input) {
    return request&&copy(input,reinterpret_cast<uint64_t>(request))&&input.magic==Magic&&input.version==Version&&input.size==sizeof(Bootstrap)&&
        input.generation&&input.controllerPid&&input.module==reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr))&&
        input.worldClass&&input.unitClass&&input.tileClass&&input.countyClass&&input.cityClass&&input.buildingClass&&input.pipe[127]==0&&
        !wcsncmp(input.pipe,L"\\\\.\\pipe\\san14-environment-",27);
}
}
extern "C" __declspec(dllexport) DWORD WINAPI EnvironmentSession(Bootstrap* request) {
    Bootstrap input{};if(!bootstrap(request,input))return ERROR_INVALID_PARAMETER;
    HMODULE pinned{};if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&EnvironmentSession),&pinned))return GetLastError();
    Recovery result;
    try{result=serve(input);}catch(...){result.generation=input.generation;result.error=ERROR_NOT_ENOUGH_MEMORY;result.complete=1;if(auto session=current.load())result=finish(session);}
    request->recovery=result;return result.error;
}
extern "C" __declspec(dllexport) DWORD WINAPI EnvironmentRecover(Bootstrap* request) {
    Bootstrap input{};if(!bootstrap(request,input))return ERROR_INVALID_PARAMETER;
    std::unique_lock guard(connection);auto session=current.load();
    if(!session||session->input.generation!=input.generation||session->input.controllerPid!=input.controllerPid)return ERROR_INVALID_STATE;
    auto result=finish(session);request->recovery=result;return result.error;
}
extern "C" __declspec(dllexport) BOOL WINAPI EnvironmentReadWeather(WeatherSnapshot* output){return readWeather(output);}
static BOOL copyNoticeQuery(notice_trace::Query* output,const notice_trace::Query& value)noexcept{
    __try{*output=value;return TRUE;}__except(EXCEPTION_EXECUTE_HANDLER){return FALSE;}
}
extern "C" __declspec(dllexport) BOOL WINAPI EnvironmentNoticeTraceExchange(notice_trace::Query* output){
    notice_trace::Query query;if(!output||!copy(query,reinterpret_cast<uint64_t>(output))||query.magic!=notice_trace::Magic||
        query.version!=notice_trace::Version||query.size!=sizeof(query)||!query.season||query.command>4)return FALSE;
    auto session=current.load();auto trace=session?session->noticeTrace:lastNoticeTrace.load();if(!trace)return FALSE;
    if(query.command==1){
        if(!session||!session->admission||query.controllerPid!=session->input.controllerPid||query.controllerStart!=session->input.controllerStartFileTime||noticeTraceUsed.exchange(true))return FALSE;
        if(!trace->arm(session->input.generation,query.season))return FALSE;
        notice_trace::State armed;armed.phase=notice_trace::Armed;trace->record(armed);
    }
    if(!trace->read(query))return FALSE;
    if(query.command==4){if(query.reserved<notice_trace::PreparationDeadline||query.reserved>notice_trace::OwnerStop)return FALSE;trace->stop(query.reserved);}
    if(query.command==3){if(!query.state.ended)return FALSE;lastNoticeTrace.store(nullptr);}
    return copyNoticeQuery(output,query);
}
#ifndef ENVIRONMENT_FIXTURE
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
#endif
