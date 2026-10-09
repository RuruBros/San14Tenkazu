# Observe both original complete search backends, their relax decisions and float priorities.
# python ai_path_search_audit.py [--smoke] [--vectors ../Tests/AiPathSearchVectors.json.gz]
# No middle-block invocation, instruction patch, helper replacement or game writes.
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
from pathlib import Path
import struct
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace
from image import Image
from oracle import OracleError
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RAX,UC_X86_REG_RBX,UC_X86_REG_RDI,UC_X86_REG_R12,UC_X86_REG_RSI,UC_X86_REG_XMM7,UC_X86_REG_EDI

def signed(value):return (value+2**31)%2**32-2**31

def verify(game,smoke,configurations=None,prepare=None):
    before=read_clock(game);h=Harness(game);o=h.o;o.insn_limit=600_000_000
    army=next(u for u in h.valid_table(0x7E838,501) if 1<=o.read(u+0x1B,1)[0]<=6);force=o.call(0x22C850,army)
    context=o.alloc(0x30);points=o.alloc(16);nodes=o.alloc(48400*40);answers=[];faults=[];exclusion_lists={};im=Image();im.md.detail=True
    functions={r:list(im.flow(r)) for r in (0x183A30,0x184EB0)}
    branches={r:{i.address:i for i in instructions if i.mnemonic.startswith('j') and i.mnemonic!='jmp'} for r,instructions in functions.items()}
    observations=[];active=[None];previous=[None];resolved_owner=[None];edges={r:set() for r in functions}
    anchors={0x183A30:{'cost':0x1841BB,'accept':0x1841CC,'skip':0x1842DF,'distance':(0x184214,0x184284),'priority':(0x184225,0x184295),'next':0x1842E3},
             0x184EB0:{'cost':0x1853F8,'accept':0x185409,'skip':0x185538,'distance':(0x185461,0x1854D5),'priority':(0x185472,0x1854E6),'next':0x18553C}}
    # The second backend's exact instruction boundaries are checked before any execution.
    text={r:im.flowtext(r) for r in functions}
    for r,anchor in anchors.items():
        known={i.address for i in functions[r]}
        for key,pc in anchor.items():
            for p in pc if isinstance(pc,tuple) else (pc,):
                if p not in known:raise AssertionError((hex(r),key,hex(p),'observer is not an instruction boundary'))
    for r in functions:
        def observe(uc,address,size,user,r=r):
            pc=address-o.exe
            if previous[0] in branches[r]:edges[r].add((previous[0],pc))
            previous[0]=pc;anchor=anchors[r]
            if pc==(0x183BC7 if r==0x183A30 else 0x185031):resolved_owner[0]=signed(uc.reg_read(UC_X86_REG_EDI))
            if pc==anchor['cost']:
                node=uc.reg_read(UC_X86_REG_RDI);parent=uc.reg_read(UC_X86_REG_R12)
                row=dict(hex=uc.reg_read(UC_X86_REG_RSI)&0xFFFFFFFF,node=node,parent=parent,parentHex=struct.unpack('<i',o.read(parent+24,4))[0],
                    parentCost=struct.unpack('<i',o.read(parent+32,4))[0],stepCost=signed(uc.reg_read(UC_X86_REG_RAX)&0xFFFFFFFF),
                    newCost=signed(uc.reg_read(UC_X86_REG_RBX)&0xFFFFFFFF),oldCost=struct.unpack('<i',o.read(node+32,4))[0],flags=o.read(node+28,1)[0])
                observations.append(row);active[0]=row
            elif active[0] is not None:
                row=active[0]
                if pc==anchor['accept']:row['accepted']=True
                elif pc==anchor['skip'] and 'accepted' not in row:row['accepted']=False
                elif pc in anchor['distance']:row['distance']=signed(uc.reg_read(UC_X86_REG_RAX)&0xFFFFFFFF)
                elif pc in anchor['priority']:
                    row['priority']=signed(uc.reg_read(UC_X86_REG_RBX)&0xFFFFFFFF)
                    row['factorBits']=uc.reg_read(UC_X86_REG_XMM7)&0xFFFFFFFF
                    row['storedCost']=struct.unpack('<i',o.read(row['node']+32,4))[0]
                    row['updatedFlags']=o.read(row['node']+28,1)[0]
                elif pc==anchor['next']:active[0]=None
        o.mu.hook_add(UC_HOOK_CODE,observe,None,o.exe+r,o.exe+max(i.address for i in functions[r]))
    # Heuristic selection is options & 5, not clearBytes: both backends overwrite
    # the initial clearing-length stack slot with that mask before searching.
    configs=configurations if configurations is not None else [dict(rva=r,options=options,context=kind,clearBytes=clear)
        for r in functions for options in (0,1,4,8,16,33,64,255) for kind in ('neutral','army') for clear in (0,48400)]
    if smoke:configs=configs[:2]
    preparation=prepare(h) if prepare else None
    for config in configs:
        r,options,kind,clear=(config[k] for k in ('rva','options','context','clearBytes'))
        chosen_army=config.get('armyPointer',army);chosen_force=o.call(0x22C850,chosen_army)
        source=config.get('source',12780);goals=config.get('goals',[12781]);limit=config.get('limit',20000)
        if read_clock(game)!=before:raise AssertionError('Game changed before search observation')
        o.poke(nodes,bytes(48400*40));o.poke(points,struct.pack('<4i',*(goals+[0]*(4-len(goals)))));o.poke(context,bytes(0x30))
        o.poke(context,struct.pack('<QQ',chosen_army if kind=='army' else 0,chosen_force if kind in ('army','force') else 0))
        for offset,value in config.get('contextWords',[]):o.poke(context+offset,struct.pack('<i',value))
        exclusion_key=tuple(config.get('excludedHexes',[]))
        if 'excludedHexes' in config:
            if exclusion_key not in exclusion_lists:
                index_pointer=o.call(0x18B60,o.exe+0x27D1128,0,0,keep=True)
                if not index_pointer:raise AssertionError('native exclusion index allocation failed')
                index=struct.unpack('<I',o.read(index_pointer,4))[0];entries=[]
                for hex_ in exclusion_key:
                    tile=o.q(h.raw+0xE270+hex_*8)
                    if not tile:raise AssertionError('missing actual exclusion tile')
                    entry=o.call(0x18DF0,o.exe+0x27D10E0,index,1,keep=True)
                    if not entry:raise AssertionError('native exclusion node allocation failed')
                    o.poke(entry,struct.pack('<Q',tile));entries.append(dict(hex=hex_,tile=tile,entry=entry))
                heads=o.q(o.exe+0x27D10F0);node=o.q(heads+index*8);native_tiles=[];seen=set()
                while node:
                    if node in seen:raise AssertionError('native exclusion list cycle')
                    seen.add(node);native_tiles.append(o.q(node));node=o.q(node+8)
                if native_tiles!=[e['tile'] for e in entries]:raise AssertionError('native exclusion list contents differ')
                exclusion_lists[exclusion_key]=dict(indexPointer=index_pointer,index=index,entries=entries,nativeTiles=native_tiles)
            o.poke(context+0x20,struct.pack('<Q',exclusion_lists[exclusion_key]['indexPointer']))
        changes=[]
        for offset,size,value in config.get('armyFields',[]):
            address=chosen_army+offset;old=o.read(address,size);new=value.to_bytes(size,'little',signed=False)
            changes.append(dict(address=address,offset=offset,before=old.hex(),after=new.hex()));o.poke(address,new)
        for rva,size,value in config.get('imageFields',[]):
            address=o.exe+rva;old=o.read(address,size);new=value.to_bytes(size,'little',signed=False)
            changes.append(dict(address=address,rva=rva,before=old.hex(),after=new.hex()));o.poke(address,new)
        observations=[];active[0]=None;previous[0]=None;resolved_owner[0]=None
        row={**config,'source':source,'target':goals[0] if goals else None,'goals':goals,'limit':limit,'changes':changes,'relaxations':observations}
        if 'excludedHexes' in config:row['nativeExclusionList']=exclusion_lists[exclusion_key]
        trace=Trace(o,{rva:None for rva in config.get('tracedHelpers',[])}) if config.get('tracedHelpers') else None
        def chain_ranges():
            pointer=o.mu.reg_read(UC_X86_REG_RAX);seen=set();ranges=[]
            while pointer:
                if pointer in seen or len(seen)>=48400:raise AssertionError('native route chain cycle')
                seen.add(pointer);ranges.append((pointer,40));pointer=o.q(pointer+16)
            return ranges
        try:
            tail=(options,0) if r==0x183A30 else (limit,options)
            row['returnPointer']=o.call(r,context,source,points,len(goals),stack_args=(nodes,clear,*tail),read_after=chain_ranges)
            row['resolvedSearchOwner']=resolved_owner[0]
            row['chain']=[dict(hex=struct.unpack_from('<i',b,24)[0],flags=b[28],cost=struct.unpack_from('<i',b,32)[0]) for b in o.after]
            answers.append(row)
        except OracleError as error:row['error']=str(error);faults.append(row)
        finally:
            if trace:row['helperCalls']=trace.events.copy();trace.close()
            for change in changes:o.poke(change['address'],bytes.fromhex(change['before']))
        print(hex(r),options,kind,goals,limit,len(observations),'decisions; chain',len(row.get('chain',[])),row.get('error',''),flush=True)
    after=read_clock(game);coverage={}
    for r,items in branches.items():
        coverage[hex(r)]=[dict(rva=pc,text=i.mnemonic+' '+i.op_str,taken=(pc,i.operands[0].imm) in edges[r],fallthrough=(pc,pc+i.size) in edges[r]) for pc,i in sorted(items.items())]
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,cases=answers,failures=faults,
                branchCoverage=coverage,instructionCounts={hex(r):len(ins) for r,ins in functions.items()},static=text,listPoolSnapshot=game.pool_snapshot,army=army,force=force,
                method='complete original search backends; valid clean scratch as in wrappers; numeric update/decision observers only; no partial-block invocation, forced results or game writes',
                oracle_sha256=hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),preparation=preparation)

def main():
    p=argparse.ArgumentParser();p.add_argument('--smoke',action='store_true');p.add_argument('--vectors',type=Path);args=p.parse_args();result=verify(ListPoolSnapshot(),args.smoke)
    folder=HERE/'.cache'/'ai_path_search_audit';folder.mkdir(parents=True,exist_ok=True);path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result)
    path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']:args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(len(result['cases']),'completed;',len(result['failures']),'faults;',path)
    if not result['clockStable']:raise SystemExit('Game changed; diagnostics only')
if __name__=='__main__':main()
