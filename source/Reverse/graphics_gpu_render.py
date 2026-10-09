"""One-shot helper invocation on the observed native rendering thread."""
import ctypes
import hashlib
import json
import struct
import time

from game import k32
from graphics_gpu import Frame,driver_path,object_text
from graphics_gpu_trace import d3d11_range


def dispatch_wrapper(helper,data):
    return (bytes.fromhex('4883ec28')+b'\x48\xb8'+struct.pack('<Q',data)+
            bytes.fromhex('4889442420')+b'\x48\xb8'+struct.pack('<Q',helper)+
            bytes.fromhex('ffd04883c428c3'))


def dispatch_selftest(code,entry):
    import unicorn as u
    from unicorn import x86_const as x
    for state,view,trigger in ((0,0xabc,0xabc),(2,0xabc,0xabc),(0,0xdef,0xabc),
                               (0,0xdef,0xFFFFFFFFFFFFFFFF),(0,0,0xFFFFFFFFFFFFFFFF)):
        uc=u.Uc(u.UC_ARCH_X86,u.UC_MODE_64)
        for p in range(0x100000,0x800000,0x100000):uc.mem_map(p,0x10000)
        uc.mem_write(0x100000,dispatch_wrapper(0x200000+entry,0x300000))
        uc.mem_write(0x200000,code);uc.mem_write(0x400000,b'\xc3');uc.mem_write(0x500000,b'\xc3')
        payload=struct.pack('<II6QIIQ',state,0,0x400000,0x500000,0x111,0x222,0x600100,trigger,4,1,0)
        uc.mem_write(0x300000,payload);uc.mem_write(0x600000,struct.pack('<Q',view))
        rsp=0x708008;uc.mem_write(rsp,struct.pack('<Q',0x700100));uc.reg_write(x.UC_X86_REG_RSP,rsp)
        regs=(x.UC_X86_REG_RCX,x.UC_X86_REG_RDX,x.UC_X86_REG_R8,x.UC_X86_REG_R9)
        args=(0x600100,4,1,0x600000)
        for r,v in zip(regs,args):uc.reg_write(r,v)
        saved=(x.UC_X86_REG_RBX,x.UC_X86_REG_RBP,x.UC_X86_REG_RSI,x.UC_X86_REG_RDI,
               x.UC_X86_REG_R12,x.UC_X86_REG_R13,x.UC_X86_REG_R14,x.UC_X86_REG_R15)
        for i,r in enumerate(saved):uc.reg_write(r,0x123000+i)
        calls=[]
        def hook(cpu,address,size,user):
            if address==0x400000:
                assert tuple(cpu.reg_read(r) for r in regs)==args;calls.append('native')
                assert struct.unpack('<I',cpu.mem_read(0x300000,4))[0]==state
            if address==0x500000:
                assert tuple(cpu.reg_read(r) for r in regs[:2])==(0x111,0x222)
                calls.append('helper');cpu.reg_write(x.UC_X86_REG_RAX,0x42)
            if address==0x700100:cpu.emu_stop()
        uc.hook_add(u.UC_HOOK_CODE,hook);uc.emu_start(0x100000,0x700101,count=10000)
        match=state==0 and (view==trigger or trigger==0xFFFFFFFFFFFFFFFF)
        assert calls==(['native','helper'] if match else ['native'])
        assert uc.reg_read(x.UC_X86_REG_RSP)==rsp+8
        assert all(uc.reg_read(r)==0x123000+i for i,r in enumerate(saved))
        if calls==['native','helper']:assert struct.unpack('<2I',uc.mem_read(0x300000,8))==(2,0x42)
    print('Render dispatch native-before-helper ordering, one-shot guard, arguments and ABI passed',flush=True)


class Render(Frame):
    def __init__(self,out,proof_path,dispatch_obj,resource_recovery=False):
        self.dispatch_code,self.dispatch_entry=object_text(dispatch_obj,'gpu_render_dispatch')
        dispatch_selftest(self.dispatch_code,self.dispatch_entry)
        super().__init__(out);g=self.g;proof=json.loads(proof_path.read_text())
        if proof['state']!=self.baseline or not proof['slot_restored']:
            raise RuntimeError('Rendering trace belongs to another game state')
        from image import Image
        im=Image()
        for start,end in ((0x957140,0x9571B3),(0x966540,0x9666E3)):
            if g.read(g.exe+start,end-start)!=im.img[start:end]:raise RuntimeError('Observed native binding code changed')
        targets=[r for r in proof['calls'] if r['caller_rva']=='0x9666c9' and r['ancestor_rva']=='0x957197'
                 and r['start']==4 and r['count']==1 and r['views']==['0x287750d49e8']]
        if not targets:raise RuntimeError('Verified terrain bind was not recorded')
        observed_packets=sorted({int(r['saved_rbx'],16) for r in targets})
        self.expected_thread=None
        stack=int(targets[0]['native_r15'],16)
        for tid in g.threads():
            teb=g.teb(tid)
            if teb and g.q(teb+16)<=stack<g.q(teb+8):self.expected_thread=tid
        if not self.expected_thread:raise RuntimeError('Observed renderer thread is unavailable')
        # Packet addresses are transient observations. Trigger from the actual
        # native API arguments instead of dereferencing or patching a packet.
        self.bind_view=int(targets[0]['views'][0],16)
        self.log('render_dispatch_identified',thread=self.expected_thread,observed_packets=[hex(p) for p in observed_packets],
                 trigger_view=hex(self.bind_view),resource_recovery=resource_recovery)

    def call(self,code,entry,args,size=0xA000,check_clock=True):
        self.assert_identity(check_clock);g=self.g;ctx=driver_path(g)['immediate'];vt=g.q(ctx);slot=vt+64;original=g.q(slot)
        dll,length=d3d11_range(g)
        if not dll<=original<dll+length:raise RuntimeError('PSSetShaderResources is already redirected')
        page=k32.VirtualAllocEx(ctypes.c_void_p(self.h),ctypes.c_void_p(0),ctypes.c_size_t(size),0x3000,0x04)
        if not page:raise ctypes.WinError(ctypes.get_last_error())
        data=page+0x1000;helper=page+0x2000;rcx,rdx=args(page)
        view=self.bind_view
        self.write(data,struct.pack('<II6QIIQ',0,0,original,helper+entry,rcx,rdx,ctx,view,4,1,0))
        self.write(page,dispatch_wrapper(page+0x200+self.dispatch_entry,data))
        self.write(page+0x200,self.dispatch_code);self.write(helper,code)
        if len(code)>0x1000 or len(self.dispatch_code)>0xE00:raise RuntimeError('Helper exceeds its code page')
        for address in (page,helper):
            self.protect(address,0x1000,0x20)
            if not k32.FlushInstructionCache(ctypes.c_void_p(self.h),ctypes.c_void_p(address),0x1000):
                raise ctypes.WinError(ctypes.get_last_error())
        self.log('dispatch_ready',page=hex(page),size=size,helper_sha256=hashlib.sha256(code).hexdigest(),
                 trigger_view=hex(view),args=[hex(rcx),hex(rdx)],render_thread=True)
        old=self.protect(slot,8,0x04);reached=0
        try:
            self.write(slot,struct.pack('<Q',page));until=time.monotonic()+5
            while time.monotonic()<until:
                reached=struct.unpack('<I',g.read(data,4))[0]
                if reached==2:break
                time.sleep(0.015)
        finally:
            if reached==0:self.write(data,struct.pack('<I',0xFFFFFFFF))
            self.write(slot,struct.pack('<Q',original));self.protect(slot,8,old)
        result=struct.unpack('<I',g.read(data+4,4))[0];stack=struct.unpack('<Q',g.read(data+64,8))[0]
        teb=g.teb(self.expected_thread)
        correct_thread=bool(teb and g.q(teb+16)<=stack<g.q(teb+8))
        self.log('frame_call',status=reached,result=result,slot_restored=g.q(slot)==original,page=hex(page),
                 native_stack=hex(stack),expected_thread=self.expected_thread,correct_thread=correct_thread)
        if reached!=2 or not correct_thread:raise RuntimeError('Render-thread call did not complete on its observed thread')
        self.assert_identity(check_clock)
        return page,result
