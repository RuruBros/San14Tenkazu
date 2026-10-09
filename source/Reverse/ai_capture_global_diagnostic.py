# Read-only diagnosis of rejected global-object captures, never native evidence.
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
from ai_facility_special import read_clock
from ai_static_facts import anchored
from image import Image


def compare(game):
    before=read_clock(game);pointer=game.q(game.exe+0x1BB3D00);size=0x31330
    if not pointer:raise ValueError('global context is null')
    start=pointer&-4096;end=(pointer+size+4095)&-4096
    first=game.read(start,end-start);second=game.read(start,end-start)
    if first is None or second is None:raise ValueError('global context pages unreadable')
    if len(first)!=end-start or len(second)!=end-start:raise ValueError('global context page read is incomplete')
    changed=[start+i-pointer for i,(a,b) in enumerate(zip(first,second)) if a!=b]
    inside=[offset for offset in changed if 0<=offset<size]
    after=read_clock(game)
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,
                pointer=pointer,pointerAfter=game.q(game.exe+0x1BB3D00),allocationSize=size,pageStart=start,pageLength=len(first),
                firstSha256=hashlib.sha256(first).hexdigest(),secondSha256=hashlib.sha256(second).hexdigest(),
                changedByteOffsets=changed,changedInsideAllocation=inside,
                changes=[dict(offset=offset,before=first[pointer-start+offset],after=second[pointer-start+offset]) for offset in changed],
                nativeCalls=0,gameWrites=0,method='two original page-range reads; compare object and neighboring bytes without relaxing the existing capture guard')


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    im=Image();anchors=anchored(im,0x1866F0,{0x1866FD:'mov ecx, 0x31330'})
    from game import Game
    result=compare(Game());result['allocationAnchors']=anchors
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('clock stable',result['clockStable'],'global pointer stable',result['pointer']==result['pointerAfter'],
          'changed bytes',len(result['changedByteOffsets']),'inside allocation',len(result['changedInsideAllocation']))


if __name__=='__main__':main()
