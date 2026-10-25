#define ENVIRONMENT_FIXTURE
#include "EnvironmentBridge.cpp"
#include <iostream>
#include <fstream>
#include <array>
#include <stdexcept>
#include <thread>
namespace {
void require(bool value,const char* text){if(!value)throw std::runtime_error(text);std::cout<<"PASS "<<text<<'\n';}
uint32_t date(unsigned day){return 192|(7u<<16)|(day<<24);}
__declspec(noinline) void __fastcall moraleAdapter(void* unit,int delta,void*,int,int deferred) {
    if(deferred!=1)throw std::runtime_error("Morale must use deferred native routing");
    auto value=reinterpret_cast<uint64_t>(unit);write<uint8_t>(value+0x1a,static_cast<uint8_t>(std::clamp(int(read<uint8_t>(value,0x1a))+delta,0,100)));
}
struct Fixture {
    uint64_t module{},root{},world{},manager{},tiles{},units{},cities{},counties{},buildings{};Bootstrap input{};int climateT{},climateP{};
    std::shared_ptr<Session> s;std::shared_ptr<Message> message;
    static uint64_t allocate(size_t size){auto value=VirtualAlloc(nullptr,size,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(!value)throw std::bad_alloc();return reinterpret_cast<uint64_t>(value);}
    Fixture() {
        module=allocate(0x2780000);root=allocate(0x90000);world=allocate(0x1000);manager=allocate(0x1000);
        tiles=allocate(48400*0x20);units=allocate(501*0x200);cities=allocate((CitySlots+1)*0x168);counties=allocate(501*0x200);buildings=allocate((CitySlots+1)*0x20);
        input.module=module;input.generation=17;input.worldClass=101;input.unitClass=102;input.tileClass=103;input.countyClass=104;input.cityClass=105;input.buildingClass=106;
        write<uint64_t>(module+0x277df18,root-0x290);write<uint64_t>(root+0x86f58,world);write<uint64_t>(world,101);
        write<uint64_t>(module+0x1b9fb70,manager);write<uint64_t>(manager+0x210,1);
        write<uint64_t>(root+0xdfe0,tiles);write<uint64_t>(root+0x7e5a8,units);
        for(unsigned tile=0;tile<48400;tile++){write<uint64_t>(tiles+tile*0x20,103);write<uint16_t>(tiles+tile*0x20+0x12,1);}
        for(unsigned i=0;i<=500;i++)write<uint64_t>(units+i*0x200,102);
        for(unsigned city=1;city<=CitySlots;city++) {
            auto value=cities+city*0x168;write<uint64_t>(root+0xdaa8+city*8,value);write<uint64_t>(value,105);
            write<uint16_t>(value+0x4e,static_cast<uint16_t>(city));write<uint16_t>(value+0x30,1);write<int>(value+0x38,10000);
            write<uint8_t>(value+0xce,1);write<uint16_t>(value+0x12,0xc591);write<uint16_t>(value+0x14,0xd3c9);
            auto building=buildings+city*0x20;write<uint64_t>(root+0x6d808+city*8,building);write<uint64_t>(building,106);
            auto tile=220*80+80+city-1;write<uint16_t>(building+0x10,static_cast<uint16_t>(tile));write<uint8_t>(building+0x12,1);
            write<uint16_t>(tiles+tile*0x20+0x12,static_cast<uint16_t>(city));
            write<uint16_t>(tiles+tile*0x20+0x1a,static_cast<uint16_t>(city));
        }
        for(unsigned county=1;county<=500;county++) {
            auto value=counties+county*0x200;write<uint64_t>(root+0x6c860+county*8,value);write<uint64_t>(value,104);write<uint8_t>(value+0x34,static_cast<uint8_t>(county<=CitySlots?county:1));
        }
        // Only the native morale adapter is synthetic here; the real game's body is separately checked in Oracle.
        BYTE jump[]{0x48,0xb8,0,0,0,0,0,0,0,0,0xff,0xe0};auto adapter=reinterpret_cast<uint64_t>(&moraleAdapter);std::memcpy(jump+2,&adapter,8);
        std::memcpy(reinterpret_cast<void*>(module+0x270550),jump,sizeof(jump));DWORD old{};
        VirtualProtect(reinterpret_cast<void*>(module+0x270000),0x1000,PAGE_EXECUTE_READ,&old);
        unit(1,220*80+80);unit(2,220*90+90);write<uint8_t>(tiles+(220*90+90)*0x20+0x10,13);
        message=std::make_shared<Message>();message->sequence=1;message->seed=1;message->values=DefaultValues;
        climate(200,30000);s=std::make_shared<Session>(input);s->pending.store(message);clock(10,23);
    }
    ~Fixture(){for(auto value:{module,root,world,manager,tiles,units,cities,counties,buildings})VirtualFree(reinterpret_cast<void*>(value),0,MEM_RELEASE);}
    void unit(unsigned slot,unsigned tile) {auto value=units+slot*0x200;write<uint8_t>(value+0x10,1);write<uint16_t>(value+0x12,static_cast<uint16_t>(slot));
        write<uint16_t>(value+0x16,10000);write<uint8_t>(value+0x1a,100);write<uint16_t>(value+0x2a,static_cast<uint16_t>(tile));}
    void climate(int16_t t,uint16_t p){climateT=t;climateP=p;double temperature=double(t)/10,precipitation=double(p)/10;
        for(unsigned i=0;i<ClimateSourceCount;i++){auto row=message->climate+i*ClimateRowBytes;uint32_t tile=i*500;std::memcpy(row,&tile,4);
            for(unsigned month=0;month<12;month++){std::memcpy(row+4+month*16,&temperature,8);std::memcpy(row+12+month*16,&precipitation,8);}}}
    void clock(unsigned day,uint8_t hour){write<uint32_t>(world+0x34,date(day));write<uint8_t>(world+0x38,hour);}
    void seedFor(uint32_t kind,bool heavy=false) {
        for(uint64_t seed=1;;seed++) {
            auto w=weather(seed,serial(date(11)),1,climateT,climateP);
            int chance=kind==10?message->values.floodChance:kind==11?message->values.wildfireChance:kind==12?message->values.droughtChance:kind==13?message->values.blizzardChance:message->values.heatwaveChance;
            if((kind==13?w==8:heavy?w==2:!w)&&random(seed,serial(date(11)),1,kind)%1000<uint32_t(chance)){message->seed=seed;break;}
        }
    }
};
void sessionChecks() {
    Fixture f;f.message->flags=WeatherMorale|RainMovement;
    while(weather(f.message->seed,serial(date(11)),1,200,30000)==0)++f.message->seed;
    f.s->frame();require(f.s->state.lastEvent==0,"initial application has no retrospective damage");
    f.clock(11,0);f.s->frame();require(read<uint8_t>(f.units+0x200,0x1a)==98&&read<uint8_t>(f.units+0x400,0x1a)==100,"rain morale affects land unit and exempts water unit");
    require(f.s->state.lastEvent==1&&f.s->state.events[0].commander==1&&f.s->state.events[0].moraleBefore==100&&f.s->state.events[0].moraleAfter==98,"event receipt matches actual record changes");
    auto slowed=f.s->speed(reinterpret_cast<void*>(f.units+0x200),100);require(slowed<100&&f.s->speed(reinterpret_cast<void*>(f.units+0x400),100)==100,"native movement callback uses regional weather and terrain");
    f.s->frame();require(f.s->state.lastEvent==1,"repeated frames cannot duplicate damage");
    f.clock(21,0);f.s->frame();require(f.s->state.lastEvent==1,"forward date jump establishes baseline");
    f.clock(10,23);f.s->frame();f.message=std::make_shared<Message>(*f.message);f.message->sequence++;f.s->pending.store(f.message);
    f.clock(11,0);f.s->frame();require(f.s->state.lastEvent==1,"configuration change suppresses boundary damage");
    f.s->admission=false;require(f.s->speed(reinterpret_cast<void*>(f.units+0x200),100)==100,"OFF immediately removes movement correction");
    f.clock(20,23);f.s->frame();f.clock(21,0);f.s->frame();require(f.s->state.lastEvent==1,"OFF prevents subsequent damage");
}
void migratingFrameChecks(){
    Fixture f;f.message->flags=WeatherMorale;f.seedFor(10,true);
    f.s->frame();auto first=f.s->thread.load();f.clock(11,0);
    uint32_t second=0;std::thread next([&]{second=GetCurrentThreadId();f.s->frame();});next.join();
    require(second!=first&&f.s->thread==second&&f.s->admission&&!f.s->failure,
        "authenticated update frame can migrate workers without retiring its session");
    auto events=f.s->state.lastEvent;
    require(events==1&&read<uint8_t>(f.units+0x200,0x1a)==98,"migrating boundary frame applies weather loss exactly once");
    std::thread a([&]{for(int i=0;i<50;i++)f.s->frame();});
    std::thread b([&]{for(int i=0;i<50;i++)f.s->frame();});a.join();b.join();
    require(f.s->frames==102&&f.s->state.lastEvent==events&&f.s->admission&&!f.s->failure,
        "concurrent authenticated frame callbacks serialize baseline and cannot duplicate boundary damage");
    f.s->admission=false;auto count=f.s->frames.load();std::thread off([&]{f.s->frame();});off.join();
    require(f.s->frames==count&&f.s->state.lastEvent==events,"OFF remains closed on a different game worker");
}
void disasterChecks(uint32_t flags,int16_t t,uint16_t p,unsigned kind,unsigned terrain,bool heavy) {
    Fixture f;f.message->flags=flags;f.climate(t,p);f.seedFor(kind,heavy);auto tile=220*80+80;
    if(flags==Flood)write<uint8_t>(f.tiles+(tile+1)*0x20+0x10,13);
    else if(flags==Wildfire)write<uint8_t>(f.tiles+tile*0x20+0x10,static_cast<uint8_t>(terrain));
    f.s->frame();f.clock(11,0);f.s->frame();
    require(read<int>(f.cities+0x168+0x38)==(kind>=13?9700:9400),"eligible enabled disaster applies configured city food loss");
    if(flags!=Drought)require(read<uint16_t>(f.units+0x200,0x16)==(kind>=13?9700:9500)&&read<uint8_t>(f.units+0x200,0x1a)==96,"disaster applies troop loss and native morale adapter");
    else require(read<uint16_t>(f.units+0x200,0x16)==10000,"drought has no invented troop casualty");
    require(f.s->state.lastEvent>=1&&!f.s->failure,"disaster emits verified receipt without fault");
    require(f.s->state.recentDates[kind-10]==date(11),"recent map indicator is created by actual disaster damage");
    Fixture off;off.climate(t,p);off.message->seed=f.message->seed;off.message->flags=0;off.s->frame();off.clock(11,0);off.s->frame();
    require(off.s->state.lastEvent==0&&read<int>(off.cities+0x168+0x38)==10000,"unselected disaster cannot cause loss under same weather seed");
}
void eligibilityChecks(){
    Values v=DefaultValues;v.floodChance=v.wildfireChance=v.droughtChance=v.blizzardChance=v.heatwaveChance=1000;
    auto check=[&](uint32_t flag,uint32_t w,int t,int p,bool river,bool trees){return environment::disasters(1,serial(date(11)),1,flag,v,w,t,p,river,trees);};
    require(check(Flood,2,200,800,true,false)==Flood&&check(Flood,2,200,799,true,false)==0&&check(Flood,1,200,800,true,false)==0&&check(Flood,2,200,800,false,false)==0,"flood requires heavy rain, river and 80 mm monthly climate");
    require(check(Wildfire,0,180,599,false,true)==Wildfire&&check(Wildfire,0,179,599,false,true)==0&&check(Wildfire,0,180,600,false,true)==0&&check(Wildfire,1,180,599,false,true)==0&&check(Wildfire,0,180,599,false,false)==0,"wildfire requires dry forest, 18 C and less than 60 mm");
    require(check(Drought,0,250,399,false,false)==Drought&&check(Drought,0,249,399,false,false)==0&&check(Drought,0,250,400,false,false)==0&&check(Drought,1,250,399,false,false)==0,"drought requires clear weather, 25 C and less than 40 mm");
    require(check(Blizzard,8,-50,1000,false,false)==Blizzard&&check(Blizzard,4,-50,1000,false,false)==0&&check(Blizzard,0,-50,1000,false,false)==0,"blizzard requires strong snow, not the season or snow cover alone");
    require(check(Heatwave,0,300,1000,false,false)==Heatwave&&check(Heatwave,0,299,1000,false,false)==0&&check(Heatwave,1,300,1000,false,false)==0&&check(Heatwave,8,300,1000,false,false)==0,"heatwave requires clear weather and at least 30 C without invented rainfall restriction");
    v.blizzardChance=v.heatwaveChance=0;
    require(check(Blizzard,8,-50,1000,false,false)==0&&check(Heatwave,0,300,1000,false,false)==0,"zero probability suppresses eligible blizzard and heatwave");
}
__declspec(noinline) int __fastcall target(void* value){return int(reinterpret_cast<uint64_t>(value))+100;}
void hookChecks() {
    auto fn=static_cast<Speed>(target);BYTE before[64]{};std::memcpy(before,reinterpret_cast<void*>(fn),64);originalSpeed=fn;
    require(transaction(true)==NO_ERROR,"Detours native attach transaction");require(fn(reinterpret_cast<void*>(7))==107,"native hook retains original result without session");
    require(transaction(false)==NO_ERROR,"Detours native detach transaction");require(!std::memcmp(before,reinterpret_cast<void*>(fn),64),"native target bytes restored");
    auto empty=std::make_shared<Session>(Bootstrap{});require(finish(empty).complete==1,"pre-attach rejection owns no callback allocation");
}
void customChecks() {
    Fixture f;f.message->flags=Flood;f.message->values.floodChance=1000;f.message->values.floodTroopsPercent=17;
    f.message->values.floodMoraleLoss=7;f.message->values.floodFoodPercent=23;f.climate(200,30000);f.seedFor(10,true);
    auto tile=220*80+80;write<uint8_t>(f.tiles+(tile+1)*0x20+0x10,13);f.s->frame();f.clock(11,0);f.s->frame();
    require(read<int>(f.cities+0x168+0x38)==7700&&read<uint16_t>(f.units+0x200,0x16)==8300&&read<uint8_t>(f.units+0x200,0x1a)==93,
        "custom flood chance and independent food troops morale reach actual frame records");
    require(std::memcmp(&f.s->state.values,&f.message->values,sizeof(Values))==0,"frame receipt acknowledges every custom numeric parameter");
    WeatherSnapshot snapshot;require(f.s->weatherSnapshot(snapshot)&&snapshot.regions[tile]==1&&snapshot.weather[0]==2,
        "render snapshot shares exact flood region and heavy rain state used by gameplay");
    require(snapshot.recentDates[0]==date(11),"both map consumers receive actual disaster occurrence date");
    f.message->values.floodChance=0;
    for(unsigned day=12;day<=20;day++){f.clock(day-1,23);f.s->frame();f.clock(day,0);f.s->frame();}
    require(f.s->state.recentDates[0]==date(11),"recent damage remains visible for ten actual calendar days without new damage");
    f.clock(20,23);f.s->frame();f.clock(21,0);f.s->frame();require(f.s->state.recentDates[0]==0,"recent damage marker expires on day ten");
    f.s->append({0,date(21),2,1,48400,0,0,0,0,0,0,7700,7000});f.clock(15,0);f.s->frame();
    require(f.s->state.recentDates[0]==0,"rewind clears recent damage from the retired timeline");
    Fixture zero;zero.message->flags=Flood;zero.message->values.floodChance=0;zero.climate(200,30000);zero.message->seed=f.message->seed;
    write<uint8_t>(zero.tiles+(tile+1)*0x20+0x10,13);zero.s->frame();zero.clock(11,0);zero.s->frame();
    require(zero.s->state.lastEvent==0,"zero chance prevents eligible disaster under identical weather seed");
    zero.s->admission=false;require(!zero.s->weatherSnapshot(snapshot),"OFF prohibits stale graphic weather snapshot");
    auto values=DefaultValues;values.rainReduction=7;values.heavyRainReduction=31;values.snowReduction=13;values.deepSnowReduction=49;
    require(movement(1,RainMovement,values)==93&&movement(2,RainMovement,values)==69&&movement(4,SnowMovement,values)==87&&movement(8,SnowMovement,values)==51,
        "heavy rain and deep snow use independent percentages instead of a preset multiplier");
    auto invalid=values;invalid.floodTroopsPercent=100;require(!valid(invalid),"invalid destructive troop percentage rejected");
    invalid=values;invalid.droughtChance=1001;require(!valid(invalid),"invalid probability rejected before frame admission");
}
void vectors(const char* path) {
    std::ofstream out(path);if(!out)throw std::runtime_error("Vector output open failed");out<<"[";bool first=true;
    for(auto seed:{1ull,0x123456789abcdef0ull,0xffffffffffffffffull})for(unsigned dateValue:{69120u,69121u,69122u,69123u})
        for(unsigned city:{1u,20u,46u,47u,51u})for(int t:{-100,20,21,300})for(int p:{0,200,800,3000}) {
            auto w=weather(seed,dateValue,city,t,p);
            for(auto rates:{std::array<int,4>{0,0,0,0},std::array<int,4>{7,31,13,49},std::array<int,4>{99,83,97,91}})for(unsigned flags:{0u,1u,2u,3u,63u}) {
                auto values=DefaultValues;values.rainReduction=rates[0];values.heavyRainReduction=rates[1];values.snowReduction=rates[2];values.deepSnowReduction=rates[3];
                if(!first)out<<",";first=false;
                out<<"{\"seed\":\""<<seed<<"\",\"date\":"<<dateValue<<",\"city\":"<<city<<",\"t\":"<<t<<",\"p\":"<<p<<",\"flags\":"<<flags<<",\"rates\":["<<rates[0]<<","<<rates[1]<<","<<rates[2]<<","<<rates[3]<<"]"<<",\"weather\":"<<w<<",\"movement\":"<<movement(w,flags,values)<<"}";
            }
        }
    out<<"]";require(bool(out),"native policy vectors exported");
}
void notificationChecks(){
    fixtureNotices.clear();fixtureNoticePages.clear();fixtureNoticeReady=false;Fixture f;f.message->flags=Flood;f.seedFor(10,true);write<uint8_t>(f.tiles+(220*80+81)*0x20+0x10,13);f.s->frame();f.clock(11,0);f.s->frame();
    require(f.s->notices.size()==1&&fixtureNotices.empty(),"busy original UI retains one aggregated city disaster report");
    auto loss=f.s->notices.front();require(loss.units==2&&loss.troops>0&&loss.food>0,"report aggregates actual unit and city losses");
    fixtureNoticeReady=true;f.s->frame();require(fixtureNotices.size()==1&&f.s->notices.empty()&&f.s->state.noticesQueued==1,"actual disaster queued once after original UI is ready");
    require(fixtureNotices.front().find(L"홍수")!=std::wstring::npos&&fixtureNotices.front().find(L"양평")!=std::wstring::npos,"report preserves Korean city, disaster and loss text");
    require(fixtureNoticePages.front().size()==3&&std::all_of(fixtureNoticePages.front().begin(),fixtureNoticePages.front().end(),[](const auto& page){return page.find(L'\n')==std::wstring::npos;}),"damage report uses original message pages and leaves wrapping to the game");
    require(fixtureNoticePages.front()[1].find(std::to_wstring(loss.troops))!=std::wstring::npos&&fixtureNoticePages.front()[2].find(std::to_wstring(loss.morale))!=std::wstring::npos&&fixtureNoticePages.front()[2].find(std::to_wstring(loss.food))!=std::wstring::npos,"pagination retains every actual loss value");
    f.s->frame();require(fixtureNotices.size()==1,"repeated frame does not repeat disaster alert");
    f.s->append({0,date(11),3,1,48400,1,1,100,95,100,99,0,0});fixtureNoticeReady=false;f.clock(5,0);f.s->frame();
    require(f.s->notices.empty(),"rewind discards pending disaster reports");fixtureNoticeReady=true;
    f.s->testNotice=L"알림창 검증";f.s->frame();require(f.s->state.reserved==1&&fixtureNotices.size()==2&&f.s->testNotice.empty(),"diagnostic notice uses normal frame queue without actual damage");
}
void noticePageChecks(){
    for(const wchar_t* invalid:{L"",L"\fempty",L"empty\f",L"one\f\ftwo"}){
        std::vector<std::wstring> pages;require(!noticePages(invalid,pages),"empty message pages are rejected without queuing");
    }
    std::vector<std::wstring> pages;require(noticePages(L"원본 한 문단",pages)&&pages==std::vector<std::wstring>{L"원본 한 문단"},"original single-page notice remains unchanged");
    pages.clear();require(noticePages(L"첫 문단\f둘째 문단\f셋째 문단",pages)&&pages==std::vector<std::wstring>{L"첫 문단",L"둘째 문단",L"셋째 문단"},"all original list pages preserve order and text");
    pages.clear();require(!noticePages(std::wstring(201,L'가').c_str(),pages),"original 200-character admission limit remains enforced");
    pages.clear();require(!noticePages(L"1\f2\f3\f4\f5\f6\f7\f8\f9",pages),"message page count remains bounded");
}
void dispatchChecks(){
    Fixture f;auto slot=reinterpret_cast<void**>(f.module+0x1387728);auto original=reinterpret_cast<Update>(f.module+0x14b600);
    originalUpdate=original;f.s->slotHooked=true;current.store(f.s);uint64_t request[]{f.module,0x12345678};*slot=reinterpret_cast<void*>(request[1]);
    require(EnvironmentRestoreDispatch(request)==NO_ERROR&&*slot==reinterpret_cast<void*>(EnvironmentFrame),"active one-shot restores admitted environment dispatch");
    f.s->admission=false;*slot=reinterpret_cast<void*>(request[1]);
    require(EnvironmentRestoreDispatch(request)==NO_ERROR&&*slot==reinterpret_cast<void*>(original),"retired one-shot restores original instead of reviving environment dispatch");
    require(EnvironmentRestoreDispatch(request)==NO_ERROR&&*slot==reinterpret_cast<void*>(original),"concurrent original restoration remains accepted");
    *slot=reinterpret_cast<void*>(0x87654321);
    require(EnvironmentRestoreDispatch(request)==ERROR_INVALID_STATE&&*slot==reinterpret_cast<void*>(0x87654321),"foreign dispatch ownership is rejected without overwriting it");
    current.store(nullptr);originalUpdate=nullptr;
}
void presentationChecks(){
    fixtureNoticeOwnership=true;fixtureNoticePending=fixtureNoticeActive=fixtureNoticeAnimation=false;
    fixturePictureShows=fixturePictureHides=fixtureNoticeSounds=0;fixtureNoticeSoundHandle=55;NoticePresentation p;
    require(p.begin(0,L"산불 알림 검사",1)&&p.busy()&&p.visual()==0,"queued notice owns its state but does not show an image early");
    p.poll(0);require(fixturePictureShows==0&&fixtureNoticeSounds==0,"pending notice does not play sound before activation");
    fixtureNoticePending=false;fixtureNoticeActive=true;p.poll(0);p.poll(0);
    require(p.visual()==2&&fixturePictureShows==1&&fixtureNoticeSounds==1,"original still and disaster sound start exactly once on the owned active dialog");
    require(p.diagnosticReceipt()==(1029u|(110u<<16)|(1u<<24)),"diagnostic retains the wildfire ID and accepted channel independently of audibility");
    fixtureNoticeActive=false;fixtureNoticeAnimation=true;p.poll(0);
    require(p.visual()==2&&fixturePictureHides==1&&p.busy(),"closing animation retains the custom illustration until the original animation ends");
    fixtureNoticeAnimation=false;p.poll(0);require(!p.busy()&&p.visual()==0,"closed dialog releases its picture ownership");
    fixtureNoticeSoundChannel=-1;p.begin(0,L"가뭄 검사",2);fixtureNoticePending=false;fixtureNoticeActive=true;p.poll(0);
    require(p.diagnosticReceipt()==(5u|(96u<<16)|(4u<<24)),"rejected original sound call is not recorded as an accepted channel");p.poll(0,true);fixtureNoticeSoundChannel=3;
    require(!p.busy()&&fixturePictureHides==2&&fixtureNoticeSounds==2,"OFF cleans up the owned picture on the game frame without repeating sound");
    fixtureNoticeOwnership=fixtureNoticeActive=fixtureNoticePending=false;
}
void soundChecks(){
    constexpr uint32_t ids[]{0x65,0x6d,0x5f,0x60,0x77};
    fixtureNoticeAudioAvailable=fixtureNoticeStopAccepted=true;fixtureNoticeSoundChannel=0;
    for(unsigned kind=0;kind<5;kind++){
        fixtureNoticeSoundClock=100;fixtureNoticeSoundHandle=55;fixtureNoticeSounds=fixtureNoticeSoundStops=0;
        NoticeSoundTicket sound;sound.begin(0,kind);
        require(sound.owned&&sound.id==ids[kind]&&sound.channel==0&&fixtureNoticeSoundId==ids[kind],"each disaster selects its authenticated original sound and accepts channel zero");
        fixtureNoticeSoundClock=6099;sound.poll(0,false);require(sound.owned&&fixtureNoticeSoundStops==0,"owned looping sound remains before the six-second limit");
        fixtureNoticeSoundClock=6100;sound.poll(0,false);sound.poll(0,false);
        require(!sound.owned&&sound.status==2&&fixtureNoticeSounds==1&&fixtureNoticeSoundStops==1,"six-second limit stops its owned sound once without replay");
    }
    fixtureNoticeSoundHandle=55;NoticeSoundTicket changed;changed.begin(0,0);auto stops=fixtureNoticeSoundStops;
    fixtureNoticeSoundHandle=56;changed.poll(0,true);
    require(!changed.owned&&changed.status==3&&fixtureNoticeSoundStops==stops,"reused channel with another handle is never stopped");
    fixtureNoticeSoundHandle=55;changed.begin(0,0);fixtureNoticeSoundId=0x13;changed.poll(0,true);
    require(!changed.owned&&changed.status==3&&fixtureNoticeSoundStops==stops,"another sound on the same channel is never stopped");
    fixtureNoticeAudioAvailable=false;NoticeSoundTicket missing;auto calls=fixtureNoticeSounds;missing.begin(0,2);
    require(missing.missing&&!missing.attempted&&!missing.owned&&fixtureNoticeSounds==calls,"missing audio does not manufacture a sound request");fixtureNoticeAudioAvailable=true;
    fixtureNoticeSoundHandle=0;NoticeSoundTicket noHandle;noHandle.begin(0,2);noHandle.poll(0,true);
    require(noHandle.attempted&&noHandle.channel==0&&!noHandle.owned&&noHandle.status==6&&fixtureNoticeSoundStops==stops,"accepted channel without handle is not a verified playback and cannot be stopped");
    fixtureNoticeSoundHandle=55;fixtureNoticeStopAccepted=false;fixtureNoticeOwnership=fixtureNoticeActive=true;fixtureNoticePending=false;
    NoticePresentation p;p.begin(0,L"산불 페이지\f다음 페이지",1,true);fixtureNoticePending=false;p.poll(0);p.poll(0);
    auto count=fixtureNoticeSounds;p.poll(0,true);
    require(p.busy()&&p.sound.owned&&p.sound.status==7&&fixtureNoticeSounds==count,"failed sound stop retains ownership and recovery without restarting playback on message pages");
    fixtureNoticeStopAccepted=true;p.poll(0,true);
    require(!p.busy()&&((p.diagnosticReceipt()>>24)&15)==2,"successful retry releases the sound and retains final stop evidence");
    fixtureNoticeOwnership=fixtureNoticeActive=fixtureNoticePending=false;fixtureNoticeSoundChannel=3;fixtureNoticeSoundHandle=55;fixtureNoticeSoundClock=100;
    Fixture f;std::memcpy(reinterpret_cast<void*>(f.module+NoticeSoundAllocateRva),NoticeSoundAllocateCode,sizeof(NoticeSoundAllocateCode));
    std::memcpy(reinterpret_cast<void*>(f.module+NoticeSoundStopRva),NoticeSoundStopCode,sizeof(NoticeSoundStopCode));
    for(auto source:NoticeSoundSources)write<uint32_t>(f.module+0x13c4950+source.id*4,source.resource);
    require(noticeSoundCode(f.module),"exact original sound allocator stop and five resource mappings pass admission");
    write<uint32_t>(f.module+0x13c4950+0x65*4,0);require(!noticeSoundCode(f.module),"changed original sound resource mapping rejects admission");
    write<uint32_t>(f.module+0x13c4950+0x65*4,0x7eed9d1e);write<uint8_t>(f.module+NoticeSoundStopRva,0);
    require(!noticeSoundCode(f.module),"changed original stop function rejects admission");
    fixtureNoticeOwnership=fixtureNoticeActive=true;fixtureNoticeSoundHandle=55;
    f.s->presentation.begin(f.module,L"메뉴 취소 검사",4,true);fixtureNoticePending=false;f.s->presentation.poll(f.module);
    auto stopRequest=std::make_shared<Message>();stopRequest->sequence=80;stopRequest->reserved=0;f.s->testRequest.store(stopRequest);f.s->frame();
    require(f.s->state.testState==5&&((f.s->state.reserved>>24)&15)==2&&((f.s->state.reserved>>16)&255)==120&&!f.s->presentation.busy(),"developer cancellation retains the actual heatwave sound ID and confirmed stop receipt");
    fixtureNoticeOwnership=fixtureNoticeActive=fixtureNoticePending=false;fixtureNoticeSoundHandle=55;
}
void developerChecks(){
    auto request=[](Fixture& f,unsigned mode,unsigned city=1,unsigned kind=0){auto r=std::make_shared<Message>();r->sequence=50;r->reserved=mode|(city<<8)|(kind<<16);f.s->testRequest.store(r);f.s->frame();};
    fixtureNoticeReady=false;
    for(unsigned city=47;city<=CitySlots;city++)for(unsigned mode:{1u,2u}){
        Fixture f;f.s->frame();request(f,mode,city,2);
        require(f.s->state.testState==6&&!f.s->state.lastEvent,"added cities reject preview and damage requests");
        f.clock(11,0);f.s->frame();
        require(read<int>(f.cities+city*0x168+0x38)==10000&&!f.s->state.recentDates[(city-1)*5+2],"rejected added city has no delayed damage or marker");
    }
    {Fixture f;write<uint8_t>(f.cities+51*0x168+0xce,0);f.s->frame();request(f,2,51,2);require(f.s->state.testState==6&&!f.s->state.lastEvent,"missing loaded geography rejects developer damage");}
    {Fixture f;f.s->frame();request(f,1,52,2);require(f.s->state.testState==6,"developer test rejects unsupported city without array access");}
    {Fixture f;f.s->frame();request(f,2,51,2);request(f,0);f.clock(11,0);f.s->frame();require(f.s->state.testState==5&&read<int>(f.cities+51*0x168+0x38)==10000,"added city cancellation prevents real loss");}
    for(unsigned kind=0;kind<5;kind++){
        Fixture f;auto tile=220*80+80;
        if(kind==0)write<uint8_t>(f.tiles+(tile+1)*0x20+0x10,13);
        if(kind==1)write<uint8_t>(f.tiles+tile*0x20+0x10,5);
        f.s->frame();request(f,2,1,kind);
        require(f.s->state.testState==3&&f.s->state.lastEvent==0,"developer request only schedules, never damages on admission");
        f.clock(11,0);f.s->frame();
        require(f.s->state.testState==4&&f.s->state.lastEvent>0&&f.s->notices.size()==1,"five developer disasters use real loss and all-force report aggregation");
        require(read<int>(f.cities+2*0x168+0x38)==10000,"forced disaster does not spread to an unselected city");
        auto count=f.s->state.lastEvent;f.s->frame();f.clock(11,23);f.s->frame();f.clock(12,0);f.s->frame();
        require(f.s->state.lastEvent==count,"forced disaster is consumed exactly once");
    }
    for(unsigned mode:{1u,2u})for(unsigned discontinuity=0;discontinuity<5;discontinuity++){
        Fixture f;f.s->frame();request(f,mode);
        if(discontinuity==0)f.clock(21,0);
        if(discontinuity==1)f.clock(9,0);
        if(discontinuity==2)write<uint64_t>(f.manager+0x210,0);
        if(discontinuity==3){f.message=std::make_shared<Message>(*f.message);f.message->sequence++;f.s->pending.store(f.message);}
        if(discontinuity==4)f.clock(10,22);
        f.s->frame();require(f.s->state.testState==5&&f.s->state.lastEvent==0,"load, jump, rewind, config and clock rewind cancel both test modes");
    }
    {Fixture f;f.s->frame();request(f,2);request(f,0);f.clock(11,0);f.s->frame();require(f.s->state.testState==5&&f.s->state.lastEvent==0,"explicit cancellation prevents boundary loss");}
    {Fixture f;f.s->frame();request(f,2,1,1);f.clock(11,0);f.s->frame();require(f.s->state.testState==4&&f.s->state.lastEvent==0,"forced wildfire retains forest eligibility");}
    {Fixture f;write<uint8_t>(f.counties+0x200+0x34,46);write<uint16_t>(f.cities+46*0x168+0x30,2);f.s->frame();request(f,2,46,2);f.clock(11,0);f.s->frame();
        require(read<int>(f.cities+46*0x168+0x38)==9400&&read<int>(f.cities+0x168+0x38)==10000&&f.s->notices.size()==1&&f.s->notices.front().city==46,"another corps and remote selected city use the same damage and report path");}
    {Fixture f;f.s->frame();f.clock(11,0);request(f,2,1,2);require(f.s->state.lastEvent==0&&f.s->state.testState==3,"request arriving on a boundary waits for the next boundary");}
    {Fixture f;f.s->frame();request(f,1);f.s->testDeadline=0;f.s->frame();require(f.s->state.testState==5&&f.s->state.testError==WAIT_TIMEOUT&&f.s->state.lastEvent==0,"unattended preview expires without damage");}
    {Fixture f;f.s->frame();request(f,1,1,4);f.s->testStillSince=f.s->testForegroundSince=GetTickCount64()-1000;fixtureNoticeReady=true;f.s->frame();
        WeatherSnapshot preview;require(f.s->state.testState==2&&f.s->state.lastEvent==0&&f.s->state.recentDates[4]==0&&f.s->weatherSnapshot(preview)&&preview.recentDates[4]==date(10),"preview exposes map marker without forging actual damage history");
        require(read<int>(f.cities+0x168+0x38)==10000&&read<uint16_t>(f.units+0x200,0x16)==10000,"preview does not change resources");
        require(fixtureNoticePages.back().size()==4&&fixtureNoticePages.back()[0].find(L"표시 전용")!=std::wstring::npos&&fixtureNoticePages.back()[2].find(L"피해 없음")!=std::wstring::npos&&fixtureNoticePages.back()[3].find(L"표식")!=std::wstring::npos&&std::all_of(fixtureNoticePages.back().begin(),fixtureNoticePages.back().end(),[](const auto& page){return page.find(L'\n')==std::wstring::npos;}),"preview retains test provenance and no-damage text on original message pages");
        request(f,0);require(f.s->weatherSnapshot(preview)&&preview.recentDates[4]==0&&preview.flags==0,"preview cancellation clears rendering metadata");}
    fixtureNotices.clear();fixtureNoticeReady=true;
}
}
void geographyChecks() {
    {
        Fixture f;
        // Observed live castle county/home references; no patch-version branch.
        const uint16_t observed[][3]{{2,339,47},{4,333,46},{24,341,49},{29,342,50},{46,343,51},{49,345,23},{50,352,29}};
        for(auto& row:observed){auto tile=read<uint16_t>(f.buildings+row[0]*0x20,0x10);write<uint16_t>(f.tiles+tile*0x20+0x12,row[1]);write<uint8_t>(f.counties+row[1]*0x200+0x34,static_cast<uint8_t>(row[2]));}
        World world{};require(f.s->world(world)&&world.availableCities==((uint64_t(1)<<51)-1)&&!world.unavailableCities,"live castle base identity does not require its county home to equal the city ID");
        for(auto& row:observed)require(f.s->region(world,world.cityTiles[row[0]])==row[0],"validated castle cell uses its actual base ID despite a different county home");
        write<uint16_t>(f.tiles+500*0x20+0x12,345);write<uint16_t>(f.tiles+500*0x20+0x1a,49);
        require(f.s->region(world,500)==23,"non-castle cell still follows actual county home instead of an unverified base marker");
        f.s->frame();WeatherSnapshot snapshot;
        require(f.s->weatherSnapshot(snapshot)&&snapshot.regions[world.cityTiles[49]]==49&&snapshot.regions[world.cityTiles[50]]==50,"weather snapshot preserves added castle cells with cross-city counties");
    }
    for(uint32_t city:{49u,50u}){
        Fixture f;uint16_t county=city==49?345:352,home=city==49?23:29;auto tile=read<uint16_t>(f.buildings+city*0x20,0x10);
        write<uint16_t>(f.tiles+tile*0x20+0x12,county);write<uint8_t>(f.counties+county*0x200+0x34,static_cast<uint8_t>(home));
        f.s->frame();auto request=std::make_shared<Message>();request->sequence=50;request->reserved=2|(city<<8)|(2<<16);f.s->testRequest.store(request);f.s->frame();f.clock(11,0);f.s->frame();
        require(f.s->state.testState==6&&read<int>(f.cities+city*0x168+0x38)==10000&&read<int>(f.cities+home*0x168+0x38)==10000,"cross-county excluded city request writes neither its record nor county home");
    }
    constexpr uint64_t last=uint64_t(1)<<50;
    {
        Fixture f;f.climate(400,0);f.message->flags=Drought;f.message->values.droughtChance=1000;
        while(weather(f.message->seed,serial(date(11)),51,400,0))++f.message->seed;
        f.s->frame();f.clock(11,0);f.s->frame();
        require(read<int>(f.cities+51*0x168+0x38)==10000&&!f.s->state.recentDates[50*5+2],"city 51 excludes natural drought loss and history");
    }
    {
        Fixture f;f.message->flags=SnowMovement|SnowVisual;f.climate(-50,30000);
        while(!weather(f.message->seed,serial(date(10)),46,-50,30000))++f.message->seed;
        f.s->frame();require(f.s->state.availableCities==((uint64_t(1)<<51)-1)&&!f.s->state.unavailableCities,"all 51 loaded city records remain discoverable");
        WeatherSnapshot snapshot;
        require(f.s->weatherSnapshot(snapshot)&&snapshot.regions[220*80+130]==51&&snapshot.weather[50]==0&&snapshot.weather[45]!=0,"added city weather is zero while original city 46 retains weather");
        write<uint8_t>(f.cities+51*0x168+0xce,0);f.s->frame();
        require((f.s->state.unavailableCities&last)&&!(f.s->state.availableCities&last)&&!f.s->state.weather[50]&&f.s->admission,"missing state excludes only the affected city without inventing weather");
        write<uint8_t>(f.cities+51*0x168+0xce,1);
        write<uint64_t>(f.root+0x6d808+51*8,0);f.s->frame();
        require((f.s->state.unavailableCities&last)&&!f.s->state.weather[50]&&f.s->admission,"missing castle is reported and does not disable valid cities");
        write<uint64_t>(f.root+0x6d808+51*8,f.buildings+51*0x20);
        write<uint64_t>(f.root+0x6d808+200*8,f.buildings+51*0x20);write<uint64_t>(f.root+0x6d808+51*8,0);f.s->frame();
        require((f.s->state.availableCities&last)&&!f.s->state.unavailableCities,"castle lookup follows its loaded base ID instead of the building table index");
        write<uint64_t>(f.root+0x6d808+201*8,f.buildings+51*0x20);f.s->frame();
        require((f.s->state.unavailableCities&last)&&!(f.s->state.availableCities&last),"ambiguous castle ownership is not resolved by inventing a preferred record");
        write<uint64_t>(f.root+0x6d808+201*8,0);
        write<uint8_t>(f.counties+51*0x200+0x34,0);f.s->frame();
        require((f.s->state.unavailableCities&last)&&!f.s->state.weather[50],"missing county ownership is not filled from a default city");
        write<uint8_t>(f.counties+51*0x200+0x34,51);
        write<uint64_t>(f.cities+51*0x168+0x12,0);f.s->frame();
        require((f.s->state.availableCities&last)&&!f.s->state.unavailableCities,"missing optional name does not invent a different city's identity");
        for(uint32_t city=47;city<=51;city++)write<uint8_t>(f.cities+city*0x168+0xce,uint8_t(city-29));
        f.s->frame();require(f.s->state.availableCities==((uint64_t(1)<<46)-1)&&!f.s->state.unavailableCities,"original tribal slots remain excluded from ordinary-city effects");
    }
    {
        Fixture f;f.climate(400,0);World world{};require(f.s->world(world),"loaded geography fixture is readable");
        auto row=f.message->climate;uint32_t tile=220*80+130;double t=10,p=20;
        std::memcpy(row,&tile,4);for(unsigned month=0;month<12;month++){std::memcpy(row+4+month*16,&t,8);std::memcpy(row+12+month*16,&p,8);}
        int temperature{},rain{};f.s->climate(*f.message,world,51,date(10),temperature,rain);
        require(temperature==100&&rain==200,"added city's exact physical climate anchor is preserved");
        write<uint16_t>(f.buildings+51*0x20+0x10,500);write<uint16_t>(f.tiles+500*0x20+0x12,51);write<uint16_t>(f.tiles+500*0x20+0x1a,51);
        require(f.s->world(world),"relocated city metadata is readable in the same storage");
        f.s->climate(*f.message,world,51,date(10),temperature,rain);
        require(temperature==400&&rain==0,"same city ID uses its newly loaded position's climate");
        f.message->flags=Drought;f.message->values.droughtChance=1000;f.seedFor(12);f.s->frame();
        f.s->state.recentDates[50*5+2]=date(10);
        write<uint16_t>(f.buildings+51*0x20+0x10,1000);write<uint16_t>(f.tiles+1000*0x20+0x12,51);write<uint16_t>(f.tiles+1000*0x20+0x1a,51);
        f.clock(11,0);f.s->frame();
        require(read<int>(f.cities+51*0x168+0x38)==10000&&!f.s->state.recentDates[50*5+2],"same-address geography reload clears stale events and cannot trigger retrospective damage");
    }
}
void excludedEffectsChecks(){
    for(uint32_t city=47;city<=51;city++)for(uint32_t kind=0;kind<5;kind++){
        Fixture f;auto tile=uint16_t(220*80+80+city-1);f.unit(1,tile);f.message->flags=All;
        f.s->frame();World world{};require(f.s->world(world),"excluded boundary fixture world is valid");
        f.s->state.weather[city-1]=8;f.s->effects(world,*f.message,serial(world.date),city,kind);
        require(read<int>(f.cities+city*0x168+0x38)==10000&&read<uint16_t>(f.units+0x200,0x16)==10000&&read<uint8_t>(f.units+0x200,0x1a)==100,
            "all five forced disaster kinds and stale weather leave excluded city and unit unchanged");
        require(f.s->speed(reinterpret_cast<void*>(f.units+0x200),100)==100,"excluded city movement stays original");
        for(const auto& e:f.s->state.events)require(e.city!=city,"excluded city creates no damage event");
        f.s->state.recentDates[(city-1)*5+kind]=date(10);WeatherSnapshot out;
        require(f.s->weatherSnapshot(out)&&!out.weather[city-1]&&!out.recentDates[(city-1)*5+kind],"excluded stale weather and disaster history cannot reach renderer");
    }
    auto ownedStore=std::make_unique<notice_trace::Store>();auto& store=*ownedStore;require(store.arm(17,66),"diagnostic arm owns exact generations");store.start(100,2);
    notice_trace::State state;state.phase=notice_trace::Poll;store.record(state);store.record(state);
    notice_trace::Query q;q.season=66;q.environment=17;require(store.read(q)&&q.state.records==1,"identical diagnostic snapshots deduplicate without padding comparison");
    for(unsigned i=0;i<notice_trace::EventCapacity+1;i++){state.phase=notice_trace::PictureBefore;store.record(state);}
    require(store.read(q)&&q.state.records==notice_trace::EventCapacity&&q.state.dropped>0&&store.expired(),"event capacity produces incomplete and cancellation instead of overwriting evidence");
}
void geographyReadChecks(){
    Fixture f;WeatherSnapshot snapshot{};
    require(!f.s->weatherSnapshot(snapshot),"weather has no unvalidated initial geography");
    f.s->frame();auto builds=f.s->topologyBuilds;
    for(int i=0;i<1000;i++)if(!f.s->weatherSnapshot(snapshot))throw std::runtime_error("stable weather read failed");
    require(f.s->topologyBuilds==builds,"one thousand Draw weather reads do not rebuild topology");
    for(uint32_t tile:{100u,TileCount-1})for(uint32_t offset:{0u,1u,2u,3u,4u,5u,6u,7u,0x10u,0x12u,0x13u,0x1au,0x1bu}){
        auto address=f.tiles+tile*0x20+offset;auto before=read<uint8_t>(address);write<uint8_t>(address,before^1);
        if(f.s->weatherSnapshot(snapshot))throw std::runtime_error("same-address tile input change escaped validation");
        write<uint8_t>(address,before);
    }
    require(f.s->weatherSnapshot(snapshot)&&f.s->topologyBuilds==builds,"all consumed tile bytes including final tile reject before update without rebuilding");
    auto unrelated=f.tiles+100*0x20+0x1f;write<uint8_t>(unrelated,1);
    require(f.s->weatherSnapshot(snapshot),"unconsumed tile state does not invalidate geography");
    for(uint32_t offset:{0u,0x34u}){auto address=f.counties+500*0x200+offset;auto before=read<uint8_t>(address);write<uint8_t>(address,before^1);
        require(!f.s->weatherSnapshot(snapshot),"same-address county class or home change rejects stale geography");write<uint8_t>(address,before);}
    auto oldName=read<uint16_t>(f.cities+0x168+0x12);write<uint16_t>(f.cities+0x168+0x12,oldName^1);
    require(!f.s->weatherSnapshot(snapshot),"same-address city identity change rejects cached geography");write<uint16_t>(f.cities+0x168+0x12,oldName);
    auto oldTile=read<uint64_t>(f.root+0xdfe0);write<uint64_t>(f.root+0xdfe0,f.tiles+0x20);
    require(!f.s->weatherSnapshot(snapshot),"changed tile table cannot reuse identical root and state");write<uint64_t>(f.root+0xdfe0,oldTile);
    auto other=std::make_shared<Message>(*f.message);other->sequence++;f.s->pending.store(other);
    require(!f.s->weatherSnapshot(snapshot),"unapplied configuration cannot reuse published weather");f.s->pending.store(f.message);
    f.clock(11,0);require(f.s->weatherSnapshot(snapshot)&&snapshot.date==date(11),"fresh date before next update computes current weather without managed polling");
    for(uint32_t city=1;city<=46;city++){int t{},p{};f.s->climate(*f.message,f.s->previous,city,date(11),t,p);
        if(snapshot.weather[city-1]!=weather(f.message->seed,serial(date(11)),city,t,p))throw std::runtime_error("fresh-date weather mismatch");}
    require(f.s->topologyBuilds==builds,"date advancement does not rebuild unchanged geography on Draw");
    f.s->presentation.layout=1;f.s->presentation.shown=true;f.s->presentation.kind=2;
    require(f.s->weatherSnapshot(snapshot)&&snapshot.reserved==3,"notice visual state is read fresh independently of cached geography");
    f.s->presentation={};require(f.s->weatherSnapshot(snapshot)&&!snapshot.reserved,"notice reset immediately removes visual kind without another update");
    f.s->admission=false;require(!f.s->weatherSnapshot(snapshot),"OFF does not return cached weather");f.s->admission=true;
    f.clock(10,23);f.s->frame();f.s->state.recentDates[2]=date(10);
    write<uint8_t>(f.tiles+100*0x20+0x10,13);require(!f.s->weatherSnapshot(snapshot),"terrain-only reload rejects old weather before update");
    f.clock(11,0);f.s->frame();require(f.s->weatherSnapshot(snapshot)&&!snapshot.recentDates[2]&&!f.s->state.lastEvent,"same-address terrain reload rebuilds baseline without stale history or retrospective damage");
    auto address=f.tiles+(TileCount-1)*0x20;auto page=address&~uint64_t(4095);DWORD old{};
    require(VirtualProtect(reinterpret_cast<void*>(page),4096,PAGE_NOACCESS,&old)!=0,"unreadable last tile fixture protected");
    auto rejected=!f.s->weatherSnapshot(snapshot);DWORD ignored{};VirtualProtect(reinterpret_cast<void*>(page),4096,old,&ignored);
    require(rejected&&f.s->weatherSnapshot(snapshot),"unreadable topology input fails closed and readable input remains usable");
    std::atomic<bool> valid=true;std::thread reader([&]{for(int i=0;i<200;i++){WeatherSnapshot value{};if(!f.s->weatherSnapshot(value))valid=false;}});
    for(int i=0;i<80;i++)f.s->frame();reader.join();
    require(valid&&!f.s->state.lastEvent,"concurrent update and Draw reads serialize complete geography without damage");
}
void noticeArmingChecks(){
    using namespace notice_trace;
    fixtureMilliseconds=1000;
    {
        auto ownedStore=std::make_unique<notice_trace::Store>();auto& store=*ownedStore;require(store.arm(17,67),"arming fixture owns generations");store.start(100,2);
        for(unsigned i=0;i<70000;i++)store.gate(ForegroundPid,(i&1)!=0,i&1);
        fixtureMilliseconds=120000;Query q;q.season=67;q.environment=17;q.command=2;
        require(!store.expired()&&store.read(q)&&q.state.records==0&&!q.state.captureStartedMs&&q.gates[0].evaluated==70000&&q.gates[0].rejected==35000&&!q.gates[1].evaluated,
            "119 second preparation with changing gates consumes no event budget and retains not-evaluated gates");
        fixtureMilliseconds=121000;require(store.expired()&&store.read(q)&&q.state.stopReason==PreparationDeadline,"120 second preparation boundary cancels without capture");
        store.stop(ManualCancel);require(store.read(q)&&q.state.stopReason==PreparationDeadline,"first stop cause survives later cleanup");
    }
    {
        auto ownedStore=std::make_unique<notice_trace::Store>();auto& store=*ownedStore;fixtureMilliseconds=1000;store.arm(17,67);store.start(100,2);
        fixtureMilliseconds=10000;State s;s.phase=QueueBefore;store.record(s);Query q;q.season=67;q.command=2;store.read(q);auto started=q.state.captureStartedQpc;
        fixtureMilliseconds=11000;store.record(s);store.read(q);
        require(q.state.startedMs==1000&&q.state.captureStartedMs==10000&&q.state.captureStartedQpc==started,"QueueBefore starts capture exactly once without replacing request time");
        fixtureMilliseconds=14999;require(!store.expired(),"capture remains admitted through 4999ms");
        fixtureMilliseconds=15000;require(store.expired()&&store.read(q)&&q.state.stopReason==CaptureDeadline,"capture cancels at 5000ms independently of preparation");
    }
    {
        Fixture f;fixtureMilliseconds=1000;f.s->frame();f.s->noticeTrace->arm(17,67);
        auto request=std::make_shared<Message>();request->sequence=100;request->reserved=1|(1<<8)|(2<<16);f.s->testRequest.store(request);f.s->frame();World w;require(f.s->world(w),"arming fixture loaded world");
        auto tick=[&](uint64_t ms){fixtureMilliseconds=ms;f.s->testFrame(w,*f.message,23,true,false);};
        f.s->fixtureForegroundPid=false;tick(1200);Query q;q.season=67;q.command=2;f.s->noticeTrace->read(q);
        require(q.gates[ForegroundPid].rejected==1&&!q.gates[EngineForeground].evaluated,"foreign foreground PID rejects before reading engine gate");
        f.s->fixtureForegroundPid=true;f.s->fixtureEngineForeground=false;tick(1300);f.s->noticeTrace->read(q);
        require(q.gates[EngineForeground].rejected==1&&!q.gates[RequestAge].evaluated,"engine foreground rejects before age admission");
        f.s->fixtureEngineForeground=true;tick(1400);tick(2199);f.s->noticeTrace->read(q);
        require(q.gates[RequestAge].rejected==1&&q.gates[ForegroundAge].rejected==1&&!q.state.captureStartedMs,"request and continuous foreground ages retain independent 800ms guards");
        f.s->fixtureForegroundPid=false;tick(2200);require(f.s->testForegroundSince==0,"foreground loss resets continuous wait");
        f.s->fixtureForegroundPid=true;tick(2300);fixtureNoticeReady=false;tick(3100);f.s->noticeTrace->read(q);
        require(q.gates[ReadyFixture].rejected==1&&!q.state.captureStartedMs,"not-ready notice never starts detailed capture");
        f.s->presentation.state=42;tick(3200);f.s->noticeTrace->read(q);require(q.gates[NotBusy].rejected==1,"busy presentation records actual rejection");f.s->presentation.state=0;
        fixtureNoticeReady=true;fixtureNoticeOwnership=true;tick(3300);f.s->noticeTrace->read(q);
        require(q.state.captureStartedMs==3300&&f.s->state.testState==2&&!f.s->state.lastEvent,"eligible queue starts detail while preview remains no-damage");
        fixtureNoticePending=false;fixtureNoticeActive=true;f.s->presentation.poll(f.module);
        fixtureNoticeActive=false;f.s->presentation.poll(f.module);f.s->noticeTrace->read(q);
        bool picture=false,hide=false,reset=false;for(unsigned first=0;first<q.state.records;first+=q.count){q.first=first;require(f.s->noticeTrace->read(q)&&q.count,"lifecycle event page readable");for(unsigned i=0;i<q.count;i++){auto phase=q.entries[i].state.phase;picture|=phase==PictureAfter;hide|=phase==HideAfter;reset|=phase==Reset;}}
        require(picture&&hide&&reset&&!f.s->state.lastEvent,"queued preview records original picture hide reset without damage");
        fixtureNoticeOwnership=fixtureNoticeActive=fixtureNoticePending=false;
    }
    // Exercise the production readiness evaluator, including its exact lazy read order.
    for(uint32_t base:{uint32_t(ReadyFirst),uint32_t(ReadyQueue)})for(int failure=-1;failure<12;failure++){
        auto ownedStore=std::make_unique<notice_trace::Store>();auto& store=*ownedStore;store.arm(17,67);store.start(100,2);
        const uint64_t manager=0x1b620b0,items=1000,a=2000,b=3000;
        auto r64=[&](uint64_t at)->uint64_t{
            if(at==manager+0x10)return failure==0?0:failure==1?129:2;
            if(at==manager+0x20)return failure==2?0:items;
            if(at==manager+0x30)return failure==3?1:0;
            if(at==manager)return failure==4?0:1;
            if(at==manager+0x28)return failure==5?0:1;
            if(at==items)return a;if(at==items+8)return b;
            if(at==a)return failure==6?0x13acdb8:failure==7?99:0x13d68c0;
            if(at==b)return failure==7||failure==11?99:0x13d68c0;throw std::runtime_error("unexpected readiness read");};
        std::vector<uint64_t> untraced,traced;auto check=[&](Store* trace,std::vector<uint64_t>& reads){return noticeReadyRead(0,[&](uint64_t at){reads.push_back(at);return r64(at);},[&](uint64_t at){reads.push_back(at);return failure==9?1u:0u;},[&]{reads.push_back(444);return failure==8?0ull:4000ull;},[&](uint64_t){reads.push_back(555);return failure==10;},trace,base);};
        auto expected=check(nullptr,untraced);auto actual=check(&store,traced);Query q;q.season=67;q.command=2;store.read(q);
        require(actual==expected&&actual==(failure==-1)&&untraced==traced,"readiness diagnostics preserve exact admission and short-circuit reads");
        if(failure>=0)require(q.gates[base+failure].rejected>0&&(failure==11||!q.gates[base+failure+1].evaluated),"readiness rejection distinguishes first and queue recheck stages without fabricated passes");
    }
    {
        auto ownedStore=std::make_unique<notice_trace::Store>();auto& store=*ownedStore;store.arm(17,67);store.start(100,2);NoticePresentation p;p.trace=&store;fixtureNoticeReady=false;fixtureNoticeOwnership=true;
        require(!p.begin(0,L"queue recheck",2,true),"failed queue readiness recheck rejects preview");Query q;q.season=67;q.command=2;store.read(q);
        require(q.state.captureStartedMs&&q.state.stopReason==QueueFailure&&q.state.phase==QueueAfter&&!p.busy(),"queue failure retains detailed start and first stop cause without claiming a picture");
        fixtureNoticeReady=true;fixtureNoticeOwnership=false;
    }
    fixtureMilliseconds=0;
}

int main(int argc,char** argv) {
    try{noticeArmingChecks();geographyReadChecks();excludedEffectsChecks();geographyChecks();sessionChecks();migratingFrameChecks();eligibilityChecks();disasterChecks(Flood,200,30000,10,0,true);disasterChecks(Wildfire,300,0,11,5,false);disasterChecks(Drought,300,0,12,0,false);disasterChecks(Blizzard,-50,30000,13,0,false);disasterChecks(Heatwave,300,0,14,0,false);customChecks();noticePageChecks();notificationChecks();presentationChecks();soundChecks();developerChecks();dispatchChecks();hookChecks();if(argc==2)vectors(argv[1]);return 0;}
    catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
}
