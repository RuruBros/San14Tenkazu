# Inventory actual paused-world inputs needed by the remaining path conditions.
# python ai_path_state_inventory.py
# Queries original classifiers only in Oracle; actual game handle remains read-only.
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
    folder=HERE/'.cache'/'ai_path_state_inventory';folder.mkdir(parents=True,exist_ok=True)
    game=Game();first=read_clock(game);time.sleep(3);second=read_clock(game)
    pre=dict(first=first,second=second,stable=first==second)
    (folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'-preflight.json')).write_text(vectors_text(pre),encoding='utf-8')
    if not pre['stable']:raise SystemExit('Game changed; no native classifier copy started')
    game=ListPoolSnapshot();before=read_clock(game);h=Harness(game);o=h.o
    tiles=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8));words=[];terrain=Counter();nulls=[]
    for hex_,tile in enumerate(tiles):
        if not tile:nulls.append(hex_);continue
        prefix=game.read(tile,0x20);terrain[prefix[0x10]]+=1;word=struct.unpack_from('<H',prefix,0x16)[0]
        if word:words.append(dict(hex=hex_,tile=tile,word16=word))
    armies=[]
    for army in h.valid_table(0x7E838,501):
        armies.append(dict(pointer=army,owner=o.call32(0x22CC40,army),leader=struct.unpack('<H',o.read(army+0x12,2))[0],special=o.call32(0x232730,army)))
    pool={hex(r):Game.read(game,game.exe+r,n).hex() for r,n in [(0x27D10E8,8),(0x27D10F0,8),(0x27D1120,4),(0x27D10B0,4),(0x2141A84,4)]}
    after=read_clock(game)
    result=dict(clockBefore=before,clockAfter=after,clockStable=before==after,tileCount=len(tiles),nullTiles=nulls,nonzeroWord16=words,
                terrainCounts=dict(terrain),armies=armies,ownerCounts=dict(Counter(a['owner'] for a in armies)),
                force46To50=[a for a in armies if 46<=a['owner']<=50],leaderAbove6000=[a for a in armies if a['leader']>6000],
                specialMinorForce=[a for a in armies if 46<=a['owner']<=51 and a['special']],directPoolFields=pool,listPoolSnapshot=game.pool_snapshot)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(vectors_text(result),encoding='utf-8')
    print('actual remaining-state inventory', {k:result[k] for k in ['clockStable','tileCount','ownerCounts','directPoolFields']},flush=True)
    print('null tiles',len(nulls),'word16 nonzero',len(words),'force46..50',len(result['force46To50']),'leader>6000',len(result['leaderAbove6000']),'minor special',len(result['specialMinorForce']),path,flush=True)
    if not result['clockStable']:raise SystemExit('Game changed; inventory is diagnostics only')


if __name__=='__main__':main()
