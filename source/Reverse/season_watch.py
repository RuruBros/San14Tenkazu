"""Observe dates and render identities once per second, without game writes."""
import argparse
import ctypes
import hashlib
import json
import pathlib
import time
from game import Game,psapi
from graphics_probe import observe
from graphics_gpu import driver_path
from season_trace_contract import check_identity,validate_snapshot
from season_shader_snapshot import EXE_HASH


def read_snapshot(g):
    before=observe(g)
    before.update({k:hex(v or 0) for k,v in driver_path(g).items()})
    after=observe(g)
    if after!={k:v for k,v in before.items() if k not in ('driver','device','immediate')}:
        raise RuntimeError('Game changed during snapshot read')
    validate_snapshot(before)
    return before


def main():
    p=argparse.ArgumentParser();p.add_argument('--out',required=True,type=pathlib.Path)
    p.add_argument('--seconds',type=int,default=30);args=p.parse_args()
    if not 1<=args.seconds<=120:raise ValueError('Observation must be 1..120 seconds')
    g=Game();path=ctypes.create_unicode_buffer(32768)
    if not psapi.GetModuleFileNameExW(ctypes.c_void_p(g.h),ctypes.c_void_p(g.exe),path,len(path)):
        raise ctypes.WinError(ctypes.get_last_error())
    if hashlib.sha256(pathlib.Path(path.value).read_bytes()).hexdigest()!=EXE_HASH:raise ValueError('Unverified executable')
    args.out.mkdir(parents=True,exist_ok=True);rows=[];previous=None;start=time.monotonic()
    with (args.out/'events.jsonl').open('w',encoding='utf-8') as log:
        for index in range(args.seconds+1):
            try:
                state=read_snapshot(g);event='snapshot'
                if previous:
                    try:
                        if check_identity(previous,state,check_clock=False):event='date_changed'
                    except ValueError:event='render_identity_changed'
                row=dict(sequence=index,elapsedSeconds=time.monotonic()-start,event=event,state=state)
                previous=state
            except (ValueError,RuntimeError,TypeError) as error:
                row=dict(sequence=index,elapsedSeconds=time.monotonic()-start,event='snapshot_rejected',error=str(error))
                previous=None
            rows.append(row);log.write(json.dumps(row)+'\n');log.flush()
            if index<args.seconds:time.sleep(max(0,start+index+1-time.monotonic()))
    summary=dict(mode='Read-only; no helper dispatch or GPU state change',samples=len(rows),
                 dateChanges=sum(r['event']=='date_changed' for r in rows),
                 renderIdentityChanges=sum(r['event']=='render_identity_changed' for r in rows),
                 rejected=sum(r['event']=='snapshot_rejected' for r in rows))
    (args.out/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');print(json.dumps(summary))


if __name__=='__main__':main()
