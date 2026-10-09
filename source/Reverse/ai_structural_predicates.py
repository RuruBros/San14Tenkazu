# Remaining relational wrappers, including unused predicates, observed native helpers with no result stubs.
# python ai_structural_predicates.py --vectors ../Tests/AiStructuralPredicateVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
import bases
from ai_friend import HERE, vectors_text
from ai_native_trace import Trace
from game import Game
from ai_native_snapshot import ListPoolSnapshot

PREDICATES = [6,15,41,42,60,87,89,109,112,122,126,133,139,157,161,162,163,164]
FUNCTIONS = {0x22F5C0:1,0x22F2D0:1,0x22F060:1,0x6D9060:None,0x2AD610:None,0x324E20:None,
             0x70C530:None,0x6DF9B0:1,0x6DCAE0:2,0x6E0590:1,0x2CA280:None,
             0x280490:None,0x280910:None,0x3FB5F0:None,0x27F6E0:None,0x2C9310:None,
             0x32CB30:None,0x2A9AA0:1,0x92440:None,0x6D7320:1,
             0x1EAE20:None,0x1EACE0:None,0x1EAAF0:None,0x1EAB60:None}


def verify(game):
    from oracle import Oracle
    raw=game.base-0x290
    manager=game.q(game.q(game.exe+ai.AI_MANAGER)+8)
    context=game.q(manager+0x38);board=game.q(context+0x18)
    oracle=Oracle(game=game)
    forces=[p for i in range(1,52) if (p:=game.q(raw+0xDF30+8*i)) and oracle.call32(0x32F500,p)==1]
    force,other_force=forces[:2]
    district=oracle.call(0x22E7E0,force);other_district=oracle.call(0x22E7E0,other_force)
    city=oracle.call(0x22D3A0,district);other_city=oracle.call(0x22D3A0,other_district)
    person=game.officer(736)
    other_person=next(p for i in range(1,6000) if (p:=game.officer(i)) and p!=person and oracle.call32(0x32F500,p)==1)
    areas=[p for i in range(1,501) if (p:=game.q(raw+0x6CAF0+8*i)) and oracle.call32(0x32F500,p)==1]
    area=areas[0]
    troops=next(p for i in range(600) if (p:=game.q(raw+0x7F8D8+8*i)) and oracle.call32(0x32F500,p)==1)
    state=game.q(raw+0x871E8)
    keys=[0x61A17061+i for i in range(3)]
    args=oracle.alloc(12);answers=[]
    trace=Trace(oracle,FUNCTIONS)
    def valid(obj):return bool(obj) and oracle.call32(0x32F500,obj)!=0
    def virtual(obj,slot):return oracle.call32(oracle.q(oracle.q(obj)+slot)-game.exe,obj)
    def u16(obj,off):return struct.unpack('<H',oracle.read(obj+off,2))[0]
    def events_of(rva):return [e for e in events if e['rva']==rva]
    def any_result(rva):return any(e['result']!=0 for e in events_of(rva))
    def lists(rva):return [p for e in events_of(rva) for p in e['list']]
    objects_by_id={6:[other_city],15:[other_force],41:[district],42:[other_city],60:[force,person,other_force],
                   87:[force,other_force,other_city],89:[force,other_city],109:[city],112:[area,force],
                   122:[force,person,area],126:[troops],133:[city],139:[force,city],157:[force,person],
                   161:[force,person,city],162:[force,person,other_person],163:[force,person,area],164:[force,person,area]}
    def run(predicate,chosen):
        for key,obj in zip(keys,chosen):oracle.call32(0xB26BA0,board,key,obj,keep=True)
        oracle.call32(0xB26990,board,ai.name_hash('AgentType'),3,keep=True)
        oracle.poke(args,struct.pack('<'+'I'*len(chosen),*keys[:len(chosen)]))
        trace.clear()
        return oracle.call32(0xC2610,predicate,0,context,district,stack_args=(args,len(chosen)))&255
    for predicate in PREDICATES:
        trace.close();oracle=Oracle(game=game);args=oracle.alloc(12);trace=Trace(oracle,FUNCTIONS)
        samples=[objects_by_id[predicate]]
        if predicate in [6,42,109,133]:samples += [[p] for p in list(bases.bases(game.read,game.q,game.base).values()) if valid(p)][:10]
        if predicate==15:samples += [[p] for p in forces]
        if predicate==112:samples += [[p,force] for p in areas[1:16]]
        if predicate==164:samples += [[force,person,p] for p in areas[1:16]]
        if predicate==139:samples += [[force,p] for p in list(bases.bases(game.read,game.q,game.base).values()) if valid(p)][:10]
        # Discover actual candidates by the native predicate first; never fabricate candidate lists.
        if predicate==157:
            run(predicate,[force,person])
            members=list(dict.fromkeys(p for e in trace.events if e['rva']==0x6D7320 for p in e['list']))
            samples += [[force,p] for p in members]
        variants=[(objects,None) for objects in samples]
        if predicate==157:
            for member in members:
                rank=oracle.call32(0x3FD20,member)+1
                scenery=oracle.q(raw+0x85DB8+8*(rank if rank<7 else 0))
                threshold=struct.unpack('<h',oracle.read(scenery+0x2C,2))[0]
                increase=struct.unpack('<i',oracle.read(game.exe+0x1A48878,4))[0]
                variants += [([force,member],v) for v in [threshold-increase-1,threshold-increase,
                                                          threshold-1,threshold,threshold+1]]
        for objects,level_override in variants:
            if predicate==157:
                oracle.poke(objects[1]+0x1BE,game.read(objects[1]+0x1BE,2) if level_override is None
                            else struct.pack('<h',level_override))
            for missing in [-1,*range(len(objects))]:
                chosen=objects.copy()
                if missing>=0:chosen[missing]=0
                actual=run(predicate,chosen)
                events=list(trace.events)
                inputs=all(valid(p) for p in chosen)
                a=b=c=False;own=hostile=required=old_level=next_level=increment=0
                if inputs:
                    if predicate==6:a=any(u16(p,0x4E)==u16(chosen[0],0x4E) for p in lists(0x22F5C0))
                    elif predicate==15:a=chosen[0]!=force and any(virtual(p,0xB0)==virtual(force,0x60) for p in lists(0x22F2D0))
                    elif predicate==41:a=any(e['result']==0 for e in events_of(0x6D9060))
                    elif predicate==42:a=any(virtual(p,0x78)==oracle.call32(0x22DBC0,district) for p in lists(0x22F5C0))
                    elif predicate==60:a=any(e['result']==5 for e in events_of(0x2AD610));b=any_result(0x324E20)
                    elif predicate==87:a=any_result(0x70C530);b=chosen[1] in lists(0x6DF9B0);c=chosen[2] in lists(0x6DCAE0)
                    elif predicate==89:a=oracle.call(0x22A7B0,chosen[1]) in lists(0x6E0590)
                    elif predicate==109:
                        a=any((oracle.call32(0x232680,p) or oracle.call32(0x232BF0,p)) and
                              virtual(oracle.call(0x32C130,raw,oracle.call32(0x22C7C0,p)),0xB0)==virtual(force,0x60)
                              for p in lists(0x22F060))
                    elif predicate==112:
                        a=oracle.call(0x22C810,chosen[0])!=chosen[1] or u16(chosen[0],0x68)!=u16(chosen[0],0x76)
                        b=any_result(0x2CA280)
                    elif predicate==122:
                        a=any(valid(e['result64']) for e in events_of(0x280490))
                        b=any_result(0x280910) and any_result(0x3FB5F0);c=any_result(0x27F6E0)
                    elif predicate==126:a=any_result(0x2C9310)
                    elif predicate==133:
                        returned=oracle.call(0x32CB30,state)
                        a=u16(chosen[0],0x4E)<52
                        b=valid(returned) and oracle.call32(0x22DB20,oracle.call(0x22A7B0,chosen[0]))==oracle.call32(0x22DB20,returned)
                    elif predicate==139:
                        a=bool(events_of(0x92440));required=events_of(0x92440)[0]['result'] if a else 0
                        for p in lists(0x2A9AA0):
                            owner=virtual(p,0xB0);own+=int(owner==virtual(force,0x60));hostile+=int(oracle.call32(0x2CA280,virtual(force,0x60),owner)!=0)
                    elif predicate==157:
                        a=chosen[1] in lists(0x6D7320)
                        if a:
                            index=oracle.call32(0x3FD20,chosen[1])+1
                            scenery=oracle.q(raw+0x85DB8+8*(index if index<7 else 0))
                            old_level=struct.unpack('<h',oracle.read(chosen[1]+0x1BE,2))[0]
                            next_level=struct.unpack('<h',oracle.read(scenery+0x2C,2))[0]
                            increment=struct.unpack('<i',oracle.read(game.exe+0x1A48878,4))[0]
                    elif predicate==161:a=valid(oracle.call(0x22A7B0,chosen[2]));b=any_result(0x1EAE20)
                    elif predicate==162:a=any_result(0x1EACE0)
                    elif predicate==163:a=any_result(0x1EAAF0)
                    elif predicate==164:a=any_result(0x1EAB60)
                expected=inputs and ({60:a or b,87:a and b and c,112:a and b,122:a or b or c,
                    133:a and b,139:a and own<required<=own+hostile,
                    157:a and old_level<next_level<=old_level+increment,161:a and b,163:False}.get(predicate,a))
                if actual!=int(expected):raise AssertionError((predicate,chosen,actual,expected,events))
                answers.append({'predicate':predicate,'inputs':inputs,'a':a,'b':b,'c':c,'own':own,'hostile':hostile,
                                'required':required,'oldLevel':old_level,'nextLevel':next_level,'increment':increment,
                                'objects':chosen,'native':actual,'events':events})
            if predicate==157:oracle.poke(objects[1]+0x1BE,game.read(objects[1]+0x1BE,2))
        print(f'P{predicate}: recorded',flush=True)
    trace.close()
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'native relational wrappers and helpers; copied-page observers, no game writes or stubs',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=verify(ListPoolSnapshot());text=vectors_text(result);folder=HERE/'.cache'/'ai_structural_predicates';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(PREDICATES)} predicates, {len(result['cases'])} native calls matched; {path}")


if __name__=='__main__':main()
