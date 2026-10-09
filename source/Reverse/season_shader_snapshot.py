"""Read recognized shader reflection metadata without executing code in SAN14.

The layout below is accepted only after hashing the actual COM getter code.
GetDesc copies 160 bytes from +0x10. The binding getter copies 40-byte records
from +0xf8, and signature getters copy 32-byte records from +0x110/+0x128.
These are facts from the recorded runtime disassembly, not a general COM ABI.
No WriteProcessMemory, VirtualProtectEx, native calls, or GPU hooks are used.
"""
import argparse
import ctypes
import hashlib
import json
import pathlib
import struct
from game import Game, psapi
from graphics_probe import observe

EXE_HASH='87ed4e46590e0939c9efcaef14fa0f892917604dfda86e0a4be319fccffa2d6a'
GETTERS={
    3:(47,'3090034afc81e5e1d344a4decc405c4a4b18a54a446d692e1f8e66946137a32f'),
    6:(84,'af787f0e43c5c931c35e05682918e335de87d6aec343299d843fb95c7af72139'),
    7:(73,'68d410844553e04ca21d9e11cc6c2e2dc3772e54f468a4c421dcc15d5575557f'),
    8:(73,'8e92aa911980187e59757bca5fb3f93366e4dcd5ff63820540ecc01d150295f0')}


def read_exact(read,address,size):
    if type(address) is not int or address<=0 or not 0<=size<=0x10000:
        raise ValueError('Invalid metadata address or length')
    data=read(address,size)
    if data is None or len(data)!=size:raise ValueError('Metadata became unreadable')
    return data


def read_name(read,address):
    data=bytearray()
    while len(data)<256:
        at=address+len(data);block=read_exact(read,at,min(32,4096-(at&4095),256-len(data)))
        data.extend(block)
        if 0 in block:return bytes(data).split(b'\0')[0].decode('ascii',errors='strict')
    raise ValueError('Metadata string exceeds 256 bytes')


def signature_record(data,name):
    semantic,index,register,system,component,mask,rw,stream=struct.unpack('<Q4I2B2xI',data[:32])
    return dict(name=name,index=index,register=register,system=system,component=component,mask=mask,rw=rw,stream=stream)


def binding_record(data,name):
    pointer,kind,slot,count,flags,result,dimension,samples=struct.unpack('<Q7I',data[:36])
    return dict(name=name,kind=kind,slot=slot,count=count,flags=flags,result=result,dimension=dimension,samples=samples)


def canonical(desc,inputs,outputs,resources):
    # Omit process addresses, ABI padding, and optional min-precision field not exposed by the legacy getter.
    return dict(version=struct.unpack_from('<I',desc)[0],
                counters=list(struct.unpack_from('<36I',desc,16)),inputs=inputs,outputs=outputs,resources=resources)


def fingerprint(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()


def snapshot_reflection(read,reflection,expected=GETTERS):
    header=read_exact(read,reflection,0x138)
    vt=struct.unpack_from('<Q',header)[0];methods=read_exact(read,vt,9*8)
    for slot,(size,sha) in expected.items():
        method=struct.unpack_from('<Q',methods,slot*8)[0]
        if hashlib.sha256(read_exact(read,method,size)).hexdigest()!=sha:
            raise ValueError('Unrecognized reflection getter code: '+str(slot))
    desc=header[0x10:0xb0]
    counts=struct.unpack_from('<3I',desc,24)
    arrays=[]
    for count,offset,stride,limit in zip(counts,(0xf8,0x110,0x128),(40,32,32),(128,32,32)):
        pointer,private_count=struct.unpack_from('<Q4xI',header,offset)
        if private_count!=count or count>limit:raise ValueError('Inconsistent reflection table count')
        raw=read_exact(read,pointer,count*stride) if count else b''
        entries=[]
        for i in range(count):
            item=raw[i*stride:(i+1)*stride];name=read_name(read,struct.unpack_from('<Q',item)[0])
            entries.append(binding_record(item,name) if stride==40 else signature_record(item,name))
        arrays.append(entries)
    creator=read_name(read,struct.unpack_from('<Q',desc,8)[0])
    after=read_exact(read,reflection,0x138)
    if header[:8]+header[12:]!=after[:8]+after[12:] or read_exact(read,vt,9*8)!=methods:
        raise ValueError('Reflection identity changed during read')
    resources,inputs,outputs=arrays;value=canonical(desc,inputs,outputs,resources)
    return dict(creator=creator,fingerprint=fingerprint(value),metadata=value,
                getterCodeSha256={str(k):v[1] for k,v in expected.items()},signatureStride=32)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--identities',required=True,type=pathlib.Path)
    parser.add_argument('--trace',required=True,type=pathlib.Path)
    parser.add_argument('--out',required=True,type=pathlib.Path)
    args=parser.parse_args();g=Game()
    trace=json.loads(args.trace.read_text());previous=trace['state']
    if previous['pid']!=g.pid or previous['exe']!=hex(g.exe):raise ValueError('Shader candidates belong to another process')
    path=ctypes.create_unicode_buffer(32768)
    if not psapi.GetModuleFileNameExW(ctypes.c_void_p(g.h),ctypes.c_void_p(g.exe),path,len(path)):
        raise ctypes.WinError(ctypes.get_last_error())
    if hashlib.sha256(pathlib.Path(path.value).read_bytes()).hexdigest()!=EXE_HASH:raise ValueError('Unverified executable')
    before=observe(g);accepted=[];rejected=[]
    for candidate in json.loads(args.identities.read_text()):
        try:
            owner=int(candidate['owner'],16);identity=read_exact(g.read,owner,48)
            owner_vtable,reflection,shader=(struct.unpack_from('<Q',identity,n)[0] for n in (0,16,40))
            if owner_vtable!=g.exe+0x14beb90 or shader!=int(candidate['shader'],16):
                raise ValueError('Shader owner identity changed')
            result=snapshot_reflection(g.read,reflection)
            if read_exact(g.read,owner,48)!=identity:raise ValueError('Shader owner changed during read')
            accepted.append(dict(owner=candidate['owner'],shader=candidate['shader'],reflection=hex(reflection),**result))
        except (ValueError,UnicodeError) as error:rejected.append(dict(owner=candidate['owner'],reason=str(error)))
    after=observe(g);output=dict(mode='ReadProcessMemory only',exeSha256=EXE_HASH,before=before,after=after,
        calendarChanged=before['clock']!=after['clock'],entries=accepted,rejected=rejected,
        coverage='Recognized current shader-owner metadata; not a new draw trace or proof of season application')
    args.out.parent.mkdir(parents=True,exist_ok=True);args.out.write_text(json.dumps(output,indent=2),encoding='utf-8')
    print(json.dumps(dict(accepted=len(accepted),rejected=len(rejected),before=before['date'],after=after['date'],mode=output['mode'])))


if __name__=='__main__':main()
