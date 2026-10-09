"""Briefly observe the immediate context's native PSSetShaderResources calls."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import pathlib
import struct
import time

from game import k32,psapi,_MODINFO
from graphics_gpu import Frame,driver_path,object_text


def wrapper(helper,data):
    # Preserve native arguments, add data, native caller and untouched R15.
    return (bytes.fromhex('4883ec48')+b'\x48\xb8'+struct.pack('<Q',data)+
            bytes.fromhex('4889442420488b4424484889442428')+
            bytes.fromhex('4c897c2430')+
            bytes.fromhex('488d4424484889442438')+
            b'\x48\xb8'+struct.pack('<Q',helper)+bytes.fromhex('ffd04883c448c3'))


def selftest(code,entry):
    import unicorn as u
    from unicorn import x86_const as x
    for enabled,start_count,capacity in ((1,0,4),(0,0,4),(1,4,4)):
        uc=u.Uc(u.UC_ARCH_X86,u.UC_MODE_64)
        for p,size in ((0x100000,0x1000),(0x200000,0x1000),(0x300000,0x2000),
                       (0x400000,0x1000),(0x500000,0x1000),(0x600000,0x1000),
                       (0x700000,0x1000),(0x800000,0x20000)):
            uc.mem_map(p,size)
        uc.mem_write(0x100000,wrapper(0x200000+entry,0x300000))
        uc.mem_write(0x200000,code);uc.mem_write(0x400000,b'\xc3')
        uc.mem_write(0x300000,struct.pack('<Q4IQ',0x400000,start_count,enabled,capacity,0,0x700000))
        uc.mem_write(0x600000,struct.pack('<3Q',0x111,0x222,0x333))
        rsp=0x810008;uc.mem_write(rsp,struct.pack('<Q',0x700000))
        uc.mem_write(rsp+0x68,struct.pack('<2Q',0xabc123,0xdef123))
        uc.reg_write(x.UC_X86_REG_RSP,rsp)
        args=(0x500000,2,3,0x600000)
        argregs=(x.UC_X86_REG_RCX,x.UC_X86_REG_RDX,x.UC_X86_REG_R8,x.UC_X86_REG_R9)
        for reg,value in zip(argregs,args):uc.reg_write(reg,value)
        saved=(x.UC_X86_REG_RBX,x.UC_X86_REG_RBP,x.UC_X86_REG_RSI,x.UC_X86_REG_RDI,
               x.UC_X86_REG_R12,x.UC_X86_REG_R13,x.UC_X86_REG_R14,x.UC_X86_REG_R15)
        for i,reg in enumerate(saved):uc.reg_write(reg,0x123000+i)
        calls=[]
        def hook(cpu,address,size,user):
            if address==0x400000:
                calls.append(tuple(cpu.reg_read(reg) for reg in argregs))
            if address==0x700000:cpu.emu_stop()
        uc.hook_add(u.UC_HOOK_CODE,hook)
        uc.emu_start(0x100000,0x700001,count=10000)
        assert calls==[args] and uc.reg_read(x.UC_X86_REG_RSP)==rsp+8
        assert all(uc.reg_read(reg)==0x123000+i for i,reg in enumerate(saved))
        count=struct.unpack('<I',uc.mem_read(0x300008,4))[0]
        assert count==start_count+(1 if enabled else 0)
        first=bytes(uc.mem_read(0x300020,184))
        if enabled and start_count==0:
            assert struct.unpack_from('<QQII3Q',first)==(args[0],0x700000,2,3,0x111,0x222,0x333)
            assert struct.unpack_from('<Q',first,152)[0]==0x123007
            assert struct.unpack_from('<2Q',first,160)==(0xdef123,0xabc123)
            assert struct.unpack_from('<I',first,176)[0]==1
        else:assert first==bytes(184)
    print('Trace helper ABI, exact native forwarding, disabled and full-buffer cases passed',flush=True)


def d3d11_range(g):
    mods=(ctypes.c_void_p*1024)();need=wt.DWORD()
    if not psapi.EnumProcessModulesEx(ctypes.c_void_p(g.h),mods,ctypes.sizeof(mods),ctypes.byref(need),3):
        raise ctypes.WinError(ctypes.get_last_error())
    for base in mods[:need.value//8]:
        name=ctypes.create_unicode_buffer(260)
        psapi.GetModuleBaseNameW(ctypes.c_void_p(g.h),ctypes.c_void_p(base),name,260)
        if name.value.lower()=='d3d11.dll':
            mi=_MODINFO()
            psapi.GetModuleInformation(ctypes.c_void_p(g.h),ctypes.c_void_p(base),ctypes.byref(mi),ctypes.sizeof(mi))
            return base,mi.size
    raise RuntimeError('D3D11 is not loaded')


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--obj',type=pathlib.Path,required=True)
    p.add_argument('--out',type=pathlib.Path)
    p.add_argument('--catalog',type=pathlib.Path)
    p.add_argument('--self-test',action='store_true')
    a=p.parse_args();code,entry=object_text(a.obj,'gpu_trace');selftest(code,entry)
    if a.self_test:return
    if not a.out or not a.catalog:raise RuntimeError('Output and catalog are required')
    f=Frame(a.out);g=f.g;context=driver_path(g)['immediate'];vt=g.q(context);slot=vt+8*8;original=g.q(slot)
    dll,length=d3d11_range(g)
    if not dll<=original<dll+length:raise RuntimeError('PSSetShaderResources is already redirected')
    size=0xC0000;capacity=4096
    page=k32.VirtualAllocEx(ctypes.c_void_p(f.h),ctypes.c_void_p(0),ctypes.c_size_t(size),0x3000,0x04)
    if not page:raise ctypes.WinError(ctypes.get_last_error())
    data=page+0x3000;helper=page+0x2000
    f.write(page,wrapper(helper+entry,data));f.write(helper,code)
    f.write(data,struct.pack('<Q4IQ',original,0,1,capacity,0,g.exe+0x9666C9))
    for address in (page,helper):
        f.protect(address,0x1000,0x20)
        k32.FlushInstructionCache(ctypes.c_void_p(f.h),ctypes.c_void_p(address),0x1000)
    f.log('ps_trace_ready',context=hex(context),vtable=hex(vt),original=hex(original),page=hex(page),capacity=capacity)
    old=f.protect(slot,8,0x04)
    try:
        f.write(slot,struct.pack('<Q',page))
        time.sleep(1)
    finally:
        f.write(data+12,struct.pack('<I',0))
        f.write(slot,struct.pack('<Q',original));f.protect(slot,8,old)
    time.sleep(0.1);f.assert_identity()
    if g.q(slot)!=original:raise RuntimeError('PSSetShaderResources restoration failed')
    count=struct.unpack('<I',g.read(data+8,4))[0]
    raw=g.read(data+32,min(count,capacity)*184)
    (a.out/'ps-calls.bin').write_bytes(raw)
    rows=[]
    for i in range(min(count,capacity)):
        at=i*184;ctx,caller,start,n=struct.unpack_from('<QQII',raw,at)
        if struct.unpack_from('<I',raw,at+176)[0]!=1:continue
        views=struct.unpack_from('<16Q',raw,at+24)[:min(n,16)]
        r15=struct.unpack_from('<Q',raw,at+152)[0]
        ancestor,savedrbx=struct.unpack_from('<2Q',raw,at+160)
        rows.append(dict(context=hex(ctx),caller=hex(caller),caller_rva=hex(caller-g.exe) if g.exe<=caller<g.exe+g.exe_size else None,
                         start=start,count=n,views=[hex(v) for v in views],native_r15=hex(r15),
                         ancestor=hex(ancestor),ancestor_rva=hex(ancestor-g.exe) if g.exe<=ancestor<g.exe+g.exe_size else None,
                         saved_rbx=hex(savedrbx)))
    liveviews={}
    for e in json.loads(a.catalog.read_text())['entries']:
        c=e['candidate'];field=int(c['resource_field'],16)
        if g.q(int(c['wrapper'],16))!=g.exe+int(c['vtable'],16) or g.q(field)!=int(e['resource'],16):continue
        for v in struct.unpack('<2Q',g.read(field+8,16)):
            if v:liveviews[hex(v)]=e
    unique=sorted({v for r in rows for v in r['views'] if v!='0x0'})
    matches={v:liveviews[v] for v in unique if v in liveviews}
    result=dict(state=f.state(),original=hex(original),slot_restored=True,count=count,capacity=capacity,
                calls=rows,unique_views=unique,known_assets=matches)
    (a.out/'ps-calls.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    f.log('ps_trace_complete',calls=count,complete_records=len(rows),unique_views=len(unique),known_assets=len(matches),slot_restored=True)
    print(json.dumps([r for r in rows if '0x287750d49e8' in r['views']]),flush=True)


if __name__=='__main__':main()
