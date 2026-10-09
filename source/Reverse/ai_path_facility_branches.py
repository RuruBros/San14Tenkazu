# Whole original A* searches around actual non-city/gate facilities in a paused world.
# python ai_path_facility_branches.py [--vectors ../Tests/AiPathFacilityBranchVectors.json.gz]
# Real tiles/buildings/armies remain unchanged; context and options are scratch inputs.
import argparse
from collections import Counter
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
    before=read_clock(game);h=Harness(game);o=h.o
    tiles=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8));armies={};profiles=[]
    for army in h.valid_table(0x7E838,501):
        owner=o.call32(0x22CC40,army);armies.setdefault(owner,army)
        profiles.append(dict(pointer=army,force=owner,leader=struct.unpack('<H',o.read(army+0x12,2))[0],special=o.call32(0x232730,army)))
    inventory=[];selected={}
    for hex_,tile in enumerate(tiles):
        if not tile:continue
        building_id=struct.unpack('<H',game.read(tile+0x1A,2))[0]
        if not 1<=building_id<=3000:continue
        building=o.call(0x22FB10,tile)
        if not building or o.call32(0x32F500,building)!=1:continue
        city=o.call32(0x232650,building);gate=o.call32(0x232BD0,building)
        if city or gate:continue
        definition=o.call(0xEE90,building);category=o.read(definition+0x2C,1)[0];owner=o.read(building+0x13,1)[0]
        site=dict(hex=hex_,tile=tile,buildingId=building_id,building=building,definition=definition,category=category,owner=owner,
                  city=city,gate=gate,definitionIndex=o.read(building+0x12,1)[0])
        inventory.append(site)
        if category in selected or len(armies)<2:continue
        neighbors=[n for d in range(1,7) if 0<=(n:=o.call32(0x2A7720,hex_,d))<48400 and tiles[n]]
        if len(neighbors)<2:continue
        # The facility is a non-source/non-goal neighbor of the actual source.
        selected[category]=dict(site,source=neighbors[0],target=neighbors[3] if len(neighbors)>3 else neighbors[1])
    rows=[]
    for site in selected.values():
        matching=armies.get(site['owner']);other=next(a for f,a in armies.items() if f!=site['owner'])
        contexts=[('neutral','neutral',other),('force','other',other),('army','other',other)]
        if matching:contexts += [('force','same',matching),('army','same',matching)]
        for kind,relation,army in contexts:
            force=o.call32(0x22CC40,army)
            for rva in (0x183A30,0x184EB0):
                for options in (0,1,5,9,64,65):
                    rows.append(dict(rva=rva,options=options,context=kind,clearBytes=48400,source=site['source'],goals=[site['target']],armyPointer=army,
                                     armyOwner=force,ownerRelation=relation,facilitySite=site,tracedHelpers=[0x18A080,0x22FB10,0x232650,0x232BD0,0xEE90,0x1864F0,0x2C6850]))
    # Exercise the available actual force-46..51/leader-over-6000 profiles without changing army fields.
    boundary_profiles={}
    for profile in profiles:
        if 46<=profile['force']<=51 or profile['leader']>6000:
            boundary_profiles.setdefault((profile['force'],profile['leader']>6000,bool(profile['special'])),profile)
    site=next(iter(selected.values()))
    for profile in boundary_profiles.values():
        for rva in (0x183A30,0x184EB0):
            for options in (0,1,65):
                rows.append(dict(rva=rva,options=options,context='army',clearBytes=48400,source=site['source'],goals=[site['target']],armyPointer=profile['pointer'],
                                 armyOwner=profile['force'],ownerRelation='boundary',facilitySite=site,armyProfile=profile,
                                 tracedHelpers=[0x18A080,0x22FB10,0x232650,0x232BD0,0xEE90,0x1864F0,0x2C6850,0x232730]))
    if before!=read_clock(game):raise AssertionError('Game changed during facility/army selection')
    if not selected:raise AssertionError('No linked actual facilities with suitable owners and neighbors')
    return rows,dict(clock=before,inventory=inventory,categoryCounts=dict(Counter(s['category'] for s in inventory)),sites=list(selected.values()),armies=profiles,boundaryProfiles=list(boundary_profiles.values()))


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    folder=HERE/'.cache'/'ai_path_facility_branches';folder.mkdir(parents=True,exist_ok=True)
    game=Game();before=read_clock(game);time.sleep(3);after=read_clock(game)
    pre=dict(first=before,second=after,stable=before==after)
    stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ');(folder/(stamp+'-preflight.json')).write_text(vectors_text(pre),encoding='utf-8')
    if not pre['stable']:raise SystemExit('Game changed; no native copy started')
    game=ListPoolSnapshot();rows,selection=configurations(game)
    print('selected',len(rows),'searches; actual facility categories',selection['categoryCounts'],'sites',selection['sites'],flush=True)
    result=verify(game,False,rows);result['facilitySelection']=selection
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable'] and not result['failures']:args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(len(result['cases']),'completed;',len(result['failures']),'faults;',path)
    for backend,branches in result['branchCoverage'].items():print(backend,sum(b['taken'] and b['fallthrough'] for b in branches),'/',len(branches),'conditions with both edges')
    if not result['clockStable'] or result['failures']:raise SystemExit('Game changed or native faults; diagnostics only')


if __name__=='__main__':main()
