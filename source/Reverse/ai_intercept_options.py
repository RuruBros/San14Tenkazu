# Resolve the two intercept option consumers through full native execution and native mobility sorting.
# python ai_intercept_options.py [--vectors ../Tests/AiInterceptOptionsVectors.json]
import argparse
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import struct
from ai_executor_audit import Harness
from ai_facility_special import read_clock, native_list, change_input
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list
from image import Image
from oracle import OracleError
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_R13

def verify(game,sort_only=False):
    before=read_clock(game);h=Harness(game);cases=[];failures=[]
    armies=h.valid_table(0x7E838,501);countries=h.valid_table(0x80BA8,501)
    groups=h.valid_table(0x7F8D8,600);surveys=[];chosen=None
    h.setup('CExecuteInterceptNode',[0,0,0],2)
    for group in groups:
        ptr=native_list(h,1,1);h.o.call(0x22A110,group,ptr,keep=True);members=linked_list(h.o,ptr)
        surveys.append(dict(group=group,members=members))
        if len(members)>1 and all(not 7<=h.o.read(m+0x1B,1)[0]<=10 for m in members):
            chosen=(group,members);break
    if not chosen:raise AssertionError('an actual multiple-unit land group is required')
    group,members=chosen;caution=countries[0];target=h.o.call32(0x22DA90,caution)
    plans=() if sort_only else ((0,0),(1,0),(-1,0),(0,1),(0,-1),(0,2),(1,1))
    for p0,p1 in plans:
        h.setup('CExecuteInterceptNode',[h.keys[0],h.keys[1],0],2);h.number(h.keys[0],p0);h.number(h.keys[1],p1)
        state=h.state();h.o.insn_limit=600_000_000;changes=[]
        for a,v,s,role in ((group+0x14,1,2,'group purpose'),(group+0x16,target,2,'caution target')):
            change_input(h,changes,a,v,s,role+' in copied input')
        for m in members:
            change_input(h,changes,m+0x2C,48400,2,'empty first relay in copied input')
        gates=[]
        def gate(uc,address,size,user):
            gates.append(dict(rva=address-h.o.exe,value=struct.unpack('<i',h.o.read(uc.reg_read(UC_X86_REG_RAX),4))[0]))
        handles=[h.o.mu.hook_add(UC_HOOK_CODE,gate,None,h.o.exe+r,h.o.exe+r) for r in (0x6AAAA,0x6BDD1)]
        trace=Trace(h.o,{0x5D3B0:None,0xCF920:None,0x97330:None,0x2EE1E0:None})
        row=dict(kind='executor',p0=p0,p1=p1,group=group,members=members,caution=target,copiedInputChanges=changes,gates=gates)
        try:
            row['status']=h.execute(group);row['stateAfter']=h.o.read(state,0x38).hex();cases.append(row)
        except OracleError as error:row['error']=str(error);failures.append(row)
        finally:
            row['events']=trace.events.copy();trace.close()
            for handle in handles:h.o.mu.hook_del(handle)
        print(p0,p1,row.get('status',row.get('error')),'gates',gates,flush=True)
        folder=HERE/'.cache'/'ai_intercept_options';folder.mkdir(parents=True,exist_ok=True)
        (folder/'executor.partial.json').write_text(vectors_text(dict(clockBefore=before,clockAfter=read_clock(game),cases=cases,failures=failures)),encoding='utf-8')
    # Independent native list sort: real unit pointers and their real formations; the byte context is unused.
    selected=armies[:6]
    for order in (selected,selected[::-1]):
        for context in (0,1,255):
            h.setup('CExecuteInterceptNode',[0,0,0],2);ptr=native_list(h,1,1);value=h.o.alloc(8);mobility=[]
            for unit in order:
                formation=h.o.read(unit+0x1B,1)[0];definition=h.o.q(h.raw+0x77428+8*(formation if formation<=20 else 0))
                factor=h.o.call(0x2B0290,unit,definition,0,0,stack_args=(0,),ret_float=True)
                mobility.append(dict(unit=unit,formation=formation,value=factor))
                h.o.poke(value,struct.pack('<Q',unit));h.o.call(0x58820,ptr,value,keep=True)
            index=struct.unpack('<I',h.o.read(h.o.q(ptr+8),4))[0];trace=Trace(h.o,{0x97330:None})
            h.o.call(0x5D3B0,h.o.exe+0x27D10E0,index,context,keep=True);output=linked_list(h.o,ptr);events=trace.events.copy();trace.close()
            expected=sorted(order,key=lambda u:-next(m['value'] for m in mobility if m['unit']==u))
            cases.append(dict(kind='sort',input=order,context=context,mobility=mobility,native=output,events=events))
            if output!=expected:raise AssertionError('native intercept mobility order; see completed executor.partial.json')
    after=read_clock(game);im=Image()
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,
                surveys=surveys,cases=cases,failures=failures,listPoolSnapshot=game.pool_snapshot,
                method='full native intercept executor with factory state and copied group/relay inputs; native six-unit mobility sort; no game writes, hooks or helper replacement',
                oracle_sha256=hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),
                static={hex(r):im.flowtext(r) for r in (0x69BD0,0x5D3B0,0x97330,0xCF920)})

def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);p.add_argument('--sort-only',action='store_true');args=p.parse_args();result=verify(ListPoolSnapshot(),args.sort_only)
    folder=HERE/'.cache'/'ai_intercept_options';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} completed; {len(result['failures'])} faults; {path}")
    if not result['clockStable']:raise SystemExit('Game clock changed; diagnostics only')
if __name__=='__main__':main()
