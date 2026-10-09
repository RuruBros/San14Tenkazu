# Full native searches on admissible actual routes to observe existing-node improvements.
# python ai_path_reinsert_branches.py [--vectors ../Tests/AiPathReinsertVectors.json.gz]
# No tile/army changes, queue seeding, helper substitution or game writes.
import argparse
from datetime import datetime, timezone
import gzip
from pathlib import Path
import struct
import time
from game import Game
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_path_search_audit import verify


def configurations(game):
    before=read_clock(game);h=Harness(game);o=h.o;tiles=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8))
    profiles={}
    for army in h.valid_table(0x7E838,501):
        owner=o.call32(0x22CC40,army);source=struct.unpack('<H',o.read(army+0x2A,2))[0]
        if owner not in (3,28,51) or owner in profiles or not 0<=source<48400 or not tiles[source]:continue
        profiles[owner]=dict(pointer=army,owner=owner,source=source)
    rows=[];routes=[]
    for profile in profiles.values():
        source=profile['source'];army=profile['pointer']
        for direction in range(1,7):
            goal=source;path=[source]
            for steps in range(1,25):
                goal=o.call32(0x2A7720,goal,direction)
                if not 0<=goal<48400 or not tiles[goal] or o.call32(0x2C6640,goal,0,army,0)!=1:break
                path.append(goal)
                if steps not in (6,12,24):continue
                route=dict(army=profile,direction=direction,steps=steps,path=path.copy());routes.append(route)
                for rva in (0x183A30,0x184EB0):
                    for options in (0,1,64,65):
                        limits=(42,20000) if rva==0x184EB0 else (20000,)
                        for limit in limits:
                            rows.append(dict(rva=rva,options=options,context='army',armyPointer=army,clearBytes=48400,source=source,goals=[goal],limit=limit,
                                             actualRoute=route,tracedHelpers=[0x18AE60]))
    if before!=read_clock(game):raise AssertionError('Game changed during route selection')
    return rows,dict(clock=before,profiles=list(profiles.values()),routes=routes)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    folder=HERE/'.cache'/'ai_path_reinsert_branches';folder.mkdir(parents=True,exist_ok=True)
    g=Game();first=read_clock(g);time.sleep(3);second=read_clock(g);pre=dict(first=first,second=second,stable=first==second)
    (folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'-preflight.json')).write_text(vectors_text(pre),encoding='utf-8')
    if not pre['stable']:raise SystemExit('Game changed; no native copy started')
    g=ListPoolSnapshot();rows,selection=configurations(g);print('actual admissible routes',len(selection['routes']),'searches',len(rows),flush=True)
    result=verify(g,False,rows);result['routeSelection']=selection
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable'] and not result['failures']:args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(len(result['cases']),'completed;',len(result['failures']),'faults;',path)
    for backend,branches in result['branchCoverage'].items():print(backend,sum(b['taken'] and b['fallthrough'] for b in branches),'/',len(branches),'conditions with both edges')
    if not result['clockStable'] or result['failures']:raise SystemExit('Game changed or native faults; diagnostics only')


if __name__=='__main__':main()
