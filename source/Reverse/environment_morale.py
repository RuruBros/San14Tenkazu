"""Execute original environmental morale inputs only on Oracle's copied pages."""
import argparse, hashlib, json, pathlib, struct
from game import Game
from oracle import Oracle
from season_watch import read_snapshot

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--out',type=pathlib.Path,required=True);args=parser.parse_args()
    if args.out.exists():raise RuntimeError('Do not overwrite evidence')
    g=Game();baseline=read_snapshot(g);root=g.q(g.exe+0x277df18)+0x290;first=g.q(root+0x7e5a8)
    before=g.read(first,501*0x200)
    if before is None:raise RuntimeError('Unit snapshot unavailable')
    active=[i for i in range(1,501) if before[i*0x200+0x10] in (1,2) and struct.unpack_from('<H',before,i*0x200+0x12)[0]]
    if not active:raise RuntimeError('No active units')
    records=[]
    for slot in active[:3]:
        address=first+slot*0x200;unit=before[slot*0x200:(slot+1)*0x200]
        for morale,delta in [(100,-1),(100,-2),(100,-3),(100,-9),(1,-3)]:
            o=Oracle(g);o.poke(address,unit);o.poke(address+0x1a,bytes([morale]))
            o.call(0x270550,address,delta,0,0,stack_args=[1],read_after=[(address,0x200)])
            after=o.after[0];actual=after[0x1a]
            if not 0<=actual<=morale:raise RuntimeError('Native morale reduction is outside input range')
            records.append(dict(slot=slot,commander=struct.unpack_from('<H',unit,0x12)[0],before=morale,delta=delta,after=actual,
                originalFunction='0x270550',sourceTile=None,reason=0,deferred=1,stubbedGameplayFunctions=[]))
    end=read_snapshot(g)
    if g.read(first,501*0x200)!=before or end!=baseline:raise RuntimeError('Live snapshot changed during read-only Oracle audit')
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps(dict(pid=g.pid,baseline=baseline,unitSnapshotSha256=hashlib.sha256(before).hexdigest(),unchanged=True,records=records),indent=2),encoding='utf-8')
    print(json.dumps(records,indent=2));print('PASS original morale body; live snapshot unchanged')

if __name__=='__main__':main()
