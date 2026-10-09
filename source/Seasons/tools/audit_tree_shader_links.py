"""Read installed archives only; audit exact IDOK/group values without live calls."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import struct
from collections import Counter
import ktgl
from shader_name_hash import name_hash

TREE_MANIFEST=Path(__file__).resolve().parents[2]/'Reverse/.cache/season-assets/trees/manifest.json'
OUT=Path(__file__).resolve().parents[2]/'Reverse/.cache/season-safety/20261007-resource-links'
DOK_TYPE=0x20A6A0BB
PROP_NAME=0xC69EB271
PROP_MODEL=0x0B6E1578
PROP_G1M=0x8AB68B3F
PROP_SHADER_TABLE=0xAD260326
SHADER_TABLE_TYPE=0x56D8DEDA


def sha(data):
    return hashlib.sha256(data).hexdigest()


def objects(data):
    if len(data)<28 or data[:8]!=b'_DOK0000':
        raise ValueError('Unsupported DOK header')
    at=struct.unpack_from('<I',data,8)[0]
    if at<28 or at>len(data):
        raise ValueError('DOK start offset rejected')
    while at<len(data):
        aligned=(at+3)&~3
        if aligned>at and aligned<=len(data) and not any(data[at:aligned]):
            at=aligned
        if at==len(data):return
        tag=data[at:at+8]
        if tag not in (b'IDOK0000',b'RDOK0000'):
            # Keep unread trailing data visible; never search inside a property's
            # bytes and mistakenly treat an embedded string as another object.
            if any(data[at:]):
                raise ValueError(f'Non-record DOK tail at {at:#x}')
            return
        header=28 if tag==b'RDOK0000' else 24
        if at+header>len(data):
            raise ValueError('Truncated IDOK header')
        size,identity,kind=struct.unpack_from('<3I',data,at+8)
        raw_context=struct.unpack_from('<I',data,at+20)[0] if tag==b'RDOK0000' else None
        count=struct.unpack_from('<I',data,at+header-4)[0]
        if size<header or at+size>len(data) or count>4096 or header+count*12>size:
            raise ValueError('IDOK record/descriptor bounds rejected')
        cursor=at+header+count*12;props=[];error=None
        try:
            for index in range(count):
                value_kind,n,name=struct.unpack_from('<3I',data,at+header+index*12)
                # Same element sizes as the existing model resolver. When another
                # object type does not fit, retain its bounded raw record only.
                if value_kind>12:raise ValueError(f'Unsupported property kind {value_kind}')
                element=1 if value_kind in (0,1,2,10,12) else 2 if value_kind==3 else 8 if value_kind==9 else 4
                length=n*element
                if length>at+size-cursor:raise ValueError('IDOK property bounds rejected')
                raw=data[cursor:cursor+length]
                props.append(dict(name=f'{name:08X}',kind=value_kind,count=n,elementBytes=element,
                                  offset=cursor-at,raw=raw))
                cursor+=length
            tail=data[cursor:at+size]
            if len(tail)>3 or any(tail):raise ValueError('Property sizes do not cover record')
        except ValueError as exception:
            error=str(exception);props=[]
        yield dict(id=f'{identity:08X}',type=f'{kind:08X}',offset=at,size=size,props=props,
                   tag=tag.decode('ascii'),rawContext=f'{raw_context:08X}' if raw_context is not None else None,
                   propertyParseError=error,rawSha256=sha(data[at:at+size]))
        at+=size


def public(node):
    result={key:value for key,value in node.items() if key!='props'}
    result['props']=[]
    for prop in node['props']:
        value={key:item for key,item in prop.items() if key!='raw'}
        raw=prop['raw'];value['rawHex']=raw.hex()
        if prop['elementBytes']==4:
            value['rawUint32']=[f'{item:08X}' for item in struct.unpack(f'<{prop["count"]}I',raw)]
        if prop['name']==f'{PROP_NAME:08X}':
            value['textPreview']=raw.rstrip(b'\0').decode('ascii',errors='backslashreplace')
        result['props'].append(value)
    return result


def first(node,name):
    for prop in node['props']:
        if prop['name']==f'{name:08X}' and prop['elementBytes']==4 and prop['count']:
            return f'{struct.unpack_from("<I",prop["raw"])[0]:08X}'
    return None


def shader_table(folder,models):
    """Resolve the exact shared model property, without assigning a Draw role."""
    for model in models:
        props=[p for p in model['props'] if p['name']==f'{PROP_SHADER_TABLE:08X}']
        if len(props)!=1 or props[0]['kind']!=5 or props[0]['count']!=1:
            raise ValueError('Shader-table reference property shape differs')
    identities={first(model,PROP_SHADER_TABLE) for model in models}
    if None in identities or len(identities)!=1:
        raise ValueError('Tree model shader-table references differ or are absent')
    identity=int(next(iter(identities)),16)
    candidates=[row for row in ktgl.index(folder,'system')
                if row['ktid']==identity and row['type']==SHADER_TABLE_TYPE]
    if len(candidates)!=1:raise ValueError('Shader name table resource is ambiguous or missing')
    entry=candidates[0];data=ktgl.read(folder,entry)
    rows=list(csv.reader(data.decode('utf-8-sig').splitlines()))
    expected=['CE1ResourceShaderStandardID',' id at ObjectDB',' id at ResourceDB']
    if not rows or rows[0]!=expected or any(len(row)!=3 or not all(row) for row in rows[1:]):
        raise ValueError('Shader name table schema differs')
    (OUT/f'model-{PROP_SHADER_TABLE:08X}-{identity:08X}.bin').write_bytes(data)
    return dict(property=f'{PROP_SHADER_TABLE:08X}',archive='system',source=entry,
                payloadSha256=sha(data),payloadBytes=len(data),header=rows[0],
                rowCount=len(rows)-1,rows=rows[1:])


def shader_name_links(table,nodes):
    # Match exact bytes, including full-width brackets. This does not infer a
    # KTID hashing algorithm or mistake a float/int property for an object ID.
    targets={}
    for row in table['rows']:
        for encoding in ('utf-8','utf-16-le','cp932'):
            targets.setdefault(row[1].encode(encoding),[]).append((row,encoding))
    matches=[]
    for node in nodes.values():
        for prop in node['props']:
            # Retain the original field, kind and bytes in public(node).
            raw=prop['raw'];found=[]
            for value in (raw,raw[:-1] if raw.endswith(b'\0') else raw,
                          raw[:-2] if raw.endswith(b'\0\0') else raw):
                for row,encoding in targets.get(value,[]):
                    item=(tuple(row),encoding)
                    if item not in found:found.append(item)
            for row,encoding in found:
                matches.append(dict(shaderId=row[0],objectName=row[1],resourceName=row[2],
                                    encoding=encoding,matchedProperty=prop['name'],object=public(node)))
    return matches


def hashed_shader_links(table,nodes,groups):
    rows={};object_matches=0
    for row in table['rows']:
        group=f'{name_hash(row[0].encode("utf-8"),group=True):08X}'
        object_id=f'{name_hash(row[1].encode("utf-8")):08X}'
        object_matches+=object_id in nodes
        rows.setdefault(group,[]).append(dict(shaderId=row[0],objectName=row[1],resourceName=row[2],objectId=object_id))
    matches=[]
    for group,identity in groups.items():
        candidates=[]
        for row in rows.get(f'{identity:08X}',[]):
            candidates.append(row|dict(object=public(nodes[row['objectId']]) if row['objectId'] in nodes else None))
        matches.append(dict(meshGroup=group,candidates=candidates,uniqueTableCandidate=len(candidates)==1,
                            gameDrawPassVerified=False))
    return dict(tableRowsWithObjectIdMatch=object_matches,hashedGroupMatches=matches,
                groupHashCollisions=sum(len(items)>1 for items in rows.values()),
                ruleEvidence='Pinned image CSV loader F5111A/ABC290 and F51137..F51172; see hash-check.json')


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--directory',required=True,type=Path)
    args=parser.parse_args();folder=args.directory/'0002';OUT.mkdir(parents=True,exist_ok=True)
    source=TREE_MANIFEST.read_bytes();trees=json.loads(source)
    groups={group['RawName']:int(group['RawName'][1:],16) for tree in trees for group in tree['meshGroups']}
    targets={tree[field] for tree in trees for field in ('placementId','modelId')}
    rows=ktgl.index(folder,'KIDSSystemResource');doks=[row for row in rows if row['type']==DOK_TYPE]
    doks.sort(key=lambda row:row['bin'].endswith('bin22'))
    merged={};related={};sources=[];errors=[];types=Counter();raw_hits=[];property_errors=[]
    for processed,row in enumerate(doks,1):
        raw=ktgl.read(folder,row);origin={key:value for key,value in row.items() if key!='type'}
        origin['ktid']=f'{row["ktid"]:08X}';origin['sha256']=sha(raw)
        try:
            parsed=list(objects(raw));origin['objects']=len(parsed)
            for node in parsed:
                types[node['type']]+=1;node['source']=origin
                if node['propertyParseError']:
                    property_errors.append(dict(objectId=node['id'],objectType=node['type'],tag=node['tag'],
                        offset=node['offset'],size=node['size'],rawSha256=node['rawSha256'],source=origin,error=node['propertyParseError']))
                for prop in node['props']:
                    if prop['elementBytes']!=4:continue
                    values=struct.unpack(f'<{prop["count"]}I',prop['raw'])
                    for index,value in enumerate(values):
                        for group,identity in groups.items():
                            if value==identity:
                                raw_hits.append(dict(group=group,objectId=node['id'],objectType=node['type'],
                                    property=prop['name'],propertyKind=prop['kind'],index=index,source=origin))
                (merged if node['tag']=='IDOK0000' else related)[node['id']]=node
            sources.append(origin)
        except ValueError as exception:
            errors.append(dict(source=origin,error=str(exception)))
        if processed%1000==0:
            print(json.dumps(dict(parsedFiles=len(sources),errors=len(errors))),flush=True)
    models=[];model_nodes=[]
    for tree in trees:
        placement=merged.get(tree['placementId']);model=merged.get(tree['modelId'])
        if placement is None or model is None or first(placement,PROP_MODEL)!=tree['modelId'] or first(model,PROP_G1M)!=tree['modelKtid']:
            raise ValueError('Independent DOK/tree resolver mismatch: '+tree['Name'])
        models.append(dict(name=tree['Name'],placement=public(placement),model=public(model)))
        model_nodes.append(model)
    table=shader_table(folder,model_nodes)
    name_links=shader_name_links(table,merged)
    hashed_links=hashed_shader_links(table,merged,groups)
    direct=[public(nodes[f'{identity:08X}']) for nodes in (merged,related) for identity in groups.values() if f'{identity:08X}' in nodes]
    result=dict(mode='Installed archive files only; no game process/GPU calls',gameProcessAccess=False,
        treeManifestSha256=sha(source),parsedDoks=len(sources),dokCount=len(doks),errors=errors,propertyParseErrors=property_errors,
        uniqueObjects=len(merged),uniqueRelatedRecords=len(related),objectTypeCounts=types,meshGroupNames=sorted(groups),
        exactGroupObjectMatches=direct,rawGroupPropertyMatches=raw_hits,verifiedTreeResolverModels=len(models),
        models=models,sources=sources,gamePassIdentityVerified=False,shaderLinksVerified=False,
        shaderNameTable=table,exactShaderNameObjects=name_links,shaderHashedLinks=hashed_links)
    (OUT/'dok-links.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({key:result[key] for key in ('parsedDoks','dokCount','uniqueObjects','verifiedTreeResolverModels')}|
                     dict(parseErrors=len(errors),unsupportedPropertyRecords=len(property_errors),groupObjectMatches=len(direct),rawGroupPropertyMatches=len(raw_hits),
                          shaderTableRows=table['rowCount'],exactShaderNameObjects=len(name_links))),flush=True)


if __name__=='__main__':
    main()
