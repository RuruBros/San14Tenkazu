"""Read-only office audit; unmodified Native code runs only on copied data in Unicorn."""
import argparse
import ctypes
import json
from pathlib import Path
import struct
import sys
import time
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / '.venv/Lib/site-packages'))
from image import Image
from tactic_rule_audit import digest, required, KNOWN_EXE
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP, UC_X86_REG_RAX, UC_X86_REG_RIP

TABLE, SIZE, COUNT, VT = 0x784F8, 0x58, 256, 0x13A72C0
FUNCTIONS = {'capacity': 0x92550, 'bonus': 0x1EE270, 'index': 0x22DF80,
             'valid': 0x234340, 'encodeByte': 0xDB60, 'decodeByte': 0xDB00,
             'encodeWord': 0xDB90, 'decodeWord': 0xDB30, 'save': 0x23ADC0, 'serialize': 0x23F380,
             'gradeComparison': 0x2565D0}

def encode(value, index, width):
    bits, rotate = (16, 13) if width == 2 else (8, 5)
    mask = (1 << bits) - 1
    x = (value ^ index) & mask
    return ((x >> rotate) | (x << (bits - rotate))) & mask

def decode(value, index, width):
    bits, rotate = (16, 13) if width == 2 else (8, 5)
    return (((value << rotate) | (value >> (bits - rotate))) ^ index) & ((1 << bits) - 1)

class Native:
    def __init__(self, im):
        self.im, self.u = im, Uc(UC_ARCH_X86, UC_MODE_64)
        self.root, self.array, self.vector, self.stack, self.stop = 0x300000000, 0x301000000, 0x302000000, 0x303000000, 0x304000000
        self.u.mem_map(im.base, (len(im.img)+4095) & ~4095)
        self.u.mem_write(im.base, im.img)
        for at, size in ((self.root,0x90000),(self.array,0x10000),(self.vector,0x10000),(self.stack,0x10000),(self.stop,4096)):
            self.u.mem_map(at,size)
        self.u.mem_write(im.base+0x277DF18,struct.pack('<Q',self.root))
        self.u.mem_write(self.root+TABLE,b''.join(struct.pack('<Q',self.array+i*SIZE) for i in range(COUNT)))
        for slot,name in ((0x40,'encodeByte'),(0x48,'decodeByte'),(0x50,'encodeWord'),(0x58,'decodeWord')):
            assert im.u64(VT+slot)==im.base+FUNCTIONS[name]

    def put(self,index,data,vector):
        record=bytearray(data); struct.pack_into('<Q',record,0,self.im.base+VT)
        start=self.vector+index*64
        struct.pack_into('<QQQ',record,0x40,start,start+len(vector),start+20)
        self.u.mem_write(self.array+index*SIZE,bytes(record))
        if vector: self.u.mem_write(start,bytes(vector))

    def call(self,name,index,arg=0,rotate=0,key=0,registers=()):
        sp=self.stack+0xF008; self.u.mem_write(sp,struct.pack('<Q',self.stop))
        for reg,value in ((UC_X86_REG_RSP,sp),(UC_X86_REG_RCX,self.array+index*SIZE),
                          (UC_X86_REG_RDX,arg),(UC_X86_REG_R8,rotate),(UC_X86_REG_R9,key)):
            self.u.reg_write(reg,value)
        for reg,value in registers: self.u.reg_write(reg,value)
        self.u.emu_start(self.im.base+FUNCTIONS[name],self.stop,count=10000)
        if self.u.reg_read(UC_X86_REG_RIP)!=self.stop: raise RuntimeError('Native did not return')
        return self.u.reg_read(UC_X86_REG_RAX)&0xFFFFFFFF

def synthetic(im):
    n,failures,calls=Native(im),[],0
    for index in (1,2,20,73,255):
        values=range(65536) if index==1 else (0,1,3000,18000,20000,32767,32768,65535)
        for value in values:
            data,vector=bytearray(SIZE),bytearray([0xA5]*20)
            struct.pack_into('<H',data,0x34,value); struct.pack_into('<H',vector,0,encode(value,index,2))
            for encoded in (False,True):
                n.put(index,data,vector if encoded else b''); got=n.call('capacity',index); calls+=1
                if got!=value: failures.append(['capacity',index,value,encoded,got])
            got=n.call('encodeWord',index,value,13,index)&65535; calls+=1
            if got!=encode(value,index,2): failures.append(['encoderWord',index,value,got])
    for index in (1,20,73,255):
        for value in range(256):
            data,vector=bytearray(SIZE),bytearray([0xA5]*20)
            for ability in range(4): data[0x38+ability]=value; vector[(ability+1)*4]=encode(value,index,1)
            for encoded in (False,True):
                n.put(index,data,vector if encoded else b'')
                for ability in range(4):
                    got=n.call('bonus',index,ability); calls+=1
                    if got!=value: failures.append(['bonus',index,ability,value,encoded,got])
            got=n.call('encodeByte',index,value,5,index)&255; calls+=1
            if got!=encode(value,index,1): failures.append(['encoderByte',index,value,got])
    for index in (0,1,73,255):
        for cap in (0,3000,65535):
            data=bytearray(SIZE); struct.pack_into('<H',data,0x34,cap); n.put(index,data,b'')
            got=n.call('valid',index)&255; calls+=1
            if got!=int(index!=0 and cap!=0): failures.append(['valid',index,cap,got])
    grade_calls=0
    for left in range(256):
        for right in (1,5,9,255):
            if left==right: continue # Equal grades fall through to officer credit/ID ordering.
            a,b=bytearray(SIZE),bytearray(SIZE); a[0x37]=left; b[0x37]=right
            n.put(1,a,b''); n.put(2,b,b'')
            pa,pb=n.array+0x8000,n.array+0x9000
            n.u.mem_write(pa+0xE7,b'\x01'); n.u.mem_write(pb+0xE7,b'\x02')
            got=n.call('gradeComparison',1,registers=((UC_X86_REG_RDX,pa),(UC_X86_REG_R8,pb)))
            calls+=1; grade_calls+=1
            if got!=int(left<right): failures.append(['gradeComparison',left,right,got])
    return {'nativeCalls':calls,'gradeComparisonCalls':grade_calls,'mismatches':failures,'nativeCodePatched':False,'memoryWritten':False,'profileSaved':False}

def live(im):
    from game import Game,k32
    g=Game(); profile=Path.home()/'AppData/Local/San14Tenkazu/rule_profiles.json'
    before=profile.read_bytes() if profile.exists() else None
    try:
        path=ctypes.create_unicode_buffer(32768); length=ctypes.c_ulong(len(path))
        if not k32.QueryFullProcessImageNameW(ctypes.c_void_p(g.h),0,path,ctypes.byref(length)): raise RuntimeError('Cannot read executable path')
        exe_hash=digest(Path(path.value).read_bytes())
        if exe_hash!=KNOWN_EXE: raise RuntimeError('Unverified executable')
        checks={name:all(required(g,g.exe+i.address,i.size)==bytes(i.bytes) for i in im.flow(rva)) for name,rva in FUNCTIONS.items()}
        if not all(checks.values()): raise RuntimeError('Live instructions differ')
        world=next(r for r,c in im.rtti_vtables().items() if c=='.?AVCWorldData@@')
        def snapshot():
            root=g.q(g.exe+0x277DF18); state=g.q(root+0x871E8) if root else 0
            if not state or g.q(state)!=g.exe+world: raise RuntimeError('World not ready')
            clock=required(g,state+0x34,5)
            if not 100<struct.unpack_from('<H',clock)[0]<400 or not 1<=clock[2]<=12 or not 1<=clock[3]<=31: raise RuntimeError('Invalid game date')
            pointers=required(g,root+TABLE,COUNT*8); array=struct.unpack_from('<Q',pointers)[0]; rows=[]
            for index in range(COUNT):
                address=struct.unpack_from('<Q',pointers,index*8)[0]
                if not array or address!=array+index*SIZE: raise RuntimeError('Table is not contiguous')
                data=required(g,address,SIZE)
                if struct.unpack_from('<Q',data)[0]!=g.exe+VT: raise RuntimeError('Record class differs')
                start,end,cap=struct.unpack_from('<QQQ',data,0x40)
                empty=start==end and (start==cap==0 or start>=65536 and cap>=end)
                if not empty and not (start>=65536 and end==start+20 and end<=cap<=start+4096): raise RuntimeError('Vector not ready')
                vector=b'' if empty else required(g,start,20)
                rows.append((address,data,vector))
            if root!=g.q(g.exe+0x277DF18) or state!=g.q(root+0x871E8) or pointers!=required(g,root+TABLE,COUNT*8): raise RuntimeError('Table changed')
            return root,state,clock,rows
        first=snapshot()
        for _ in range(16):
            time.sleep(0.05)
            if required(g,first[1]+0x34,5)!=first[2]: raise RuntimeError('Game is running')
        second=snapshot()
        if first!=second: raise RuntimeError('Office data changed')
        rows,failures,n,calls=[],[],Native(im),0
        for index,(address,data,vector) in enumerate(second[3]):
            n.put(index,data,vector)
            row={'id':index,'name':data[0x10:0x1A].decode('utf-16-le').split('\0')[0],'address':hex(address),
                 'data':data.hex(),'vector':vector.hex(),'grade':data[0x37],'category':data[0x36],'fields':[]}
            for slot in range(5):
                at,width=(0x34,2) if slot==0 else (0x37+slot,1)
                plain=int.from_bytes(data[at:at+width],'little')
                decoded=decode(int.from_bytes(vector[slot*4:slot*4+width],'little'),index,width) if vector else plain
                got=n.call('capacity' if slot==0 else 'bonus',index,slot-1); calls+=1
                row['fields'].append({'id':'capacity' if slot==0 else ('leadership','strength','intelligence','politics')[slot-1],
                                      'plain':plain,'decoded':decoded,'native':got})
                if plain!=decoded or got!=decoded: failures.append([index,slot,plain,decoded,got])
            row['nativeValid']=bool(n.call('valid',index)&255); calls+=1
            rows.append(row)
        after=profile.read_bytes() if profile.exists() else None
        if before!=after or second!=snapshot(): raise RuntimeError('User data changed during audit')
        return {'processId':g.pid,'exeSha256':exe_hash,'root':hex(second[0]),'state':hex(second[1]),'clockHex':second[2].hex(),
                'stable':True,'pace':'Paused','codeChecks':checks,'records':rows,'nativeCallsOnCopiedData':calls,'mismatches':failures,
                'profileSha256Before':digest(before) if before else None,'profileSha256After':digest(after) if after else None,
                'memoryWritten':False,'profileSaved':False,'nativeCodePatched':False}
    finally: k32.CloseHandle(ctypes.c_void_p(g.h))

def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--synthetic',action='store_true'); p.add_argument('--live',action='store_true')
    p.add_argument('--output',type=Path,required=True); a=p.parse_args(); im=Image()
    result={'utc':datetime.now(timezone.utc).isoformat(),'imageSha256':digest(im.img),'assembly':{name:im.flowtext(rva) for name,rva in FUNCTIONS.items()}}
    if a.synthetic: result['synthetic']=synthetic(im)
    if a.live: result['live']=live(im)
    a.output.parent.mkdir(parents=True,exist_ok=True); a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:{'mismatches':len(v['mismatches']),'calls':v.get('nativeCalls',v.get('nativeCallsOnCopiedData'))} for k,v in result.items() if k in ('live','synthetic')}))
    if any(result[k]['mismatches'] for k in ('live','synthetic') if k in result): raise SystemExit(1)

if __name__=='__main__': main()
