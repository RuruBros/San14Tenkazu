"""Execute unmodified Native scalar-read instructions on copied/synthetic rank data; no live writes."""
import argparse, json, struct, sys
from pathlib import Path
from datetime import datetime, timezone
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE/'.venv/Lib/site-packages'))
from image import Image
from tactic_rule_audit import digest
from unicorn import Uc,UC_ARCH_X86,UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RDX,UC_X86_REG_R15,UC_X86_REG_R14,UC_X86_REG_RDI,UC_X86_REG_R12,UC_X86_REG_RAX,UC_X86_REG_RCX

def audit(im,records):
    u=Uc(UC_ARCH_X86,UC_MODE_64); u.mem_map(im.base,(len(im.img)+4095)&~4095); u.mem_write(im.base,im.img)
    record=0x300000000; u.mem_map(record,4096)
    reads={'capacity':(0x2B2986,0x2B298E,UC_X86_REG_R15,0xAA,2),
           'grade':(0x1DE944,0x1DE94B,UC_X86_REG_RCX,0xAC,1)}
    for slot,name in enumerate(('leadership','strength','intelligence','politics','charm')):
        reads[name]=(0x22FE97,0x22FEA0,UC_X86_REG_R12,0xAD+slot,1)
    def read(name,data):
        start,end,out,offset,width=reads[name]; u.mem_write(record,bytes(data))
        for reg,val in ((UC_X86_REG_RDX,record),(UC_X86_REG_RAX,record),(UC_X86_REG_RDI,record),(UC_X86_REG_R14,offset-0xAD)):
            u.reg_write(reg,val & ((1<<64)-1))
        u.emu_start(im.base+start,im.base+end,count=1)
        return u.reg_read(out)&0xFFFFFFFF
    mismatches=[]; calls=0
    for name,(_,_,_,offset,width) in reads.items():
        for value in range(1<<(width*8)):
            data=bytearray([0xA5]*0xB8); data[offset:offset+width]=value.to_bytes(width,'little'); calls+=1
            got=read(name,data)
            if got!=value: mismatches.append([name,value,got])
    copied=[]
    for row in records:
        if row['id']==0: continue
        data=bytes.fromhex(row['data']); values={}
        for name,(_,_,_,offset,width) in reads.items():
            got=read(name,data); plain=int.from_bytes(data[offset:offset+width],'little'); calls+=1
            values[name]=got
            if got!=plain: mismatches.append([row['id'],name,plain,got])
        copied.append({'id':row['id'],'name':row['name'],'fields':values})
    return {'nativeInstructionExecutions':calls,'unmodifiedCodeBlocks':{n:im.text(a,b) for n,(a,b,*_) in reads.items()},
            'copiedRecords':copied,'mismatches':mismatches,'fullGameplayFunctionsExecuted':False,
            'nativeCodePatched':False,'memoryWritten':False,'profileSaved':False}

def main():
    p=argparse.ArgumentParser(); p.add_argument('--copied-tables',type=Path,required=True); p.add_argument('--output',type=Path,required=True); a=p.parse_args()
    im=Image(); copied=json.loads(a.copied_tables.read_text(encoding='utf-8-sig'))
    result={'utc':datetime.now(timezone.utc).isoformat(),'imageSha256':digest(im.img),'copiedProcessId':copied['processId'],
            'copiedReadUtc':copied['utc'],'audit':audit(im,copied['tables']['rank']['records'])}
    a.output.parent.mkdir(parents=True,exist_ok=True); a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'calls':result['audit']['nativeInstructionExecutions'],'mismatches':len(result['audit']['mismatches'])}))
    if result['audit']['mismatches']: raise SystemExit(1)
if __name__=='__main__': main()
