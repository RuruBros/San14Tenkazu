# Complete original A* calls on a validated frozen game. No live fallback.
# Fire is either already in the saved tile or produced by the complete original
# 2E39E0 call, including its callbacks. No timer poke or helper replacement.
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct

from ai_frozen_preflight import FrozenImage
from ai_path_search_audit import verify
from image import Image
from oracle import OracleError
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RBX, UC_X86_REG_RDX


def separated_neighbors(oracle, index, tiles):
    neighbors=[n for d in range(1,7) if 0<=(n:=oracle.call32(0x2A7720,index,d))<48400 and tiles[n]]
    for source in neighbors:
        adjacent={oracle.call32(0x2A7720,source,d) for d in range(1,7)}
        for target in neighbors:
            if target!=source and target not in adjacent:return source,target
    return None


def candidate_tiles(game):
    raw=game.q(game.exe+0x277DF18)
    if not raw or game.base-0x290!=raw:raise ValueError('world identity differs from frozen base')
    pointers=game.read(raw+0xE270,48400*8)
    if pointers is None:raise ValueError('missing actual tile table')
    tiles=struct.unpack('<48400Q',pointers);existing=[];dry=[]
    for index,pointer in enumerate(tiles):
        prefix=game.read(pointer,32) if pointer else None
        if prefix is None:continue
        timer=struct.unpack_from('<H',prefix,0x16)[0]
        if timer:
            existing.append(dict(hex=index,tile=pointer,timer=timer,origin='saved frozen tile'))
            continue
        terrain_id=prefix[0x10]
        if terrain_id>30:continue
        terrain=game.q(raw+0x73860+terrain_id*8)
        record=game.read(terrain,0x48) if terrain else None
        # Input selection only; original Ignite still checks validity and all immunity gates.
        if record and struct.unpack_from('<h',record,0x2A)[0] and record[0x41] and not struct.unpack_from('<H',prefix,0x1A)[0] and not 1<=struct.unpack_from('<h',prefix,0x1C)[0]<=500:
            dry.append(dict(hex=index,tile=pointer,timer=0,origin='original Ignite required'))
    return tiles,existing,dry


def replay(path):
    im=Image();game=FrozenImage(path,im,True)
    result=dict(utc=datetime.now(timezone.utc).isoformat(),snapshot=dict(file=str(path),sha256=game.snapshot_sha256),
                codeSource=game.code_source,listPoolSnapshot=game.pool_snapshot,gameAccess=False,helperReplacements=[],timerPokes=[],
                imageSha256=hashlib.sha256(im.img).hexdigest(),analyzerSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    if not game.pool_snapshot['valid']:return dict(result,error='frozen pool failed validation',completed=False)
    rows=[];preparation=dict(attempts=[])
    try:tiles,existing,dry=candidate_tiles(game)
    except ValueError as error:return dict(result,error=str(error),completed=False)
    result['candidateCounts']=dict(existingFire=len(existing),dryPreselection=len(dry))
    def prepare(h):
        o=h.o
        for candidate in (existing or dry[:16]):
            pair=separated_neighbors(o,candidate['hex'],tiles)
            if pair is None or not o.call32(0x32F500,candidate['tile']):continue
            before=o.read(candidate['tile'],32)
            attempt=dict(**candidate,before=before.hex(),igniteStarted=False,igniteCompleted=False)
            preparation['attempts'].append(attempt)
            if not candidate['timer']:
                attempt.update(igniteStarted=True,entry=0x2E39E0,args=[candidate['tile'],120,1])
                try:o.call(0x2E39E0,candidate['tile'],120,1,keep=True,read_after=[(candidate['tile'],32)])
                except OracleError:
                    attempt['tileAfterFailedCall']=o.read(candidate['tile'],32).hex()
                    raise
                attempt['igniteCompleted']=True
            after=o.read(candidate['tile'],32);timer=struct.unpack_from('<H',after,0x16)[0]
            attempt.update(after=after.hex(),writtenTimer=timer)
            if not timer:continue
            source,target=pair;preparation['site']={**candidate,'timer':timer,'source':source,'target':target}
            for enabled in (0,1):
                rows.append(dict(rva=0x183A30,options=1,context='neutral',clearBytes=48400,source=source,goals=[target],contextWords=[(0x28,enabled)],fireFilter=enabled))
            observations=[];pending=[None];preparation['fireFilterInputs']=observations
            def observe(uc,address,size,user):
                pc=address-o.exe
                if pc==0x184079:
                    tile=uc.reg_read(UC_X86_REG_RBX);context=uc.reg_read(UC_X86_REG_RDX)
                    event=dict(tile=tile,timer=struct.unpack('<H',o.read(tile+0x16,2))[0],
                               contextWord=struct.unpack('<i',o.read(context+0x28,4))[0])
                    observations.append(event);pending[0]=event
                elif pending[0] is not None:
                    pending[0]['nativeRejected']=pc==0x1842DF;pending[0]=None
            for pc in (0x184079,0x18408A,0x1842DF):
                o.mu.hook_add(UC_HOOK_CODE,observe,None,o.exe+pc,o.exe+pc)
            return preparation
        raise ValueError('no existing fire or original Ignite-completed tile among selected actual inputs')
    try:
        # verify calls prepare before it iterates this same initially empty rows list.
        evidence=verify(game,False,rows,prepare)
        condition=next(c for c in evidence['branchCoverage']['0x183a30'] if c['rva']==0x184084)
        complete=not evidence['failures'] and evidence['clockStable'] and condition['taken']
        result.update(evidence,completed=complete,fireEdgeObserved=condition['taken'])
        if not complete:result['error']='fire edge not observed or original search did not complete'
    except (OracleError,ValueError,RuntimeError,TypeError) as error:
        result.update(completed=False,error=str(error),preparation=preparation)
    return result


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--snapshot',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    result=replay(args.snapshot);args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('completed',result['completed'],'fire edge',result.get('fireEdgeObserved'),result.get('error',''))
    if not result['completed']:raise SystemExit(2)


if __name__=='__main__':main()
