"""Compare bounded CSV key rules to the saved code in Unicorn only."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_PROT_READ, UC_PROT_WRITE, UC_PROT_EXEC
from unicorn.x86_const import (UC_X86_REG_RSP,UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R13,
                              UC_X86_REG_RAX,UC_X86_REG_R9,UC_X86_REG_RIP)
from audit_model_bind_tables import IMAGE_SHA
from shader_name_hash import name_hash


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--image',required=True,type=Path)
    parser.add_argument('--image-base',required=True,type=lambda value:int(value,0))
    parser.add_argument('--links',required=True,type=Path)
    parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args();data=args.image.read_bytes();base=args.image_base
    if hashlib.sha256(data).hexdigest()!=IMAGE_SHA:raise ValueError('Inspected saved-image Hash differs')
    raw=args.links.read_bytes();links=json.loads(raw);table=links['shaderNameTable']
    uc=Uc(UC_ARCH_X86,UC_MODE_64);stack=0x500000000;text=0x600000000;stop=0x700000000
    uc.mem_map(base,(len(data)+4095)&~4095,UC_PROT_READ|UC_PROT_EXEC);uc.mem_write(base,data)
    uc.mem_map(stack,0x10000,UC_PROT_READ|UC_PROT_WRITE)
    uc.mem_map(text,0x1000,UC_PROT_READ);uc.mem_map(stop,0x1000,UC_PROT_READ|UC_PROT_EXEC)
    sp=stack+0xFE00
    counts={'group':0,'object':0};errors=[]

    def check(value,count,group,label):
        if len(value)>=4096:raise ValueError('Hash fixture is too long')
        uc.mem_write(text,value+b'\0');uc.mem_write(sp,struct.pack('<Q',stop))
        uc.reg_write(UC_X86_REG_RSP,sp)
        if group:
            uc.reg_write(UC_X86_REG_RCX,text);uc.reg_write(UC_X86_REG_RDX,count)
            begin=base+0xABC290;end=stop;register=UC_X86_REG_RAX
        else:
            uc.reg_write(UC_X86_REG_R13,0)
            uc.mem_write(sp+0x50,struct.pack('<I',count));uc.mem_write(sp+0x58,struct.pack('<Q',text))
            begin=base+0xF51137;end=base+0xF51172;register=UC_X86_REG_R9
        uc.emu_start(begin,end,count=50000)
        if uc.reg_read(UC_X86_REG_RIP)!=end:raise ValueError('Hash emulation stopped before completion')
        actual=uc.reg_read(register)&0xFFFFFFFF;expected=name_hash(value,count,group)
        counts['group' if group else 'object']+=1
        if actual!=expected:errors.append(dict(label=label,count=count,group=group,bytes=value.hex(),actual=actual,expected=expected))

    for value in range(256):
        for count in (0,1):
            for group in (False,True):check(bytes([value]),count,group,'single byte '+str(value))
    fixtures=[b'',b'ABC',b'A\0BC',b'\0ABC',bytes(range(256)),bytes(reversed(range(256))),b'X'*2048,
              'CE1ResourceShaderStandard\uff3bSTD\uff3d'.encode('utf-8')]
    for value in fixtures:
        for count in sorted({0,len(value)//2,len(value)}):
            for group in (False,True):check(value,count,group,'bounded/NUL/UTF8')
    for index,row in enumerate(table['rows']):
        for column,group in ((0,True),(1,False)):
            value=row[column].encode('utf-8');check(value,len(value),group,'CSV row '+str(index+1))
        if (index+1)%5000==0:print(json.dumps(dict(csvRows=index+1,comparisons=sum(counts.values()))),flush=True)
    result=dict(mode='Saved image emulation only',gameProcessAccess=False,imageSha256=IMAGE_SHA,
        linksSha256=hashlib.sha256(raw).hexdigest(),shaderTableSha256=table['payloadSha256'],
        groupFunctionRva='ABC290',objectInlineRange=['F51137','F51172'],csvRows=table['rowCount'],
        comparisons=counts,totalComparisons=sum(counts.values()),errors=errors,passed=not errors,
        gameDrawPassVerified=False,shaderBytecodeIdentityVerified=False)
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    if errors:raise ValueError('Native/Python hash comparison failed')
    print(json.dumps(dict(comparisons=sum(counts.values()),passed=True,gameProcessAccess=False)),flush=True)


if __name__=='__main__':main()
