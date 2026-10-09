"""Unmodified Native panel getter and cost helper on copied/synthetic data; no live access."""
import argparse,json,struct,sys
from pathlib import Path
from datetime import datetime,timezone
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE/'.venv/Lib/site-packages'))
from image import Image
from tactic_rule_audit import digest
from unicorn import Uc,UC_ARCH_X86,UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_RSP,UC_X86_REG_RAX,UC_X86_REG_RIP

class Native:
    def __init__(self,im):
        self.im,self.u=im,Uc(UC_ARCH_X86,UC_MODE_64)
        self.root,self.array,self.persons,self.force,self.stack,self.stop=0x300000000,0x301000000,0x302000000,0x303000000,0x304000000,0x305000000
        self.u.mem_map(im.base,(len(im.img)+4095)&~4095); self.u.mem_write(im.base,im.img)
        for at,size in ((self.root,0x90000),(self.array,0x10000),(self.persons,0x10000),(self.force,4096),(self.stack,0x10000),(self.stop,4096)):
            self.u.mem_map(at,size)
        self.u.mem_write(im.base+0x277DF18,struct.pack('<Q',self.root))
        self.u.mem_write(self.root+0x85F20,b''.join(struct.pack('<Q',self.array+i*0x50) for i in range(80)))
        self.u.mem_write(self.root+0x73A50,struct.pack('<Q',self.persons))
        person=bytearray(0x1F0); struct.pack_into('<Q',person,0,im.base+0x13A6B88); struct.pack_into('<H',person,0x10,1); person[0xE6]=1
        self.person=self.persons+0x1F0; self.u.mem_write(self.person,bytes(person))
        self.u.mem_write(self.force+0x1CB,b'\xff'*10)
        for i in range(80): self.put(i,bytearray(0x50))
    def put(self,id,data):
        b=bytearray(data); struct.pack_into('<Q',b,0,self.im.base+0x13A67D8); self.u.mem_write(self.array+id*0x50,bytes(b))
    def call(self,rva,a,b):
        sp=self.stack+0xF008; self.u.mem_write(sp,struct.pack('<Q',self.stop))
        for reg,val in ((UC_X86_REG_RSP,sp),(UC_X86_REG_RCX,a),(UC_X86_REG_RDX,b)): self.u.reg_write(reg,val)
        self.u.emu_start(self.im.base+rva,self.stop,count=10000)
        if self.u.reg_read(UC_X86_REG_RIP)!=self.stop: raise RuntimeError('Native helper did not return')
        return self.u.reg_read(UC_X86_REG_RAX)
    def effect(self,id): return struct.unpack('<i',struct.pack('<I',self.call(0x22C0F0,self.force,id)&0xFFFFFFFF))[0]
    def cost(self,id,clan):
        self.u.mem_write(self.person+0x14C,bytes([clan])); out=self.call(0x322D50,id,self.person)
        return None if out==0 else bytes(self.u.mem_read(out,6))

def expected_cost(data,clan):
    out=bytearray(data[0x3D:0x43])
    for at in (0,3):
        if out[at]:
            if not out[at+1]: return None
            out[at+2]=out[at+1]
            if clan==out[at]: out[at+1]=max(out[at+1]-1,1)
    return bytes(out)

def main():
    p=argparse.ArgumentParser(); p.add_argument('--copied-tables',type=Path,required=True); p.add_argument('--output',type=Path,required=True); a=p.parse_args()
    im=Image(); n=Native(im); failures=[]; calls=0
    for value in range(-32768,32768):
        data=bytearray(0x50); struct.pack_into('<h',data,0x44,value); n.put(2,data); got=n.effect(2); calls+=1
        if got!=value: failures.append(['effect',value,got])
    for slot in (0,3):
        for kind in range(7):
            for amount in range(256):
                data=bytearray(0x50); data[0x3D+slot]=kind; data[0x3E+slot]=amount; n.put(2,data)
                for clan in range(7):
                    got=n.cost(2,clan); expected=expected_cost(data,clan); calls+=1
                    if got!=expected: failures.append(['cost',slot,kind,amount,clan,str(got),str(expected)])
    copied=json.loads(a.copied_tables.read_text(encoding='utf-8-sig')); rows=[]
    for row in copied['tables']['doctrine']['records']:
        if row['id']==0: continue
        id=row['id']; data=bytes.fromhex(row['data']); n.put(id,data); got=n.effect(id); expected=struct.unpack_from('<h',data,0x44)[0]; calls+=1
        if got!=expected: failures.append(['copied-effect',id,expected,got])
        for clan in range(7):
            got_cost=n.cost(id,clan); calls+=1
            if got_cost!=expected_cost(data,clan): failures.append(['copied-cost',id,clan])
        rows.append({'id':id,'name':row['name'],'effect':got,'kind1':data[0x3D],'cost1':data[0x3E],'kind2':data[0x40],'cost2':data[0x41]})
    n.u.mem_write(n.force+0x1CB,b'\x00'*10)
    if n.effect(2)!=0: failures.append(['closed-effect'])
    calls+=1; n.u.mem_write(n.force+0x1CB,b'\xff'*10); n.u.mem_write(n.force+0x1D5,b'\xff'*10)
    if n.effect(2)!=0: failures.append(['disabled-effect'])
    calls+=1
    out={'utc':datetime.now(timezone.utc).isoformat(),'imageSha256':digest(im.img),'copiedReadUtc':copied['utc'],'copiedProcessId':copied['processId'],
         'nativeCalls':calls,'mismatches':failures,'copiedRecords':rows,'functions':{hex(r):im.flowtext(r) for r in (0x22C0F0,0x322D50,0x22AB90,0x2341F0)},
         'nativeCodePatched':False,'liveAccess':False,'liveMemoryWritten':False,'profileSaved':False}
    a.output.parent.mkdir(parents=True,exist_ok=True); a.output.write_text(json.dumps(out,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'nativeCalls':calls,'mismatches':len(failures)}))
    if failures: raise SystemExit(1)
if __name__=='__main__': main()
