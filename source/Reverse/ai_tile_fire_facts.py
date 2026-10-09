# Identify the path fire filter from saved instructions and original proposal RTTI.
# No Game imports, process access or native execution.
# python ai_tile_fire_facts.py --scan-writers --output .cache/ai_tile_fire_facts/report.json
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import time

import capstone
from capstone.x86_const import X86_OP_MEM
from ai_static_facts import anchored
from ai_static_switch import executable
from image import Image

HERE = Path(__file__).resolve().parent


def proposal_binding(im, vtable, expected_class, target):
    name = im.rtti_vtables().get(vtable)
    if name != expected_class or im.u64(vtable + 0x48) - im.base != target or not executable(im, target):
        raise ValueError('Proposal RTTI or executable slot target changed')
    return dict(vtable=vtable, slot=0x48, target=target, originalClass=name)


def scan_writers(im):
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    start, size = im.sections['.text']; rows = {}; scanned = 0
    began = time.monotonic()
    for begin, end, _ in im.funcs:
        if not start <= begin < end <= start + size: continue
        for pc, length, mnemonic, operands in im.md.disasm_lite(im.img[begin:end], begin):
            scanned += 1
            if '+ 0x16]' not in operands or 'word ptr' not in operands: continue
            ins = next(decoder.disasm(im.img[pc:pc + length], pc))
            writes = [op for op in ins.operands if op.type == X86_OP_MEM and op.mem.disp == 0x16
                      and op.size == 2 and op.access & capstone.CS_AC_WRITE]
            if writes:
                rows[pc] = dict(entry=begin, rva=pc, bytes=bytes(ins.bytes).hex(), text=mnemonic + ' ' + operands,
                                base=ins.reg_name(writes[0].mem.base), index=ins.reg_name(writes[0].mem.index))
    return dict(instructions=scanned, candidates=list(rows.values()), elapsedSeconds=time.monotonic() - began,
                scope='pdata-covered text instructions; literal displacement16 two-byte writes only; other receiver classes and indirect/aliased writers are not identified by this scan')


def build(im):
    setter = anchored(im, 0x2EDD40, {0x2EDD4A:'mov ebx, edx',0x2EDD4C:'mov rdi, rcx',
        0x2EDD4F:'call 0x32f500',0x2EDD56:'je 0x2edd92',0x2EDD58:'test ebx, ebx',
        0x2EDD5A:'jns 0x2edd60',0x2EDD5C:'xor eax, eax',0x2EDD60:'mov ecx, 0x2d0',
        0x2EDD69:'cmovg eax, ecx',0x2EDD6C:'mov word ptr [rdi + 0x16], ax',
        0x2EDD73:'mov rcx, qword ptr [rip + 0x18c5f86]',0x2EDD7C:'jle 0x2edd8d',
        0x2EDD88:'jmp 0x18acf0',0x2EDD8D:'call 0x18c270'})
    update = anchored(im, 0x2A1DC0, {0x2A1DCF:'mov rax, qword ptr [rip + 0x24dc142]',
        0x2A1DD6:'lea rsi, [rax + 0xe270]',0x2A1DDD:'add rax, 0x6caf0',
        0x2A1DFB:'mov r14d, 0x2d0',0x2A1E10:'mov rdi, qword ptr [rsi]',
        0x2A1E13:'movzx ebx, word ptr [rdi + 0x16]',0x2A1E19:'je 0x2a1ec3',
        0x2A1E1F:'sub ebx, dword ptr [rip + 0x17a71ff]',0x2A1E28:'cmovs ebx, r15d',
        0x2A1E2C:'call 0x32f500',0x2A1E43:'cmovg eax, r14d',
        0x2A1E47:'mov word ptr [rdi + 0x16], ax',0x2A1E65:'cmp word ptr [rdi + 0x16], r15w',
        0x2A1E82:'mov rax, qword ptr [rbp + 8]',0x2A1EA0:'call 0x18df0',
        0x2A1EAA:'mov qword ptr [rax], rdi',0x2A1ECA:'add rsi, 8',0x2A1ED7:'jne 0x2a1e10'})
    proposals = [proposal_binding(im,0x13D2088,'.?AVCProposalHandler_BurnEnemy@@',0x415AC0),
                 proposal_binding(im,0x13D2188,'.?AVCProposalHandler_ExtinguishFireFriend@@',0x416A30)]
    proposals[0]['anchors'] = anchored(im,0x415AC0,{0x415B61:'mov rcx, rdi',
        0x415B64:'idiv dword ptr [rip + 0x1632406]',0x415B6A:'shl eax, 5',
        0x415B6D:'add eax, dword ptr [rip + 0x1632409]',0x415B73:'mov edx, eax',0x415B75:'call 0x2edd40'})
    proposals[1]['anchors'] = anchored(im,0x416A30,{0x416AB1:'mov rcx, qword ptr [rdx + rax*8 + 0xe270]',
        0x416AC6:'xor edx, edx',0x416AC8:'call 0x2edd40'})
    ignite = anchored(im,0x2E39E0,{0x2E3E6C:'mov edx, edi',0x2E3E6E:'mov rcx, r14',0x2E3E71:'call 0x2edd40'})
    path = anchored(im,0x183A30,{0x184079:'cmp dword ptr [rdx + 0x28], 0',0x18407D:'je 0x18408a',
        0x18407F:'cmp word ptr [rbx + 0x16], 0',0x184084:'ja 0x1842df'})
    return dict(field=dict(className='CHexData',offset=0x16,size=2,role='remaining fire timer',minimum=0,maximum=720),
                setter=dict(entry=0x2EDD40,anchors=setter,visualCallbacks=[0x18ACF0,0x18C270]),
                update=dict(entry=0x2A1DC0,tileCount=(0x6CAF0-0xE270)//8,decrementRva=0x1A49024,
                            savedDecrement=im.i32(0x1A49024),anchors=update),
                proposals=proposals,ignite=dict(entry=0x2E39E0,anchors=ignite),
                path=dict(entry=0x183A30,contextOffset=0x28,branch=0x184084,skip=0x1842DF,anchors=path),
                nativeObserved=False,nativePathFireEdgeObserved=False,
                limits=['timer unit and update scheduling are not inferred from the decrement body',
                        'class names are original RTTI; function role labels are descriptive, not recovered symbols',
                        'setter writes precede fire visual callbacks; callback side effects are outside the timer statement',
                        'static fire identity does not turn the previously unobserved A* fire branch into native coverage'])


def existing_evidence():
    files = ['../Core/Formulas/TacticFire.cs','vectors/tactic_fire.py','../Tests/Formulas/TacticFireTests.cs',
             '../Tests/Vectors/tactic_fire.json.gz']
    evidence = [dict(file=str((HERE / f).resolve().relative_to(HERE.parent.parent)),
                     sha256=hashlib.sha256((HERE / f).read_bytes()).hexdigest()) for f in files]
    fixture = json.loads(gzip.decompress((HERE/'../Tests/Vectors/tactic_fire.json.gz').read_bytes()))
    return dict(files=evidence,fixtureCounts={key:len(fixture[key]) for key in ('ignite','policy','apply')},
                scope='existing independently recorded Formulas vectors; this analyzer does not rerun or certify their native calls')


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--scan-writers',action='store_true');args=parser.parse_args()
    im=Image();result=build(im);result['existingEvidence']=existing_evidence()
    if args.scan_writers:result['writerScan']=scan_writers(im)
    result['imageSha256']=hashlib.sha256(im.img).hexdigest()
    result['analyzerSha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('tile+16 fire timer; two original proposal classes; path fire edge remains unobserved')
    if args.scan_writers:print(len(result['writerScan']['candidates']),'literal word16 writers; receiver binding required')


if __name__=='__main__':main()
