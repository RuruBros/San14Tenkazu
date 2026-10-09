"""Resolve tree shader-table objects and DXBC from installed files only."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

import ktgl
from audit_tree_shader_links import objects,public
from audit_model_bind_tables import descriptors,IMAGE_SHA


def sha(data):return hashlib.sha256(data).hexdigest()


def dxbc(data):
    result=[];at=0
    while (at:=data.find(b'DXBC',at))>=0:
        if at+32>len(data):raise ValueError('Truncated DXBC header')
        length=struct.unpack_from('<I',data,at+24)[0]
        count=struct.unpack_from('<I',data,at+28)[0]
        if length<32+count*4 or length>len(data)-at:raise ValueError('DXBC container bounds rejected')
        # Container chunk offsets are relative to this DXBC, not archive bytes.
        for index in range(count):
            offset=struct.unpack_from('<I',data,at+32+index*4)[0]
            if offset<32+count*4 or offset+8>length:raise ValueError('DXBC chunk offset rejected')
            size=struct.unpack_from('<I',data,at+offset+4)[0]
            if size>length-offset-8:raise ValueError('DXBC chunk bounds rejected')
        result.append(dict(offset=at,bytes=length,sha256=sha(data[at:at+length])))
        at+=length
    return result


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--directory',required=True,type=Path)
    parser.add_argument('--image',required=True,type=Path)
    parser.add_argument('--image-base',required=True,type=lambda value:int(value,0))
    parser.add_argument('--links',required=True,type=Path)
    parser.add_argument('--hash-check',required=True,type=Path)
    parser.add_argument('--shader-directory',required=True,type=Path)
    parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args();raw=args.links.read_bytes();links=json.loads(raw)
    checked_raw=args.hash_check.read_bytes();checked=json.loads(checked_raw)
    if not checked['passed'] or checked['errors'] or checked['linksSha256']!=sha(raw) or checked['imageSha256']!=IMAGE_SHA:
        raise ValueError('Native hash receipt differs from these links')
    expected={'193025DF':'G1SFileResourceHash','DB59AAFC':'KTGL3DShaderRenderStateObjectHash'}
    fields=descriptors(args.image.read_bytes(),args.image_base,rva=0x14D360,count=2,expected_names=expected)
    folder=args.directory/'0002';catalog=ktgl.index(folder,'system')
    targets={candidate['objectId'] for group in links['shaderHashedLinks']['hashedGroupMatches'] for candidate in group['candidates']}
    selected={identity:[] for identity in targets};sources=[];errors=[];property_errors=[];all_ids=set();types=Counter()
    for row in (r for r in catalog if r['type']==0x20A6A0BB):
        data=ktgl.read(folder,row);source=dict(archive='system',entry=row,payloadBytes=len(data),sha256=sha(data))
        try:
            nodes=list(objects(data));source['records']=len(nodes)
            for node in nodes:
                if node['tag']!='IDOK0000':continue
                all_ids.add(node['id']);types[node['type']]+=1
                if node['propertyParseError']:
                    property_errors.append(dict(objectId=node['id'],objectType=node['type'],offset=node['offset'],
                        size=node['size'],rawSha256=node['rawSha256'],source=source,error=node['propertyParseError']))
                if node['id'] in targets:
                    node['source']=source;selected[node['id']].append(public(node))
            sources.append(source)
        except ValueError as error:errors.append(dict(source=source,error=str(error)))
    index_raw=(args.shader_directory/'system-index.json').read_bytes();index=json.loads(index_raw)
    archive_matches_raw=(args.shader_directory/'archive-matches.json').read_bytes();archive_matches=json.loads(archive_matches_raw)
    resources={};groups=[]
    for group in links['shaderHashedLinks']['hashedGroupMatches']:
        candidates=[]
        for candidate in group['candidates']:
            versions=selected[candidate['objectId']]
            if not versions:raise ValueError('Shader-table ObjectDB key absent in system archive')
            refs=set()
            for node in versions:
                if node['propertyParseError'] or [p['name'] for p in node['props']]!=[f['property'] for f in fields]:
                    raise ValueError('Shader object property schema differs')
                prop=node['props'][0]
                if prop['kind']!=5 or prop['count']!=1:raise ValueError('Shader resource reference shape differs')
                refs.add(prop['rawUint32'][0])
            for identity in sorted(refs):
                if identity in resources:continue
                matches=[row for row in catalog if f'{row["ktid"]:08X}'==identity and row['type']==0x7BCD279F]
                if len(matches)!=1:raise ValueError('G1S resource reference ambiguous or absent')
                source=matches[0];payload=ktgl.read(folder,source);containers=dxbc(payload)
                if not containers:raise ValueError('Referenced G1S has no bounded DXBC')
                existing=[row for row in index if int(row['ktid'],16)==int(identity,16)]
                if Counter((item['offset'],item['sha256']) for item in containers)!=Counter((item['offset'],item['sha256']) for item in existing):
                    raise ValueError('Installed G1S differs from the recorded catalog')
                for item in existing:
                    file=args.shader_directory/item['file']
                    if sha(file.read_bytes())!=item['sha256']:raise ValueError('Extracted DXBC Hash differs')
                    next(c for c in containers if c['offset']==item['offset'])['stage']=item['stage']
                resources[identity]=dict(archive='system',source=source,payloadBytes=len(payload),sha256=sha(payload),containers=containers,
                                        recordedCatalogMatches=True)
            shas={item['sha256'] for identity in refs for item in resources[identity]['containers']}
            metadata_candidates=[]
            for owner in archive_matches['entries']:
                matches=[item for item in owner['matches'] if item['sha256'] in shas]
                if matches:metadata_candidates.append(dict(shaderOwner=owner['shader'],fingerprint=owner['fingerprint'],
                    matchingArchiveSha256s=sorted({item['sha256'] for item in matches}),
                    fullCatalogCandidateBytecodes=owner['candidateBytecodes'],bytecodeIdentityVerified=False))
            candidates.append(dict(shaderId=candidate['shaderId'],objectName=candidate['objectName'],objectId=candidate['objectId'],
                resourceName=candidate['resourceName'],objectVersions=versions,resourceKtids=sorted(refs),
                shaderResourceIdenticalAcrossRecordedVersions=len(refs)==1,
                renderStateArraysIdenticalAcrossRecordedVersions=len({tuple(v['props'][1]['rawUint32']) for v in versions})==1,
                shaderOwnerMetadataCandidates=metadata_candidates))
        groups.append(dict(meshGroup=group['meshGroup'],candidates=candidates,gameDrawPassVerified=False))
    result=dict(mode='Installed files + pinned saved-image emulation; no game access',gameProcessAccess=False,
        imageSha256=IMAGE_SHA,linksSha256=sha(raw),hashCheckSha256=sha(checked_raw),
        systemIndexSha256=sha(index_raw),archiveMatchesSha256=sha(archive_matches_raw),
        shaderPropertyFunctionRva='14D360',descriptors=fields,parsedDoks=len(sources),uniqueObjectIds=len(all_ids),
        objectTypeCounts=types,sources=sources,errors=errors,propertyParseErrors=property_errors,
        groups=groups,resources=resources,gameDrawPassVerified=False,shaderBytecodeIdentityVerified=False,
        runtimeCallbackVerified=False,productionReady=False)
    args.out.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    if errors:raise ValueError('System DOK file parsing is incomplete')
    print(json.dumps(dict(systemDoks=len(sources),groupLinks=len(groups),resources=len(resources),
        dxbcContainers=sum(len(r['containers']) for r in resources.values()),
        shaderObjectVersions=sum(len(v) for v in selected.values()),unsupportedPropertyRecords=len(property_errors),gameProcessAccess=False)))


if __name__=='__main__':main()
