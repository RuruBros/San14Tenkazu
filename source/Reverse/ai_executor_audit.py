# Remaining uncertain executors: native constructors, state factories and helpers, copied pages only.
# python ai_executor_audit.py --vectors ../Tests/AiExecutorAuditVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list


class Harness:
    def __init__(self,game):
        from oracle import Oracle
        self.game=game;self.Oracle=Oracle;self.raw=game.base-0x290
        manager=game.q(game.q(game.exe+ai.AI_MANAGER)+8)
        self.context=game.q(manager+0x38);self.board=game.q(self.context+0x18)
        self.classes={c['name']:c for c in ai.classes().values()}
        self.keys=[0x61A17D10+i for i in range(8)]
        self.o=Oracle(game=game)
    def valid_table(self,offset,count):
        return [p for i in range(count) if (p:=self.game.q(self.raw+offset+8*i)) and self.o.call32(0x32F500,p)==1]
    def setup(self,name,params,agent_type=3):
        print(name,params,flush=True);self.name=name;self.o=self.Oracle(game=self.game);self.o.insn_limit=300_000_000
        self.node=self.o.alloc(0x200);self.out=self.o.alloc(8);values=self.o.alloc(max(4,len(params)*4))
        self.o.call(self.classes[name]['ctor'],self.node,keep=True)
        if params:
            self.o.poke(values,struct.pack('<'+'I'*len(params),*(v&0xFFFFFFFF for v in params)))
            self.o.call(self.classes[name]['load'],self.node,values,len(params),keep=True)
        heap=self.o.call(0x94A00,keep=True)
        self.o.call32(0xB26BA0,self.board,ai.name_hash('HeapListInfo'),heap,keep=True)
        self.o.call32(0xB26990,self.board,ai.name_hash('AgentType'),agent_type,keep=True)
    def obj(self,key,value):self.o.call32(0xB26BA0,self.board,key,value,keep=True)
    def number(self,key,value):self.o.call32(0xB26990,self.board,key,value,keep=True)
    def output(self,key):
        ok=self.o.call32(0xB26470,self.board,key,self.out,0,read_after=[(self.out,8)])
        if not ok:raise AssertionError('missing executor output')
        return struct.unpack('<Q',self.o.after[0])[0]
    def execute(self,agent):return self.o.call32(self.classes[self.name]['exec'],self.node,self.context,agent,keep=True)
    def state(self):
        getter=self.o.q(self.o.q(self.node)+8)-self.o.exe
        factory=self.o.call(getter,self.node)
        # The same genuine allocator selected by native typed-list factory 0x191D0.
        allocator=self.o.call(0x1AC10)+0x7F8
        allocator+=0x68 if struct.unpack('<I',self.o.read(allocator+0xE8,4))[0] else 8
        state=self.o.call(factory-self.o.exe,allocator,keep=True)
        if not state:raise AssertionError('native state allocation failed')
        reset=self.o.q(self.o.q(state)+0x10)-self.o.exe
        self.o.call(reset,state,keep=True)
        self.o.poke(self.context+0x38,struct.pack('<Q',state))
        return state


def records(o,address):
    head=o.q(address);node=o.q(head);values=[];seen=set()
    while node!=head:
        if node in seen:raise AssertionError('record list cycle')
        seen.add(node);values.append([o.q(node+16),o.q(node+24)]);node=o.q(node)
    return values


def verify(game):
    h=Harness(game);force=h.valid_table(0xDF30,52)[1];district=h.o.call(0x22E7E0,force);city=h.o.call(0x22D3A0,district)
    armies=h.valid_table(0x7E838,501);group=h.valid_table(0x7F8D8,600)[0];area=h.o.call(0x229D40,city)
    keys=h.keys;answers=[]
    name='CExclusionUserArmyAttachedFootholdNode'
    for army in [0,*armies]:
        h.setup(name,[],1);before=struct.unpack('<I',h.o.read(army+0x1F0,4))[0] if army else 0
        trace=Trace(h.o,{0xB51A0:None,0x26C940:None});status=h.execute(army);events=trace.events.copy()
        helper=next((e['result']!=0 for e in events if e['rva']==0xB51A0),False)
        after=struct.unpack('<I',h.o.read(army+0x1F0,4))[0] if army else 0
        if status!=(1 if helper else 2) or after!=(before|0x20 if helper else before):raise AssertionError('exclusion flags')
        answers.append({'class':name,'helper':helper,'before':before,'after':after,'status':status,'events':events});trace.close()
    name='CDefendFootholdRemoveNode'
    for value in [city,0]:
        h.setup(name,keys[:2]);h.obj(keys[0],value)
        # This node takes a raw std::list of pairs, not the typed pool list factory.
        pointer=h.o.alloc(16);head=h.o.call(0x4DC40,pointer,0,0,keep=True)
        h.o.poke(pointer,struct.pack('<Q',head));h.obj(keys[1],pointer);data=h.o.alloc(16)
        for base,marker in [(city,10),(0,20),(city,30)]:
            h.o.poke(data,struct.pack('<QQ',base,marker));head=h.o.q(pointer)
            node=h.o.call(0x206A0,pointer,head,h.o.q(head+8),data,keep=True)
            count=struct.unpack('<Q',h.o.read(pointer+8,8))[0]
            h.o.poke(pointer+8,struct.pack('<Q',count+1));h.o.poke(head+8,struct.pack('<Q',node));h.o.poke(h.o.q(node+8),struct.pack('<Q',node))
        before=records(h.o,pointer);status=h.execute(district);after=records(h.o,pointer)
        if status!=1 or after!=[r for r in before if r[0]!=value]:raise AssertionError('defend records')
        answers.append({'class':name,'value':value,'before':before,'native':after,'status':status})
    name='CTroopsSetTargetTroopsNode'
    targets={'base':city,'caution':h.valid_table(0x80BA8,501)[0],'area':area,
             'building':h.valid_table(0x6DA98,3063)[0],'hex':game.q(h.raw+0xE270+8*struct.unpack('<H',game.read(armies[0]+0x2A,2))[0])}
    for purpose in range(26):
        h.setup(name,keys[:2],2);h.obj(keys[0],group)
        kind='base' if purpose in [0,2,8,9,10,11,12,17,18,22] else 'caution' if purpose in [1,19] else 'area' if purpose in [3,13,14,15,16,21,23] else 'building' if purpose in [4,20] else 'hex'
        target=targets[kind];h.obj(keys[1],target);h.o.poke(group+0x14,struct.pack('<H',purpose))
        h.o.call(0x1A2F0,keys[2],1,h.context,h.out,keep=True);pointer=h.output(keys[2]);h.o.call(0x22A110,group,pointer,keep=True)
        members=linked_list(h.o,pointer)
        for member in members:h.o.poke(member+0x13C,b'\0')
        before=struct.unpack('<H',h.o.read(group+0x16,2))[0]
        resolver={'caution':0x22DA90,'area':0x22D9E0,'building':0x22DEA0,'hex':0x22DDA0}.get(kind)
        expected=struct.unpack('<H',h.o.read(city+0x4E,2))[0] if kind=='base' else max(0,min(65535,h.o.call32(resolver,target)))
        if purpose in [24,25]:expected=before
        status=h.execute(group);after=struct.unpack('<H',h.o.read(group+0x16,2))[0]
        if status!=1 or after!=expected:raise AssertionError(('troops target',purpose,after,expected))
        answers.append({'class':name,'purpose':purpose,'kind':kind,'expectedTarget':expected,'nativeTarget':after,'status':status})
    if not members:raise AssertionError('group needs a real member for the busy gate')
    h.setup(name,keys[:2],2);h.obj(keys[0],group);h.obj(keys[1],0)
    h.o.poke(members[0]+0x13C,b'\1');before=struct.unpack('<H',h.o.read(group+0x16,2))[0]
    status=h.execute(group);after=struct.unpack('<H',h.o.read(group+0x16,2))[0]
    if status!=2 or before!=after:raise AssertionError('busy member gate')
    answers.append({'class':name,'blocked':True,'expectedTarget':before,'nativeTarget':after,'status':status})
    for index in [-1,0,65535,65536,-2147483648,2147483647]:
        h.setup(name,keys[:2],2);h.obj(keys[0],group)
        # The native area resolver only subtracts pointers; this node does not validate this target.
        target=game.q(h.raw+0x6CAF0)+index*0x98;h.obj(keys[1],target)
        h.o.poke(group+0x14,struct.pack('<H',3))
        for member in members:h.o.poke(member+0x13C,b'\0')
        status=h.execute(group);after=struct.unpack('<H',h.o.read(group+0x16,2))[0];expected=max(0,min(65535,index))
        if status!=1 or after!=expected:raise AssertionError('target index clamp')
        answers.append({'class':name,'purpose':3,'kind':'area','inputIndex':index,'expectedTarget':expected,'nativeTarget':after,'status':status})
    for name in ['CExecuteAreaOccupyNode','CExecuteAttackAreaBuildingNode','CExecuteAttractNode']:
        h.setup(name,[keys[0],50,keys[1]],2);state=h.state()
        h.o.poke(group+0x18,struct.pack('<H',48400));h.o.poke(group+0x16,struct.pack('<H',struct.unpack('<H',game.read(city+0x4E,2))[0]))
        h.o.poke(group+0x14,struct.pack('<H',7))
        target_allowed=False
        if name=='CExecuteAttractNode':
            index=struct.unpack('<H',h.o.read(group+0x16,2))[0];caution=game.q(h.raw+0x80BA8+8*(index if index<501 else 0))
            target_allowed=bool(h.o.call32(0x32F500,caution) and linked_list(h.o,caution+0x28) and
                h.o.call32(0x2CA280,h.o.call32(0x22CCD0,caution),h.o.call32(0x22CEF0,group)))
        before=h.o.read(state,0x18).hex();status=h.execute(group);after=h.o.read(state,0x18).hex()
        answers.append({'class':name,'emptyRelay':True,'purpose':7,'stateBefore':before,'stateAfter':after,
                        'targetAllowed':target_allowed,'status':status})
        if name=='CExecuteAttractNode':
            # Independently validate the completed-state input; do not claim natural movement to stage 5.
            h.o.poke(state+8,struct.pack('<I',1));h.o.poke(state+0x10,b'\5')
            h.o.poke(h.context+0x38,struct.pack('<Q',state));before=h.o.read(state,0x18).hex()
            status=h.execute(group);after=h.o.read(state,0x18).hex()
            answers.append({'class':name,'inputStage':5,'stateBefore':before,'stateAfter':after,'targetAllowed':target_allowed,'status':status})
    name='CDecideAreaOccupyRouteNode'
    for purpose in [0,7,17,18,3]:
        h.setup(name,keys[:1],2);h.o.poke(group+0x14,struct.pack('<H',purpose));h.o.poke(group+0x16,game.read(city+0x4E,2))
        trace=Trace(h.o,{0x1843E0:None});status=h.execute(group);output=linked_list(h.o,h.output(keys[0]));events=trace.events.copy()
        if purpose in [0,7,17,18] and (status!=2 or output or events):raise AssertionError('route purpose gate')
        answers.append({'class':name,'purpose':purpose,'native':output,'status':status,'events':events});trace.close()
    name='CGetCandidateFacilityListNode'
    for mode in range(5):
        h.setup(name,keys[:5]);h.number(keys[1],mode);h.obj(keys[2],city);h.obj(keys[3],area)
        trace=Trace(h.o,{0x3F5D0:None});status=h.execute(district);output=linked_list(h.o,h.output(keys[0]));events=trace.events.copy()
        h.o.call32(0xB26300,h.board,keys[1],h.out,0,read_after=[(h.out,4)]);out_mode=struct.unpack('<i',h.o.after[0])[0]
        h.o.call32(0xB26300,h.board,keys[4],h.out,0,read_after=[(h.out,4)]);special=struct.unpack('<i',h.o.after[0])[0]
        answers.append({'class':name,'mode':mode,'outputMode':out_mode,'special':special,'native':output,'status':status,'events':events});trace.close()
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'native execute bodies, loaders and state factories; copied pages only; no helper substitution',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=verify(ListPoolSnapshot());text=vectors_text(result);folder=HERE/'.cache'/'ai_executor_audit';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} executor cases completed; {path}")


if __name__=='__main__':main()
