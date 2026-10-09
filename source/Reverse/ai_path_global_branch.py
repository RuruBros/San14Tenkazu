# Observe the gated original 183A30 path using the actual separately allocated global object.
# python ai_path_global_branch.py [--vectors ../Tests/AiPathGlobalBranchVectors.json.gz]
# Game reads only; enabling 2141A84 happens in Oracle's copy and is restored per call.
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
from pathlib import Path

from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_path_search_audit import verify
from image import Image


def capture_global(game):
    before=read_clock(game);pointer=game.q(game.exe+0x1BB3D00)
    if not pointer:raise SystemExit('global context is null; no native trials started')
    size=0x31330 # Original allocation 1866FD and deallocation 18675A.
    start=pointer&~0xFFF;end=(pointer+size+0xFFF)&~0xFFF
    first=game.read(start,end-start);second=game.read(start,end-start)
    if first is None or first!=second or before!=read_clock(game):raise SystemExit('global context capture changed; no native trials started')
    if any(start<s+len(b) and s<end for s,b in game._pool_ranges):raise SystemExit('global context overlaps frozen pool; no native trials started')
    game._pool_ranges.append((start,first))
    return dict(pointer=pointer,allocationSize=size,pageStart=start,pageLength=len(first),sha256=hashlib.sha256(first).hexdigest(),twoReadsEqual=True)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    game=ListPoolSnapshot();im=Image();flag_before=game.read(game.exe+0x2141A84,4).hex();context=capture_global(game)
    rows=[dict(rva=0x183A30,options=options,context=kind,clearBytes=48400,goals=goals,imageFields=[(0x2141A84,4,value)],tracedHelpers=[0x185B30])
        for value in (0,1) for kind in ('neutral','force','army') for options in (0,1) for goals in ([12780],[12781])]
    result=verify(game,False,rows)
    result['liveFlagBefore']=flag_before;result['liveFlagAfter']=game.read(game.exe+0x2141A84,4).hex()
    result['globalContextSnapshot']=context
    result['staticGlobalContext']={hex(r):im.flowtext(r) for r in (0x1866F0,0x186740,0x182C10,0x185B30,0x1887D0)}
    folder=HERE/'.cache'/'ai_path_global_branch';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result);path.write_text(text,encoding='utf-8')
    live_flag_stable=result['liveFlagBefore']==result['liveFlagAfter']
    if args.vectors and result['clockStable'] and live_flag_stable:args.vectors.write_bytes(gzip.compress(text.replace('\n','\r\n').encode('utf-8'),mtime=0))
    print(len(result['cases']),'completed;',len(result['failures']),'faults;',path)
    for backend,branches in result['branchCoverage'].items():print(backend,sum(b['taken'] and b['fallthrough'] for b in branches),'/',len(branches),'conditions with both edges')
    if not result['clockStable']:raise SystemExit('Game changed; diagnostics only')
    if not live_flag_stable:raise SystemExit('Live global flag changed; diagnostics only')


if __name__=='__main__':main()
