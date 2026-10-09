# Path predicates: actual native routing, copied-page return observers, no game writes or result stubs.
# python ai_path_predicates.py --vectors ../Tests/AiPathPredicateVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
import bases
import units
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace
from unicorn.x86_const import UC_X86_REG_RSP, UC_X86_REG_XMM0


class PathTrace(Trace):
    def enter(self,uc,address,size,user):
        super().enter(uc,address,size,user)
        e=self.events[-1]
        e['stackArgs']=[self.oracle.q(e['rsp']+0x28+i*8) for i in range(4)]

    def leave(self,uc,address,size,user):
        pending=self.pending.get(address,[])
        e=pending[-1] if pending and uc.reg_read(UC_X86_REG_RSP)==pending[-1]['rsp']+8 else None
        super().leave(uc,address,size,user)
        if e and e['rva'] in [0x1843E0,0x185620]:
            pointer,count,cost=struct.unpack('<Qii',self.oracle.read(e['args'][0],16))
            if not 0<=count<=48400:raise AssertionError('invalid native path count')
            e.update(count=count,cost=cost,path=list(struct.unpack('<'+'I'*count,self.oracle.read(pointer,count*4))) if count else [])
        elif e and e['rva']==0x2B0290:
            e['floatResult']=struct.unpack('<f',struct.pack('<I',uc.reg_read(UC_X86_REG_XMM0)&0xFFFFFFFF))[0]


def verify(game):
    from oracle import Oracle
    raw=game.base-0x290;outer=game.q(game.exe+ai.AI_MANAGER);manager=game.q(outer+8)
    context=game.q(manager+0x38);board=game.q(context+0x18)
    oracle=Oracle(game=game)
    force=next(p for i in range(1,52) if (p:=game.q(raw+0xDF30+8*i)) and oracle.call32(0x32F500,p)==1)
    district=oracle.call(0x22E7E0,force)
    main_city=oracle.call(0x22D3A0,district)
    cities=[main_city]+[p for p in bases.bases(game.read,game.q,game.base).values()
                        if p!=main_city and oracle.call32(0x32F500,p)==1][:9]
    army=next(p for i in range(units.UNIT_COUNT) if (p:=game.q(raw+units.UNITS+8*i)) and oracle.call32(0x32F500,p)==1)
    keys=[0x61A170A1+i for i in range(4)];answers=[]
    functions={0x1843E0:None,0x185620:None,0x2B0290:None,0xBF300:2}
    def valid(p):return bool(p) and oracle.call32(0x32F500,p)!=0
    def virtual(p,slot):return oracle.call32(oracle.q(oracle.q(p)+slot)-game.exe,p)
    force_id=virtual(force,0x60)
    for predicate in [43,55,56,127,130,132,134]:
        for a,b in [(cities[0],p) for p in cities[:8]]:
            oracle=Oracle(game=game);args=oracle.alloc(20);trace=PathTrace(oracle,functions)
            start=oracle.call32(0x22D740,a);end=oracle.call32(0x22D740,b)
            start_hex=oracle.q(raw+0xE270+start*8);end_hex=oracle.q(raw+0xE270+end*8)
            if predicate==127 and a==b:end_hex=oracle.call(0x6FD70,army)
            start_area=oracle.call(0x229EA0,start_hex);end_area=oracle.call(0x229EA0,end_hex)
            first_area=struct.unpack('<H',oracle.read(start_hex+0x12,2))[0]
            second_area=struct.unpack('<H',oracle.read(end_hex+0x12,2))[0]
            cache_address=outer+0xC0+(first_area*500+second_area)*4
            cache_eligible=first_area<501 and second_area<501 and \
                struct.unpack('<H',oracle.read(start_area+0x3A,2))[0]==start and \
                struct.unpack('<H',oracle.read(end_area+0x3A,2))[0]==end
            cache_original=oracle.read(cache_address,4) if cache_eligible else None
            tests=[(False,20000,0),(True,20000,0)]
            if predicate in [43,55] and cache_eligible:
                tests += [(False,limit,cached) for cached in [-1,5,20000] for limit in [-1,0,4,5,6,20000]]
            for missing,limit,cached in tests:
                if cache_eligible and predicate in [43,55]:oracle.poke(cache_address,struct.pack('<i',cached))
                chosen=[army,end_hex] if predicate==127 else [a,b,force]
                if missing:chosen[0]=0
                for key,obj in zip(keys,chosen):oracle.call32(0xB26BA0,board,key,obj,keep=True)
                oracle.call32(0xB26990,board,ai.name_hash('AgentType'),3,keep=True)
                if predicate in [43,55,56,130,132,134]:
                    parameters=[5,keys[0],5,keys[1]]
                    if predicate==43:parameters.append(limit&0xFFFFFFFF)
                    elif predicate==55:
                        oracle.call32(0xB26990,board,keys[3],limit,keep=True);parameters.append(keys[3])
                    elif predicate!=56:parameters.append(keys[2])
                else:parameters=keys[:2]
                oracle.poke(args,struct.pack('<'+'I'*len(parameters),*parameters));trace.clear()
                actual=oracle.call32(0xC2610,predicate,0,context,district,stack_args=(args,len(parameters)))&255
                events=list(trace.events);paths=[e for e in events if e['rva'] in [0x1843E0,0x185620]]
                path=paths[-1] if paths else None
                success=bool(path and path['result']&255);cost=path['cost'] if path else cached
                count=path['count'] if path else 0;mobility=0;hostile=False;facts=[]
                if predicate in [130,132,134] and path:
                    for index in path['path']:
                        hex_=oracle.q(raw+0xE270+8*(index if index<48400 else 0));area=oracle.call(0x229EA0,hex_)
                        city_or_gate=oracle.call32(0x232680,area)!=0 or oracle.call32(0x232BF0,area)!=0
                        owner=oracle.call32(0x22CC00,area);eligible=True
                        if predicate==130:eligible=city_or_gate and area not in [start_area,end_area]
                        if predicate==132:
                            building=oracle.call(0x22FB10,hex_)
                            eligible=city_or_gate and valid(building) and valid(oracle.call(0x91B00,building))
                            owner=oracle.read(building+0x13,1)[0] if eligible else 0
                        if predicate==134:eligible=owner!=force_id
                        enemy=eligible and oracle.call32(0x2CA280,force_id,owner)!=0
                        hostile |= enemy;facts.append({'hex':index,'area':area,'owner':owner,'eligible':bool(eligible),'hostile':bool(enemy)})
                if predicate==127:
                    mobility=next((e['floatResult'] for e in events if e['rva']==0x2B0290),0)
                    expected=not missing and success and cost<int(mobility)*120
                elif predicate in [43,55]:expected=not missing and ((cache_eligible and cached!=0) or success) and cost<=limit
                elif predicate==56:expected=not missing and success
                elif predicate==130:expected=not missing and hostile
                else:expected=not missing and success and count>0 and hostile
                if actual!=int(expected):raise AssertionError((predicate,missing,limit,cached,actual,expected,events,facts))
                answers.append({'predicate':predicate,'valid':not missing,'limit':limit,'cached':cached,'cacheEligible':cache_eligible,
                                'pathSuccess':success,'count':count,'cost':cost,'mobility':mobility,'hostile':hostile,
                                'facts':facts,'events':events,'native':actual})
            if cache_original is not None:oracle.poke(cache_address,cache_original)
            trace.close()
        print(f'P{predicate}: matched',flush=True)
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'native routing and dispatcher; copied memory only; return observers, no result stubs',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=verify(ListPoolSnapshot());text=vectors_text(result);folder=HERE/'.cache'/'ai_path_predicates';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} native path cases matched; {path}")


if __name__=='__main__':main()
