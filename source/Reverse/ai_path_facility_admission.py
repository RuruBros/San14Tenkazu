# Original tile admission across every linked non-city/gate facility and actual army.
# python ai_path_facility_admission.py
# Inputs and functions are executed only in Oracle, with no game writes or forced results.
from collections import Counter
from datetime import datetime, timezone
import struct
import time
from game import Game
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot


def main():
    folder=HERE/'.cache'/'ai_path_facility_admission';folder.mkdir(parents=True,exist_ok=True)
    game=Game();first=read_clock(game);time.sleep(3);second=read_clock(game)
    if first!=second:raise SystemExit('Game changed; no native copy started')
    game=ListPoolSnapshot();before=read_clock(game);h=Harness(game);o=h.o;points=o.alloc(4)
    armies=h.valid_table(0x7E838,501);tiles=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8));sites=[];rows=[]
    for hex_,tile in enumerate(tiles):
        if not tile:continue
        index=struct.unpack('<H',game.read(tile+0x1A,2))[0]
        if not 1<=index<=3000:continue
        building=o.call(0x22FB10,tile)
        if not building or o.call32(0x32F500,building)!=1 or o.call32(0x232650,building) or o.call32(0x232BD0,building):continue
        definition=o.call(0xEE90,building);terrain=o.call(0x2318B0,tile)
        neighbor=next((n for d in range(1,7) if 0<=(n:=o.call32(0x2A7720,hex_,d))<48400 and tiles[n]),None)
        if neighbor is None:raise AssertionError('linked facility has no actual neighbor')
        site=dict(hex=hex_,tile=tile,buildingId=index,building=building,category=o.read(definition+0x2C,1)[0],terrainCode=o.call32(0x22E0D0,terrain),neighbor=neighbor)
        sites.append(site)
        for army in [0,*armies]:
            force=o.call32(0x22CC40,army) if army else 0
            for flag in (0,1):
                for goal in (hex_,neighbor):
                    o.poke(points,struct.pack('<i',goal))
                    native=o.call32(0x18A080,army,tile,force,0,stack_args=(flag,points,1))
                    rows.append(dict(site=index,army=army,force=force,contextFlag=flag,goal=goal,native=native))
        if before!=read_clock(game):raise AssertionError('Game changed during facility admission matrix')
    after=read_clock(game);by_index={s['buildingId']:s for s in sites}
    admitted=[r for r in rows if r['native'] and by_index[r['site']]['category'] in (2,3)]
    result=dict(clockBefore=before,clockAfter=after,clockStable=before==after,sites=sites,armies=armies,cases=rows,admittedCategory2Or3=admitted,listPoolSnapshot=game.pool_snapshot,
                method='complete original 18A080 with mask64=0, real facility and army objects, scratch flag0/1 and goal candidate/neighbor; no forced returns or game writes')
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(vectors_text(result),encoding='utf-8')
    print('sites',len(sites),'armies',len(armies),'cases',len(rows),'category2/3 admitted',len(admitted),'terrain/category',dict(Counter((s['category'],s['terrainCode']) for s in sites)),path,flush=True)
    if not result['clockStable']:raise SystemExit('Game changed; diagnostics only')


if __name__=='__main__':main()
