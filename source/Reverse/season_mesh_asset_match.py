"""Compare saved actual Mesh GPU readbacks to an original extracted G1M.

No game process or GPU API is opened. Parsing follows GameModels.cs sections;
all reads and slices are bounded and the original model hash is required.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct


def sha(data):return hashlib.sha256(data).hexdigest()


def parse(data):
    def u(at):
        if at<0 or at+4>len(data):raise ValueError('Model integer exceeds bounds')
        return struct.unpack_from('<I',data,at)[0]
    def take(at,n):
        if at<0 or n<0 or at+n>len(data):raise ValueError('Model slice exceeds bounds')
        return data[at:at+n]
    at=u(12);geometry=None;version=None
    for _ in range(u(20)):
        name=take(at,4);size=u(at+8)
        if size<12:raise ValueError('Invalid model chunk')
        take(at,size)
        if name==b'GM1G':geometry=at;version=int(take(at+4,4)[::-1],16)
        at+=size
    if geometry is None:raise ValueError('No model geometry')
    vertices=[];indices=[];layouts=[];meshes=[];at=geometry+48
    for _ in range(u(geometry+44)):
        kind,size,n=u(at),u(at+4),u(at+8);take(at,size);q=at+12
        if kind==0x10004:
            for i in range(n):
                stride,count=u(q+4),u(q+8);q+=16 if version>0x30 else 12
                payload=take(q,stride*count);vertices.append(dict(stride=stride,count=count,offset=q,bytes=payload));q+=len(payload)
        elif kind==0x10007:
            for i in range(n):
                count,bits=u(q),u(q+4);q+=12 if version>0x30 else 8
                if bits not in (8,16,32):raise ValueError('Unknown model index type')
                payload=take(q,count*(bits//8));indices.append(dict(count=count,bits=bits,offset=q,bytes=payload));q=(q+len(payload)+3)&~3
        elif kind==0x10005:
            for i in range(n):
                count=u(q);q+=4;buffers=[u(q+j*4) for j in range(count)];q+=count*4
                count=u(q);q+=4;attributes=[]
                for j in range(count):
                    raw=take(q,8);buffer,offset=struct.unpack_from('<HH',raw)
                    attributes.append(dict(buffer=buffer,offset=offset,type=raw[4],semantic=raw[6],layer=raw[7]));q+=8
                layouts.append(dict(buffers=buffers,attributes=attributes))
        elif kind==0x10008:
            for i in range(n):
                take(q,56);meshes.append(dict(index=i,layout=u(q+4),material=u(q+24),indexBuffer=u(q+28),primitive=u(q+36),start=u(q+48),count=u(q+52)));q+=56
        at+=size
    return vertices,indices,layouts,meshes


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--draws',type=Path,required=True);p.add_argument('--assets',type=Path,required=True);p.add_argument('--model',required=True)
    a=p.parse_args();audit=json.loads((a.draws/'audit.json').read_text());read=audit['meshResourceRead']
    if not read['gpuReadbackCompleted'] or not audit['independentCodeAndProtectionReadback']:raise ValueError('Completed GPU readback/recovery required')
    manifest=json.loads((a.assets/'manifest.json').read_text());model=next(x for x in manifest if x['Name']==a.model)
    original=(a.assets/(a.model+'.g1m')).read_bytes()
    if sha(original)!=model['modelSha256']:raise ValueError('Original model differs from recorded source')
    def payload(b):
        data=(a.draws/b['file']).read_bytes()
        if len(data)!=b['bytes'] or sha(data)!=b['sha256']:raise ValueError('Actual GPU blob changed')
        return data
    blobs=read['blobs'];gpuV=next(b for b in blobs if b['kind']==1 and b['slot']==0);gpuI=next(b for b in blobs if b['kind']==2)
    vbytes=payload(gpuV);ibytes=payload(gpuI);vertices,indices,layouts,meshes=parse(original);accepted=[]
    for mesh in meshes:
        if mesh['count']!=read['indexCount'] or mesh['primitive']!=3:continue
        index=indices[mesh['indexBuffer']];n=index['bits']//8;start=mesh['start']*n
        if index['bytes'][start:start+mesh['count']*n]!=ibytes:continue
        layout=layouts[mesh['layout']]
        for position,bid in enumerate(layout['buffers']):
            vertex=vertices[bid]
            if vertex['stride']!=gpuV['stride'] or vertex['bytes']!=vbytes:continue
            accepted.append(dict(submesh=mesh,vertexBuffer=bid,vertexCount=vertex['count'],vertexSha256=sha(vbytes),indexSha256=sha(ibytes),layout=layout,
                sourceVertexAndIndexExact=True,actualInputLayoutDescriptorCaptured=False))
    result=dict(mode='Original G1M and saved actual GPU data only',model=a.model,modelSha256=sha(original),matches=accepted,
        actualVertexAndIndexMatchVerified=bool(accepted),finalTreePolicyVerified=False,worldCoordinatesVerified=False,completeFrame=False,productionReady=False)
    (a.draws/'geometry-source-comparison.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))


if __name__=='__main__':main()
