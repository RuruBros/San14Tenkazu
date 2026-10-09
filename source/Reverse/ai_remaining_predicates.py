# Remaining numeric predicates and unused scheme outcome classifiers, copied native code only.
# python ai_remaining_predicates.py --vectors ../Tests/AiRemainingPredicateVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
import bases
import units
from ai_friend import HERE, vectors_text
from ai_native_trace import Trace
from game import Game

SCHEMES = {82: (0x70BBF0, 0x1EB880, 4), 91: (0x70CC00, 0x1EBEE0, 3),
           95: (0x70C8D0, 0x1EBE30, 3), 99: (0, 0x1EBBA0, 3),
           103: (0x70C1E0, 0x1EBCA0, 3), 145: (0x70BDD0, 0x1EB980, 3)}


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    raw = game.base - 0x290
    manager = game.q(game.q(game.exe+ai.AI_MANAGER)+8)
    context = game.q(manager+0x38)
    board = game.q(context+0x18)
    forces = [p for i in range(1,52) if (p:=game.q(raw+0xDF30+8*i)) and oracle.call32(0x32F500,p)==1]
    force, other_force = forces[:2]
    district = oracle.call(0x22E7E0,force)
    city = oracle.call(0x22D3A0,district)
    army = next(p for i in range(units.UNIT_COUNT) if (p:=game.q(raw+units.UNITS+8*i))
                and oracle.call32(0x32F500,p)==1
                and (commander_id:=struct.unpack('<H',game.read(p+0x12,2))[0]) < 6001
                and oracle.call32(0x32F500,game.q(raw+0x158+commander_id*8))==1)
    person = game.officer(736)
    other_person = next(p for i in range(1,6000) if (p:=game.officer(i)) and p != person
                        and oracle.call32(0x32F500,p)==1)
    area = next(p for i in range(1,501) if (p:=game.q(raw+0x6CAF0+8*i)) and oracle.call32(0x32F500,p)==1)
    hex_ = oracle.call(0x6FD70,army)
    state = game.q(raw+0x871E8)
    keys = [0x61A17041+i for i in range(4)]
    args = oracle.alloc(20)
    functions = {gate: None for gate,_,_ in SCHEMES.values() if gate}
    functions.update({helper: None for _,helper,_ in SCHEMES.values()})
    functions.update({0x2AC170: None,0x1DEF80: None})
    trace = Trace(oracle,functions)
    answers=[]

    def call(predicate, objects, agent=0):
        for key,obj in zip(keys,objects):
            if oracle.call32(0xB26BA0,board,key,obj,keep=True)!=1:
                raise AssertionError('blackboard setup')
        oracle.poke(args,struct.pack('<'+'I'*len(objects),*keys[:len(objects)]))
        oracle.call32(0xB26990,board,ai.name_hash('AgentType'),3,keep=True)
        trace.clear()
        return oracle.call32(0xC2610,predicate,0,context,agent,stack_args=(args,len(objects)))&255

    def add(predicate, expected, actual, **facts):
        if int(expected)!=actual:
            raise AssertionError((predicate,facts,expected,actual))
        answers.append({'predicate':predicate,**facts,'native':actual})

    for predicate,objects,offset in [(30,[army,city],None),(35,[person,city],0xE4),(45,[district,person],0x162)]:
        matching=oracle.call32(0x22E970,oracle.call(0x40190,district)) if predicate==45 else \
            struct.unpack('<H',oracle.read(city+0x4E,2))[0]
        for valid in [True,False]:
            for value in sorted({0,1,46,51,52,61,32767,32768,65535,matching}):
                if predicate==30:
                    commander=struct.unpack('<H',oracle.read(army+0x12,2))[0]
                    if commander>=6001:raise AssertionError('army has invalid commander')
                    officer=game.q(raw+0x158+commander*8)
                    oracle.poke(officer+0xE2,struct.pack('<H',value))
                    home=value if oracle.call32(0x32F500,officer)==1 else 0
                    other=struct.unpack('<H',oracle.read(city+0x4E,2))[0]
                elif predicate==35:
                    oracle.poke(person+offset,struct.pack('<H',value));home=value
                    other=struct.unpack('<H',oracle.read(city+0x4E,2))[0]
                else:
                    oracle.poke(person+offset,struct.pack('<H',value));home=value
                    other=oracle.call32(0x22E970,oracle.call(0x40190,district))
                add(predicate,valid and home==other,call(predicate,objects if valid else [0,objects[1]]),
                    value=home,other=other,valid=valid)
        if offset:oracle.poke(person+offset,game.read(person+offset,2))
    oracle.poke(army,game.read(army,units.UNIT_SIZE))
    commander=struct.unpack('<H',game.read(army+0x12,2))[0]
    commander_pointer=game.q(raw+0x158+commander*8)
    oracle.poke(commander_pointer+0xE2,game.read(commander_pointer+0xE2,2))

    def date_value(v):
        year,month,day,hour=v
        return hour+(day-31+(month+year*12)*30)*24
    dates=[(184,2,11,0),(184,2,10,23),(184,2,11,1),(0,0,0,0),(-1,-1,-1,-1),
           (-32768,-128,-128,-128),(32767,127,127,127)]
    for stored in dates:
        for now in dates:
            oracle.poke(force+0x4C,struct.pack('<hbbb',*stored))
            oracle.poke(state+0x34,struct.pack('<hbbb',*now))
            add(83,date_value(stored)<=date_value(now),call(83,[force]),stored=list(stored),now=list(now),valid=True)
    oracle.poke(force+0x4C,game.read(force+0x4C,5))
    oracle.poke(state+0x34,game.read(state+0x34,5))
    for exists in [False,True]:
        for world_exists in [False,True]:
            for value in [-128,-1,0,1,127]:
                oracle.poke(game.exe+0x2143268,struct.pack('<Q',game.q(game.exe+0x2143268) if exists else 0))
                # A non-null manager value is only checked, never dereferenced by P141.
                if exists:oracle.poke(game.exe+0x2143268,struct.pack('<Q',1))
                oracle.poke(raw+0x871E8,struct.pack('<Q',state if world_exists else 0))
                oracle.poke(state+0xBC,struct.pack('<b',value))
                oracle.poke(args,b'\0'*4)
                actual=oracle.call32(0xC2610,141,0,context,0,stack_args=(args,0))&255
                add(141,exists and world_exists and value!=-1,actual,manager=exists,world=world_exists,challenge=value)
    oracle.poke(raw+0x871E8,struct.pack('<Q',state))
    oracle.poke(game.exe+0x2143268,game.read(game.exe+0x2143268,8))
    oracle.poke(state+0xBC,game.read(state+0xBC,1))

    for ailments in range(8):
        for byte140 in [0,1,255]:
            for kind in [1,2,255]:
                values=[(ailments>>i)&1 for i in range(3)]
                oracle.poke(army+0x23,bytes(values))
                oracle.poke(army+0x140,bytes([byte140]))
                oracle.poke(army+0x10,bytes([kind]))
                valid=oracle.call32(0x32F500,army)==1
                actual=call(137,[army,army])
                event=next((e for e in trace.events if e['rva']==0x2AC170),None)
                count=event['result'] if event else 0
                if count&0x80000000:count-=1<<32
                expected=valid and ailments!=7 and byte140==0 and kind!=2 and count>0
                add(137,expected,actual,ailments=values,byte140=byte140,kind=kind,areaCount=count,valid=valid)
    oracle.poke(army,game.read(army,units.UNIT_SIZE))

    for predicate,(gate,helper,argc) in SCHEMES.items():
        objects={82:[force,person,force,other_force],91:[force,person,city],95:[force,person,other_person],
                 99:[force,person,area],103:[force,person,area],145:[force,person,hex_]}[predicate]
        for action in [0,255]:
            oracle.poke(district+0x14,bytes([action]))
            for missing in range(-1,argc):
                chosen=objects.copy()
                if missing>=0:chosen[missing]=0
                actual=call(predicate,chosen,district)
                # Both outer gate and success helper may be called from nested helper logic; choose outer return site.
                start=ai.predicate_functions()[predicate]
                events=[e for e in trace.events if start<=e['return_address']<start+0x1000]
                gate_event=next((e for e in events if e['rva']==gate),None)
                outcome_event=next((e for e in events if e['rva']==helper),None)
                outcome=outcome_event['result'] if outcome_event else 0
                player=oracle.call(0x32E910,state)
                target_force={82:None,91:oracle.call(0x22C990,city),95:oracle.call(0x22CA00,other_person),
                              99:oracle.call(0x22C810,area),103:None,145:None}[predicate]
                player_target=player in objects[2:] if predicate==82 else player==target_force
                prereq=missing<0 and (not gate or gate_event and gate_event['result']!=0)
                expected=bool(prereq) and ((outcome in [1,2] if player_target else outcome==2)
                         if predicate in [82,91,95,99] else outcome==1 if predicate==103 else outcome in [1,2])
                add(predicate,expected,actual,outcome=outcome,playerTarget=player_target,prerequisites=bool(prereq),events=events)
    trace.close()
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,
            'method':'native remaining predicates and original helpers in copied pages; no stubs or game writes',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=verify(Game());text=vectors_text(result)
    folder=HERE/'.cache'/'ai_remaining_predicates';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} native answers matched; {path}")


if __name__=='__main__':main()
