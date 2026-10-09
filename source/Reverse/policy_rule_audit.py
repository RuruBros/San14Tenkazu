"""Read-only policy audit: original Native instructions run only in Unicorn."""
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

TABLES = [('policy', 0x78D50, 0xA8, 121, 0x13A7258), ('policyeffect', 0x80880, 0xB0, 101, 0x13A7328)]
FUNCTIONS = {'value': 0x22C590, 'encodeWord': 0xDB90, 'decodeWord': 0xDB30,
             'validPolicy': 0x2342C0, 'validEffect': 0x234300, 'save': 0x23ADC0,
             'visit': 0x258D90, 'policySerialize': 0x23F060, 'effectSerialize': 0x23F180}

def encode(value, index):
    x = value ^ index
    return ((x >> 11) | (x << 5)) & 65535

def decode(value, index):
    return (((value << 11) | (value >> 5)) ^ index) & 65535

class Native:
    def __init__(self, im):
        self.im, self.u = im, Uc(UC_ARCH_X86, UC_MODE_64)
        self.root, self.array, self.vector, self.stack, self.stop = 0x300000000, 0x301000000, 0x302000000, 0x303000000, 0x304000000
        self.u.mem_map(im.base, (len(im.img)+4095) & ~4095)
        self.u.mem_write(im.base, im.img)
        for at, size in ((self.root, 0x90000), (self.array, 0x10000), (self.vector, 0x10000), (self.stack, 0x10000), (self.stop, 4096)):
            self.u.mem_map(at, size)
        self.u.mem_write(im.base+0x277DF18, struct.pack('<Q', self.root))
        self.u.mem_write(self.root+0x80880, b''.join(struct.pack('<Q', self.array+i*0xB0) for i in range(101)))
        assert im.u64(0x13A7328+0x50) == im.base+0xDB90
        assert im.u64(0x13A7328+0x58) == im.base+0xDB30

    def put(self, index, data, vector):
        record = bytearray(data)
        struct.pack_into('<Q', record, 0, self.im.base+0x13A7328)
        start = self.vector+index*64
        struct.pack_into('<QQQ', record, 0x98, start, start+len(vector), start+40)
        self.u.mem_write(self.array+index*0xB0, bytes(record))
        if vector: self.u.mem_write(start, bytes(vector))

    def call(self, rva, index, arg=0, rotate=0, key=0):
        sp = self.stack+0xF008
        self.u.mem_write(sp, struct.pack('<Q', self.stop))
        for reg, value in ((UC_X86_REG_RSP,sp),(UC_X86_REG_RCX,self.array+index*0xB0),
                           (UC_X86_REG_RDX,arg),(UC_X86_REG_R8,rotate),(UC_X86_REG_R9,key)):
            self.u.reg_write(reg,value)
        self.u.emu_start(self.im.base+rva,self.stop,count=10000)
        if self.u.reg_read(UC_X86_REG_RIP)!=self.stop: raise RuntimeError('Native did not return')
        return self.u.reg_read(UC_X86_REG_RAX) & 65535

def synthetic(im):
    n, failures, calls = Native(im), [], 0
    # Every ushort at effect ID 1, encoded and fallback; boundary values at other IDs.
    for index in (1,2,10,40,100):
        values = range(65536) if index==1 else (0,1,100,255,256,32767,32768,65535)
        for value in values:
            data, vector = bytearray(0xB0), bytearray([0xA5]*40)
            for slot in range(10):
                struct.pack_into('<H',data,0x82+slot*2,value)
                struct.pack_into('<H',vector,slot*4,encode(value,index))
            for encoded in (False,True):
                n.put(index,data,vector if encoded else b'')
                got=n.call(0x22C590,index,5); calls+=1
                if got!=value: failures.append([index,value,encoded,got])
            got=n.call(0xDB90,index,value,11,index); calls+=1
            if got!=encode(value,index): failures.append(['encoder',index,value,got])
    data, vector = bytearray(0xB0), bytearray(40)
    for slot in range(10):
        struct.pack_into('<H',data,0x82+slot*2,slot*101)
        struct.pack_into('<H',vector,slot*4,encode(slot*101,100))
    for encoded in (False,True):
        n.put(100,data,vector if encoded else b'')
        for level in (-100,0,1,2,9,10,11,255):
            got=n.call(0x22C590,100,level & 0xFFFFFFFF); calls+=1
            expected=max(0,min(level-1,9))*101
            if got!=expected: failures.append(['level',level,encoded,got,expected])
    return {'nativeCalls':calls,'allUshortValuesAtId':1,'otherIds':[2,10,40,100], 'mismatches':failures,
            'nativeCodePatched':False,'memoryWritten':False,'profileSaved':False}

def live(im):
    from game import Game,k32
    g=Game()
    profile=Path.home()/'AppData/Local/San14Tenkazu/rule_profiles.json'
    before=profile.read_bytes() if profile.exists() else None
    try:
        path=ctypes.create_unicode_buffer(32768); length=ctypes.c_ulong(len(path))
        if not k32.QueryFullProcessImageNameW(ctypes.c_void_p(g.h),0,path,ctypes.byref(length)): raise RuntimeError('Cannot read executable path')
        exe_hash=digest(Path(path.value).read_bytes())
        if exe_hash!=KNOWN_EXE: raise RuntimeError('Unverified executable')
        checks={name:all(required(g,g.exe+ins.address,ins.size)==bytes(ins.bytes) for ins in im.flow(rva)) for name,rva in FUNCTIONS.items()}
        if not all(checks.values()): raise RuntimeError('Live instructions differ')
        world=next(r for r,c in im.rtti_vtables().items() if c=='.?AVCWorldData@@')
        def snapshot():
            root=g.q(g.exe+0x277DF18); state=g.q(root+0x871E8) if root else 0
            if not state or g.q(state)!=g.exe+world: raise RuntimeError('World not ready')
            clock=required(g,state+0x34,5)
            if not 100<struct.unpack_from('<H',clock)[0]<400 or not 1<=clock[2]<=12 or not 1<=clock[3]<=31: raise RuntimeError('Invalid game date')
            result=[]
            for table,offset,stride,count,vt in TABLES:
                pointers=required(g,root+offset,count*8); array=struct.unpack_from('<Q',pointers)[0]; rows=[]
                for index in range(count):
                    address=struct.unpack_from('<Q',pointers,index*8)[0]
                    if not array or address!=array+index*stride: raise RuntimeError('Table is not contiguous')
                    data=required(g,address,stride)
                    if struct.unpack_from('<Q',data)[0]!=g.exe+vt: raise RuntimeError('Record class differs')
                    vector=b''
                    if table=='policyeffect':
                        start,end,cap=struct.unpack_from('<QQQ',data,0x98)
                        empty=start==end and (start==cap==0 or start>=65536 and cap>=end)
                        if not empty and not (start>=65536 and end==start+40 and end<=cap<=start+4096): raise RuntimeError('Vector not ready')
                        vector=b'' if empty else required(g,start,40)
                    rows.append((address,data,vector))
                if pointers!=required(g,root+offset,count*8): raise RuntimeError('Table changed')
                result.append(rows)
            if root!=g.q(g.exe+0x277DF18) or state!=g.q(root+0x871E8): raise RuntimeError('Root changed')
            return root,state,clock,result
        first=snapshot()
        for _ in range(16):
            time.sleep(0.05)
            if required(g,first[1]+0x34,5)!=first[2]: raise RuntimeError('Game is running')
        second=snapshot()
        if second!=first: raise RuntimeError('Policy data changed')
        rows,failures,n=[],[],Native(im)
        for number,(table,*_) in enumerate(TABLES):
            for index,(address,data,vector) in enumerate(second[3][number]):
                name=data[0x10:0x1C].decode('utf-16-le').split('\0')[0]
                description=data[0x1C if table=='policyeffect' else 0x3C:0x82 if table=='policyeffect' else 0x98].decode('utf-16-le').split('\0')[0]
                row={'table':table,'id':index,'name':name,'description':description,'address':hex(address),'data':data.hex(),'vector':vector.hex()}
                if table=='policy': row['effects']=list(data[0x98:0xA0])
                else:
                    n.put(index,data,vector); row['levels']=[]
                    for slot in range(10):
                        plain=struct.unpack_from('<H',data,0x82+slot*2)[0]
                        decoded=decode(struct.unpack_from('<H',vector,slot*4)[0],index) if vector else plain
                        got=n.call(0x22C590,index,slot+1)
                        row['levels'].append({'plain':plain,'decoded':decoded,'native':got})
                        if plain!=decoded or decoded!=got: failures.append([table,index,slot,plain,decoded,got])
                rows.append(row)
        after=profile.read_bytes() if profile.exists() else None
        if after!=before or second!=snapshot(): raise RuntimeError('User data changed during audit')
        return {'processId':g.pid,'exeSha256':exe_hash,'root':hex(second[0]),'state':hex(second[1]),'clockHex':second[2].hex(),
                'stable':True,'pace':'Paused','codeChecks':checks,'records':rows,'nativeCallsOnCopiedData':1010,
                'profileSha256Before':digest(before) if before else None,'profileSha256After':digest(after) if after else None,
                'mismatches':failures,'memoryWritten':False,'profileSaved':False,'nativeCodePatched':False}
    finally: k32.CloseHandle(ctypes.c_void_p(g.h))

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--synthetic',action='store_true'); p.add_argument('--live',action='store_true'); p.add_argument('--output',type=Path,required=True)
    a=p.parse_args(); im=Image()
    result={'utc':datetime.now(timezone.utc).isoformat(),'imageSha256':digest(im.img),
            'assembly':{name:im.flowtext(rva) for name,rva in FUNCTIONS.items()}}
    if a.synthetic: result['synthetic']=synthetic(im)
    if a.live: result['live']=live(im)
    a.output.parent.mkdir(parents=True,exist_ok=True)
    a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:{'mismatches':len(v['mismatches']),'calls':v.get('nativeCalls',v.get('nativeCallsOnCopiedData'))} for k,v in result.items() if k in ('live','synthetic')}))
    if any(result[k]['mismatches'] for k in ('live','synthetic') if k in result): raise SystemExit(1)

if __name__=='__main__': main()
