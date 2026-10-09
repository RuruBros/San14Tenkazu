"""Installed Shader object graph and local reflection only; no SAN14 access."""
import argparse
from collections import Counter,defaultdict
import hashlib
import json
from pathlib import Path
import re
import sys

import ktgl
from audit_tree_shader_links import objects,public
from audit_system_shader_links import dxbc
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Reverse'))
from season_shader_layout import LayoutReflector,buffer_binding


def sha(data):return hashlib.sha256(data).hexdigest()


def shader_fields(node):
    if node['propertyParseError'] or [p['name'] for p in node['props']]!=['193025DF','DB59AAFC']:
        raise ValueError('Reachable Shader object has a different schema')
    primary,states=node['props']
    if primary['kind']!=5 or primary['count']!=1 or states['kind']!=5 or states['count']>256:
        raise ValueError('Reachable Shader reference shape rejected')
    if len(primary['rawUint32'])!=1 or len(states['rawUint32'])!=states['count']:
        raise ValueError('Reachable Shader reference count differs from payload')
    if primary['rawUint32'][0]=='00000000' or any(not re.fullmatch('[0-9A-F]{8}',s)
            for s in primary['rawUint32']+states['rawUint32']):
        raise ValueError('Reachable Shader key is invalid')
    return primary['rawUint32'][0],states['rawUint32']


def reachable_shader_objects(all_nodes,roots,limit=4096):
    if not roots or not 1<=limit<=4096 or any(not re.fullmatch('[0-9A-F]{8}',s) or s=='00000000' for s in roots):
        raise ValueError('Shader graph root/limit rejected')
    pending=list(sorted(roots));reachable={};edges=[]
    while pending:
        identity=pending.pop()
        if identity in reachable:continue
        if len(reachable)>=limit:raise ValueError('Reachable graph size rejected')
        versions=all_nodes.get(identity,[])
        if not versions:raise ValueError('Reachable Shader object is absent')
        reachable[identity]=versions
        for version in versions:
            _,states=shader_fields(version)
            for index,target in enumerate(states):
                edges.append(dict(sourceObject=identity,sourceRecordSha256=version['rawSha256'],
                                  rawArrayIndex=index,targetObject=target,nullReference=target=='00000000'))
                if target!='00000000':pending.append(target)
    return reachable,edges


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--directory',required=True,type=Path)
    parser.add_argument('--links',required=True,type=Path)
    parser.add_argument('--shader-directory',required=True,type=Path)
    parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args();raw=args.links.read_bytes();links=json.loads(raw)
    if links['errors']:raise ValueError('Root shader links are incomplete')
    folder=args.directory/'0002';catalog=ktgl.index(folder,'system')
    all_nodes=defaultdict(list);sources=[];errors=[]
    for row in (r for r in catalog if r['type']==0x20A6A0BB):
        payload=ktgl.read(folder,row)
        source=dict(archive='system',entry=row,payloadBytes=len(payload),sha256=sha(payload))
        try:
            for node in objects(payload):
                if node['tag']!='IDOK0000':continue
                node['source']=source;all_nodes[node['id']].append(public(node))
            sources.append(source)
        except ValueError as error:errors.append(dict(source=source,error=str(error)))
    if errors:raise ValueError('System DOK boundaries are incomplete')
    roots={c['objectId'] for g in links['groups'] for c in g['candidates']}
    reachable,edges=reachable_shader_objects(all_nodes,roots)
    # Retain every recorded version; no archive-order choice is made for runtime.
    resources=sorted({shader_fields(v)[0] for versions in reachable.values() for v in versions})
    index_raw=(args.shader_directory/'system-index.json').read_bytes();index=json.loads(index_raw)
    owner_raw=(args.shader_directory/'archive-matches.json').read_bytes();owners=json.loads(owner_raw)
    reflector=LayoutReflector();reflected={};resource_results={}
    for identity in resources:
        entries=[r for r in catalog if f'{r["ktid"]:08X}'==identity and r['type']==0x7BCD279F]
        if len(entries)!=1:raise ValueError('Reachable G1S resource is ambiguous or missing')
        entry=entries[0];payload=ktgl.read(folder,entry);containers=dxbc(payload)
        previous=[r for r in index if int(r['ktid'],16)==int(identity,16)]
        if not containers or Counter((c['offset'],c['sha256']) for c in containers)!=Counter((r['offset'],r['sha256']) for r in previous):
            raise ValueError('Reachable G1S differs from recorded catalog')
        for container in containers:
            record=next(r for r in previous if r['offset']==container['offset'])
            container['stage']=record['stage'];digest=container['sha256']
            if digest not in reflected:
                file=args.shader_directory/record['file'];data=file.read_bytes()
                if sha(data)!=digest:raise ValueError('Archived DXBC snapshot changed')
                result=reflector.inspect(data);layouts=reflector.layouts(data)
                for layout in layouts:
                    layout['binding']=buffer_binding(layout,result['metadata']['resources'])
                reflected[digest]=dict(**result,constantBuffers=[b for b in layouts if b['bufferType']==0],
                                      otherBufferMetadata=[b for b in layouts if b['bufferType']!=0])
        resource_results[identity]=dict(source=entry,payloadBytes=len(payload),sha256=sha(payload),containers=containers)
    all_shas=set(reflected)
    owner_candidates=[]
    for owner in owners['entries']:
        matches={r['sha256'] for r in owner['matches'] if r['sha256'] in all_shas}
        if matches:owner_candidates.append(dict(shaderOwner=owner['shader'],matchingArchiveSha256s=sorted(matches),
            fullCatalogCandidateBytecodes=owner['candidateBytecodes'],bytecodeIdentityVerified=False))
    result=dict(mode='Installed files and D3DReflect in analysis process only',gameProcessAccess=False,gameComCalls=False,
        rootLinksSha256=sha(raw),systemIndexSha256=sha(index_raw),archiveMatchesSha256=sha(owner_raw),
        compilerPath=str(reflector.path),compilerSha256=reflector.sha,sources=sources,roots=sorted(roots),
        objects=reachable,edges=edges,resources=resource_results,bytecodes=reflected,
        recordedOwnerMetadataCandidates=owner_candidates,gameDrawPassVerified=False,coordinateValuesVerified=False,
        runtimeVersionSelectionVerified=False,shaderBytecodeIdentityVerified=False,productionReady=False)
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(rootObjects=len(roots),reachableObjects=len(reachable),objectVersions=sum(map(len,reachable.values())),
        resources=len(resource_results),containers=sum(len(r['containers']) for r in resource_results.values()),
        uniqueBytecodes=len(reflected),constantBuffers=sum(len(r['constantBuffers']) for r in reflected.values()),
        otherBufferMetadata=sum(len(r['otherBufferMetadata']) for r in reflected.values()),
        variables=sum(len(b['variables']) for r in reflected.values() for b in r['constantBuffers']),
        recordedOwnerMetadataCandidates=len(owner_candidates),gameProcessAccess=False)))


if __name__=='__main__':main()
