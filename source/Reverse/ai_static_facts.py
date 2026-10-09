# Anchored code facts for the remaining path cases. No Game imports or execution.
# python ai_static_facts.py --output .cache/ai_static_facts/report.json
import argparse
import hashlib
import json
from pathlib import Path
import capstone
from capstone.x86_const import X86_OP_IMM, X86_OP_MEM, X86_REG_RCX, X86_REG_RDI, X86_REG_RSP
from image import Image
from ai_static_switch import executable

HERE = Path(__file__).resolve().parent


def dominates(im, entry, before, call):
    code = {i.address: i for i in im.flow(entry)}
    pending, seen = [entry], set()
    while pending:
        pc = pending.pop()
        if pc == before or pc in seen: continue
        if pc == call: return False
        seen.add(pc); ins = code.get(pc)
        if ins is None or ins.mnemonic in ('ret', 'int3'): continue
        next_ = pc + ins.size
        if ins.mnemonic.startswith('j'):
            if not ins.operands or ins.operands[0].type != X86_OP_IMM:
                raise ValueError('Unresolved jump prevents a constructor dominance claim')
            pending.append(ins.operands[0].imm)
            if ins.mnemonic != 'jmp': pending.append(next_)
        else: pending.append(next_)
    return True


def anchored(im, entry, expected):
    instructions = {i.address: i for i in im.flow(entry)}
    rows = []
    for pc, text in expected.items():
        ins = instructions.get(pc)
        actual = (ins.mnemonic + ' ' + ins.op_str).strip() if ins else None
        if actual != text: raise ValueError(f'Changed fact anchor {pc:X}: {actual}, expected {text}')
        rows.append(dict(rva=pc, bytes=bytes(ins.bytes).hex(), text=actual))
    return rows


def bindings(im):
    im.md.detail = True
    anchors = []
    anchors += anchored(im, 0x222DA0, {0x222DB3:'mov rbx, rdx',
        0x222E02:'lea rax, [rip + 0x1183717]', 0x222E09:'mov qword ptr [rbx], rax',
        0x222E20:'mov rax, qword ptr [rbx]', 0x222E27:'mov rcx, rbx', 0x222E2A:'call qword ptr [rax + 8]'})
    anchors += anchored(im, 0x223060, {0x223073:'mov rbx, rdx',
        0x223088:'lea rcx, [rip + 0x1183491]', 0x22308F:'mov qword ptr [rbx], rcx',
        0x2230A0:'mov rax, qword ptr [rbx]', 0x2230A7:'mov rcx, rbx', 0x2230AA:'call qword ptr [rax + 8]'})
    anchors += anchored(im, 0x185B30, {0x185C79:'lea rdx, [rbp - 0x71]', 0x185C80:'call 0x222da0',
        0x185CD2:'lea rcx, [rbp - 0x71]', 0x185CD6:'mov rax, qword ptr [rbp - 0x71]', 0x185CDA:'call qword ptr [rax + 8]',
        0x185DFB:'lea rdx, [rbp - 0x71]', 0x185E02:'call 0x222da0',
        0x185E62:'lea rcx, [rbp - 0x71]', 0x185E66:'mov rax, qword ptr [rbp - 0x71]', 0x185E6A:'call qword ptr [rax + 8]'})
    vtable = 0x13A6520; target = im.u64(vtable + 8) - im.base
    if im.rtti_vtables().get(vtable) != '.?AVCFootholdArmyUnitIterator@@' or not executable(im, target):
        raise ValueError('Iterator RTTI or executable slot target changed')
    anchors += anchored(im, target, {0x33311A:'mov eax, dword ptr [rcx + 0x18]',
        0x333122:'mov rdi, rcx', 0x33312A:'mov rcx, qword ptr [rdx]', 0x33312D:'call 0x233440',
        0x333136:'mov rcx, qword ptr [rbx]', 0x333139:'call 0x22a610', 0x33313E:'cmp eax, dword ptr [rdi + 0x18]'})
    factories = [(0x222DA0,0x222E09,0x222E2A),(0x223060,0x22308F,0x2230AA),
                 (0x185B30,0x185C80,0x185CDA),(0x185B30,0x185E02,0x185E6A)]
    for entry, factory, call in factories:
        if not dominates(im, entry, factory, call): raise ValueError('Iterator factory no longer dominates its dispatch')
    for ins in im.flow(target):
        if any(op.type == X86_OP_MEM and op.mem.base in (X86_REG_RCX,X86_REG_RDI) and op.access & capstone.CS_AC_WRITE for op in ins.operands):
            raise ValueError('Iterator dispatch acquired a receiver write')
    return dict(vtable=vtable, slot=8, target=target, className='CFootholdArmyUnitIterator', anchors=anchors,
                factoryDominance=[dict(entry=e,factory=f,call=c) for e,f,c in factories],
                callSites=[0x222E2A,0x2230AA,0x185CDA,0x185E6A], nativeObserved=False,
                scope='original constructor-installed iterator vtable and valid separate pool payloads; decoded local caller paths; not arbitrary overridden/corrupt iterator objects',
                method='explicit constructor store and matching local receiver loads; slot body reads the iterator and passes payload objects to its callees')


def read_only_leaf(im, entry):
    im.md.detail=True;code=list(im.flow(entry));positions={i.address for i in code};stores=[]
    if not code:raise ValueError('No reader body')
    for ins in code:
        if ins.mnemonic in ('call','.byte','syscall','sysenter','int','out','wrmsr','movsb','movsw','movsq') or ins.mnemonic.startswith(('stos','ins')):
            raise ValueError('Reader acquired a call or unsupported implicit write')
        if ins.mnemonic.startswith('j') and (not ins.operands or ins.operands[0].type!=X86_OP_IMM or ins.operands[0].imm not in positions):
            raise ValueError('Reader acquired an unresolved or external jump')
        for op in ins.operands:
            if op.type==X86_OP_MEM and op.access&capstone.CS_AC_WRITE:
                if op.mem.base!=X86_REG_RSP or op.mem.index:raise ValueError('Reader acquired a non-stack write')
                stores.append(ins.address)
    return dict(entry=entry,instructions=len(code),stackStores=stores,calls=0,nonStackWrites=0,
                scope='entire decoded local body; direct internal jumps only; implicit/external writes rejected')


def pool_ownership(im):
    fresh=anchored(im,0x183A30,{0x183DB4:'call 0x18b60',0x183DB9:'mov rbx, rax',
        0x183DBC:'mov qword ptr [rbp - 0x49], rax',0x183E20:'mov eax, dword ptr [rbx]',
        0x183E35:'call 0x18df0',0x183E3F:'mov qword ptr [rax], rdi',
        0x183EB2:'xor r9d, r9d',0x183EB5:'lea r8d, [r9 + 0x2c]',
        0x183EB9:'mov rdx, qword ptr [rdi]',0x183EBC:'mov rcx, rsi',
        0x183EBF:'call 0x185b30',0x183EC4:'mov rdi, qword ptr [rdi + 8]',0x183ECB:'jne 0x183ea0'})
    callee=anchored(im,0x185B30,{0x185B32:'push rbx',0x185B67:'movsxd r13, r9d',
        0x185B6A:'movsxd r14, r8d',0x185B6D:'mov rbx, rdx',
        0x185B70:'mov qword ptr [rbp - 0x51], rcx',0x1860B4:'pop rbx'})
    clear=anchored(im,0x56EAA0,{0x56EAAB:'mov rbx, rcx',0x56EAD9:'mov rdx, qword ptr [rbx + 8]',
        0x56EAE7:'mov edx, dword ptr [rdx]',0x56EAF0:'call 0x18770'})
    seven=anchored(im,0x9AB0,{0x9AD2:'mov rdi, rcx',0x9B48:'mov esi, 7',
        0x9BC3:'lea rbx, [rdi + 0x378]',0x9BF6:'mov rax, qword ptr [rbx]',
        0x9C08:'mov edx, dword ptr [rax]',0x9C11:'call 0x18770',
        0x9C2C:'add rbx, 0x10',0x9C30:'sub rsi, 1',0x9C34:'jne 0x9bd0'})
    append=anchored(im,0x1B4600,{0x1B4610:'mov rbp, rcx',0x1B4801:'mov rax, qword ptr [rbp + 0x10]',
        0x1B480A:'mov r14d, dword ptr [rax]',0x1B4817:'mov edx, r14d',0x1B481A:'call 0x18df0'})
    reader=read_only_leaf(im,0x86E7E0)
    reader['anchors']=anchored(im,0x86E7E0,{0x86E7EA:'movsxd rdi, dword ptr [rdx + 0x14]',
        0x86E806:'lea rax, [rdi + 7]',0x86E80A:'add rax, rax',
        0x86E80D:'mov r9, qword ptr [rcx + rax*8 + 8]',0x86E81E:'mov ecx, dword ptr [r9]',
        0x86E82B:'mov rcx, qword ptr [r8 + rcx*8]',0x86E840:'cmp qword ptr [rcx], rdx',
        0x86E845:'mov rcx, qword ptr [rcx + 8]'})
    return dict(freshCaller=dict(anchors=fresh,calleeAnchors=callee,
                    fact='185B30 formal inputs are global context, tile payload, force44 and previousForce0; fresh index pointer remains in caller RBX/stack. Callee preserves incoming RBX then binds its semantic RBX to the tile.'),
                receiverClear=dict(anchors=clear,indexPointerOffset=8),
                sevenReceiverLists=dict(anchors=seven,firstIndexPointerOffset=0x378,stride=0x10,count=7),
                messageAppend=dict(anchors=append,indexPointerOffset=0x10),membershipReader=reader,
                transitiveMutationClosureProved=False,loopTakenObserved=False,
                limits=['these are specific argument/field sources, not a proof that all indices have distinct aliases',
                        'unresolved callees and arbitrary stack/global alias mutation are not excluded',
                        'read-only membership traversal does not scan or mutate every allocated pool index'])


def build(im):
    iterator = bindings(im)
    fields = {
        'tileWord16': dict(anchors=anchored(im,0x2211F0,{0x221215:'mov qword ptr [rbx + 0x16], rax'}) +
            anchored(im,0x238370,{0x2383D0:'lea rdx, [rdi + 0x16]',0x2383D4:'mov r8d, 2',
                0x2383DF:'call 0x3fa6d0',0x2383E6:'call 0x3fa780'}),
            fact='CHexData constructor clears the field; serialization passes its two-byte address. Fire writer identity is separately anchored by ai_tile_fire_facts.py; this constructor fact does not prove a native path fire edge.'),
        'minorArmyPredicate': dict(anchors=anchored(im,0x232730,{0x232736:'movzx eax, word ptr [rcx + 0x12]',
            0x232744:'cmp eax, 0x1770',0x23275C:'movzx ecx, word ptr [rdx + 0xe2]',
            0x232763:'lea eax, [rcx - 1]',0x232766:'cmp eax, 0x3c',
            0x232772:'call 0x22cc40',0x232792:'call qword ptr [rax + 0x60]',
            0x232795:'add eax, -0x2e',0x232798:'cmp eax, 4'}),
            fact='Army leader lookup uses ID<=6000 or the original fallback; leader base must be1..61 and selected force virtual index46..50. World validity and lookup remain required.'),
        'integerIndexAllocation': dict(anchors=anchored(im,0x1AE010,{0x1AE073:'lea rcx, [rip + 0x19ae95e]',
            0x1AE07A:'call 0x18b60',0x1AE082:'mov qword ptr [rbx + 0x20], rdi'}),pool=0x1B5C9D8,
            fact='This18B60 call uses the integer index pool, not the generic pool index object27D1128.'),
        'treeListAppend': dict(anchors=anchored(im,0x42DB60,{0x42DC1A:'mov rax, qword ptr [rbx + 0x30]',
            0x42DC28:'mov ecx, dword ptr [rax]',0x42DC36:'lea rcx, [rip + 0x23a34a3]',0x42DC3D:'call 0x18df0'}),
            fact='Generic list append reads its index pointer from the tree record+30. Aliasing with the temporary path index is not proved.'),
        'treeListIndexConstruction': dict(anchors=anchored(im,0x425160,{0x425193:'lea rdx, [rax + 0x20]',
            0x4251AD:'lea rcx, [rdx + 8]',0x4251BB:'mov qword ptr [rcx + 8], 0',0x4251C3:'call 0x21e2b0'}),
            fact='New tree record list receiver is record+28, whose index pointer is receiver+8=record+30.'),
        'receiverListClear': dict(anchors=anchored(im,0x424D60,{0x424D90:'mov rax, qword ptr [rbx + 8]',
            0x424D99:'mov ecx, dword ptr [rax]',0x424DA4:'lea rcx, [rip + 0x23ac335]',0x424DAB:'call 0x18770',
            0x424DB0:'mov rdx, qword ptr [rbx + 8]',0x424DB4:'lea rcx, [rip + 0x23ac36d]',0x424DBB:'call 0x18830'}),
            fact='Clear receives an index value/pointer from its own receiver+8, not automatically the caller path temporary index.'),
    }
    return dict(iteratorBinding=iterator, codeFacts=fields, poolOwnership=pool_ownership(im), nativeObserved=False,
                transitiveMutationClosureProved=False,
                limits=['anchors verify these statements against saved code, not a whole-program proof',
                        'constructor-installed iterator binding is scoped; mutation of arbitrary object vtables is not excluded',
                        'field serialization does not prove nonzero values are produced by normal gameplay',
                        'pool argument sources do not prove global index alias absence'])


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    im=Image();result=build(im);result['imageSha256']=hashlib.sha256(im.img).hexdigest()
    result['analyzerSha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('iterator sites',len(result['iteratorBinding']['callSites']),'target',hex(result['iteratorBinding']['target']),
          'code facts',len(result['codeFacts']),'native observed',result['nativeObserved'])


if __name__=='__main__':main()
