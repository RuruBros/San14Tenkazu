"""Compiled static-command bootstrap ABI/ownership checks; Unicorn only."""
import pathlib
import struct
import uuid
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.x86_const import *
from graphics_gpu import object_text

CONTEXT1 = uuid.UUID('bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1').bytes_le
CONTROL = struct.Struct('<7Q2i3Q4I16s')
assert CONTROL.size == 120


def wrapper(helper, data):
    # Engine command handlers have three arguments and return the next packet.
    # Supply the helper's private fourth argument without changing the stack.
    return b'\x49\xb9'+struct.pack('<Q',data)+b'\x48\xb8'+struct.pack('<Q',helper)+b'\xff\xe0'


def check(mode):
    code, entry = object_text(pathlib.Path(__file__).resolve().parent.parent/'Seasons/Native/CommandProbe.obj', 'season_command_probe')
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    for address in range(0x100000, 0x900000, 0x100000):cpu.mem_map(address, 0x10000)
    control, slot, driver, context, device = 0x300000,0x300100,0x400000,0x401000,0x402000
    dvt,cvt,devvt,qivt = 0x403000,0x404000,0x405000,0x406000
    native,tick,thread,methods,qi = 0x600000,0x600100,0x600200,0x601000,0x407000
    cpu.mem_write(0x100000,wrapper(0x200000+entry,control));cpu.mem_write(0x200000,code)
    state = {'running':1,'done':2}.get(mode,0)
    cpu.mem_write(control,CONTROL.pack(native,slot,0x100000,dvt,thread,tick,500,state,0,0,0,0,0,0,0,0,CONTEXT1))
    cpu.mem_write(slot,struct.pack('<Q',{'foreign':0xdeadbeef,'already_restored':native}.get(mode,0x100000)))
    cpu.mem_write(driver,struct.pack('<Q',dvt+int(mode=='wrong_driver')))
    cpu.mem_write(driver+0x10,struct.pack('<Q',0 if mode=='null_context' else context))
    cpu.mem_write(context,struct.pack('<Q',cvt));cpu.mem_write(device,struct.pack('<Q',devvt));cpu.mem_write(qi,struct.pack('<Q',qivt))
    labels={native:'native',tick:'tick',thread:'thread'}
    for vt,slots,prefix in ((cvt,(0,1,2,3,112),'context'),(devvt,(2,38),'device'),(qivt,(2,),'context1')):
        for index in slots:
            address=methods+len(labels)*16;labels[address]=(prefix,index)
            cpu.mem_write(vt+index*8,struct.pack('<Q',address))
    for address in labels:cpu.mem_write(address,b'\xc3')
    calls=[];refs={'context':1,'device':1,'context1':0}
    args=(0 if mode=='null_driver' else driver,0x500000,0x0104001e)
    def result(value=0):
        rsp=cpu.reg_read(UC_X86_REG_RSP);dest=struct.unpack('<Q',cpu.mem_read(rsp,8))[0]
        cpu.reg_write(UC_X86_REG_RAX,value&0xffffffffffffffff)
        cpu.reg_write(UC_X86_REG_RSP,rsp+8);cpu.reg_write(UC_X86_REG_RIP,dest)
    def hook(mu,address,size,user):
        label=labels.get(address)
        if label is None:return
        calls.append(label)
        rcx,rdx,r8=(mu.reg_read(r) for r in (UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8))
        if label=='native':
            assert (rcx,rdx,r8)==args
            assert struct.unpack('<i',mu.mem_read(control+56,4))[0] in (1,2)
            result(args[1]+20);return
        if label=='tick':result(500 if mode=='expired' else 100);return
        if label=='thread':result(42);return
        owner,index=label
        assert rcx=={'context':context,'device':device,'context1':qi}[owner]
        if index==1:refs[owner]+=1;result(refs[owner]);return
        if index==2:refs[owner]-=1;result(refs[owner]);return
        if owner=='context' and index==3:
            if mode!='null_device':mu.mem_write(rdx,struct.pack('<Q',device));refs['device']+=1
        elif owner=='context' and index==112:result(1 if mode=='deferred' else 0);return
        elif owner=='device' and index==38:result(int(mode=='single_threaded'));return
        elif owner=='context' and index==0:
            assert bytes(mu.mem_read(rdx,16))==CONTEXT1
            if mode=='qi_failure':result(0x80004002);return
            if mode=='null_interface':result();return
            mu.mem_write(r8,struct.pack('<Q',qi));refs['context1']+=1
        else:raise AssertionError(label)
        result()
    def guard(mu,access,address,size,value,user):
        if control<=address<control+0x100:assert address+size<=control+CONTROL.size
    cpu.hook_add(UC_HOOK_CODE,hook);cpu.hook_add(UC_HOOK_MEM_WRITE,guard)
    rsp=0x808008;stop=0x800100;cpu.mem_write(rsp,struct.pack('<Q',stop));cpu.reg_write(UC_X86_REG_RSP,rsp)
    for register,value in zip((UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8),args):cpu.reg_write(register,value)
    saved=(UC_X86_REG_RBX,UC_X86_REG_RBP,UC_X86_REG_RSI,UC_X86_REG_RDI,UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15)
    for i,register in enumerate(saved):cpu.reg_write(register,0xabc000+i)
    cpu.emu_start(0x100000,stop,count=100000)
    assert cpu.reg_read(UC_X86_REG_RSP)==rsp+8
    assert cpu.reg_read(UC_X86_REG_RAX)==args[1]+20
    assert all(cpu.reg_read(register)==0xabc000+i for i,register in enumerate(saved))
    assert calls.count('native')==1 and refs=={'context':1,'device':1,'context1':0}
    output=CONTROL.unpack(bytes(cpu.mem_read(control,CONTROL.size)))
    assert output[7]==(1 if mode=='running' else 2)
    assert struct.unpack('<Q',cpu.mem_read(slot,8))[0]==({'foreign':0xdeadbeef,'running':0x100000,'done':0x100000}.get(mode,native))
    if mode in ('valid','already_restored'):assert output[8]==0 and output[9:16]==(driver,context,device,0,0,42,1)
    elif mode not in ('running','done'):assert output[8]<0
    if mode in ('running','done','foreign','expired','wrong_driver','null_driver','null_context'):
        assert not any(isinstance(c,tuple) for c in calls)
    print(dict(mode=mode,codeBytes=len(code),passed=True))


if __name__=='__main__':
    for mode in ('valid','already_restored','foreign','running','done','expired','wrong_driver','null_driver',
                 'null_context','null_device','qi_failure','null_interface','deferred','single_threaded'):check(mode)
