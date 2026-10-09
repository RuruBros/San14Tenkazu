# Broaden whole original path searches using actual paused-game input copied by Oracle.
# python ai_path_search_branches.py [--smoke] [--vectors ../Tests/AiPathSearchBranchVectors.json.gz]
# Every changed input is confined to copied memory and restored; no game writes/hooks.
import argparse
from datetime import datetime, timezone
import gzip
from pathlib import Path
import struct

from ai_executor_audit import Harness
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_path_search_audit import verify


def configurations():
    rows=[]
    def add(r,options,kind,goals,**extra):
        rows.append(dict(rva=r,options=options,context=kind,clearBytes=48400,goals=goals,**extra))
    for r in (0x183A30,0x184EB0):
        for kind in ('neutral','force','army'):
            for options in (0,1,2,3,4,5,8,9,12,16,17,32,33,64,65,255):
                for goals in ([],[12780],[12781],[12781,12782]):add(r,options,kind,goals)
        for offset in (0x10,0x18,0x1C,0x28,0x2C):
            for kind in ('neutral','force','army'):
                for options in (0,8,64):add(r,options,kind,[12781],contextWords=[(offset,1)])
        for offset in (0x1B,0x1C):
            for value in (0,2,9,19,20,21,255):add(r,1,'army',[12781],armyFields=[(offset,1,value)])
        if r==0x184EB0:
            for kind in ('neutral','force','army'):
                for options in (0,1,5):
                    for limit in (-1,0,14,15,299,300,9999,10000,20000):add(r,options,kind,[12781],limit=limit)
    return rows


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--smoke',action='store_true');parser.add_argument('--terrain',action='store_true');parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    game=ListPoolSnapshot()
    rows=configurations()
    if args.smoke:rows=[r for r in rows if r['context']=='army' and r['options']==1 and not r.get('armyFields')][:4]
    if args.terrain:
        h=Harness(game);pointers=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8));sites={}
        for target,tile in enumerate(pointers):
            if not tile:continue
            kind=game.read(tile+0x10,1)[0]
            if kind in sites or not h.o.call32(0x32F500,tile):continue
            source=next((s for direction in range(1,7) if 0<=(s:=h.o.call32(0x2A7720,target,direction))<48400 and pointers[s]),None)
            if source is not None:sites[kind]=(source,target)
        for terrain,(source,target) in sorted(sites.items()):
            for r in (0x183A30,0x184EB0):
                for kind in ('neutral','force','army'):
                    for options in (0,1,5,8,16,64,65,255):
                        rows.append(dict(rva=r,options=options,context=kind,clearBytes=48400,source=source,goals=[target],terrain=terrain))
        print('terrain sites',sites,'total configurations',len(rows),flush=True)
    result=verify(game,False,rows)
    folder=HERE/'.cache'/'ai_path_search_branches';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result)
    path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']:args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(len(result['cases']),'completed;',len(result['failures']),'faults;',path)
    for backend,branches in result['branchCoverage'].items():print(backend,sum(b['taken'] and b['fallthrough'] for b in branches),'/',len(branches),'conditions with both edges')
    if not result['clockStable']:raise SystemExit('Game changed; diagnostics only')


if __name__=='__main__':main()
