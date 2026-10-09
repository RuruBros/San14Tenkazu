"""Exercise only TextureChecks' own process; no game handle, Hook or browser."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT.parent/'Reverse/.cache/season-safety/20261007-textures'
EXE = ROOT/'Native/TextureChecks.exe'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def fixture(climate=False):
    words = [0]*37
    for index, value in {0:0x20534444, 1:124, 2:0x100F if climate else 0x2100F,
                         3:1, 4:1, 5:8 if climate else 4, 7:1, 19:32, 20:4,
                         21:0x30315844, 27:0x1008 if climate else 0x401008,
                         32:16 if climate else 91, 33:3, 35:12 if climate else 1}.items():
        words[index] = value
    payload = bytes(96) if climate else bytes([0,0,0,255])
    return bytearray(struct.pack('<37I', *words)+payload)


def rejected(name, data, reason, role=1, pinned=None):
    path = CACHE/f'rejected-{name}.dds'
    path.write_bytes(data)
    listing = CACHE/f'rejected-{name}.tsv'
    listing.write_text(f'SeasonTextureList\t1\n{role}\t{pinned or sha(data)}\t{path}\n', encoding='utf-8')
    run = subprocess.run([str(EXE),str(listing)],capture_output=True,text=True,timeout=30)
    assert run.returncode != 0 and reason in run.stderr and 'deviceCreated=false' in run.stderr, (name,run.stdout,run.stderr)
    (CACHE/f'rejected-{name}.log').write_text(run.stdout+run.stderr,encoding='utf-8')
    return dict(case=name,rejected=True,beforeDeviceCreation=True,reason=reason)


def main():
    contract = json.loads((CACHE/'texture-contract.json').read_text(encoding='utf-8-sig'))
    raw = (CACHE/'texture-warp.json').read_bytes()
    warp = json.loads(raw.decode('utf-16' if raw.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8-sig'))
    resources = contract['resources']
    assert warp['resources'] == len(resources) == 42 and warp['bitExactReadback'] and warp['failures'] == 0
    assert warp['subresources'] == sum(len(item['parts']) for item in resources) == 428
    assert warp['payloadBytesChecked'] == sum(item['PayloadBytes'] for item in resources)
    assert warp['maxSimultaneousTexturePayloadBytes'] == max(item['PayloadBytes'] for item in resources)*2
    (CACHE/'texture-warp.json').write_text(json.dumps(warp,indent=2)+'\n',encoding='utf-8')
    results = []
    for index,value,reason in [(0,0,'header'),(1,123,'header'),(2,0,'flags/depth'),(3,3,'dimensions'),
                               (4,0xFFFFFFFF,'dimensions'),(5,99,'row pitch'),(6,1,'flags/depth'),
                               (7,14,'mip/layer'),(8,1,'reserved header'),(19,31,'header'),(20,0,'header'),
                               (21,0,'header'),(22,32,'legacy pixel'),(27,0,'resource flags'),(28,0x200,'volume/cube'),
                               (32,87,'format differs'),(33,4,'resource flags'),(34,4,'resource flags'),
                               (35,6,'mip/layer'),(36,2,'resource flags')]:
        data=fixture();struct.pack_into('<I',data,index*4,value)
        results.append(rejected(f'header-{index}',data,reason))
    data=fixture()
    results.append(rejected('hash',data,'SHA256 mismatch',pinned='0'*64))
    results.append(rejected('role',data,'role/hash/path',role=9))
    results.append(rejected('truncated',data[:-1],'Truncated season DDS payload'))
    results.append(rejected('trailing',data+b'\0','trailing season DDS payload'))
    data[151]=0;results.append(rejected('terrain-alpha',data,'Non-opaque terrain'))
    for name,temperature,rain in [('nan',float('nan'),0),('infinity',float('inf'),0),('temperature',61,0),('negative-rain',0,-1),('rain-limit',0,3001)]:
        data=fixture(True);struct.pack_into('<ff',data,148,temperature,rain)
        results.append(rejected(name,data,'Invalid season climate texel',role=5))
    summary=dict(gameAccess=False,productionReady=False,upload=warp,rejectionCases=len(results),rejections=results,
                 nativeExeSha256=sha(EXE.read_bytes()),listSha256=sha((CACHE/'verified-textures.tsv').read_bytes()))
    (CACHE/'upload-validation.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(resources=warp['resources'],subresources=warp['subresources'],bitExactReadback=True,
                          rejectedBeforeDeviceCreation=len(results),gameAccess=False)))


if __name__ == '__main__':
    main()
