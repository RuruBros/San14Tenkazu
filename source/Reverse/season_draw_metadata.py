"""Read-only shader metadata for completed engine Draw receipts.

Retained VS bytes are checked against native reflection. PS metadata matching
remains a catalog candidate, never a bytecode identity claim.
"""
import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from game import Game
from season_draw_observe import read_snapshot, check_identity
from season_shader_snapshot import snapshot_reflection, read_exact
from season_shader_layout import LayoutReflector


def sha(data):return hashlib.sha256(data).hexdigest()


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--draws',type=Path,required=True)
    p.add_argument('--catalog',type=Path,action='append',default=[])
    args=p.parse_args()
    audit=json.loads((args.draws/'audit.json').read_text())
    if not audit['actualDrawDispatched'] or not audit['independentCodeAndProtectionReadback']:
        raise ValueError('Completed actual Draw and recovery required')
    rows=json.loads((args.draws/'draws.json').read_text())
    # Sparse frame rows contain CPU owner chronology only unless explicitly
    # sampled. Never read native shader identity from their empty GPU fields.
    if rows and 'bindingsCaptured' in rows[0]:rows=[row for row in rows if row['bindingsCaptured']==1 and row['kind']<9]
    g=Game();before=read_snapshot(g);check_identity(audit['baseline'],before,check_clock=True)
    reflector=LayoutReflector();catalog=[]
    for file in args.catalog:
        doc=json.loads(file.read_text())
        entries=doc.get('entries',[])
        if 'bytecodes' in doc:entries=[dict(sha256=key,**value) for key,value in doc['bytecodes'].items()]
        for entry in entries:
            if 'metadata' in entry and 'fingerprint' in entry:
                catalog.append(dict(source=str(file),**entry))
    accepted=[]
    for stage in ('ps','vs'):
        identities=sorted({(row[stage+'Owner'],row[stage]) for row in rows if row[stage+'Owner']!='0x0'})
        for owner_text,shader_text in identities:
            owner=int(owner_text,16);identity=read_exact(g.read,owner,64 if stage=='vs' else 48)
            vt,reflection,shader=(struct.unpack_from('<Q',identity,n)[0] for n in (0,16,40))
            if vt!=g.exe+(0x14beb58 if stage=='vs' else 0x14beb90) or shader!=int(shader_text,16):
                raise ValueError('Shader owner identity differs')
            native=snapshot_reflection(g.read,reflection)
            entry=dict(stage=stage,owner=owner_text,shader=shader_text,reflection=hex(reflection),**native,
                actualCreationCallCaptured=False,gameBytecodeIdentityVerified=False)
            if stage=='vs':
                pointer,length=struct.unpack_from('<QQ',identity,48)
                if not 32<=length<=65536:raise ValueError('Retained VS bytecode length invalid')
                data=read_exact(g.read,pointer,length);local=reflector.inspect(data)
                if local['metadata']!=native['metadata']:raise ValueError('Retained VS/native metadata differ')
                if read_exact(g.read,pointer,length)!=data:raise ValueError('Retained VS bytes changed')
                file=args.draws/(sha(data)+'.dxbc');file.write_bytes(data)
                entry.update(retainedBytecodeSha256=sha(data),retainedBytecodeMetadataVerified=True,
                    retainedBytecodeFile=file.name,layouts=reflector.layouts(data))
            if read_exact(g.read,owner,len(identity))!=identity:raise ValueError('Shader owner changed')
            entry['catalogMetadataMatches']=[dict(source=x['source'],sha256=x.get('sha256'),resourceKtid=x.get('resourceKtid'),
                offset=x.get('offset')) for x in catalog if x['fingerprint']==native['fingerprint']]
            accepted.append(entry)
    after=read_snapshot(g);check_identity(before,after,check_clock=True)
    output=dict(mode='ReadProcessMemory only; no game COM/GPU calls',baseline=before,after=after,entries=accepted,
        compilerSha256=reflector.sha,gamePassVerified=False,sceneUiOrderVerified=False,seasonApplied=False,productionReady=False)
    (args.draws/'bound-shader-metadata.json').write_text(json.dumps(output,indent=2),encoding='utf-8')
    print(json.dumps([dict(stage=x['stage'],owner=x['owner'],fingerprint=x['fingerprint'],
        retainedSha256=x.get('retainedBytecodeSha256'),matches=x['catalogMetadataMatches'],
        resources=x['metadata']['resources']) for x in accepted]))


if __name__=='__main__':main()
