# Facility candidates outside the base's home area; actual full native bodies, copied pages only.
import argparse
from datetime import datetime,timezone
import hashlib
from pathlib import Path
import struct
from ai_friend import HERE,vectors_text
from ai_executor_audit import Harness
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace,linked_list


def verify(game):
    h=Harness(game);forces=h.valid_table(0xDF30,52);answers=[];skipped=[]
    for force in forces:
        district=h.o.call(0x22E7E0,force);city=h.o.call(0x22D3A0,district)
        home=h.o.call(0x229D40,city);gate=h.o.call(0x229E40,city)
        for area in dict.fromkeys([home,gate]):
            if not area or h.o.call32(0x32F500,area)==0:
                skipped.append({'force':h.o.call32(0x22DC80,force),'area':area});continue
            for mode in [0,1]:
                h.setup('CGetCandidateFacilityListNode',h.keys[:5]);h.obj(h.keys[2],city);h.obj(h.keys[3],area);h.number(h.keys[1],mode)
                trace=Trace(h.o,{0x3F5D0:None,0x22F060:1,0x233340:None});status=h.execute(district);events=trace.events.copy();trace.close()
                output=linked_list(h.o,h.output(h.keys[0]));special=None;after_mode=mode
                if status==1:
                    h.o.call32(0xB26300,h.board,h.keys[4],h.out,0,read_after=[(h.out,4)]);special=struct.unpack('<i',h.o.after[0])[0]
                    h.o.call32(0xB26300,h.board,h.keys[1],h.out,0,read_after=[(h.out,4)]);after_mode=struct.unpack('<i',h.o.after[0])[0]
                    if special not in [0,1]:raise AssertionError('facility border flag')
                elif status!=2:raise AssertionError('facility border status')
                answers.append({'force':h.o.call32(0x22DC80,force),'home':home,'gate':gate,'area':area,
                                'inputMode':mode,'outputMode':after_mode,'special':special,'native':output,'status':status,'events':events})
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'actual city/home/gate area and mode inputs; full native facility body; copied pages only',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers,'skippedInvalidAreas':skipped}


def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);args=p.parse_args();result=verify(ListPoolSnapshot());text=vectors_text(result)
    folder=HERE/'.cache'/'ai_facility_audit';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} facility border cases; {sum(r['special']==1 for r in result['cases'])} special results; {path}")


if __name__=='__main__':main()
