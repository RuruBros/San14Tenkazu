# Two complete original backends on an actual frozen minor-force army.
# Source=goal isolates initialization gates without changing army/world/code fields.
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import struct
from ai_executor_audit import Harness
from ai_frozen_preflight import FrozenImage
from ai_path_search_audit import verify
from ai_static_facts import anchored
from image import Image
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_EDI, UC_X86_REG_RSI


def army_inputs(h):
    rows=[]
    for slot in range(501):
        pointer=h.game.q(h.raw+0x7E838+slot*8)
        if not pointer or h.o.call32(0x32F500,pointer)!=1:continue
        data=h.game.read(pointer,0x200)
        rows.append(dict(slot=slot,pointer=pointer,kind=data[0x10],leader=struct.unpack_from('<H',data,0x12)[0],
                         source=struct.unpack_from('<H',data,0x2A)[0],owner=h.o.call32(0x22CC40,pointer),
                         special=h.o.call32(0x232730,pointer),recordSha256=hashlib.sha256(data).hexdigest()))
    return rows


def select_army(rows):
    candidates=[row for row in rows if 46<=row['owner']<=50 and row['special']==1 and 0<=row['source']<48400]
    if not candidates:raise ValueError('no actual valid minor-force army with original232730=true')
    return min(candidates,key=lambda row:row['slot'])


class GateObservers:
    def __init__(self,h,functions):
        self.h=h;self.o=h.o;self.pending={};self.events=[];self.homeChecks=[]
        self.branches={0x183B2D:(0x183A30,0x183B42,2),0x183B39:(0x183A30,0x183B8F,2),0x184FAD:(0x184EB0,0x184FF9,2)}
        for entry,instructions in functions.items():
            self.o.mu.hook_add(UC_HOOK_CODE,self.observe,entry,self.o.exe+entry,self.o.exe+max(i.address for i in instructions))

    def observe(self,uc,address,size,backend):
        pc=address-self.o.exe
        if backend in self.pending:
            row=self.pending.pop(backend);target,length=self.branches[row['rva']][1:]
            if pc not in (target,row['rva']+length):raise ValueError('unexpected native successor after observed branch')
            row.update(nextRva=pc,taken=pc==target);self.events.append(row)
        if pc in self.branches:
            owner=uc.reg_read(UC_X86_REG_EDI)&0xFFFFFFFF
            row=dict(backend=backend,rva=pc,owner=owner,army=self.o.q(uc.reg_read(UC_X86_REG_RSI)))
            if pc==0x183B39:row['specialReturn']=uc.reg_read(UC_X86_REG_EAX)&0xFFFFFFFF
            self.pending[backend]=row
        if pc in (0x183B42,0x184FAF):self.homeChecks.append(dict(backend=backend,rva=pc))


def build(snapshot):
    im=Image();im.md.detail=True
    anchors=anchored(im,0x183A30,{0x183B22:'cmp edi, 0x33',0x183B25:'je 0x183b2f',
        0x183B27:'lea eax, [rdi - 0x2e]',0x183B2A:'cmp eax, 4',0x183B2D:'ja 0x183b42',
        0x183B32:'call 0x232730',0x183B37:'test eax, eax',0x183B39:'je 0x183b8f',
        0x183B42:'mov rdx, qword ptr [rsi]',0x183B45:'movzx eax, word ptr [rdx + 0x12]'})
    anchors+=anchored(im,0x184EB0,{0x184FA2:'cmp edi, 0x33',0x184FA5:'je 0x184ff9',
        0x184FA7:'lea eax, [rdi - 0x2e]',0x184FAA:'cmp eax, 4',0x184FAD:'jbe 0x184ff9',
        0x184FAF:'movzx eax, word ptr [rdx + 0x12]'})
    game=FrozenImage(snapshot,im,False)
    if not game.pool_snapshot['valid']:raise ValueError('frozen list pool invalid')
    inputs=army_inputs(Harness(game));chosen=select_army(inputs)
    functions={entry:list(im.flow(entry)) for entry in (0x183A30,0x184EB0)}
    rows=[dict(rva=entry,source=chosen['source'],goals=[chosen['source']],options=0,limit=20000,
               context='army',clearBytes=48400,armyPointer=chosen['pointer']) for entry in functions]
    observed=[]
    def prepare(h):
        actual=next((row for row in army_inputs(h) if row['slot']==chosen['slot']),None)
        if actual!=chosen:raise ValueError('chosen original army identity changed')
        observer=GateObservers(h,functions);observed.append(observer)
        return dict(selectedArmy=actual,gates=observer.events,commanderHomeChecks=observer.homeChecks)
    result=verify(game,False,rows,prepare)
    result.update(snapshot=dict(file=str(snapshot),sha256=game.snapshot_sha256),gameAccess=False,gameWrites=0,
                  codeSource=game.code_source,originalAnchors=anchors,actualArmies=inputs,helperReplacements=[],
                  sourceFieldEdits=[],completed=not result['failures'] and result['clockStable'],
                  observerSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  scope='complete original source=goal searches isolate initialization; no claim about a nonzero-distance path or absent leader/facility inputs')
    if observed[0].pending:raise ValueError('native branch observation is incomplete')
    return result


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--snapshot',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=build(args.snapshot);data=(json.dumps(result,indent=2)+'\n').encode('utf-8')
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_bytes(data)
    print('selected original army',result['preparation']['selectedArmy'])
    print('native gates',result['preparation']['gates'],'home checks',result['preparation']['commanderHomeChecks'])
    if not result['completed']:raise SystemExit('faulted or unstable trial; diagnostic only')
    if args.vectors:args.vectors.write_bytes(gzip.compress(data,mtime=0))


if __name__=='__main__':main()
