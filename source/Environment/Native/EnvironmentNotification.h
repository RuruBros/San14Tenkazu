#pragma once
#include "EnvironmentNotificationSignatures.h"
#include "EnvironmentNoticeTraceStore.h"
// The original blocking narration helper waits for the game-state worker.
// Use its original asynchronous state-queue API on the admitted app frame.
// The original constructor owns its list nodes; text stays in resident DLL storage until the dialog closes.
inline wchar_t activeNoticeText[2048]{};
// The original dialog consumes one list node per message page. Keep paragraphs
// separate instead of spending its automatic wrapping budget on manual LF lines.
inline bool noticePages(const wchar_t* text,std::vector<std::wstring>& pages){
    if(!text||!*text||wcslen(text)>200)return false;
    const wchar_t* begin=text;
    for(auto at=text;;++at){
        if(*at!=L'\f'&&*at)continue;
        if(at==begin||pages.size()==8)return false;
        pages.emplace_back(begin,at);
        if(!*at)return true;
        begin=at+1;
    }
}
#ifdef ENVIRONMENT_FIXTURE
inline bool fixtureNoticeReady=true;
inline std::vector<std::wstring> fixtureNotices;
inline std::vector<std::vector<std::wstring>> fixtureNoticePages;
inline bool fixtureNoticeOwnership=false,fixtureNoticeActive=false,fixtureNoticePending=false,fixtureNoticeAnimation=false;
inline uint32_t fixturePictureShows=0,fixturePictureHides=0;
#endif
#include "EnvironmentNoticeSound.h"
inline bool noticeCode(uint64_t module){
    return !std::memcmp(reinterpret_cast<void*>(module+NoticeQueueRva),NoticeQueueCode,sizeof(NoticeQueueCode))
        &&!std::memcmp(reinterpret_cast<void*>(module+NoticeHeadRva),NoticeHeadCode,sizeof(NoticeHeadCode))
        &&!std::memcmp(reinterpret_cast<void*>(module+NoticeNodeRva),NoticeNodeCode,sizeof(NoticeNodeCode))
        &&!std::memcmp(reinterpret_cast<void*>(module+NoticeFreeRva),NoticeFreeCode,sizeof(NoticeFreeCode))
        &&!std::memcmp(reinterpret_cast<void*>(module+NoticePictureRva),NoticePictureCode,sizeof(NoticePictureCode))
        &&!std::memcmp(reinterpret_cast<void*>(module+NoticeHideRva),NoticeHideCode,sizeof(NoticeHideCode))
        &&!std::memcmp(reinterpret_cast<void*>(module+NoticeAnimationRva),NoticeAnimationCode,sizeof(NoticeAnimationCode))
        &&!std::memcmp(reinterpret_cast<void*>(module+NoticeSoundRva),NoticeSoundCode,sizeof(NoticeSoundCode))&&noticeSoundCode(module);
}
inline uint64_t noticeLayout(uint64_t module){
#ifdef ENVIRONMENT_FIXTURE
    return fixtureNoticeOwnership?43:0;
#else
    auto manager=module+0x1b620b0,count=read<uint64_t>(manager,0x10),items=read<uint64_t>(manager,0x20);
    if(count>128||!items)return 0;
    for(uint64_t i=0;i<count;i++){auto state=read<uint64_t>(items+i*8);if(read<uint64_t>(state)!=module+0x13d68c0)continue;
        auto gameLayout=read<uint64_t>(state,0x480);if(read<uint64_t>(gameLayout)!=module+0x13d6050)return 0;
        auto layout=read<uint64_t>(gameLayout,0x1b8);return read<uint64_t>(layout)==module+0x13d5ec0?layout:0;}
    return 0;
#endif
}
inline bool noticeAnimation(uint64_t module,uint64_t layout){
#ifdef ENVIRONMENT_FIXTURE
    return fixtureNoticeAnimation;
#else
    using Animation=int(__fastcall*)(void*,const char*);return reinterpret_cast<Animation>(module+NoticeAnimationRva)(reinterpret_cast<void*>(layout),"stl_event_out")!=0;
#endif
}
inline bool noticeGate(notice_trace::Store* trace,uint32_t gate,bool passed,uint64_t value){return trace?trace->gate(gate,passed,value):passed;}
// The same ordered reads drive both admission and its diagnostic explanation.
template<class Read64,class Read32,class Layout,class Animation>
bool noticeReadyRead(uint64_t module,Read64 r64,Read32 r32,Layout getLayout,Animation animation,notice_trace::Store* trace,uint32_t base){
    auto check=[&](uint32_t g,bool pass,uint64_t value){return noticeGate(trace,base+g,pass,value);};
    uint64_t manager=module+0x1b620b0,count=r64(manager+0x10),items=r64(manager+0x20);
    if(!check(0,count!=0,count)||!check(1,count<=128,count)||!check(2,items!=0,items))return false;
    auto pending=r64(manager+0x30);if(!check(3,!pending,pending))return false;
    auto head=r64(manager);if(!check(4,head!=0,head))return false;
    auto ready=r64(manager+0x28);if(!check(5,ready!=0,ready))return false;
    bool game=false;for(uint64_t i=0;i<count;i++){auto at=r64(items+i*8);auto klass=r64(at);
        if(!check(6,klass!=module+0x13acdb8,klass))return false;
        game|=r64(at)==module+0x13d68c0;}
    auto top=r64(r64(items+(count-1)*8));auto layout=getLayout();
    if(!check(7,game,game)||!check(8,layout!=0,layout))return false;
    auto shown=r32(layout+0x178);if(!check(9,!shown,shown))return false;
    auto anim=animation(layout);if(!check(10,!anim,anim))return false;
    return check(11,top==module+0x13d68c0||top==module+0x13d7328||top==module+0x13d63a0,top);
}
inline bool noticeReady(uint64_t module,notice_trace::Store* trace=nullptr,uint32_t base=notice_trace::ReadyFirst){
#ifdef ENVIRONMENT_FIXTURE
    return noticeGate(trace,notice_trace::ReadyFixture,fixtureNoticeReady,fixtureNoticeReady);
#else
    return noticeReadyRead(module,[](uint64_t a){return read<uint64_t>(a);},[](uint64_t a){return read<uint32_t>(a);},
        [&]{return noticeLayout(module);},[&](uint64_t l){return noticeAnimation(module,l);},trace,base);
#endif
}
inline bool queueNotice(uint64_t module,const wchar_t* text,uint64_t* state=nullptr,notice_trace::Store* trace=nullptr){
    std::vector<std::wstring> pages;
    bool pagesValid=noticePages(text,pages);
    if(!noticeGate(trace,notice_trace::TextPages,pagesValid,pages.size())||!noticeReady(module,trace,notice_trace::ReadyQueue))return false;
#ifdef ENVIRONMENT_FIXTURE
    fixtureNotices.emplace_back(text);fixtureNoticePages.push_back(pages);if(state)*state=fixtureNoticeOwnership?42:0;fixtureNoticePending=fixtureNoticeOwnership;return true;
#else
    wcscpy_s(activeNoticeText,text);
    std::vector<const wchar_t*> pagePointers{activeNoticeText};
    for(auto at=activeNoticeText;*at;++at)if(*at==L'\f'){*at=0;pagePointers.push_back(at+1);}
    struct List {uint64_t* head;uint64_t count;uint32_t mode,padding;} list{};
    using Head=uint64_t*(__fastcall*)(void*,uint64_t*,uint64_t*);
    using Node=uint64_t*(__fastcall*)(void*,uint64_t*,uint64_t*,const wchar_t**);
    using Queue=void(__fastcall*)(void*,const char*,List*,void*);
    using Free=void(__fastcall*)(void*);
    std::vector<uint64_t*> nodes;nodes.reserve(pagePointers.size());
    auto release=reinterpret_cast<Free>(module+NoticeFreeRva);
    struct Owner {List& list;std::vector<uint64_t*>& nodes;Free release;~Owner(){for(auto node:nodes)release(node);if(list.head)release(list.head);}} owner{list,nodes,release};
    list.head=reinterpret_cast<Head>(module+NoticeHeadRva)(&list,nullptr,nullptr);if(!noticeGate(trace,notice_trace::QueueHead,list.head!=nullptr,reinterpret_cast<uint64_t>(list.head)))return false;
    for(const wchar_t* pointer:pagePointers){
        auto node=reinterpret_cast<Node>(module+NoticeNodeRva)(&list,list.head,reinterpret_cast<uint64_t*>(list.head[1]),&pointer);
        if(!noticeGate(trace,notice_trace::QueueNode,node!=nullptr,reinterpret_cast<uint64_t>(node)))return false;
        list.head[1]=reinterpret_cast<uint64_t>(node);*reinterpret_cast<uint64_t*>(node[1])=reinterpret_cast<uint64_t>(node);++list.count;nodes.push_back(node);
    }
    alignas(16) uint8_t emptyCallback[64]{};auto manager=module+0x1b620b0,previous=read<uint64_t>(manager,0x30);
    reinterpret_cast<Queue>(module+NoticeQueueRva)(reinterpret_cast<void*>(manager),"CDialogGameStateBase<CCmnDlgMsgWithRInputDialog>",&list,emptyCallback);
    auto count=read<uint64_t>(manager,0x30),items=read<uint64_t>(manager,0x40);
    auto queued=read<uint64_t>(items+previous*16+8);bool valid=count==previous+1&&read<uint64_t>(queued)==module+0x13acdb8;
    noticeGate(trace,notice_trace::QueueReadback,valid,queued);if(valid&&state)*state=queued;return valid;
#endif
}
struct NoticePresentation {
    notice_trace::Store* trace=nullptr;
    uint64_t state=0,layout=0;uint32_t kind=0,lastReceipt=0;bool shown=false,closing=false,diagnostic=false;
    NoticeSoundTicket sound;
    void traceEvent(uint64_t module,uint32_t phase,uint32_t reason=0,bool active=false,bool pending=false){
        if(!trace||!trace->enabled)return;notice_trace::State value;
        value.phase=phase;value.reason=reason;value.state=state;value.layout=layout;value.visual=visual();
        value.shown=shown;value.closing=closing;value.active=active;value.pending=pending;value.soundOwned=sound.owned;
#ifndef ENVIRONMENT_FIXTURE
        if(layout&&noticeLayout(module)==layout){value.layoutValid=1;value.layoutShown=read<uint32_t>(layout,0x178);
            value.category=read<uint32_t>(layout,0x184);value.picture=read<uint32_t>(layout,0x188);
            if(noticeLayout(module)!=layout)value.layoutValid=0;}
#endif
        trace->record(value);
    }
    bool busy()const{return state||layout||sound.owned;}
    uint32_t visual()const{return layout&&shown?kind+1:0;}
    // Diagnostic Reply.reserved: low two bits retain queue status; bit 2 is a
    // sound call, bit 3 missing audio; 8..15 channel+1, 16..23 sound ID+1,
    // 24..27 request/ownership status. Preserve the final receipt after drain.
    uint32_t diagnosticReceipt()const{return lastReceipt?lastReceipt:1u|sound.receipt();}
    bool begin(uint64_t module,const wchar_t* text,uint32_t value,bool test=false){
        auto sink=test?trace:nullptr;auto occupied=busy();
        if(!noticeGate(sink,notice_trace::BeginNotBusy,!occupied,occupied)||!noticeGate(sink,notice_trace::KindValid,value<DisasterCount,value))return false;auto target=noticeLayout(module);
#ifndef ENVIRONMENT_FIXTURE
        if(!noticeGate(sink,notice_trace::LayoutPresent,target!=0,target))return false;
#endif
        traceEvent(module,notice_trace::QueueBefore);
        uint64_t queued=0;if(!queueNotice(module,text,&queued,sink)){if(sink)sink->stop(notice_trace::QueueFailure);traceEvent(module,notice_trace::QueueAfter,ERROR_INVALID_STATE);return false;}
        state=queued;layout=queued?target:0;kind=value;diagnostic=test;lastReceipt=0;sound={};traceEvent(module,notice_trace::QueueAfter);return true;
    }
    bool listed(uint64_t module,bool pending)const{
#ifdef ENVIRONMENT_FIXTURE
        return pending?fixtureNoticePending:fixtureNoticeActive;
#else
        auto manager=module+0x1b620b0,count=read<uint64_t>(manager,pending?0x30:0x10),items=read<uint64_t>(manager,pending?0x40:0x20);
        if(count>128||!items)return false;for(uint64_t i=0;i<count;i++)if(read<uint64_t>(items+i*(pending?16:8)+(pending?8:0))==state)return true;return false;
#endif
    }
    void poll(uint64_t module,bool stop=false){
        if(!busy())return;
        auto target=noticeLayout(module);
        bool active=listed(module,false),pending=listed(module,true);
        traceEvent(module,notice_trace::Poll,stop?1:0,active,pending);
        sound.poll(module,stop||target!=layout||!active&&!pending);
        if(diagnostic&&shown)lastReceipt=1u|sound.receipt();
        auto reset=[&]{traceEvent(module,notice_trace::Reset,target!=layout?1:stop?2:3,active,pending);auto receipt=lastReceipt;auto sink=trace;*this={};lastReceipt=receipt;trace=sink;};
        if(target!=layout){if(!sound.owned)reset();return;}
        if(active&&!shown&&!closing&&!stop){
            traceEvent(module,notice_trace::PictureBefore,0,active,pending);
#ifdef ENVIRONMENT_FIXTURE
            ++fixturePictureShows;
#else
            // Original disaster caller 0x298682/0x298685 clears XMM3/XMM2.
            // On Windows x64 these are floating-point arguments, not R8D/R9D integers.
            using Picture=void(__fastcall*)(void*,int,int);
            // Preserve the original still's animation, geometry and UI placement.
            // The renderer replaces this sprite's texture only for an owned wildfire/drought report.
            reinterpret_cast<Picture>(module+NoticePictureRva)(reinterpret_cast<void*>(layout),17,0);
#endif
            sound.begin(module,kind);shown=true;if(diagnostic)lastReceipt=1u|sound.receipt();
            traceEvent(module,notice_trace::PictureAfter,0,active,pending);
        }
        if(shown&&!closing&&(stop||!active&&!pending)){
            traceEvent(module,notice_trace::HideBefore,stop?1:2,active,pending);
#ifdef ENVIRONMENT_FIXTURE
            ++fixturePictureHides;
#else
            if(read<uint32_t>(layout,0x184)==13&&read<uint32_t>(layout,0x188)==17){using Hide=void(__fastcall*)(void*);reinterpret_cast<Hide>(module+NoticeHideRva)(reinterpret_cast<void*>(layout));}
#endif
            closing=true;
            traceEvent(module,notice_trace::HideAfter,stop?1:2,active,pending);
        }
        if(!sound.owned&&(closing&&!noticeAnimation(module,layout)||!shown&&(stop||!active&&!pending)))reset();
    }
};
