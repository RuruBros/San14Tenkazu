# Real native state factories and executor bodies with populated relay inputs in copied pages.
# python ai_state_audit.py --vectors ../Tests/AiStateAuditVectors.json
import argparse
from datetime import datetime,timezone
from pathlib import Path
import hashlib
import struct
from ai_friend import HERE,vectors_text
from ai_executor_audit import Harness
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace


def verify(game):
    h=Harness(game);forces=h.valid_table(0xDF30,52)
    group=next(g for g in h.valid_table(0x7F8D8,600) if h.o.call32(0x22CEF0,g)==h.o.call32(0x22DC80,forces[0]))
    owner=h.o.call32(0x22CEF0,group)
    own_force=next(f for f in forces if h.o.call32(0x22DC80,f)==owner)
    enemy_force=next(f for f in forces if h.o.call32(0x2CA280,owner,h.o.call32(0x22DC80,f)))
    cities=[h.o.call(0x22D3A0,h.o.call(0x22E7E0,f)) for f in [own_force,enemy_force]]
    centers=[struct.unpack('<H',game.read(h.o.call(0x229D40,c)+0x3A,2))[0] for c in cities]
    target=struct.unpack('<H',game.read(cities[1]+0x4E,2))[0];answers=[];failures=[]
    for name in ['CExecuteAreaOccupyNode','CExecuteAttackAreaBuildingNode']:
        for relay in centers:
            for purpose in [0,3,7,9,13,17,18]:
                h.setup(name,[0,0,h.keys[2]],2);h.number(h.keys[2],1);state=h.state()
                h.o.poke(group+0x14,struct.pack('<HH',purpose,target))
                h.o.poke(group+0x18,struct.pack('<10H',relay,*([48400]*9)))
                before=h.o.read(state,40 if name=='CExecuteAreaOccupyNode' else 32)
                trace=Trace(h.o,{0xC8F00:None,0x2EAAE0:None,0x2EAB40:None})
                from oracle import OracleError
                try:status=h.execute(group)
                except OracleError as error:
                    failures.append({'class':name,'inputPurpose':purpose,'inputRelay':relay,
                                     'error':str(error),'events':trace.events.copy()})
                    trace.close();continue
                after=h.o.read(state,len(before));events=trace.events.copy();trace.close()
                if status not in [1,2,3]:raise AssertionError('executor status')
                if name=='CExecuteAttackAreaBuildingNode' and purpose in [0,7,17,18]:
                    if status!=2 or struct.unpack_from('<QQi',after,8)!=(0,0,0):raise AssertionError('building reject/reset')
                answers.append({'class':name,'inputPurpose':purpose,'inputRelay':relay,'inputTargetBase':target,
                                'stateBefore':before.hex(),'stateAfter':after.hex(),'status':status,
                                'outputPurpose':struct.unpack('<H',h.o.read(group+0x14,2))[0],'events':events})
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'native reset and full executor; copied purpose/target/one actual area-center relay inputs; no game writes or helper result replacements',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers,'failures':failures}


def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);args=p.parse_args()
    result=verify(ListPoolSnapshot());text=vectors_text(result);folder=HERE/'.cache'/'ai_state_audit';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} populated relay state cases completed, {len(result['failures'])} failed and recorded separately; {path}")


if __name__=='__main__':main()
