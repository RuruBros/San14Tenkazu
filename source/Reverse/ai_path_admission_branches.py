# Full 18A080 city/gate/return branches, with native nested terrain and route calls.
# python ai_path_admission_branches.py [--smoke] [--vectors ../Tests/AiPathAdmissionBranchVectors.json.gz]
# Only copied data inputs change. No instructions, helper results or game bytes are replaced.
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
from itertools import product
from pathlib import Path
import struct
from ai_executor_audit import Harness
from ai_facility_special import read_clock, change_input
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace
from image import Image
from oracle import OracleError
from unicorn import UC_HOOK_CODE


def facts(h, tile, army, enforce, requested, count):
    o=h.o;ground=o.call(0x2318B0,tile) if tile else 0
    kind=o.call32(0x22E0D0,ground) if ground else 0
    valid=bool(o.call32(0x32F500,army))
    city=o.call(0x22A850 if kind==16 else 0x22CF90,tile) if kind in (16,17) else 0
    vc=bool(o.call32(0x32F500,city));center=o.call32(0x22D740,city) if vc else 0
    index=o.call32(0x22DDA0,tile) if tile else 48400
    f=dict(hasTile=tile!=0,hasGround=ground!=0,hasArmy=army!=0,terrain=kind,validArmy=valid,validCity=vc,
           atCenter=index==center,waypointsEnforced=enforce!=0,goalMatch=index in requested or center in requested,
           positiveGoalCount=count>0,armyAtCenter=False,attackOrder=False,sameTargetCity=False,distant=False,
           sameForce=False,firstRelayMissing=False,returnOrder=False,returnNeighbor=False,primaryAllowed=False,boatAllowed=False)
    if valid:
        data=o.read(army,0x50);hex_=int.from_bytes(data[0x2A:0x2C],'little');order=data[0x37]
        tk=data[0x3C] if data[0x3C]!=255 else data[0x38]
        tid=int.from_bytes(data[0x3E:0x40] if data[0x3C]!=255 else data[0x3A:0x3C],'little')
        target=o.q(h.raw+0xDD38+8*(tid if tid<=51 else 0))
        f.update(armyAtCenter=hex_==center,attackOrder=order in (14,16),sameTargetCity=tk==5 and target==city,
                 distant=bool(vc and o.call32(0x2AA850,center,hex_)>2),
                 sameForce=bool(vc and o.call(0x22C850,army)==o.call(0x22C990,city)),
                 firstRelayMissing=int.from_bytes(data[0x2C:0x2E],'little')==48400,returnOrder=order in (2,11))
    return f


def verify(game,smoke=False):
    before=read_clock(game);h=Harness(game);o=h.o;im=Image();cases=[];failures=[];surveys=[]
    army=h.valid_table(0x7E838,501)[-1];owner=o.call(0x22C850,army);force_id=o.call32(0x22DC80,owner)
    cities=[];gates=[]
    for i in range(1,62):
        city=o.call(0x32C130,h.raw,i)
        if not o.call32(0x32F500,city):continue
        center=o.call32(0x22D740,city);tile=o.q(h.raw+0xE270+8*center);ground=o.call(0x2318B0,tile)
        kind=o.call32(0x22E0D0,ground)
        row=dict(id=i,city=city,center=center,tile=tile,ground=ground,kind=kind,sameForce=o.call(0x22C990,city)==owner)
        surveys.append(row)
        if kind==16:cities.append(row)
        elif kind==17:gates.append(row)
    own=next(c for c in cities if c['sameForce']);foreign=next(c for c in cities if not c['sameForce'])
    im.md.detail=True
    requested=o.alloc(16);branch_ins={i.address:i for i in im.flow(0x18A080) if i.mnemonic.startswith('j') and i.mnemonic!='jmp'}
    visited=set();last=[None];edges=set()
    def coverage(uc,address,size,user):
        pc=address-o.exe;visited.add(pc)
        if last[0] in branch_ins:edges.add((last[0],pc))
        last[0]=pc
    handle=o.mu.hook_add(UC_HOOK_CODE,coverage,None,o.exe+0x18A080,o.exe+0x18A3BD)
    trace=Trace(o,{0x2C6640:None,0x2C6700:None,0x2C4D00:None,0x2C5070:None})
    def run(role,tile,unit,enforce,goals,count,inputs=(),requested_force=None):
        actual_force=force_id if requested_force is None else requested_force
        changes=[];row=dict(role=role,tile=tile,army=unit,enforce=enforce,goals=goals,count=count,requestedForce=actual_force,copiedInputChanges=changes)
        try:
            for a,v,s,label in inputs:change_input(h,changes,a,v,s,label)
            o.poke(requested,struct.pack('<4i',*(goals+[0]*(4-len(goals)))))
            row['facts']=facts(h,tile,unit,enforce,goals,count);trace.clear();last[0]=None
            row['native']=o.call32(0x18A080,unit,tile,actual_force,0,stack_args=(enforce,requested,count))
            row['events']=trace.events.copy()
            for r,key in [(0x2C6640,'primaryAllowed'),(0x2C6700,'primaryAllowed'),(0x2C4D00,'boatAllowed'),(0x2C5070,'returnNeighbor')]:
                matched=[e for e in trace.events if e['rva']==r]
                # A non-null army uses the outer 2C6640 result, even if it calls 2C6700 internally.
                if matched and not (r==0x2C6700 and unit):row['facts'][key]=matched[0]['result']!=0
            cases.append(row)
        except OracleError as error:
            row['events']=trace.events.copy();row['error']=str(error);failures.append(row)
        finally:
            for change in reversed(changes):o.poke(change['address'],change['before'].to_bytes(change['size'],'little'))
    try:
        for city in (own,foreign):
            positions=[city['center']]
            previous=city['center']
            for distance in range(1,4):
                choices=[o.call(0x2A76F0,previous,d) for d in range(1,7)]
                previous=next(o.call32(0x22DDA0,t) for t in choices if o.call32(0x32F500,t) and o.call32(0x2AA850,city['center'],o.call32(0x22DDA0,t))==distance)
                positions.append(previous)
            targets=[(0,0,255,0),(5,city['id'],255,0),(5,0,255,0),(0,0,5,city['id']),(5,city['id'],5,0)]
            waypoints=[(0,[],0),(1,[city['center']],1),(1,[48400],1),(1,[],0),(1,[],-1)]
            plans=product(positions,[0,2,11,14,16,255],targets,waypoints,[48400,city['center']])
            if smoke:plans=[(positions[1],2,targets[1],waypoints[0],48400),(positions[0],2,targets[1],waypoints[0],48400)]
            for pos,order,(pt,pi,st,si),(enforce,goals,count),relay in plans:
                inputs=[(city['ground']+0x2A,0,2,'copied terrain base movement cost forces the original primary helper to reject'),
                        (army+0x2A,pos,2,'copied current hex'),(army+0x2C,relay,2,'copied first relay'),
                        (army+0x37,order,1,'copied order'),(army+0x38,pt,1,'copied primary target kind'),
                        (army+0x3A,pi,2,'copied primary target ID'),(army+0x3C,st,1,'copied secondary target kind'),(army+0x3E,si,2,'copied secondary target ID')]
                run('cityFallback',city['tile'],army,enforce,goals,count,inputs)
            run('cityWithoutArmy',city['tile'],0,0,[],0,[(city['ground']+0x2A,0,2,'copied zero base movement cost')])
            run('invalidCity',city['tile'],army,1,[city['center']],1,[(city['ground']+0x2A,0,2,'copied zero base movement cost'),(city['tile']+0x1A,0,2,'copied dummy base index')])
            edge=o.q(h.raw+0xE270+8*positions[1])
            for order,relay,(enforce,goals,count),target in product([2,11,14],[48400,city['center']],[(0,[],0),(1,[city['center']],1),(1,[48400,city['center']],2),(1,[48400,48400],2)],[city['id'],65535]):
                run('cityEdgeReturn',edge,army,enforce,goals,count,
                    [(edge+0x10,16,1,'copied city terrain at an actual neighboring tile'),(edge+0x1A,city['id'],2,'copied city base index'),
                     (city['ground']+0x2A,0,2,'copied zero base movement cost'),(army+0x2A,positions[1],2,'copied neighboring army hex'),
                     (army+0x2C,relay,2,'copied first relay'),(army+0x37,order,1,'copied return or attack order'),
                     (army+0x38,5,1,'copied primary city target'),(army+0x3A,target,2,'copied city target ID'),(army+0x3C,255,1,'copied absent secondary target')])
        for gate in gates[:1]:
            neighbor=o.call(0x2A76F0,gate['center'],1)
            for tile,valid_base,unit,(enforce,goals,count) in product([gate['tile'],neighbor],[True,False],[0,army],[(0,[],0),(1,[gate['center']],1),(1,[48400],1),(1,[],-1)]):
                run('gateFallback',tile,unit,enforce,goals,count,[(tile+0x10,17,1,'copied gate terrain type'),(tile+0x1A,gate['id'] if valid_base else 0,2,'copied gate base index'),(gate['ground']+0x2A,0,2,'copied zero base movement cost')])
            for pos,goals in product([gate['center'],own['center']],[[48400,gate['center']],[48400,48400]]):
                run('gateGoalLoop',gate['tile'],army,1,goals,2,[(gate['ground']+0x2A,0,2,'copied zero base movement cost'),(army+0x2A,pos,2,'copied army hex at or away from the gate center')])
        run('nullTile',0,army,0,[],0)
        tile=own['tile'];run('nullGround',tile,army,0,[],0,[(h.raw+0x73860+16*8,0,8,'copied null ground definition pointer')])
        # Terrain 24's actual boat codes exercise both nested boat admission and the no-army tech gate.
        table=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8))
        water=next(t for t in table if t and game.read(t+0x10,1)==b'\x18');ground=o.call(0x2318B0,water)
        for code,unit in product([0,11,12,13,14,15,16,17,18,19,20,255],[0,army]):
            a=owner+0x15E if not unit else unit+0x1C
            run('waterBoatCode',water,unit,0,[],0,[(a,code,1,'copied force or army boat code'),(ground+0x2A,1,2,'copied positive water movement cost')])
        for id_ in [52,255]:run('waterForceFallback',water,0,0,[],0,[(ground+0x2A,1,2,'copied positive water movement cost')],requested_force=id_)
        run('cityWithoutArmyPositiveCost',own['tile'],0,0,[],0,[(own['ground']+0x2A,1,2,'copied positive city movement cost')])
        land=next((i,t) for i,t in enumerate(table) if t and game.read(t+0x10,1)!=b'\x18'
                  and o.call32(0x2C6700,i,0,force_id,0,stack_args=(0,)))
        run('landWithoutArmy',land[1],0,0,[],0)
    finally:trace.close();o.mu.hook_del(handle)
    after=read_clock(game)
    coverage_rows=[]
    for pc,ins in sorted(branch_ins.items()):
        target=ins.operands[0].imm;fall=pc+ins.size
        coverage_rows.append(dict(rva=pc,text=ins.mnemonic+' '+ins.op_str,taken=(pc,target) in edges,fallthrough=(pc,fall) in edges))
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,
                cases=cases,failures=failures,branchCoverage=coverage_rows,surveys=surveys,army=army,forceId=force_id,
                method='original full 18A080 and original nested helpers with logged copied data changes; branch edges observed, not forced; no game writes or hooks',
                listPoolSnapshot=game.pool_snapshot,oracle_sha256=hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),
                static={hex(r):im.flowtext(r) for r in (0x18A080,0x2C6640,0x2C6700,0x2C4D00,0x2C5070)})


def main():
    p=argparse.ArgumentParser();p.add_argument('--smoke',action='store_true');p.add_argument('--vectors',type=Path);args=p.parse_args()
    result=verify(ListPoolSnapshot(),args.smoke);folder=HERE/'.cache'/'ai_path_admission_branches';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']:args.vectors.write_bytes(gzip.compress(text.replace('\r\n','\n').replace('\n','\r\n').encode('utf-8'),mtime=0))
    complete=sum(r['taken'] and r['fallthrough'] for r in result['branchCoverage'])
    print(f"{len(result['cases'])} completed; {len(result['failures'])} faults; {complete}/{len(result['branchCoverage'])} conditional branches both ways; {path}")
    if not result['clockStable']:raise SystemExit('Game changed; diagnostics only')
if __name__=='__main__':main()
