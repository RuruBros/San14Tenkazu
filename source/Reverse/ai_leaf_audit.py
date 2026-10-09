# Exact rules of previously low-confidence leaf nodes; native bodies and helpers remain unchanged.
# python ai_leaf_audit.py --vectors ../Tests/AiLeafAuditVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list

LORD_IDS=[0x209,0x3B8,0x229,0x3BA,0x303,0x3AA,0x108,0x29A,0x9E,0xDF,0x3B5,0x28E,0x155]


def integer_list(oracle,address):
    index=struct.unpack('<I',oracle.read(oracle.q(address+8),4))[0]
    head=oracle.q(oracle.q(oracle.exe+0x1B5C9A0)+index*8)
    values=[];seen=set()
    while head:
        if head in seen:raise AssertionError('integer list cycle')
        seen.add(head);values.append(struct.unpack('<I',oracle.read(head,4))[0]);head=oracle.q(head+8)
    return values


class CommandTrace(Trace):
    def enter(self,uc,address,size,user):
        super().enter(uc,address,size,user)
        event=self.events[-1];payload=event['args'][0]
        if event['rva']==0x1EE9B0:
            event['force']=self.oracle.q(payload)
            event['advisor']=struct.unpack('<I',self.oracle.read(payload+8,4))[0]
            event['groups']=[integer_list(self.oracle,payload+0x10+16*i) for i in range(5)]
        elif event['rva']==0x1F9230:
            event['kind']=struct.unpack('<I',self.oracle.read(payload,4))[0]
            event['person']=self.oracle.q(payload+8);event['city']=self.oracle.q(payload+16)


def verify(game):
    from oracle import Oracle
    raw=game.base-0x290;manager=game.q(game.q(game.exe+ai.AI_MANAGER)+8)
    context=game.q(manager+0x38);board=game.q(context+0x18);classes={c['name']:c for c in ai.classes().values()}
    oracle=Oracle(game=game)
    force=next(p for i in range(1,52) if (p:=game.q(raw+0xDF30+8*i)) and oracle.call32(0x32F500,p)==1)
    district=oracle.call(0x22E7E0,force);city=oracle.call(0x22D3A0,district)
    keys=[0x61A170C1+i for i in range(4)];answers=[];node=out=0
    def setup(name,params):
        nonlocal oracle,node,out
        print(name,params,flush=True)
        oracle=Oracle(game=game);node=oracle.alloc(0x200);out=oracle.alloc(8)
        oracle.call(classes[name]['ctor'],node,keep=True)
        heap=oracle.call(0x94A00,keep=True)
        oracle.call32(0xB26BA0,board,ai.name_hash('HeapListInfo'),heap,keep=True)
        oracle.call32(0xB26990,board,ai.name_hash('AgentType'),3,keep=True)
        for i,v in enumerate(params):oracle.poke(node+0x38+i*4,struct.pack('<I',v&0xFFFFFFFF))
    def object_at(key):
        ok=oracle.call32(0xB26470,board,key,out,0,read_after=[(out,8)])
        if not ok:raise AssertionError('missing leaf output')
        return struct.unpack('<Q',oracle.after[0])[0]
    def execute(name,agent=None):return oracle.call32(classes[name]['exec'],node,context,district if agent is None else agent,keep=True)

    name='CGetSpecificForceDataNode'
    for choice in [-1,*range(14),2147483647]:
        setup(name,[keys[0],choice]);forces=linked_list(oracle,raw+0xB8)
        lords=[oracle.call32(0x22E970,p) for p in forces];lord=LORD_IDS[choice] if 0<=choice<13 else 0
        expected=next((p for p,k in reversed(list(zip(forces,lords))) if k==lord),0)
        status=execute(name);actual=object_at(keys[0])
        if status!=1 or actual!=expected:raise AssertionError('specific ruler selection')
        answers.append({'class':name,'choice':choice,'forces':forces,'lords':lords,'native':actual,'status':status})

    helpers={0x6DAF20:1,0x6DECA0:1,0x6E0590:1,0x70DC70:None,0x6D7EA0:1}
    for name in ['CGetPlacationDataNode','CGetUnattendedhomeDataNode','CGetTransactionDataNode']:
        for points in [0,1,255]:
            setup(name,keys[:2]);oracle.poke(district+0x14,bytes([points]));trace=Trace(oracle,helpers)
            status=execute(name);events=trace.events.copy()
            outputs=[linked_list(oracle,object_at(k)) for k in keys[:2]]
            lists=lambda r:[p for e in events if e['rva']==r for p in e['list']]
            if name=='CGetPlacationDataNode':expected=[lists(0x6DAF20),lists(0x6DECA0)]
            elif name=='CGetUnattendedhomeDataNode':expected=[lists(0x6DAF20),lists(0x6E0590)]
            else:
                city_list=list(dict.fromkeys(oracle.call(0x22A7B0,e['args'][0]) for e in events if e['rva']==0x70DC70 and e['result']!=0))
                expected=[city_list,list(dict.fromkeys(lists(0x6D7EA0)))]
            if status!=1 or outputs!=expected:raise AssertionError((name,outputs,expected,events))
            answers.append({'class':name,'points':points,'native':outputs,'expected':expected,'events':events,'status':status})
            trace.close()

    for name,list_type,base_value in [('CCautionFootholdRemoveNode',5,city),('CDistrictListAddTacticsNode',13,district)]:
        for value,present in [(v,p) for v in [base_value,0] for p in [False,True]]:
            setup(name,keys[:2]);oracle.call(0x1A2F0,keys[1],list_type,context,out,keep=True)
            pointer=object_at(keys[1]);value_pointer=oracle.alloc(8);oracle.poke(value_pointer,struct.pack('<Q',value))
            if present:
                for _ in range(2):oracle.call(0x58820,pointer,value_pointer,keep=True)
            before=linked_list(oracle,pointer)
            oracle.call32(0xB26BA0,board,keys[0],value,keep=True)
            status=execute(name);after=linked_list(oracle,pointer)
            expected=[p for p in before if p!=value] if name=='CCautionFootholdRemoveNode' else before+([value] if value else [])
            if status!=1 or after!=expected:raise AssertionError((name,before,after,expected))
            answers.append({'class':name,'before':before,'value':value,'native':after,'status':status})
    armies=[p for i in range(501) if (p:=game.q(raw+0x7E838+8*i)) and oracle.call32(0x32F500,p)==1]
    army,other_army=armies[:2]
    for limit in [0,1,10]:
        name='CCutSupplyNode';setup(name,keys[:3])
        oracle.call32(0xB26BA0,board,keys[0],army,keep=True)
        for key,v in zip(keys[1:3],[limit,limit]):oracle.call32(0xB26990,board,key,v,keep=True)
        trace=Trace(oracle,{0xBF470:3});status=execute(name)
        calls=trace.events.copy()
        if status!=2 or len(calls)!=1 or calls[0]['args'][:3]!=[army,limit,limit]:raise AssertionError('cut supply always failure')
        answers.append({'class':name,'limit':limit,'events':calls,'status':status});trace.close()
        name='CResolutionFakeNewsNode';setup(name,[keys[0],keys[1],limit])
        for key,p in zip(keys[:2],[army,other_army]):oracle.call32(0xB26BA0,board,key,p,keep=True)
        trace=Trace(oracle,{0xCE0C0:None});status=execute(name);calls=trace.events.copy()
        if len(calls)!=1 or calls[0]['args'][:3]!=[other_army,army,limit] or status!=2-int(calls[0]['result']!=0):raise AssertionError('resolution helper result')
        answers.append({'class':name,'inputs':[army,other_army],'limit':limit,'events':calls,'status':status});trace.close()
    name='CGetNearCapitalNode';setup(name,[])
    oracle.call32(0xB26990,board,ai.name_hash('AgentType'),1,keep=True)
    trace=Trace(oracle,{0x9BD30:None});status=execute(name,army);calls=trace.events.copy()
    if status!=1 or len(calls)!=1 or calls[0]['args'][:2]!=[army,2]:raise AssertionError('near capital mode')
    answers.append({'class':name,'events':calls,'status':status});trace.close()
    name='CCommandExecutionInspectNode'
    for value in [0,499,500]:
        setup(name,[]);oracle.poke(district+0x24,struct.pack('<i',value))
        if value>=500:oracle.insn_limit=300_000_000
        trace=Trace(oracle,{0x6DDCB0:1,0x1F8DE0:None,0x231510:2});status=execute(name);calls=trace.events.copy()
        if value<500 and (status!=1 or calls):raise AssertionError('inspect reserve gate')
        answers.append({'class':name,'storedReserve':value,'events':calls,'status':status});trace.close()
    name='CTradePriorityCountrySelectNode';setup(name,keys[:2])
    oracle.call32(0xB26BA0,board,keys[0],force,keep=True)
    trace=Trace(oracle,{0x6DDFC0:1,0x74980:None});status=execute(name);calls=trace.events.copy()
    answers.append({'class':name,'events':calls,'status':status});trace.close()

    person=game.q(raw+0x158+8*oracle.call32(0x22E970,force))
    name='CCommandExecutionGovernmentNode'
    person_id=struct.unpack('<H',game.read(person+0x10,2))[0]
    old_advisor=struct.unpack('<H',game.read(force+0x52,2))[0]
    old_groups=[list(struct.unpack('<7H',game.read(force+0x54+i*0x1E,14))) for i in range(5)]
    for slot in range(6):
        setup(name,[keys[0],slot,keys[1]])
        for key,value in zip(keys[:2],[district,person]):oracle.call32(0xB26BA0,board,key,value,keep=True)
        trace=CommandTrace(oracle,{0x1EE9B0:None});status=execute(name);calls=trace.events.copy()
        advisor=person_id if slot==0 else (0 if old_advisor==person_id else old_advisor)
        groups=[list(dict.fromkeys(v for v in group if v not in [0,person_id,advisor])) for group in old_groups]
        if slot:groups[slot-1].append(person_id)
        if status!=1 or len(calls)!=1 or calls[0]['advisor']!=advisor or calls[0]['groups']!=groups:raise AssertionError('policy assignments')
        answers.append({'class':name,'slot':slot,'personId':person_id,'oldAdvisor':old_advisor,'oldGroups':old_groups,
                        'events':calls,'status':status});trace.close()
    name='CCommandExecutionUnattendedHomeNode'
    person_district=oracle.call(0x40160,person)
    requirement=struct.unpack('<i',game.read(game.exe+0x1A486EC,4))[0]
    for points in [0,1,255]:
        setup(name,keys[:3])
        for key,value in zip(keys[:3],[force,person,city]):oracle.call32(0xB26BA0,board,key,value,keep=True)
        oracle.poke(person_district+0x14,bytes([points]));trace=CommandTrace(oracle,{0x1F9230:None})
        status=execute(name);calls=trace.events.copy()
        expected=1 if points>=requirement else 2
        if status!=expected or len(calls)!=int(expected==1):raise AssertionError('unattended command gate')
        answers.append({'class':name,'points':points,'requirement':requirement,'person':person,'city':city,
                        'events':calls,'status':status});trace.close()
    group=next(p for i in range(600) if (p:=game.q(raw+0x7F8D8+8*i)) and oracle.call32(0x32F500,p)==1)
    name='CExecuteAttackBuildingNode'
    for purpose in range(26):
        setup(name,[]);oracle.poke(group+0x14,struct.pack('<H',purpose));oracle.poke(group+0x18,struct.pack('<H',48400))
        status=execute(name,group);expected=2 if purpose in [0,7,17,18] else 1
        if status!=expected:raise AssertionError('building purpose gate')
        answers.append({'class':name,'purpose':purpose,'emptyRelay':True,'status':status})
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'native low-confidence leaf bodies and actual helper lists; copied pages only, no result stubs',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=verify(ListPoolSnapshot(integer_pool=True));text=vectors_text(result);folder=HERE/'.cache'/'ai_leaf_audit';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} native leaf cases matched; {path}")


if __name__=='__main__':main()
