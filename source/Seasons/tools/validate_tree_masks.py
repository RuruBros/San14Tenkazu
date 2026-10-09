"""Run only owned WARP fixture processes; never opens SAN14 or a browser."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess

ROOT=Path(__file__).resolve().parents[1]
CACHE=ROOT.parent/'Reverse/.cache/season-safety/20261007-mask'


def main():
    folder=CACHE/'fixture';source=(folder/'fixture-source.dds').read_bytes()
    coverage=(folder/'fixture-coverage.dds').read_bytes();vectors=(folder/'sampling.bin').read_bytes();results=[]
    def rejected(name,reason,mask=coverage,diffuse=source,cases=vectors):
        path=CACHE/'rejected'/name;path.mkdir(parents=True,exist_ok=True)
        (path/'fixture-source.dds').write_bytes(diffuse);(path/'fixture-coverage.dds').write_bytes(mask);(path/'sampling.bin').write_bytes(cases)
        run=subprocess.run([str(ROOT/'Native/TreeMaskChecks.exe'),str(ROOT/'Shaders/SeasonTreeMipCheck.hlsl'),str(path)],
                           capture_output=True,text=True,timeout=30)
        assert run.returncode!=0 and reason in run.stderr and 'deviceCreated=false' in run.stderr,(name,run.stdout,run.stderr)
        (path/'result.log').write_text(run.stdout+run.stderr,encoding='utf-8');results.append(dict(name=name,reason=reason,beforeDeviceCreation=True))
    for name,value in [('nan',float('nan')),('infinity',float('inf')),('negative',-0.1),('excess',1.1)]:
        mask=bytearray(coverage);struct.pack_into('<f',mask,148,value);rejected(name,'Invalid leaf/branch coverage texel',mask=mask)
    diffuse=bytearray(source);diffuse[151]=254;rejected('source-alpha-mismatch','Coverage contributions differ',diffuse=diffuse)
    diffuse=bytearray(source);diffuse[151]=0;mask=bytearray(coverage);struct.pack_into('<2f',mask,148,0.0000005,0)
    rejected('transparent-tiny-coverage','Coverage contributions differ',diffuse=diffuse,mask=mask)
    for index,value in [(7,1),(35,2),(32,91)]:
        mask=bytearray(coverage);struct.pack_into('<I',mask,index*4,value)
        rejected('mask-header-'+str(index),'mip/layer' if index in (7,35) else 'format differs',mask=mask)
    rejected('mask-truncated','Truncated season DDS payload',mask=coverage[:-1])
    rejected('mask-trailing','Unexpected trailing',mask=coverage+b'\0')
    for name,offset,value,kind in [('policy',16+16,0,'I'),('lod',16+8,2.0,'f'),
                                   ('padding',16+20,1,'I'),('nonfinite-expected',16+32,float('nan'),'f')]:
        cases=bytearray(vectors);struct.pack_into('<'+kind,cases,offset,value)
        rejected(name,'expectation rejected' if name=='nonfinite-expected' else 'parameters rejected',cases=cases)
    # A valid mask DDS with an incompatible layout must be rejected as a pair.
    words=list(struct.unpack_from('<37I',coverage));words[4]=4;words[5]=32;words[7]=3
    wider=struct.pack('<37I',*words)+struct.pack('<2f',0,1)*7
    rejected('layout-mismatch','Coverage and diffuse mip layouts differ',mask=wider)
    # Recheck existing malformed texture fixtures after the shared parser change.
    previous=ROOT.parent/'Reverse/.cache/season-safety/20261007-textures'
    contract=json.loads((previous/'upload-validation.json').read_bytes())
    old_results=[]
    for row in contract['rejections']:
        run=subprocess.run([str(ROOT/'Native/TextureChecks.exe'),str(previous/('rejected-'+row['case']+'.tsv'))],
                           capture_output=True,text=True,timeout=30)
        assert run.returncode!=0 and row['reason'] in run.stderr and 'deviceCreated=false' in run.stderr,(row['case'],run.stderr)
        (CACHE/('existing-rejected-'+row['case']+'.log')).write_text(run.stdout+run.stderr,encoding='utf-8');old_results.append(row['case'])
    result=dict(gameProcessAccess=False,gameRenderModified=False,deviceCreatedForRejections=False,
                newRejections=results,existingRejections=old_results,
                sourceFixtureSha256=hashlib.sha256(source).hexdigest(),coverageFixtureSha256=hashlib.sha256(coverage).hexdigest(),
                samplingFixtureSha256=hashlib.sha256(vectors).hexdigest(),productionReady=False)
    (CACHE/'rejections.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(newCases=len(results),existingCases=len(old_results),allRejectedBeforeDeviceCreation=True,gameProcessAccess=False)))


if __name__=='__main__':main()
