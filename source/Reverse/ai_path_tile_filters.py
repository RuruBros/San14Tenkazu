# Complete path backends around actual terrain-7, occupied and force-border tiles.
# python ai_path_tile_filters.py [--vectors ../Tests/AiPathTileFilterVectors.json.gz]
# Input context/options vary; real tile, unit, officer and force records are unchanged.
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


def configurations(game):
    before=read_clock(game);h=Harness(game);o=h.o;tiles=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8))
    armies={}
    for army in h.valid_table(0x7E838,501):armies.setdefault(o.call32(0x22CC40,army),army)
    sites={};rows=[]
    for hex_,tile in enumerate(tiles):
        if not tile:continue
        prefix=game.read(tile,0x20);terrain=prefix[0x10];owner=prefix[0x14];word16=struct.unpack_from('<H',prefix,0x16)[0];occupant=struct.unpack_from('<h',prefix,0x1C)[0]
        tags=[]
        if terrain==7 and 'terrain7' not in sites:tags.append('terrain7')
        if 1<=occupant<=500 and 'occupied' not in sites:
            unit=game.q(h.raw+0x7E838+occupant*8)
            if unit and o.call32(0x32F500,unit) and o.call32(0x2C6950,hex_):tags.append('occupied') # Original 231500: signed +1C.
        if word16 and 'word16' not in sites:tags.append('word16')
        if owner in armies and 'border' not in sites:tags.append('border')
        if not tags:continue
        neighbors=[n for direction in range(1,7) if 0<=(n:=o.call32(0x2A7720,hex_,direction))<48400 and tiles[n]]
        for tag in tags:
            if tag=='border':
                other=next((n for n in neighbors if game.read(tiles[n]+0x14,1)[0]!=owner),None)
                if other is None:continue
                source=next((n for n in neighbors if n!=other),None);goal=hex_
            else:
                goal=neighbors[0] if neighbors else None
                source=next((n for n in neighbors if n!=goal),None)
            if source is None or goal is None:continue
            sites[tag]=dict(tag=tag,hex=hex_,tile=tile,terrain=terrain,owner=owner,word16=word16,occupant=occupant,source=source,target=goal,armyPointer=armies.get(owner))
        if len(sites)==4:break
    for tag,site in sites.items():
        for r in (0x183A30,0x184EB0):
            for kind in ('neutral','army'):
                for options in (0,1,5,9,16,17,65,81):
                    words=[(0x2C,1)] if tag=='terrain7' else [(0x28,1)] if tag=='word16' else [(0x10,1)] if tag=='border' else []
                    extra={'armyPointer':site['armyPointer']} if tag=='border' and kind=='army' else {}
                    for actual_words in (words,[]) if tag in ('terrain7','border') else (words,):
                        rows.append(dict(rva=r,options=options,context=kind,clearBytes=48400,source=site['source'],goals=[site['target']],contextWords=actual_words,tileSite=site,tracedHelpers=[0x2AE6F0,0x1864F0,0x2C6950],**extra))
    if before!=read_clock(game):raise AssertionError('Game changed during actual tile selection')
    return rows,list(sites.values())


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    game=ListPoolSnapshot();rows,sites=configurations(game);print('actual selected sites',sites,'searches',len(rows),flush=True)
    result=verify(game,False,rows);result['tileSites']=sites
    folder=HERE/'.cache'/'ai_path_tile_filters';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable'] and not result['failures']:args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(len(result['cases']),'completed;',len(result['failures']),'faults;',path)
    for backend,branches in result['branchCoverage'].items():print(backend,sum(b['taken'] and b['fallthrough'] for b in branches),'/',len(branches),'conditions with both edges')
    if not result['clockStable'] or result['failures']:raise SystemExit('Game changed or native faults; diagnostics only')


if __name__=='__main__':main()
