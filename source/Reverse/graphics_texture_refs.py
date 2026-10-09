"""Read-only search for the identified terrain texture's native references."""
import argparse
import json
import pathlib
import struct
import time

from game import Game


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--identified',required=True,type=pathlib.Path)
    p.add_argument('--out',required=True,type=pathlib.Path)
    p.add_argument('--unaligned',action='store_true')
    p.add_argument('--views-only',action='store_true')
    a=p.parse_args();g=Game()
    proof=json.loads(a.identified.read_text())
    if proof['state']['pid']!=g.pid or int(proof['state']['exe'],16)!=g.exe:
        raise RuntimeError('Identified texture belongs to another game process')
    c=proof['candidate']['candidate'];field=int(c['resource_field'],16)
    record=g.read(field,24)
    if not record or struct.unpack_from('<Q',record)[0]!=int(c['resource'],16):
        raise RuntimeError('Original terrain record is no longer installed')
    targets=dict(zip(('texture','srv0','srv1'),struct.unpack('<3Q',record)))
    targets.update(record=field,owner=int(c['wrapper'],16))
    if a.views_only:targets={k:v for k,v in targets.items() if k.startswith('srv')}
    patterns={k:struct.pack('<Q',v) for k,v in targets.items()}
    found={k:[] for k in targets};scanned=0;start=time.monotonic()
    for base,size,typ in g.regions():
        for offset in range(0,size,0x800000):
            length=min(0x800000,size-offset)
            data=g.read(base+offset,length)
            if data is None: continue
            scanned+=length
            for name,pattern in patterns.items():
                at=data.find(pattern)
                while at>=0:
                    address=base+offset+at
                    if a.unaligned or address%8==0: found[name].append(address)
                    at=data.find(pattern,at+1)
    result=dict(pid=g.pid,exe=hex(g.exe),targets={k:hex(v) for k,v in targets.items()},
                scanned=scanned,seconds=time.monotonic()-start,references={})
    for name,addresses in found.items():
        result['references'][name]=[dict(address=hex(addr),context=(g.read(addr-64,192) or b'').hex())
                                    for addr in sorted(set(addresses))]
    a.out.parent.mkdir(parents=True,exist_ok=True)
    a.out.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k!='references'}))
    print(json.dumps({k:len(v) for k,v in result['references'].items()}))


if __name__=='__main__':main()
