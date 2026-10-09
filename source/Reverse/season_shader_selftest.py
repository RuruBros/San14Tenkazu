"""Check compiled reflection helper ABI and failure paths in Unicorn only."""
import pathlib
import struct
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.x86_const import *
from graphics_gpu import object_text


def check(mode='valid',count=1):
    code,entry=object_text(pathlib.Path(__file__).resolve().parent.parent/'Seasons/Native/ShaderInspect.obj','season_inspect_shaders')
    mu=Uc(UC_ARCH_X86,UC_MODE_64)
    codebase,inp,out,heap,methods,stack=0x100000,0x200000,0x300000,0x700000,0x800000,0x900000
    output_size=8+128*2408
    for address,size in ((codebase,0x10000),(inp,0x2000),(out,(output_size+4095)&~4095),
                         (heap,0x20000),(methods,0x10000),(stack,0x10000)):
        mu.mem_map(address,size)
    mu.mem_write(codebase,code)
    owner,owner_vtable,shader,reflection,vtable=heap,heap+0x10000,heap+0x12000,heap+0x14000,heap+0x18000
    mu.mem_write(owner,struct.pack('<Q',owner_vtable))
    mu.mem_write(owner+16,struct.pack('<Q',reflection));mu.mem_write(owner+40,struct.pack('<Q',shader))
    mu.mem_write(reflection,struct.pack('<Q',vtable))
    for slot in range(9):
        mu.mem_write(vtable+8*slot,struct.pack('<Q',methods+16*slot));mu.mem_write(methods+16*slot,b'\xc3')
    candidates=struct.pack('<4Q',0 if mode=='null_owner' else owner,owner_vtable+int(mode=='identity'),shader,reflection)
    mu.mem_write(inp,struct.pack('<II',count,0)+candidates*min(count,128))
    refs=1;calls=[]
    def return_from_method(result=0):
        rsp=mu.reg_read(UC_X86_REG_RSP)
        dest=struct.unpack('<Q',mu.mem_read(rsp,8))[0]
        mu.reg_write(UC_X86_REG_RSP,rsp+8);mu.reg_write(UC_X86_REG_RIP,dest)
        mu.reg_write(UC_X86_REG_RAX,result&0xffffffff)
    def observe(cpu,address,size,user):
        nonlocal refs
        if not methods<=address<methods+9*16:return
        slot=(address-methods)//16;calls.append(slot)
        rdx,r8=[mu.reg_read(r) for r in (UC_X86_REG_RDX,UC_X86_REG_R8)]
        if slot==0:
            assert bytes(mu.mem_read(rdx,16))==bytes.fromhex('a16c538dca0c5649a837786963755584')
            if mode=='qi_failure':return_from_method(0x80004002);return
            mu.mem_write(r8,struct.pack('<Q',0 if mode=='null_interface' else reflection))
            if mode!='null_interface':refs+=1
        elif slot==2:refs-=1
        elif slot==3:
            if mode=='desc_failure':return_from_method(0x80004005);return
            desc=bytearray(160);struct.pack_into('<3I',desc,24,33 if mode=='resource_limit' else 32,
                                             17 if mode=='input_limit' else 16,9 if mode=='output_limit' else 8)
            mu.mem_write(rdx,bytes(desc))
        elif slot in (6,7,8):
            if mode=='getter_failure':return_from_method(0x80004005);return
            assert rdx < {6:32,7:16,8:8}[slot]
            mu.mem_write(r8,bytes([slot])*40)
        else:raise AssertionError('Unexpected reflection method '+str(slot))
        return_from_method()
    def guard_output(cpu,access,address,size,value,user):
        if out<=address<out+((output_size+4095)&~4095):
            assert address+size<=out+output_size,'Native output exceeds complete Header+records allocation'
    mu.hook_add(UC_HOOK_CODE,observe);mu.hook_add(UC_HOOK_MEM_WRITE,guard_output)
    rsp=stack+0x8008;stop=stack+0x100
    mu.mem_write(rsp,struct.pack('<Q',stop));mu.reg_write(UC_X86_REG_RSP,rsp)
    mu.reg_write(UC_X86_REG_RCX,0 if mode=='null_input' else inp)
    mu.reg_write(UC_X86_REG_RDX,0 if mode=='null_output' else out)
    saved=(UC_X86_REG_RBX,UC_X86_REG_RBP,UC_X86_REG_RSI,UC_X86_REG_RDI,
           UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15)
    for i,r in enumerate(saved):mu.reg_write(r,0x123400+i)
    mu.emu_start(codebase+entry,stop,count=1000000)
    assert mu.reg_read(UC_X86_REG_RSP)==rsp+8
    assert all(mu.reg_read(r)==0x123400+i for i,r in enumerate(saved))
    result=mu.reg_read(UC_X86_REG_RAX)&0xffffffff
    if mode in ('null_input','null_output'):assert result==0xfffffffd and not calls
    elif count>128:assert result==0xffffffff and not calls
    elif mode in ('identity','null_owner'):assert result==0xfffffffe and not calls
    else:
        assert result==0 and struct.unpack('<2I',mu.mem_read(out,8))==(count,1)
        for i in range(count):
            valid=struct.unpack('<I',mu.mem_read(out+8+i*2408+4,4))[0]
            assert valid==int(mode=='valid')
        if mode in ('qi_failure','null_interface'):assert 2 not in calls
        else:assert calls.count(2)==count
        if mode in ('resource_limit','input_limit','output_limit','desc_failure'):assert not any(s in calls for s in (6,7,8))
    assert refs==1
    print(dict(mode=mode,count=count,codeBytes=len(code),calls=len(calls),outputBytes=output_size,passed=True))


if __name__=='__main__':
    for mode in ('valid','identity','null_owner','null_input','null_output','qi_failure','null_interface',
                 'desc_failure','resource_limit','input_limit','output_limit','getter_failure'):check(mode)
    for count in (0,128,129):check(count=count)
