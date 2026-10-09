# Pointer-free native 2C4D00: complete byte boat codes plus signed-int boundaries.
# python ai_boat_codes.py --vectors ../Tests/AiBoatCodeVectors.json
# Uses the saved executable image only. No game pause, process write or hooks are needed.
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
from ai_friend import HERE, vectors_text
from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RSP, UC_X86_REG_RIP, UC_X86_REG_RAX

class ScalarImage:
    def __init__(self,im):
        self.im=im;self.mu=Uc(UC_ARCH_X86,UC_MODE_64);page=0x2C4000
        self.mu.mem_map(im.base+page,0x1000);self.mu.mem_write(im.base+page,im.img[page:page+0x1000])
        self.stack=0x100000000;self.sentinel=0xD0000000
        self.mu.mem_map(self.stack,0x1000);self.mu.mem_map(self.sentinel,0x1000)
    def call(self,terrain,code):
        sp=self.stack+0x800;self.mu.mem_write(sp,struct.pack('<Q',self.sentinel))
        for r,v in [(UC_X86_REG_RCX,terrain),(UC_X86_REG_RDX,code),(UC_X86_REG_RSP,sp)]:self.mu.reg_write(r,v&0xFFFFFFFFFFFFFFFF)
        self.mu.emu_start(self.im.base+0x2C4D00,self.sentinel,count=1000)
        if self.mu.reg_read(UC_X86_REG_RIP)!=self.sentinel or self.mu.reg_read(UC_X86_REG_RSP)!=sp+8:raise AssertionError('native scalar did not return')
        return self.mu.reg_read(UC_X86_REG_RAX)&0xFFFFFFFF

def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);args=p.parse_args();im=Image();o=ScalarImage(im);cases=[]
    for kind in [-2147483648,-1,0,23,24,25,2147483647]:
        for code in list(range(256))+[-2147483648,-1,256,2147483647]:
            cases.append(dict(terrain=kind,code=code,native=o.call(kind,code)))
    result=dict(utc=datetime.now(timezone.utc).isoformat(),method='original pointer-free 2C4D00 on saved executable image; every byte code and signed-int boundaries; no result replacement or game writes',
                cases=cases,image_sha256=hashlib.sha256(im.img).hexdigest(),static=im.flowtext(0x2C4D00))
    text=vectors_text(result);folder=HERE/'.cache'/'ai_boat_codes';folder.mkdir(parents=True,exist_ok=True)
    file=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');file.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(len(cases),'completed;',file)
if __name__=='__main__':main()
