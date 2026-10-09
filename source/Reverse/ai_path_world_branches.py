# Whole native path searches over real multi-tile routes, armies and exclusion lists.
# python ai_path_world_branches.py [--smoke] [--vectors ../Tests/AiPathWorldBranchVectors.json.gz]
# Actual native list allocators run only inside Oracle; the game is opened for reads.
import argparse
from datetime import datetime, timezone
import gzip
from pathlib import Path
import struct

from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_path_search_audit import verify


def configurations(game,facilities=False):
    before=read_clock(game);h=Harness(game);o=h.o
    tiles=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8));rows=[];sites=[]
    def add(r,options,kind,source,goals,**extra):
        rows.append(dict(rva=r,options=options,context=kind,clearBytes=48400,source=source,goals=goals,**extra))
    for direction in (1,3,5):
        target=12780
        for distance in range(1,13):
            target=o.call32(0x2A7720,target,direction)
            if not 0<=target<48400 or not tiles[target]:break
            if distance not in (2,6,12):continue
            sites.append(dict(source=12780,target=target,direction=direction,steps=distance))
            for r in (0x183A30,0x184EB0):
                for kind in ('neutral','force','army'):
                    for options in (1,5,65):add(r,options,kind,12780,[target],routeSteps=distance,direction=direction)
    armies=[]
    for army in h.valid_table(0x7E838,501):
        force=o.call32(0x22CC40,army);leader=struct.unpack('<H',o.read(army+0x12,2))[0]
        skill=o.call32(0x2C3780,army,102);special=o.call32(0x232730,army)
        record=dict(pointer=army,force=force,leader=leader,skill102=skill,special=special)
        armies.append(record)
    profiles={}
    for a in armies:profiles.setdefault((a['force'],a['leader']>6000,bool(a['skill102']),bool(a['special'])),a)
    for a in profiles.values():
        for r in (0x183A30,0x184EB0):
            for options in (0,1,5,65):add(r,options,'army',12780,[12781],armyPointer=a['pointer'],armyProfile=a)
    for r in (0x183A30,0x184EB0):
        for options in (1,65):
            for excluded in ([],[12780],[12781],[12782],[12782,12780],[12780,12782]):
                add(r,options,'neutral',12780,[12781],excludedHexes=excluded)
    for r in (0x183A30,0x184EB0):
        for options in (1,65):
            # 12779 is an actual intermediate node returned in the earlier complete run.
            for excluded in ([],[12780],[12558],[12779],[12779,12780],[12780,12779]):
                add(r,options,'neutral',12780,[12558],excludedHexes=excluded)
    facility_sites={}
    if facilities:
        for tile_id,tile in enumerate(tiles):
            if not tile:continue
            prefix=game.read(tile,0x20);building_id=struct.unpack_from('<H',prefix,0x1A)[0] # Original 22FB10.
            if not 1<=building_id<=3000:continue
            building=game.q(h.raw+0x6DA98+building_id*8)
            if not building or not o.call32(0x32F500,building):continue
            definition=o.call(0xEE90,building);category=o.read(definition+0x2C,1)[0]
            key=(o.call32(0x232650,building),o.call32(0x232BD0,building),category)
            if key in facility_sites:continue
            goal=o.call32(0x2A7720,tile_id,1);source=o.call32(0x2A7720,goal,1)
            if not (0<=source<48400 and 0<=goal<48400 and tiles[source] and tiles[goal]):continue
            facility_sites[key]=dict(hex=tile_id,buildingId=building_id,building=building,definition=definition,category=category,city=key[0],gate=key[1],source=source,target=goal)
        for site in facility_sites.values():
            for r in (0x183A30,0x184EB0):
                for kind in ('neutral','army'):
                    for options in (1,5,65):add(r,options,kind,site['source'],[site['target']],facilitySite=site)
    if read_clock(game)!=before:raise AssertionError('Game changed during route/army selection')
    return rows,dict(clock=before,routeSites=sites,armies=armies,armyProfiles=list(profiles.values()),facilitySites=list(facility_sites.values()))


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--smoke',action='store_true');parser.add_argument('--facilities',action='store_true');parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    game=ListPoolSnapshot();rows,selection=configurations(game,args.facilities)
    if args.smoke:rows=[r for r in rows if 'excludedHexes' in r][:6]+rows[:2]
    print('selected',len(rows),'searches,',len(selection['armyProfiles']),'actual army profiles',flush=True)
    result=verify(game,False,rows);result['worldSelection']=selection
    folder=HERE/'.cache'/'ai_path_world_branches';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable'] and not result['failures']:args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(len(result['cases']),'completed;',len(result['failures']),'faults;',path)
    for backend,branches in result['branchCoverage'].items():print(backend,sum(b['taken'] and b['fallthrough'] for b in branches),'/',len(branches),'conditions with both edges')
    if not result['clockStable'] or result['failures']:raise SystemExit('Game changed or native faults; diagnostics only')


if __name__=='__main__':main()
