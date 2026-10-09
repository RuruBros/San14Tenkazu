# Construct every registered node class and verify linear parameter loaders with distinct native inputs.
# python ai_class_audit.py --vectors ../Tests/AiClassAuditVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot


def verify(game):
    from oracle import Oracle
    answers=[]
    for key,cls in ai.classes().items():
        oracle=Oracle(game=game);node=oracle.alloc(0x1000)
        returned=oracle.call(cls['ctor'],node,keep=True)
        actual=oracle.q(node)-game.exe
        if actual!=cls['vt'] or returned!=node:raise AssertionError((cls['name'],'constructor',hex(actual),returned,node))
        row={'hash':key,**cls,'constructedVtable':actual}
        offsets=cls['offsets']
        if offsets and cls['load']:
            values=[0x61100000+i for i in range(max(offsets)+1)]
            params=oracle.alloc(len(values)*4);oracle.poke(params,struct.pack('<'+'I'*len(values),*values))
            oracle.call(cls['load'],node,params,len(values),keep=True)
            size=max(0x80,max(offsets.values())+4);raw=oracle.read(node,size)
            for index,offset in offsets.items():
                if struct.unpack_from('<I',raw,offset)[0]!=values[index]:
                    raise AssertionError((cls['name'],'loader',index,hex(offset),raw.hex(),values))
            row.update(params=values,loaded=raw.hex())
        answers.append(row)
        print(cls['name'],flush=True)
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'native constructors and linear loaders; copied pages only, no result stubs',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=verify(ListPoolSnapshot());text=vectors_text(result);folder=HERE/'.cache'/'ai_class_audit';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} native classes matched; {path}")


if __name__=='__main__':main()
