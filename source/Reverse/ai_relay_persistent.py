# Repeated native executor calls preserve the factory-created state and all copied writes.
# python ai_relay_persistent.py [--vectors ../Tests/AiRelayPersistentVectors.json]
# Unit_MoveTick is also called in copies; no invented movement, construction completion or forced return.
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
from ai_executor_audit import Harness
from ai_facility_special import read_clock, change_input
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list
from image import Image
from oracle import OracleError

def choose(h):
    cities=[]
    for i in range(1,47):
        city=h.o.call(0x32C130,h.raw,i);force=h.o.call(0x22C990,city);area=h.o.call(0x229D40,city)
        if not h.o.call32(0x32F500,force) or not h.o.call32(0x32F500,area):continue
        center=int.from_bytes(h.o.read(area+0x3A,2),'little');owner=h.o.call32(0x22DC80,force)
        center_tile=h.o.q(h.raw+0xE270+8*center)
        if h.o.call32(0x32F500,center_tile) and h.o.read(center_tile+0x14,1)[0]==owner:
            cities.append(dict(id=i,city=city,area=area,center=center,owner=owner))
    choices=[]
    for group in h.valid_table(0x7F8D8,600):
        unit=h.o.call(0x22E1B0,group)
        if not h.o.call32(0x32F500,unit):continue
        owner=h.o.call32(0x22CEF0,group);hex_=int.from_bytes(h.o.read(unit+0x2A,2),'little')
        for city in cities:
            if owner!=city['owner']:continue
            d=h.o.call32(0x2AA850,hex_,city['center'])
            if d<11:choices.append((d,group,unit,city))
    if not choices:raise AssertionError('no actual nearby owned area')
    return min(choices,key=lambda c:(c[0],c[1],c[3]['id']))

def verify(game):
    before=read_clock(game);h=Harness(game);distance,group,unit,city=choose(h);answers=[];faults=[]
    enemies=[]
    for i in range(1,47):
        base=h.o.call(0x32C130,h.raw,i);force=h.o.call(0x22C990,base)
        if not h.o.call32(0x32F500,force):continue
        owner=h.o.call32(0x22DC80,force)
        if not h.o.call32(0x2CA280,city['owner'],owner):continue
        area=h.o.call(0x229D40,base)
        if not h.o.call32(0x32F500,area):continue
        center=int.from_bytes(h.o.read(area+0x3A,2),'little');tile=h.o.q(h.raw+0xE270+8*center)
        if not h.o.call32(0x32F500,tile):continue
        neighbors=[h.o.call(0x2A76F0,center,d) for d in range(1,7)]
        neighbor=next(n for n in neighbors if h.o.call32(0x32F500,n))
        enemies.append((h.o.call32(0x2AA850,int.from_bytes(h.o.read(unit+0x2A,2),'little'),center),i,center,h.o.call32(0x22DDA0,neighbor)))
    enemy=min(enemies)
    for name in ('CExecuteAreaOccupyNode','CExecuteAttackAreaBuildingNode'):
        variants=[([],0,100,False),([city['center']],0,100,False),
                  ([city['center']],1,100,False),([city['center']],1,100,True)]
        if name=='CExecuteAreaOccupyNode':variants += [([city['center']],1,0,False),([city['center']],1,0,True),
                                                       ([city['center']],1,-1,False),([city['center']],1,-1,True)]
        variants += [([enemy[2],enemy[3]],1,0,False),([enemy[2],enemy[3]],1,0,True)]
        for relays,enabled,threshold,advance in variants:
            h.setup(name,[0,threshold,h.keys[2]],2);h.number(h.keys[2],enabled);state=h.state();changes=[]
            target=enemy[1] if relays and relays[0]==enemy[2] else city['id']
            for addr,val,size,role in ((group+0x14,3,2,'purpose'),(group+0x16,target,2,'target base')):
                change_input(h,changes,addr,val,size,role+' in copied input')
            for i in range(10):change_input(h,changes,group+0x18+2*i,relays[i] if i<len(relays) else 48400,2,'relay in copied input')
            # Actual group membership is obtained by the native getter, not guessed group offsets.
            from ai_facility_special import native_list
            member_list=native_list(h,1,1);h.o.call(0x22A110,group,member_list,keep=True)
            members=linked_list(h.o,member_list)
            trace=Trace(h.o,{0x229EA0:None,0x22DDA0:None,0x723E0:None,0x70830:None,0xCB0D0:None,0xB9070:3,
                            0x2EAAE0:None,0x2EAB40:None,0x2EE010:None,0x2EE1E0:None,
                            0x3011A0:None,0x2DA250:None})
            trial=dict(className=name,relays=relays,enabled=enabled,threshold=threshold,advance=advance,statePointer=state,
                       copiedInputChanges=changes,initialState=h.o.read(state,40).hex(),frames=[])
            try:
                for tick in range(64 if advance and target==enemy[1] else 4):
                    if read_clock(game)!=before:raise AssertionError('game changed during copied execution')
                    frame=dict(tick=tick,stateBefore=h.o.read(state,40).hex(),unitBefore=h.o.read(unit,0x160).hex())
                    # B254C0 consumes the +38 override once. Rebind the same retained state for each call,
                    # as an input binding only; do not reset or advance any state byte.
                    h.o.poke(h.context+0x38,struct.pack('<Q',state))
                    trace.events.clear();frame['status']=h.execute(group)
                    frame['stateAfter']=h.o.read(state,40).hex();frame['events']=trace.events.copy()
                    trial['frames'].append(frame)
                    if advance:
                        frame['movement']=[]
                        for member in members:
                            move=dict(member=member,before=h.o.read(member,0x160).hex())
                            try:
                                trace.clear()
                                h.o.call(0x2D95F0,member,keep=True)
                                move['after']=h.o.read(member,0x160).hex()
                                move['events']=trace.events.copy()
                            except OracleError as error:
                                move['error']=str(error);frame['movement'].append(move);raise
                            frame['movement'].append(move)
                answers.append(trial)
            except OracleError as error:
                trial['error']=str(error);faults.append(trial)
            finally:trace.close()
            print(name,relays,enabled,threshold,advance,[f['status'] for f in trial['frames']],trial.get('error','complete'),flush=True)
    after=read_clock(game);im=Image()
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,
                group=group,unit=unit,city=city,enemy=enemy,distance=distance,listPoolSnapshot=game.pool_snapshot,
                oracle_sha256=hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),cases=answers,failures=faults,
                method='factory state initialized once; each call binds the same state through the one-shot context+38 override consumed by B254C0; 4 or 64 full native ticks with retained writes; optional full Unit_MoveTick between ticks; no game writes, hooks, teleport or forced result',
                static={hex(r):im.flowtext(r) for r in (0x65E30,0x66C20,0x723E0,0x70830,0x2D95F0,0xB254C0)})

def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);args=p.parse_args();result=verify(ListPoolSnapshot())
    folder=HERE/'.cache'/'ai_relay_persistent';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result)
    path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} completed trials; {len(result['failures'])} faults; {path}")
    if not result['clockStable']:raise SystemExit('Game clock changed; diagnostics only')
if __name__=='__main__':main()
