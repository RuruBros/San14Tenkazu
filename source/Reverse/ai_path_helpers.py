# Original path admission and cached mobility cost, independent of full map search.
# python ai_path_helpers.py [--vectors ../Tests/AiPathHelperVectors.json]
import argparse
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import struct
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace
from image import Image
from oracle import OracleError
from unicorn.x86_const import UC_X86_REG_RSP, UC_X86_REG_XMM0

class FloatTrace(Trace):
    def leave(self,uc,address,size,user):
        pending=self.pending.get(address,[])
        e=pending[-1] if pending and uc.reg_read(UC_X86_REG_RSP)==pending[-1]['rsp']+8 else None
        super().leave(uc,address,size,user)
        if e and e['rva']==0x2B0290:e['floatResult']=struct.unpack('<f',struct.pack('<I',uc.reg_read(UC_X86_REG_XMM0)&0xFFFFFFFF))[0]

def verify(game):
    before=read_clock(game);h=Harness(game);o=h.o;cases=[];failures=[];im=Image()
    army=h.valid_table(0x7E838,501)[-1]
    pointer_table=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8));tiles={}
    for i,tile in enumerate(pointer_table):
        if not tile:continue
        kind=game.read(tile+0x10,1)[0]
        if kind not in tiles and o.call32(0x32F500,tile):tiles[kind]=(i,tile)
    goals=o.alloc(16)
    for kind,(index,tile) in tiles.items():
        for unit in (0,army):
            for option in (0,64):
                for enforce,requested in ((0,[]),(1,[index]),(1,[0]),(1,[])):
                    o.poke(goals,struct.pack('<4i',*(requested+[0]*(4-len(requested)))))
                    ground=o.call(0x2318B0,tile);gkind=o.call32(0x22E0D0,ground)
                    valid_army=bool(o.call32(0x32F500,unit))
                    city=o.call(0x22A850 if gkind==16 else 0x22CF90,tile) if gkind in (16,17) else 0
                    city_valid=bool(o.call32(0x32F500,city));center=o.call32(0x22D740,city) if city_valid else 0
                    facts=dict(hasTile=True,hasGround=bool(ground),hasArmy=unit!=0,terrain=gkind,validArmy=valid_army,
                        validCity=city_valid,atCenter=index==center,waypointsEnforced=enforce!=0,
                        goalMatch=index in requested or center in requested,positiveGoalCount=len(requested)>0,
                        armyAtCenter=False,attackOrder=False,sameTargetCity=False,distant=False,sameForce=False,
                        firstRelayMissing=False,returnOrder=False,returnNeighbor=False)
                    if valid_army:
                        data=o.read(unit,0x50);hex_=int.from_bytes(data[0x2A:0x2C],'little');order=data[0x37]
                        target_kind=data[0x3C] if data[0x3C]!=255 else data[0x38]
                        target=int.from_bytes(data[0x3E:0x40] if data[0x3C]!=255 else data[0x3A:0x3C],'little')
                        target_city=o.q(h.raw+0xDD38+8*(target if target<=51 else 0))
                        facts.update(armyAtCenter=hex_==center,attackOrder=order in (14,16),sameTargetCity=target_kind==5 and target_city==city,
                                     distant=bool(city_valid and o.call32(0x2AA850,center,hex_)>2),
                                     sameForce=bool(city_valid and o.call(0x22C850,unit)==o.call(0x22C990,city)),
                                     firstRelayMissing=int.from_bytes(data[0x2C:0x2E],'little')==48400,returnOrder=order in (2,11))
                    trace=Trace(o,{0x2C6640:None,0x2C6700:None,0x2C4D00:None,0x2C5070:None})
                    row=dict(kind='admission',hex=index,option=option,goals=requested,facts=facts)
                    try:
                        row['native']=o.call32(0x18A080,unit,tile,29,option,stack_args=(enforce,goals,len(requested)))
                        row['events']=trace.events.copy();cases.append(row)
                        primary=next((e['result']!=0 for e in trace.events if e['rva'] in (0x2C6640,0x2C6700)),False)
                        boat=next((e['result']!=0 for e in trace.events if e['rva']==0x2C4D00),False)
                        route=next((e['result']!=0 for e in trace.events if e['rva']==0x2C5070),False)
                        facts.update(primaryAllowed=primary,boatAllowed=boat,returnNeighbor=route)
                    except OracleError as error:row['error']=str(error);failures.append(row)
                    finally:trace.close()
    # Actual std::map constructor used by 183A30 at 183C30, with native cached-value insertion.
    for definition_kind in (0,2,9,19):
        land=o.q(h.raw+0x77428+8*definition_kind);boat=o.q(h.raw+0x77428+8*9)
        cache=o.alloc(16);head=o.call(0x18DD40,cache,keep=True);o.poke(cache,struct.pack('<QQ',head,0));factors={}
        for tile in (tiles[next(iter(tiles))][1],pointer_table[12781]):
            for cost in (-2147483648,-1,0,1,299,300,2147483647):
                for options in (0,1,2,3,4,5,64,65,255):
                    trace=FloatTrace(o,{0x2B0290:None});row=dict(kind='cost',cost=cost,options=options,definition=definition_kind,tile=tile,hasTile=True,hasArmy=True)
                    try:
                        row['native']=o.call32(0x1864F0,cost,options,tile,army,stack_args=(land,boat,cache),keep=True)
                        row['events']=trace.events.copy()
                        ground=o.call(0x2318B0,tile)
                        if row['events']:factors[ground]=row['events'][0]['floatResult']
                        row['factor']=factors.get(ground,1.0);cases.append(row)
                    except OracleError as error:row['error']=str(error);failures.append(row)
                    finally:trace.close()
    for tile,unit in ((0,army),(pointer_table[12781],0)):
        for cost in (-1,0,300,2147483647):
            actual=o.call32(0x1864F0,cost,1,tile,unit)
            cases.append(dict(kind='cost',cost=cost,options=1,hasTile=tile!=0,hasArmy=unit!=0,factor=1.0,native=actual,events=[]))
    after=read_clock(game)
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,
                terrainKinds=sorted(tiles),cases=cases,failures=failures,listPoolSnapshot=game.pool_snapshot,
                method='full native path admission and mobility cost helper; native map construction and cached factor insertion; copied pages only, no helper replacement or forced results',
                oracle_sha256=hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),
                static={hex(r):im.flowtext(r) for r in (0x18A080,0x1864F0,0x18DD40,0x2318B0,0x22E0D0)})

def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);args=p.parse_args();result=verify(ListPoolSnapshot())
    folder=HERE/'.cache'/'ai_path_helpers';folder.mkdir(parents=True,exist_ok=True);path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json')
    text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} completed; {len(result['failures'])} faults; terrain kinds {result['terrainKinds']}; {path}")
    if not result['clockStable']:raise SystemExit('Game clock changed; diagnostics only')
if __name__=='__main__':main()
