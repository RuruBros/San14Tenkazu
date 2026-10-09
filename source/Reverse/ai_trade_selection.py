# Actual candidate generation plus full selection; standalone native sorting of real country pointers.
# All changed country bytes and list inputs are in copied pages, never game writes or helper replacements.
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
    h=Harness(game);forces=h.valid_table(0xDF30,52);countries=h.valid_table(0x85A20,5);answers=[];survey=[]
    for force in forces:
        for byte in [None,0,1,255]:
            h.setup('CTradePriorityCountrySelectNode',h.keys[:2]);h.obj(h.keys[0],force)
            if byte is not None:
                for country in countries:h.o.poke(country+0x28,bytes([byte])*5)
            trace=Trace(h.o,{0x6DDFC0:1,0x74980:None});status=h.execute(h.o.call(0x22E7E0,force));events=trace.events.copy();trace.close()
            candidates=events[0]['list'];force_id=h.o.call32(0x22DC80,force)
            if byte is None:survey.append({'force':force_id,'candidates':candidates})
            if not candidates:break
            survived=[]
            for candidate in candidates:
                checks=[{'other':other,'kind':h.o.call32(0x22DCB0,other),
                         'byte':h.o.read(candidate+0x28+max(0,min(4,h.o.call32(0x22DCB0,other))),1)[0]}
                        for other in countries if other!=candidate]
                if all(c['byte']==1 for c in checks):survived.append(candidate)
            if status!=(1 if survived else 2):raise AssertionError('trade candidate filter')
            selected=h.output(h.keys[1]) if status==1 else None
            if len(survived)==1 and selected!=survived[0]:raise AssertionError('trade first selection')
            answers.append({'kind':'selection','force':force_id,'copiedRelationByte':byte,'candidates':candidates,
                            'relations':[list(h.o.read(c+0x28,5)) for c in candidates],
                            'otherKinds':[[c,h.o.call32(0x22DCB0,c)] for c in countries],
                            'nativeSelected':selected,'status':status,'events':events})
    profiles=[None,[0,999,1000,1001,65535],[0,1001,1000,999,0],[0,65535,65534,65533,65532],[1000]*5]
    for force in [forces[0],forces[1],forces[-1]]:
        for profile in profiles:
            for order in [countries,countries[::-1]]:
                h.setup('CTradePriorityCountrySelectNode',h.keys[:2]);h.o.call(0x1A2F0,h.keys[2],0,h.context,h.out,keep=True)
                if profile is not None:h.o.poke(force+0x184,struct.pack('<5H',*profile))
                values=list(struct.unpack('<5H',h.o.read(force+0x184,10)))
                kinds={c:h.o.call32(0x22DCB0,c) for c in countries}
                pointer=h.output(h.keys[2]);source=h.o.alloc(8)
                for country in order:h.o.poke(source,struct.pack('<Q',country));h.o.call(0x58820,pointer,source,keep=True)
                index=struct.unpack('<I',h.o.read(h.o.q(pointer+8),4))[0]
                trace=Trace(h.o,{0x978E0:None});h.o.call(0x74980,h.o.exe+0x27D10E0,index,force,keep=True)
                output=linked_list(h.o,pointer);events=trace.events.copy();trace.close()
                expected=sorted(order,key=lambda c:(values[max(0,min(4,kinds[c]))]>=1000,-values[max(0,min(4,kinds[c]))],kinds[c]))
                if output!=expected:raise AssertionError('trade sort numeric order')
                answers.append({'kind':'sort','force':h.o.call32(0x22DC80,force),'copiedValues':profile,'values':values,
                                'countryKinds':[[c,kinds[c]] for c in countries],'input':order,'native':output,'events':events})
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'full native selection and standalone unchanged native comparator/sort; copied relation bytes and native list inputs',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'survey':survey,'cases':answers}


def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);args=p.parse_args();result=verify(ListPoolSnapshot());text=vectors_text(result)
    folder=HERE/'.cache'/'ai_trade_selection';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['survey'])} force surveys, {len(result['cases'])} selection/sort cases matched; {path}")


if __name__=='__main__':main()
