# Numeric parameter effects previously marked low; no game writes and no native result replacement.
# python ai_parameter_audit.py --vectors ../Tests/AiParameterAuditVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
from ai_friend import HERE, vectors_text
from ai_executor_audit import Harness
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list


def prefix(h,stop,agent):
    # Only verify cache writes up to this address. Do not treat an unfinished command as successful.
    from oracle import SENTINEL
    from unicorn import UC_HOOK_CODE
    from unicorn.x86_const import UC_X86_REG_RIP
    reached=[]
    def done(uc,address,size,user):
        reached.append(address-h.o.exe);uc.reg_write(UC_X86_REG_RIP,SENTINEL);uc.emu_stop()
    handle=h.o.mu.hook_add(UC_HOOK_CODE,done,None,h.o.exe+stop,h.o.exe+stop)
    try:
        h.execute(agent)
        if reached!=[stop]:raise AssertionError((h.name,'cache prefix not reached',reached))
    finally:h.o.mu.hook_del(handle)


def verify(game):
    from oracle import Oracle
    h=Harness(game);forces=h.valid_table(0xDF30,52);district=h.o.call(0x22E7E0,forces[1]);keys=h.keys;answers=[]
    for value in [-2147483648,-129,-128,-127,-1,0,1,2,3,126,127,128,2147483647]:
        h.setup('CDistrictSetPurposeNode',[value]);status=h.execute(district)
        actual=struct.unpack('<b',h.o.read(district+0x15,1))[0]
        if status!=1 or actual!=max(-128,min(127,value)):raise AssertionError('district type clamp')
        answers.append({'kind':'districtType','input':value,'native':actual,'status':status})
    people=set(game.q(h.raw+0x158+8*h.o.call32(0x22E970,force)) for force in forces)
    for army in h.valid_table(0x7E838,501):
        index=struct.unpack('<H',game.read(army+0x12,2))[0];people.add(game.q(h.raw+0x158+8*index))
    profiles={}
    for person in sorted(people):
        if not person or not h.o.call32(0x32F500,person):continue
        flags=tuple(h.o.call32(0x2B2DA0,person,k)!=0 for k in [1,2,3])
        profiles.setdefault(flags,person)
    for flags,person in profiles.items():
        for value in [-2147483648,-1,0,1,2,3,2147483647]:
            o=Oracle(game=game);payload=o.alloc(0x80);before=[value]*5
            o.poke(payload+0x44,struct.pack('<5i',*before));trace=Trace(o,{0x2B2DA0:None})
            o.call(0xA1D00,person,payload,keep=True);after=list(struct.unpack('<5i',o.read(payload+0x44,20)))
            observed=[e['result']!=0 for e in trace.events];expected=before.copy()
            if observed[0]:expected[0:2]=[2,0]
            if observed[1]:expected[0:3]=[1,1,0]
            if observed[2]:expected[2:4]=[1,2]
            routes=list(struct.unpack('<5i',o.read(payload+0x20,20)))
            if observed!=list(flags) or after!=expected or routes!=[48400]*5:raise AssertionError('departure policy')
            answers.append({'kind':'departurePolicies','person':person,'before':before,'flags':observed,'native':after,'routes':routes,'events':trace.events.copy()});trace.close()
    person=game.q(h.raw+0x158+8*h.o.call32(0x22E970,forces[1]));home_id=struct.unpack('<H',game.read(person+0xE2,2))[0]
    # Use the actual World_GetBase API from the image; do not guess a pointer-table base.
    import re
    names=(HERE/'names.txt').read_text(encoding='utf-8')
    match=re.search(r'^(0x[0-9A-Fa-f]+) World_GetBase(?:\s|$)',names,re.M)
    if not match:raise AssertionError('World_GetBase symbol missing')
    home=h.o.call(int(match[1],16),h.raw,home_id)
    home_area=h.o.call(0x229D40,home);gate_area=h.o.call(0x229E40,home)
    areas=[home_area,gate_area,next(a for a in h.valid_table(0x6CAF0,501) if a not in [home_area,gate_area])]
    from unicorn import UC_HOOK_CODE
    from unicorn.x86_const import UC_X86_REG_RBP
    for area in dict.fromkeys(areas):
        for option in [-1,0,1]:
            h.setup('CGetFacilityCandidateHexListNode',[keys[0],keys[1],1,keys[3],keys[4],keys[5],keys[6]])
            h.obj(keys[0],area);h.obj(keys[1],person);h.number(keys[6],option);modes=[]
            def observe(uc,address,size,user):
                modes.append(struct.unpack('<i',h.o.read(uc.reg_read(UC_X86_REG_RBP)+0xC0,4))[0])
            handle=h.o.mu.hook_add(UC_HOOK_CODE,observe,None,h.o.exe+0x3AA67,h.o.exe+0x3AA67)
            trace=Trace(h.o,{0x22F060:1})
            status=h.execute(district);output=linked_list(h.o,h.output(keys[3]));events=trace.events.copy()
            adjacent=any(e['args'][0]==area and gate_area in e['list'] for e in events)
            expected=area!=home_area and option==0 and (area==gate_area or adjacent)
            if modes!=[int(expected)]:raise AssertionError(('facility border mode',modes,expected))
            answers.append({'kind':'facilityHexOption','area':area,'homeArea':home_area,'gateArea':gate_area,
                            'option':option,'adjacent':adjacent,'nativeMode':modes[0],'native':output,'status':status,'events':events})
            trace.close();h.o.mu.hook_del(handle)
    for value in [-2147483648,-1,0,1,2147483647]:
        h.setup('CExecuteInterceptNode',keys[:2],2);h.number(keys[0],value);h.number(keys[1],-value if value!=-2147483648 else 2147483647)
        prefix(h,0x69C6D,0)
        actual=list(struct.unpack('<ii',h.o.read(h.node+0x44,8)))
        answers.append({'kind':'interceptCache','input':[value,-value if value!=-2147483648 else 2147483647],
                        'native':actual,'stop':'0x69C6D'})
        if actual!=[answers[-1]['input'][1],value]:raise AssertionError('intercept int cache')
        h.setup('CCommandAreaOccupyDepatureNode',keys[:6]);h.number(keys[1],123);h.number(keys[3],value)
        h.o.call(0x1A2F0,keys[0],6,h.context,h.out,keep=True);h.obj(keys[5],home)
        prefix(h,0x29664,district)
        actual=list(struct.unpack('<ii',h.o.read(h.node+0x54,8)));pointer=h.o.q(h.node+0x68)
        if actual!=[123,value] or pointer!=home:raise AssertionError('occupy cached inputs')
        answers.append({'kind':'occupyCache','input':value,'native':actual,'pointer':pointer,'expectedPointer':home,'stop':'0x29664'})
        h.setup('CCommandFootholdAttackDepatureNode',keys)
        own=struct.unpack('<B',game.read(district+0x10,1))[0]
        enemy=next(h.o.call(0x22D3A0,h.o.call(0x22E7E0,f)) for f in forces if h.o.call32(0x2CA280,own,h.o.call32(0x22DC80,f)))
        # The current corps has zero commands. A copied input of one reaches the cache-writing prefix.
        h.o.poke(district+0x14,b'\x01')
        h.obj(keys[0],enemy);h.o.call(0x1A2F0,keys[1],5,h.context,h.out,keep=True);h.number(keys[2],value)
        prefix(h,0x2F41D,district);actual=struct.unpack('<i',h.o.read(h.node+0x80,4))[0]
        if actual!=value:raise AssertionError('attack cached option')
        answers.append({'kind':'baseAttackCache','input':value,'native':actual,'copiedCorpsCommands':1,'stop':'0x2F41D'})
    group=h.valid_table(0x7F8D8,600)[0]
    for value in [-2147483648,-1,0,1,2147483647]:
        h.setup('CAttackFootholdRelayAreaAttackNode',[0,0,keys[2],keys[3],keys[4]],2)
        for key in keys[2:5]:h.number(key,value)
        owner=h.o.call32(0x22CEF0,group)
        enemy=next(h.o.call(0x22D3A0,h.o.call(0x22E7E0,f)) for f in forces if h.o.call32(0x2CA280,owner,h.o.call32(0x22DC80,f)))
        target=struct.unpack('<H',h.o.read(enemy+0x4E,2))[0]
        h.o.poke(group+0x14,struct.pack('<HH',9,target))
        state=h.state();prefix(h,0x620DB,group);actual=list(h.o.read(state+0x1C,3))
        if actual!=[int(value!=0)]*3:raise AssertionError('relay boolean cache')
        answers.append({'kind':'relayFlags','input':value,'native':actual,'copiedPurpose':9,'copiedTargetBase':target,'stop':'0x620DB'})
    countries=h.valid_table(0x85A20,5)
    if countries:
        capital=h.o.call(0x22E8C0,forces[1]);troops=struct.unpack('<i',game.read(capital+0x3C,4))[0]
        for value in [troops-1,troops,troops+1]:
            h.setup('CCommandExecutionTradeNode',keys[:5]);h.obj(keys[0],countries[0]);h.number(keys[2],0);h.number(keys[3],0);h.number(keys[4],value)
            h.o.call(0x1A2F0,keys[1],4,h.context,h.out,keep=True);trace=Trace(h.o,{0x1EA8C0:None})
            status=h.execute(district);called=bool(trace.events)
            if called!=(value<troops) or status!=(1 if value<troops else 2):raise AssertionError('trade troops strict bound')
            answers.append({'kind':'tradeTroops','input':value,'troops':troops,'commandCalled':called,'status':status,'events':trace.events.copy()});trace.close()
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'native parameter storage, policy helper and placement body; copied pages only',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=verify(ListPoolSnapshot());text=vectors_text(result);folder=HERE/'.cache'/'ai_parameter_audit';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} parameter cases matched; {path}")


if __name__=='__main__':main()
