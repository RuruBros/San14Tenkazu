"""Independent file validation of local game derivatives. No game process/GPU access."""
import collections
import hashlib
import json
import pathlib
import struct
import numpy as np

ROOT=pathlib.Path(__file__).resolve().parents[1]
LOCAL=ROOT.parent/'Reverse/.cache/season-assets/trees'
CACHE=ROOT.parent/'Reverse/.cache/season-safety/20261006-trees'


def digest(data): return hashlib.sha256(data).hexdigest()


def geometry_sections(b):
    at=struct.unpack_from('<I',b,12)[0]
    for _ in range(struct.unpack_from('<I',b,20)[0]):
        size=struct.unpack_from('<I',b,at+8)[0]
        assert size>=12 and at+size<=len(b)
        if b[at:at+4]==b'GM1G':
            p=at+48
            for _ in range(struct.unpack_from('<I',b,at+44)[0]):
                kind,n,count=struct.unpack_from('<3I',b,p)
                assert n>=12 and p+n<=at+size
                yield kind,count,b[p+12:p+n]
                p+=n
        at+=size


def main():
    models=json.loads((LOCAL/'manifest.json').read_text(encoding='utf-8'))
    sources={};diffuse={};triangles=materials=mip_count=0;uv_ranges=[]
    for model in models:
        b=(LOCAL/(model['Name']+'.g1m')).read_bytes();assert digest(b)==model['modelSha256']
        slots={s['slot']:s['ktid'] for s in model['textureSlots']}
        mats={m['Index']:m for m in model['materials']}
        meshes={m['Index']:m for m in model['submeshes']}
        for kind,count,data in geometry_sections(b):
            if kind==0x10002:
                assert count==len(mats);p=0
                for i in range(count):
                    bindings=struct.unpack_from('<I',data,p+4)[0];p+=16
                    assert bindings==len(mats[i]['bindings'])
                    for actual in mats[i]['bindings']:
                        raw=struct.unpack_from('<6H',data,p);p+=12
                        assert tuple(actual['RawWords'])==raw
                        assert (actual['Slot'],actual['TypeId'])==(raw[0],raw[2])
                        assert actual['ktid']==slots[actual['Slot']]
            if kind==0x10008:
                assert count==len(meshes)
                for i in range(count):
                    raw=struct.unpack_from('<14i',data,i*56)
                    assert tuple(meshes[i]['RawWords'])==raw
                    assert (meshes[i]['VertexLayout'],meshes[i]['MaterialIndex'],meshes[i]['IndexBuffer'],meshes[i]['Primitive'],meshes[i]['IndexOffset'],meshes[i]['IndexCount'])==(raw[1],raw[6],raw[7],raw[9],raw[12],raw[13])
        uv_bytes=(LOCAL/model['uvFile']).read_bytes();assert digest(uv_bytes)==model['uvSha256']
        tris=json.loads(uv_bytes);assert len(tris)==model['triangles'];triangles+=len(tris);materials+=len(mats)
        counts=collections.Counter(t['submesh'] for t in tris)
        groups={g['Index']:g for g in model['meshGroups']}
        uv=[]
        for triangle in tris:
            mesh=meshes[triangle['submesh']];mat=mats[triangle['material']]
            assert triangle['material']==mesh['MaterialIndex'] and triangle['diffuseSlot']==mat['DiffuseSlot']
            assert triangle['diffuseKtid']==slots[triangle['diffuseSlot']]
            assert all(triangle['submesh'] in groups[i]['Submeshes'] for i in triangle['meshGroups'])
            assert set(triangle['meshGroups'])=={i for i,g in groups.items() if triangle['submesh'] in g['Submeshes']}
            assert np.isfinite([v for k in ('a','b','c','ua','ub','uc') for v in triangle[k]]).all()
            uv.extend(triangle[k] for k in ('ua','ub','uc'))
        for index,count in counts.items():
            assert meshes[index]['Primitive']==3 and count*3==meshes[index]['IndexCount']
        uv=np.array(uv);uv_ranges.append(dict(model=model['Name'],minimum=uv.min(axis=0).tolist(),maximum=uv.max(axis=0).tolist(),
                                             addressingVerified=False))
        assert digest((LOCAL/(model['Name']+'.png')).read_bytes())==model['previewSha256']
        assert model['unchangedOriginalPreview'] is True
        for texture in model['textures']:
            raw=(LOCAL/(texture['ktid']+'.g1t')).read_bytes();assert digest(raw)==texture['sha256']
            assert texture['ktid'] not in sources or sources[texture['ktid']]==texture['sha256']
            sources[texture['ktid']]=texture['sha256']
            if texture['diffuse']:diffuse[texture['ktid']]=texture
        assert not model['seasonVariantsBuilt'] and not model['leafBranchMasksVerified'] and not model['gamePassMappingVerified']
    max_alpha_drift=0
    for identifier,texture in diffuse.items():
        raw=(LOCAL/texture['cutoutDds']).read_bytes();assert digest(raw)==texture['cutoutSha256']
        words=struct.unpack_from('<37I',raw);assert words[32:37]==(91,3,0,1,0)
        w,h=words[4],words[3];base_length=w*h*4
        assert digest(raw[148:148+base_length])==texture['bgraSha256']
        offset=148;mean=texture['mips'][0]['MeanAlpha']
        for expected in texture['mips']:
            assert (w,h)==(expected['Width'],expected['Height'])
            n=w*h*4;image=np.frombuffer(raw[offset:offset+n],np.uint8).reshape(h,w,4)
            alpha=image[:,:,3].astype(np.float64)/255
            current=float(alpha.mean());drift=abs(current-mean);max_alpha_drift=max(max_alpha_drift,drift)
            assert drift<=.5/255+1e-6
            assert abs(current-expected['MeanAlpha'])<1e-10
            for cutoff,key in ((.1,'CoverageAt10'),(.5,'CoverageAt50'),(.9,'CoverageAt90')):
                assert abs(float((alpha>=cutoff).mean())-expected[key])<1e-10
            if offset==148:
                assert np.bincount(image[:,:,3].ravel(),minlength=256).tolist()==texture['alphaHistogram']
            offset+=n;w,h=max(1,w//2),max(1,h//2);mip_count+=1
        assert offset==len(raw) and (w,h)==(1,1) and words[7]==len(texture['mips'])
    result=dict(gameProcessAccess=False,gameGpuAccess=False,models=len(models),materials=materials,
                triangles=triangles,boundTextureSources=len(sources),diffuseAtlases=len(diffuse),mips=mip_count,
                unchangedOriginalPreviews=len(models),maxMeanAlphaDrift=max_alpha_drift,failures=0,uvRanges=uv_ranges,
                seasonVariantsBuilt=False,leafBranchMasksVerified=False,gamePassMappingVerified=False)
    (CACHE/'asset-validation.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k!='uvRanges'},indent=2))


if __name__=='__main__':main()
