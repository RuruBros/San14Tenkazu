# Whole gated A* with native pool allocation exhaustion, only in copied memory.
# python ai_path_pool_branches.py [--stage unlocked|indices|nodes] [--vectors ../Tests/AiPathPoolBranchVectors.json.gz]
# No forced helper return, changed list headers, game writes or game hooks.
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
from pathlib import Path
import struct

from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_path_global_branch import capture_global
from ai_path_search_audit import verify
from game import Game


def pool_state(o,base):
    return dict(head=o.q(base+0x10),tail=o.q(base+0x18),freeHead=o.q(base+0x20),active=o.q(base+0x30),capacity=o.q(base+0x38),orderedCache=o.q(base+0x28))


def prepare(h,stage):
    o=h.o;indices=o.exe+0x27D1128;nodes=o.exe+0x27D10E0
    before=dict(indices=pool_state(o,indices),nodes=pool_state(o,nodes));allocated=[];digest=hashlib.sha256();holder=None
    if stage=='indices':
        expected=before['indices']['capacity']-before['indices']['active']
        for _ in range(expected):
            pointer=o.call(0x18B60,indices,0,0,keep=True)
            if not pointer:raise AssertionError('index allocator returned null before its genuine free list ended')
            allocated.append(pointer);digest.update(struct.pack('<Q',pointer))
            if len(allocated)%4096==0:print(stage,len(allocated),'/',expected,flush=True)
        exhausted_return=o.call(0x18B60,indices,0,0,keep=True)
        if exhausted_return:raise AssertionError('index allocator still had an entry after capacity allocations')
    elif stage=='nodes':
        holder=o.call(0x18B60,indices,0,0,keep=True)
        if not holder:raise AssertionError('cannot allocate a genuine holder index')
        index=struct.unpack('<I',o.read(holder,4))[0];tile=o.q(h.raw+0xE270+12780*8)
        expected=before['nodes']['capacity']-before['nodes']['active']
        for _ in range(expected):
            pointer=o.call(0x18DF0,nodes,index,1,keep=True)
            if not pointer:raise AssertionError('node allocator returned null before its genuine free list ended')
            o.poke(pointer,struct.pack('<Q',tile));allocated.append(pointer);digest.update(struct.pack('<Q',pointer))
            if len(allocated)%4096==0:print(stage,len(allocated),'/',expected,flush=True)
        exhausted_return=o.call(0x18DF0,nodes,index,1,keep=True)
        if exhausted_return:raise AssertionError('node allocator still had an entry after capacity allocations')
        head=o.q(o.q(nodes+0x10)+index*8);tail=o.q(o.q(nodes+0x18)+index*8);count=o.q(o.q(nodes+0x28)+index*8)
        seen=set();current=head;previous=0
        while current:
            if current in seen or o.q(current+0x10)!=previous or o.q(current)!=tile:raise AssertionError('native exhausted holder list is invalid')
            seen.add(current);previous=current;current=o.q(current+8)
        if previous!=tail or len(seen)!=count or count!=expected:raise AssertionError('native holder list count/tail differs')
    else:exhausted_return=None
    after=dict(indices=pool_state(o,indices),nodes=pool_state(o,nodes))
    seen=set();ids=set();current=after['indices']['head'];previous=0
    while current:
        index=struct.unpack('<I',o.read(current,4))[0]
        if current in seen or index in ids or index>=after['indices']['capacity'] or o.q(current+0x10)!=previous:raise AssertionError('native active index list is invalid')
        seen.add(current);ids.add(index);previous=current;current=o.q(current+8)
    if previous!=after['indices']['tail'] or len(seen)!=after['indices']['active']:raise AssertionError('native active index list count/tail differs')
    if stage in ('indices','nodes'):
        if after[stage]['freeHead'] or after[stage]['active']!=after[stage]['capacity']:raise AssertionError('native exhaustion did not reach the complete pool')
    print('prepared',stage,'genuine allocations',len(allocated),flush=True)
    return dict(stage=stage,before=before,after=after,allocations=len(allocated),allocationSha256=digest.hexdigest(),firstPointers=allocated[:3],lastPointers=allocated[-3:],holderIndexPointer=holder,exhaustedReturn=exhausted_return,validatedActiveIndices=len(seen))


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--stage',choices=('unlocked','indices','nodes'));parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    stages=[]
    for stage in ([args.stage] if args.stage else ('unlocked','indices','nodes')):
        game=ListPoolSnapshot();clock=read_clock(game);global_context=capture_global(game)
        flags_before={hex(r):Game.read(game,game.exe+r,4).hex() for r in (0x2141A84,0x27D10B0)}
        rows=[dict(rva=0x183A30,options=options,context=kind,clearBytes=48400,goals=goals,imageFields=[(0x2141A84,4,1),(0x27D10B0,4,0 if stage=='unlocked' else 1)],tracedHelpers=[0x185B30])
            for kind in ('neutral','force','army') for options in (0,1) for goals in ([12780],[12781])]
        result=verify(game,False,rows,prepare=lambda h:prepare(h,stage));result['stage']=stage;result['globalContextSnapshot']=global_context
        result['liveFlagsBefore']=flags_before;result['liveFlagsAfter']={hex(r):Game.read(game,game.exe+r,4).hex() for r in (0x2141A84,0x27D10B0)}
        if clock!=result['clockBefore'] or result['liveFlagsBefore']!=result['liveFlagsAfter']:raise AssertionError('Game changed during native resource trials')
        stages.append(result)
    result=dict(stages=stages,clockStable=all(s['clockStable'] and s['clockBefore']==stages[0]['clockBefore'] for s in stages),method='complete original backend; native allocators exhaust actual copied free lists; no list header edits or forced results')
    folder=HERE/'.cache'/'ai_path_pool_branches';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable'] and not any(s['failures'] for s in stages):args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(sum(len(s['cases']) for s in stages),'completed;',sum(len(s['failures']) for s in stages),'faults;',path)
    for s in stages:print(s['stage'],sum(b['taken'] and b['fallthrough'] for b in s['branchCoverage']['0x183a30']),'conditions with both edges')
    if not result['clockStable'] or any(s['failures'] for s in stages):raise SystemExit('Game changed or native faults; diagnostics only')


if __name__=='__main__':main()
