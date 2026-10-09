# Saved-code evidence for the attack caller's cached troops validity receiver.
# No Game imports, native calls, or changes to the generic boundary report.
import argparse
from collections import deque
import hashlib
import json
from pathlib import Path
from capstone.x86_const import X86_OP_IMM, X86_OP_MEM, X86_OP_REG, X86_REG_RBP
import capstone
from ai_attack_access import audit, reg
from ai_static_facts import anchored, dominates
from ai_static_switch import executable
from image import Image


def r12_constant(im, entry, point):
    # Only the nonvolatile R12 constant is tracked. Calls preserve it under the
    # stated ordinary Windows x64 ABI; differing/unsupported writes widen to unknown.
    code={i.address:i for i in im.flow(entry)};states={entry:None};pending=deque([entry])
    while pending:
        pc=pending.popleft();ins=code[pc];value=states[pc];ops=ins.operands
        if any(reg(ins,r)=='r12' for r in ins.regs_access()[1]):
            value=None
            if len(ops)==2 and ops[0].type==X86_OP_REG and reg(ins,ops[0].reg)=='r12' and ops[0].size in (4,8):
                if ins.mnemonic=='xor' and ops[1].type==X86_OP_REG and reg(ins,ops[1].reg)=='r12':value=0
                elif ins.mnemonic=='mov' and ops[1].type==X86_OP_IMM:value=ops[1].imm&((1<<(ops[0].size*8))-1)
        successors=[]
        if ins.mnemonic.startswith('j'):
            if not ops or ops[0].type!=X86_OP_IMM:raise ValueError('Unknown jump in cached initializer constant proof')
            successors.append(ops[0].imm)
            if ins.mnemonic!='jmp':successors.append(pc+ins.size)
        elif ins.mnemonic not in ('ret','int3'):successors.append(pc+ins.size)
        for target in successors:
            if target not in code:continue
            if target not in states:states[target]=value;pending.append(target)
            elif states[target]!=value and states[target] is not None:states[target]=None;pending.append(target)
    return states.get(point)


def r8_reads(im, entry, kill=None):
    rows=[]
    for ins in im.flow(entry):
        if not any(reg(ins,r)=='r8' for r in ins.regs_access()[0]) or ins.address==kill:continue
        if kill is None or not dominates(im,entry,kill,ins.address):
            raise ValueError(f'Incoming R8 acquired a read at {ins.address:X}')
        rows.append(dict(rva=ins.address,text=ins.mnemonic+' '+ins.op_str))
    return rows


def build(im):
    im.md.detail=True
    anchors={
        'worldArray':anchored(im,0x3262E0,{0x326303:'mov rbx, rcx',0x3263DD:'mov r12d, 0x1f5',
            0x3277DF:'mov qword ptr [rax], r12',0x3277E2:'lea rdi, [rax + 8]',
            0x3277F2:'lea r9, [rip - 0x105e79]',0x3277F9:'mov r8, r12',0x3277FC:'mov edx, 0x40',
            0x327801:'mov rcx, rdi',0x327804:'call 0xff945c',0x32780E:'lea rcx, [rbx + 0x7f8d8]',
            0x32786C:'mov rax, r12',0x327870:'mov qword ptr [rcx], rdi',0x327873:'add rdi, 0x40',
            0x327877:'lea rcx, [rcx + 8]',0x32787B:'sub rax, 1',0x32787F:'jne 0x327870'}),
        'arrayConstructorDispatch':anchored(im,0xFF945C,{0xFF9478:'mov rsi, r9',0xFF947B:'mov r15, r8',
            0xFF947E:'mov r14, rdx',0xFF9481:'mov rdi, rcx',0xFF9484:'xor ebx, ebx',
            0xFF948D:'cmp rbx, r15',0xFF9490:'je 0xff94b1',0xFF949A:'mov rcx, rdi',
            0xFF949D:'call rsi',0xFF949F:'add rdi, r14',0xFF94A7:'inc rbx',0xFF94AF:'jmp 0xff948d'}),
        'troopsConstructor':anchored(im,0x221980,{0x221993:'mov rbx, rcx',
            0x22199C:'lea rax, [rip + 0x1184f75]',0x2219A3:'mov qword ptr [rbx], rax'}),
        'attackReceiver':anchored(im,0x48CE0,{0x48D3C:'xor r12d, r12d',
            0x49054:'mov qword ptr [rbp - 0x58], r12',0x49A17:'mov rdi, qword ptr [rbp - 0x58]',
            0x49A21:'test rdi, rdi',0x49A24:'jne 0x49a65',0x49A26:'cmp eax, 0x1f4',
            0x49A2B:'ja 0x49a40',0x49A2F:'mov rax, qword ptr [rip + 0x27344e2]',
            0x49A36:'mov rdi, qword ptr [rax + rcx*8 + 0x7f8d8]',
            0x49A47:'mov rdi, qword ptr [rax + 0x7f8d8]',0x49A4E:'mov qword ptr [rbp - 0x58], rdi',
            0x49BA5:'mov rcx, rdi',0x49BA8:'call 0x32f500'}),
        'validityWrapper':anchored(im,0x32F500,{0x32F504:'test rcx, rcx',
            0x32F509:'mov rax, qword ptr [rcx]',0x32F50C:'call qword ptr [rax + 0x18]'}),
        'troopsValidity':anchored(im,0x234540,{0x23454B:'mov rdx, rcx',
            0x23454E:'sub rdx, qword ptr [rax + 0x7f8d8]',0x234555:'sar rdx, 6',
            0x234564:'call 0x22bcb0'}),
        'aliasKill':anchored(im,0x222F20,{0x222F39:'xor r8d, r8d'})}
    vtable=0x13A6918;target=im.u64(vtable+0x18)-im.base
    if im.rtti_vtables().get(vtable)!='.?AVCTroopsData@@' or target!=0x234540 or not executable(im,target):
        raise ValueError('Troops RTTI or validity slot changed')
    if not dominates(im,0x48CE0,0x49A17,0x49BA8):raise ValueError('Cached receiver load no longer dominates the call')
    if r12_constant(im,0x48CE0,0x49054)!=0:raise ValueError('Cached receiver initializer is no longer proved zero')
    cache_writes=[]
    for ins in im.flow(0x48CE0):
        if any(op.type==X86_OP_MEM and op.mem.base==X86_REG_RBP and op.mem.disp==-0x58
               and op.access&capstone.CS_AC_WRITE for op in ins.operands):cache_writes.append(ins.address)
    if cache_writes!=[0x49054,0x49A4E]:raise ValueError('Cached receiver acquired another direct writer')
    audits=[audit(im,entry,{'r8':('node',0)}) for entry in (target,0x22BCB0,0x222F20)]
    expected=[[(0x234564,'0x22bcb0')],[(0x22BCC7,'0x222f20')],[]]
    for row,sites in zip(audits,expected):
        if [(c['rva'],c['target']) for c in row['callBoundaries']]!=sites:
            raise ValueError('Incoming node alias call path changed')
        if row['accesses'] or row['pointerEscapes'] or row['stackCallCandidates']:
            raise ValueError('Incoming node alias acquired a field access or escape')
    reads={entry:r8_reads(im,entry,0x222F39 if entry==0x222F20 else None)
           for entry in (target,0x22BCB0,0x222F20)}
    return dict(receiverCall=0x49BA8,wrapperDispatch=0x32F50C,receiverTable=0x7F8D8,
        cachedReceiverOffset=-0x58,cacheInitializerR12=0,directCacheWriters=cache_writes,arrayCount=501,arrayStride=0x40,
        constructor=0x221980,className='CTroopsData',vtable=vtable,slot=0x18,target=target,
        incomingNodeAlias='r8',aliasKill=0x222F39,downstreamAliasAudits=audits,
        r8ReadsAfterKill=reads,anchors=anchors,nativeObserved=False,wholeProgramNonuseProved=False,
        attackP2MeaningKnown=False,
        scope='original world CTroopsData array construction, cached table receiver and ordinary x64 ABI; intact separate objects and unmodified constructor vtables',
        limits=['generic affine analysis still reports the unresolved virtual receiver',
                'this scoped binding is not observed execution or proof for arbitrary replaced/corrupt vtables',
                'other external escaped aliases and global side effects remain outside this proof',
                'absence of an incoming R8 field read on this path does not establish the meaning of p2'])


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    im=Image();result=build(im);result['imageSha256']=hashlib.sha256(im.img).hexdigest()
    result['analyzerSha256']={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
        for name in ('ai_attack_receiver.py','ai_attack_access.py','ai_static_facts.py','ai_static_switch.py')}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('receiver',result['className'],'slot target',hex(result['target']),'R8 killed',hex(result['aliasKill']),
          'p2 meaning known',result['attackP2MeaningKnown'])


if __name__=='__main__':main()
