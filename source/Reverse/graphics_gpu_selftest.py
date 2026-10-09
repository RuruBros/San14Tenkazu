"""Execute the compiled swap routine in Unicorn with controlled COM objects.

Checks actual machine code: successful ownership transfer/restoration, an
identity rejection, and cleanup when the second SRV creation fails.
"""
import argparse
import pathlib
import struct
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *
from graphics_gpu import object_text


def check(code,entry,fail=False,invalid=False,detail=False):
    mu=Uc(UC_ARCH_X86,UC_MODE_64);mu.mem_map(0x10000,0x300000)
    codebase=0x10000;spec=0x20000;session=0x30000;owner=0x40000;field=owner+0x18
    objects=[0x60000+i*0x1000 for i in range(7)]
    device,oldtex,oldsrv1,oldsrv2,newtex,newsrv1,newsrv2=objects
    refs={obj:1 for obj in objects};srv_calls=0;api_calls=[]
    desc=struct.pack('<11I',1024,1024,9,19,76,1,0,1,8,0,0) if detail else struct.pack('<11I',4096,4096,11,1,76,1,0,1,8,0,0)
    def write(a,b):mu.mem_write(a,bytes(b))
    def q(a):return struct.unpack('<Q',mu.mem_read(a,8))[0]
    def u(a):return struct.unpack('<I',mu.mem_read(a,4))[0]
    def ret(result=0):
        rsp=mu.reg_read(UC_X86_REG_RSP);mu.reg_write(UC_X86_REG_RIP,q(rsp));mu.reg_write(UC_X86_REG_RSP,rsp+8);mu.reg_write(UC_X86_REG_RAX,result&0xFFFFFFFFFFFFFFFF)
    callbacks={}
    def method(fn):
        a=0x100000+len(callbacks)*0x100;callbacks[a]=fn;write(a,b'\xc3');return a
    def addref():
        obj=mu.reg_read(UC_X86_REG_RCX);refs[obj]+=1;api_calls.append(('addref',obj));ret(refs[obj])
    def release():
        obj=mu.reg_read(UC_X86_REG_RCX);refs[obj]-=1;api_calls.append(('release',obj));ret(refs[obj])
    def getdesc():write(mu.reg_read(UC_X86_REG_RDX),desc);ret()
    def viewdesc():
        fmt=77 if mu.reg_read(UC_X86_REG_RCX)==oldsrv1 else 78
        write(mu.reg_read(UC_X86_REG_RDX),struct.pack('<6I',fmt,5,0,9,0,19) if detail else struct.pack('<6I',fmt,4,0,11,0,0));ret()
    def create_tex():
        assert bytes(mu.mem_read(mu.reg_read(UC_X86_REG_RDX),44))==desc
        write(mu.reg_read(UC_X86_REG_R9),struct.pack('<Q',newtex));api_calls.append(('create_texture',newtex));ret()
    def create_view():
        nonlocal srv_calls
        srv_calls+=1;api_calls.append(('create_view',srv_calls))
        if fail and srv_calls==2:ret(0x80004005);return
        assert mu.reg_read(UC_X86_REG_RDX)==newtex
        fmt=u(mu.reg_read(UC_X86_REG_R8));assert fmt==(77 if srv_calls==1 else 78)
        if detail:assert bytes(mu.mem_read(mu.reg_read(UC_X86_REG_R8)+4,20))==struct.pack('<5I',5,0,9,0,19)
        write(mu.reg_read(UC_X86_REG_R9),struct.pack('<Q',newsrv1 if srv_calls==1 else newsrv2));ret()
    add,rel,gd,vd,ct,cv=[method(fn) for fn in (addref,release,getdesc,viewdesc,create_tex,create_view)]
    for obj in objects:
        vt=obj+0x100;write(obj,struct.pack('<Q',vt));write(vt+8,struct.pack('<Q',add));write(vt+16,struct.pack('<Q',rel))
        write(vt+80,struct.pack('<Q',gd));write(vt+64,struct.pack('<Q',vd))
    write(device+0x100+40,struct.pack('<Q',ct));write(device+0x100+56,struct.pack('<Q',cv))
    write(owner,struct.pack('<Q',0x50000));write(owner+8,struct.pack('<I',1));original=struct.pack('<3Q',oldtex,oldsrv1,oldsrv2);write(field,original)
    identity=struct.pack('<5Q',owner,0x50001 if invalid else 0x50000,field,oldtex,oldtex+0x100)
    write(spec,identity+struct.pack('<4Q',device,0x90000,oldsrv1,oldsrv2)+desc+struct.pack('<I',0));write(codebase,code)
    def hook(uc,a,size,user):
        if a in callbacks:callbacks[a]()
    mu.hook_add(UC_HOOK_CODE,hook)
    saved=(UC_X86_REG_RBX,UC_X86_REG_RBP,UC_X86_REG_RSI,UC_X86_REG_RDI,UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15)
    def call():
        rsp=0x2F0008;stop=0x2F1000;write(rsp,struct.pack('<Q',stop))
        for i,r in enumerate(saved):mu.reg_write(r,0x12345600+i)
        mu.reg_write(UC_X86_REG_RSP,rsp);mu.reg_write(UC_X86_REG_RCX,spec);mu.reg_write(UC_X86_REG_RDX,session)
        mu.emu_start(codebase+entry,stop,count=1000000)
        assert mu.reg_read(UC_X86_REG_RSP)==rsp+8
        assert all(mu.reg_read(r)==0x12345600+i for i,r in enumerate(saved))
        return mu.reg_read(UC_X86_REG_RAX)&0xFFFFFFFF
    result=call()
    if invalid:
        assert result==0xFFFFFFFF and u(session)==0 and not api_calls and bytes(mu.mem_read(field,24))==original
    elif fail:
        assert result==0xFFFFFFFA and u(session)==0 and bytes(mu.mem_read(field,24))==original
        assert refs[newtex]==0 and refs[newsrv1]==0 and refs[oldtex]==refs[oldsrv1]==refs[oldsrv2]==1
    else:
        assert result==0 and u(session)==1 and [q(field+i*8) for i in range(3)]==[newtex,newsrv1,newsrv2]
        assert refs[oldtex]==refs[oldsrv1]==refs[oldsrv2]==1
        write(spec+116,struct.pack('<I',1));assert call()==0 and u(session)==2
        assert bytes(mu.mem_read(field,24))==original
        assert refs[newtex]==refs[newsrv1]==refs[newsrv2]==0
        assert refs[oldtex]==refs[oldsrv1]==refs[oldsrv2]==1
    return dict(detail=detail,invalid=invalid,fail=fail,result=hex(result),calls=len(api_calls))


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('obj',type=pathlib.Path);a=p.parse_args()
    code,entry=object_text(a.obj,'gpu_swap')
    for detail in (False,True):
        for kw in ({},{'invalid':True},{'fail':True}):print(check(code,entry,detail=detail,**kw))
