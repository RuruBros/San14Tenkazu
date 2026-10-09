"""Reflect archived DXBC in this Python process and compare read-only metadata.

No game attachment is made. Matches may be ambiguous: a metadata fingerprint
does not establish bytecode identity when several archived shaders share it.
"""
import argparse
import ctypes as c
import hashlib
import json
import os
import pathlib
import struct
import uuid
from season_shader_snapshot import canonical, signature_record, binding_record, fingerprint


class Reflector:
    def __init__(self):
        self.path=pathlib.Path(os.environ['SystemRoot'])/'System32/d3dcompiler_43.dll'
        self.sha=hashlib.sha256(self.path.read_bytes()).hexdigest()
        self.dll=c.WinDLL(str(self.path))
        self.reflect=self.dll.D3DReflect
        self.reflect.argtypes=(c.c_void_p,c.c_size_t,c.c_void_p,c.POINTER(c.c_void_p))
        self.reflect.restype=c.c_int32
        self.iid=c.create_string_buffer(uuid.UUID('0a233719-3960-4578-9d7c-203b8b1d9cc1').bytes_le)

    @staticmethod
    def method(pointer,slot,types=(),return_type=c.c_int32):
        vtable=c.cast(pointer,c.POINTER(c.c_void_p))[0]
        address=c.cast(vtable,c.POINTER(c.c_void_p))[slot]
        return c.WINFUNCTYPE(return_type,c.c_void_p,*types)(address)

    def inspect(self,data,descriptions=None):
        data=c.create_string_buffer(data);obj=c.c_void_p()
        result=self.reflect(data,len(data)-1,self.iid,c.byref(obj))
        if result<0 or not obj:raise ValueError('D3DReflect failed: '+hex(result&0xffffffff))
        try:
            desc=c.create_string_buffer(160)
            if self.method(obj,3,(c.c_void_p,))(obj,desc)<0:raise ValueError('Local GetDesc failed')
            raw=desc.raw
            if descriptions is not None and (struct.unpack_from('<I',raw)[0],*struct.unpack_from('<36I',raw,16)) not in descriptions:return None
            count_resource,count_input,count_output=struct.unpack_from('<3I',raw,24)
            arrays=[]
            for slot,count,limit in ((7,count_input,32),(8,count_output,32),(6,count_resource,128)):
                if count>limit:raise ValueError('Unexpected local reflection count')
                entries=[];getter=self.method(obj,slot,(c.c_uint32,c.c_void_p))
                for index in range(count):
                    item=c.create_string_buffer(40)
                    if getter(obj,index,item)<0:raise ValueError('Local metadata getter failed')
                    name=c.string_at(struct.unpack_from('<Q',item.raw)[0]).decode('ascii')
                    entries.append(binding_record(item.raw,name) if slot==6 else signature_record(item.raw,name))
                arrays.append(entries)
            inputs,outputs,resources=arrays;metadata=canonical(raw,inputs,outputs,resources)
            return dict(fingerprint=fingerprint(metadata),metadata=metadata)
        finally:self.method(obj,2)(obj)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--native',required=True,type=pathlib.Path)
    parser.add_argument('--directory',required=True,type=pathlib.Path)
    parser.add_argument('--out',required=True,type=pathlib.Path)
    args=parser.parse_args();native=json.loads(args.native.read_text());reflector=Reflector()
    descriptions={(e['metadata']['version'],*e['metadata']['counters']) for e in native['entries']}
    requested={e['fingerprint'] for e in native['entries']};files={}
    for row in json.loads((args.directory/'system-index.json').read_text()):
        if row['stage']!=0:continue
        file=row['file'];files.setdefault(file,dict(sha256=row['sha256'],ktids=set()))['ktids'].add(row['ktid'])
    for row in json.loads((args.directory/'index.json').read_text()):
        file=row['file'] if row['file'].endswith('.dxbc') else row['file']+'.dxbc'
        files.setdefault(file,dict(sha256=row['sha256'],ktids=set()))
    matches={key:[] for key in requested};errors=[]
    for index,(file,origin) in enumerate(files.items()):
        try:
            data=(args.directory/file).read_bytes()
            if hashlib.sha256(data).hexdigest()!=origin['sha256']:raise ValueError('Archived DXBC hash changed')
            result=reflector.inspect(data,descriptions)
            if result and result['fingerprint'] in requested:
                matches[result['fingerprint']].append(dict(file=file,sha256=origin['sha256'],ktids=sorted(origin['ktids'])))
        except ValueError as error:errors.append(dict(file=file,error=str(error)))
        if (index+1)%5000==0:print(json.dumps(dict(archiveShadersChecked=index+1,total=len(files))),flush=True)
    output=dict(mode='D3DReflect only in analysis process; no game attachment',
        compilerPath=str(reflector.path),compilerSha256=reflector.sha,checked=len(files),errors=errors,
        entries=[dict(shader=e['shader'],fingerprint=e['fingerprint'],matches=matches[e['fingerprint']],
                      candidateBytecodes=len({m['sha256'] for m in matches[e['fingerprint']]}),
                      uniqueCatalogCandidate=len({m['sha256'] for m in matches[e['fingerprint']]})==1,
                      bytecodeIdentityVerified=False) for e in native['entries']])
    args.out.parent.mkdir(parents=True,exist_ok=True);args.out.write_text(json.dumps(output,indent=2),encoding='utf-8')
    print(json.dumps(dict(checked=len(files),matched=sum(bool(e['matches']) for e in output['entries']),
                         uniqueCatalogCandidates=sum(e['uniqueCatalogCandidate'] for e in output['entries']),errors=len(errors))),flush=True)


if __name__=='__main__':main()
