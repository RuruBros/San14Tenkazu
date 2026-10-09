"""Execute the compiled trace and wrappers before installing a live observer."""
import pathlib
import struct
import hashlib
import json
import unicorn as u
from unicorn import x86_const as x
from graphics_gpu import object_text
from season_trace import wrapper, SYMBOLS, ARGUMENT_COUNTS, RECORD_SIZE, HEADER_SIZE


def check(kind, enabled, full, boundary=False, old_allocation=False, invalid_capacity=False, resources=False, capacity_override=None, count_override=None):
    symbols=SYMBOLS+('season_trace_views','season_trace_shader')
    if old_allocation:
        fixture=json.loads((pathlib.Path(__file__).parent/'fixtures/season-trace-missing-header.json').read_text())
        code,entry=bytes.fromhex(fixture['codeHex']),fixture['entry']
        assert hashlib.sha256(code).hexdigest()==fixture['sha256']
    else:
        code, entry = object_text(pathlib.Path(__file__).resolve().parent.parent/'Seasons/Native/Trace.obj', symbols[kind])
    mu = u.Uc(u.UC_ARCH_X86, u.UC_MODE_64)
    for address in (0x100000,0x200000,0x900000,0x400000,0x500000,0x600000,0x700000):
        mu.mem_map(address, ((8192*RECORD_SIZE + (0 if old_allocation else HEADER_SIZE)+4095)//4096)*4096 if boundary and address==0x900000 else 0x10000)
    argc = (ARGUMENT_COUNTS+(4,4))[kind]
    mu.mem_write(0x100000,wrapper(0x200000+entry,0x900000,argc))
    mu.mem_write(0x200000,code)
    mu.mem_write(0x400000,b'\xc3')
    initial=8191 if boundary else (1 if full else 0)
    capacity=8192 if boundary else 1
    if capacity_override is not None:capacity=capacity_override
    if count_override is not None:initial=count_override
    storage_capacity=0 if old_allocation or invalid_capacity else capacity
    mu.mem_write(0x900000,struct.pack('<4Q4I',*[0x400000]*4,enabled,initial,capacity,storage_capacity))
    mu.mem_write(0x600000,struct.pack('<Q',0x600100))
    for slot in range(120):
        mu.mem_write(0x600100+slot*8,struct.pack('<Q',0x500000+slot*16))
        mu.mem_write(0x500000+slot*16,b'\xc3')
    resource=0x604000;resource_vtable=0x604100
    mu.mem_write(resource,struct.pack('<Q',resource_vtable))
    mu.mem_write(resource_vtable+16,struct.pack('<Q',0x500020))
    rsp=0x708008;mu.mem_write(rsp,struct.pack('<Q',0x700100));mu.reg_write(x.UC_X86_REG_RSP,rsp)
    registers=(x.UC_X86_REG_RCX,x.UC_X86_REG_RDX,x.UC_X86_REG_R8,x.UC_X86_REG_R9)
    args=((0x600000,123,7,-9),(0x600000,123,7),
          (0x600000,123,2,7,-9,11),(0x600000,123,2,7,11),
          (0x600000,7,123,0x601000),(0x600000,123,0x601000,9))[kind]
    for r,value in zip(registers,args):mu.reg_write(r,value & 0xffffffffffffffff)
    for i,value in enumerate(args[4:],4):
        mu.mem_write(rsp+8+8*i,struct.pack('<Q',value & 0xffffffffffffffff))
    saved=(x.UC_X86_REG_RBX,x.UC_X86_REG_RBP,x.UC_X86_REG_RSI,x.UC_X86_REG_RDI,
           x.UC_X86_REG_R12,x.UC_X86_REG_R13,x.UC_X86_REG_R14,x.UC_X86_REG_R15)
    for i,r in enumerate(saved):mu.reg_write(r,0x100+i)
    calls=[];references=1
    def observe(cpu,address,size,user):
        nonlocal references
        if address==0x700100:cpu.emu_stop()
        elif address==0x400000:
            stack=cpu.reg_read(x.UC_X86_REG_RSP)
            actual=[cpu.reg_read(r) for r in registers[:min(4,argc)]]
            actual += [struct.unpack('<Q',cpu.mem_read(stack+8+8*i,8))[0] for i in range(4,argc)]
            assert actual[0]==args[0]
            assert [v & 0xffffffff for v in actual[1:]]==[v & 0xffffffff for v in args[1:]]
            calls.append('native')
        elif 0x500000<=address<0x500800:
            slot=(address-0x500000)//16;calls.append(slot)
            rdx,r8,r9=(cpu.reg_read(r) for r in registers[1:])
            if slot==2:
                assert resources and cpu.reg_read(registers[0])==resource
                references-=1
            elif slot in (74,76):
                mu.mem_write(rdx,struct.pack('<Q',resource if resources else 0));references+=int(resources)
            elif slot==89:
                mu.mem_write(r8,struct.pack('<4Q',*[resource if resources else 0]*4))
                mu.mem_write(r9,struct.pack('<Q',resource if resources else 0));references+=5*int(resources)
            elif slot in (72,73,77):
                mu.mem_write(r9,struct.pack('<'+'Q'*r8,*[resource if resources else 0]*r8));references+=r8*int(resources)
            elif slot==95:mu.mem_write(rdx,struct.pack('<I',1));mu.mem_write(r8,struct.pack('<6f',0,0,640,480,0,1))
            else:raise AssertionError('Unexpected getter '+str(slot))
    mu.hook_add(u.UC_HOOK_CODE,observe)
    try:
        mu.emu_start(0x100000,0x700101,count=100000)
    except u.UcError:
        if not old_allocation: raise
        pc=mu.reg_read(x.UC_X86_REG_RIP)-0x200000
        assert kind==4 and pc==0x8d
        print(dict(reproduced=True, instructionOffset=hex(pc), missingHeaderBytes=HEADER_SIZE))
        return
    assert mu.reg_read(x.UC_X86_REG_RSP)==rsp+8
    assert all(mu.reg_read(r)==0x100+i for i,r in enumerate(saved))
    expected_calls=[74,76,89,73,77,72,95]
    expected_calls=(['native']+expected_calls) if kind>=4 else (expected_calls+['native'])
    captured=enabled and not full and not invalid_capacity and 0<capacity<=8192 and initial<capacity and initial<0x80000000
    assert [c for c in calls if c!=2]==(expected_calls if captured else ['native'])
    assert references==1
    if resources and captured:assert calls.count(2)==35
    assert struct.unpack('<I',mu.mem_read(0x900024,4))[0]==initial+int(captured)
    if captured:
        record=bytes(mu.mem_read(0x900000+HEADER_SIZE+initial*RECORD_SIZE,RECORD_SIZE))
        assert struct.unpack_from('<Q',record)[0]==0x700100
        expected=((0,123,7,0xfffffff7,1,0),(1,123,7,0,1,0),
                  (2,123,7,0xfffffff7,2,11),(3,123,7,0,2,11),
                  (4,123,7,0,1,0),(5,9,0,0,1,0))[kind]
        assert struct.unpack_from('<6I',record,288)==expected
        assert struct.unpack_from('<I',record,336)[0]==1
    print(dict(kind=kind,enabled=enabled,full=full,boundary=boundary,invalidCapacity=invalid_capacity,
               resources=resources,capacity=capacity,initialCount=initial,passed=True))


if __name__=='__main__':
    for kind in range(6):
        for enabled,full in ((0,False),(1,False),(1,True)):check(kind,enabled,full)

    check(4,1,False,boundary=True,old_allocation=True)
    for kind in range(6):check(kind,1,False,boundary=True)
    for kind in range(6):check(kind,1,False,invalid_capacity=True)
    for kind in range(6):check(kind,1,False,resources=True)
    for kind in range(6):
        for capacity in (0,8193):check(kind,1,False,capacity_override=capacity)
        for count in (0x7fffffff,0xffffffff):check(kind,1,False,count_override=count)
