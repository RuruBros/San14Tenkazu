"""Read-only ownership audit for the retired one-shot command capability probe.

The authorized native query completed, but its controller failed to handle
early self-restoration and left page protection pending. That live method is
retired under the user's no-repeat instruction; this CLI performs no writes.
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import hashlib
import json
import pathlib
import struct
import time
from game import Game, k32, psapi
from image import Image
from season_watch import read_snapshot
from season_trace_contract import check_identity
from season_shader_snapshot import EXE_HASH


def exports(g, wanted):
    modules=(ctypes.c_void_p*1024)();needed=wt.DWORD()
    if not psapi.EnumProcessModulesEx(ctypes.c_void_p(g.h),modules,ctypes.sizeof(modules),ctypes.byref(needed),3):
        raise ctypes.WinError(ctypes.get_last_error())
    for base in modules[:needed.value//8]:
        name=ctypes.create_unicode_buffer(260)
        psapi.GetModuleBaseNameW(ctypes.c_void_p(g.h),ctypes.c_void_p(base),name,len(name))
        if name.value.lower()!='kernelbase.dll':continue
        header=g.read(base,0x1000);pe=struct.unpack_from('<I',header,0x3c)[0]
        rva,size=struct.unpack_from('<II',header,pe+24+112)
        row=struct.unpack('<IIHHIIIIIII',g.read(base+rva,40));result={}
        for index in range(row[7]):
            at=struct.unpack('<I',g.read(base+row[9]+4*index,4))[0]
            symbol=g.read(base+at,128).split(b'\0')[0].decode('ascii')
            if symbol not in wanted:continue
            ordinal=struct.unpack('<H',g.read(base+row[10]+index*2,2))[0]
            target=struct.unpack('<I',g.read(base+row[8]+ordinal*4,4))[0]
            if rva<=target<rva+size:raise RuntimeError('Forwarded bootstrap API is not accepted')
            region=g.region(base+target)
            if not region or region[3]&0xff not in (0x20,0x40,0x80):raise RuntimeError('Bootstrap API is not executable')
            result[symbol]=base+target
        if set(result)!=set(wanted):raise RuntimeError('Missing native bootstrap APIs')
        return result
    raise RuntimeError('KernelBase is unavailable')


def verify_code(g,im,start):
    # Include each reachable chained fragment, not just .pdata's first fragment.
    instructions=im.flow(start)
    if not instructions:raise RuntimeError('Missing preserved engine code')
    chunks=[]
    for instruction in instructions:
        if chunks and chunks[-1][1]==instruction.address:chunks[-1]=(chunks[-1][0],instruction.address+instruction.size)
        else:chunks.append((instruction.address,instruction.address+instruction.size))
    for begin,end in chunks:
        if g.read(g.exe+begin,end-begin)!=im.img[begin:end]:raise RuntimeError(f'Engine code changed at {begin:#x}')
    return dict(rva=hex(start),ranges=[[hex(a),hex(b)] for a,b in chunks],
                sha256=hashlib.sha256(b''.join(im.img[a:b] for a,b in chunks)).hexdigest())


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--out',required=True,type=pathlib.Path)
    parser.add_argument('--live',action='store_true');args=parser.parse_args()
    if args.live:
        raise RuntimeError("Live command probe retired after controller readback/protection failure; read-only audit is available")
    g=Game();im=Image();args.out.mkdir(parents=True,exist_ok=True)
    path=ctypes.create_unicode_buffer(32768)
    if not psapi.GetModuleFileNameExW(ctypes.c_void_p(g.h),ctypes.c_void_p(g.exe),path,len(path)):
        raise ctypes.WinError(ctypes.get_last_error())
    if hashlib.sha256(pathlib.Path(path.value).read_bytes()).hexdigest()!=EXE_HASH:raise RuntimeError('Unverified executable')
    before=read_snapshot(g);time.sleep(1);check_identity(before,read_snapshot(g),False)
    evidence=[verify_code(g,im,start) for start in (0x95b0a0,0x95b240,0x957ea0,0x95cda0,0x957140,0x966540)]
    slot,original=g.exe+0x14bd3c0,g.exe+0x957140
    if g.q(slot)!=original:raise RuntimeError('Engine command table is already redirected')
    apis=exports(g,{'GetCurrentThreadId','GetTickCount64'})
    # The queued command record's worker driver has the base driver vtable,
    # not the scene manager's main driver vtable. Retain this distinction.
    driver=int(before['driver'],16);ring=g.q(driver+0x2b70)
    capacity=struct.unpack('<I',g.read(driver+0x60,4))[0]
    if not ring or not 1<=capacity<=512:raise RuntimeError('Invalid command ring capacity')
    records=g.read(ring,capacity*32)
    workers={struct.unpack_from('<Q',records,index*32+16)[0] for index in range(capacity)}-{0}
    matches=[worker for worker in workers if g.q(worker)==g.exe+0x14bd760 and g.q(worker+0x10)==int(before['immediate'],16)]
    if not matches:raise RuntimeError('The verified queued worker driver was not observed')
    audit=dict(mode='Read-only engine command ownership audit',state=before,code=evidence,
               workerDrivers=[hex(v) for v in matches],apis={k:hex(v) for k,v in apis.items()})
    (args.out/'audit.json').write_text(json.dumps(audit,indent=2),encoding='utf-8')
    print(json.dumps(audit))


if __name__=='__main__':main()
