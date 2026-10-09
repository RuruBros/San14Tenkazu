# Conservative affine receiver audit: keep node, vtable and entry-stack pointers distinct.
# python ai_attack_access.py
# Calls are listed as boundaries, not assumed pure; runtime +80 access evidence is ai_attack_cache_audit.py.
from collections import deque
from datetime import datetime, timezone
import json
from pathlib import Path
import capstone
from capstone.x86_const import X86_OP_REG, X86_OP_MEM, X86_OP_IMM
from image import Image

HERE=Path(__file__).resolve().parent
FUNCTIONS=(0x24790,0x5CDC0,0x2F2B0,0x4ABE0,0x48CE0,0x46B80,0x4BA50,0x44A80,0x42E70)
def reg(ins,r):
    n=ins.reg_name(r) or ''
    aliases={part:full for full,parts in {
        'rax':('eax','ax','al','ah'),'rbx':('ebx','bx','bl','bh'),
        'rcx':('ecx','cx','cl','ch'),'rdx':('edx','dx','dl','dh'),
        'rsi':('esi','si','sil'),'rdi':('edi','di','dil'),
        'rbp':('ebp','bp','bpl'),'rsp':('esp','sp','spl')}.items() for part in parts}
    if n in aliases:return aliases[n]
    return n[:-1] if n.startswith('r') and n[-1:] in ('d','w','b') else n

def audit(im,start,arguments=None,return_aliases=None):
    im.md.detail=True;instructions={i.address:i for i in im.flow(start)}
    # A differing incoming value widens to unknown; no speculative dereference follows it.
    initial={**({'rcx':('node',0)} if arguments is None else arguments),'rsp':('stack',0)}
    states={start:initial};todo=deque([start]);events={};boundaries={};stack_calls={};escapes={};incoming=set();returns={}
    def address(ins,op,state):
        if op.type!=X86_OP_MEM or op.mem.index:return None
        b=state.get(reg(ins,op.mem.base))
        return (b[0],b[1]+op.mem.disp) if b else None
    def read(ins,op,state):
        if op.size!=8:return None
        if op.type==X86_OP_REG:return state.get(reg(ins,op.reg))
        a=address(ins,op,state)
        if a and a[0]=='stack':return state.get('@'+str(a[1]))
        if a and a[0]=='node' and '%'+str(a[1]) in state:return state['%'+str(a[1])]
        if a and a[0]=='node' and a[1] in (0,0x68,0x98) and op.size==8:return ('vtable',a[1])
        return None
    while todo:
        at=todo.popleft();ins=instructions[at];state=dict(states[at]);ops=ins.operands;m=ins.mnemonic
        for j,op in enumerate(ops):
            a=address(ins,op,state)
            if a and a[0]=='stack' and a[1]>=0x28 and m!='lea' and op.access & capstone.CS_AC_READ:
                if any('#'+str(b) not in state for b in range(a[1],a[1]+op.size)):incoming.add((a[1],op.size))
            if op.type==X86_OP_MEM and op.mem.index and state.get(reg(ins,op.mem.base),('',))[0]=='node':
                escapes[(at,'indexedNodeAccess')]=dict(rva=at,kind='indexedNodeAccess',text=m+' '+ins.op_str)
            if a and a[0]=='node' and a[1]<0x84 and a[1]+op.size>0x80:
                kind='address' if m=='lea' else 'write' if j==0 and m.startswith('mov') else 'readOrModify'
                events[(at,kind)]=dict(rva=at,offset=a[1],size=op.size,kind=kind,text=m+' '+ins.op_str)
            if a and a[0]=='vtable' and a[1] in (0x80,0xE8,0x118):
                events[(at,'vtable')]=dict(rva=at,offset=a[1],kind='virtualSlot',text=m+' '+ins.op_str)
        if m in ('mov','movabs','movzx','movsx','movsxd','lea') and len(ops)==2:
            value=address(ins,ops[1],state) if m=='lea' else read(ins,ops[1],state)
            if ops[0].size!=8:value=None
            dst=reg(ins,ops[0].reg) if ops[0].type==X86_OP_REG else None
            a=address(ins,ops[0],state)
            if a and a[0]=='stack':
                # A narrow store invalidates a pointer spanning any overwritten byte.
                for key in list(state):
                    if key.startswith('@') and int(key[1:])<a[1]+ops[0].size and a[1]<int(key[1:])+8:state.pop(key)
                if ops[0].size==8:dst='@'+str(a[1])
                for b in range(max(0x28,a[1]),a[1]+ops[0].size):state['#'+str(b)]=True
            if a and a[0]=='node':
                for key in list(state):
                    if key.startswith('%') and int(key[1:])<a[1]+ops[0].size and a[1]<int(key[1:])+8:state.pop(key)
                if ops[0].size==8:dst='%'+str(a[1])
            if ops[0].type==X86_OP_MEM and value and value[0]=='node' and (not a or a[0]!='stack'):
                escapes[(at,'pointerStore')]=dict(rva=at,kind='pointerStore',value=list(value),destination=list(a) if a else None,text=m+' '+ins.op_str)
            if dst:
                if value:state[dst]=value
                else:state.pop(dst,None)
        elif m in ('add','sub') and len(ops)==2 and ops[0].type==X86_OP_REG:
            dst=reg(ins,ops[0].reg);value=state.get(dst)
            if value and ops[0].size==8 and ops[1].type==X86_OP_IMM:state[dst]=(value[0],value[1]+ops[1].imm*(1 if m=='add' else -1))
            else:state.pop(dst,None)
        elif m=='push':
            sp=state.get('rsp');value=read(ins,ops[0],state)
            if sp:
                state['rsp']=('stack',sp[1]-8)
                if value:state['@'+str(sp[1]-8)]=value
        elif m=='pop':
            sp=state.get('rsp');dst=reg(ins,ops[0].reg);value=state.get('@'+str(sp[1])) if sp else None
            if value:state[dst]=value
            else:state.pop(dst,None)
            if sp:state['rsp']=('stack',sp[1]+8)
        elif m=='call' or (m=='jmp' and (not ops or ops[0].type!=X86_OP_IMM or ops[0].imm not in instructions)):
            args={r:list(state[r]) for r in ('rcx','rdx','r8','r9') if r in state and state[r][0]=='node'}
            sp=state.get('rsp');stack_args={}
            if sp and sp[0]=='stack':
                for key,value in state.items():
                    if key.startswith('@') and value[0]=='node':
                        offset=int(key[1:])-sp[1]+(8 if m=='call' else 0)
                        if offset>=0x28:stack_args['@'+str(offset)]=list(value)
            call=dict(rva=at,target=ins.op_str,args=args,stackCandidates=stack_args,kind='call' if m=='call' else 'tail')
            signature=(at,tuple((k,tuple(v)) for k,v in sorted({**args,**stack_args}.items())))
            if args:boundaries[signature]=call
            if stack_args:stack_calls[signature]=call
            # Unknown callees may change a stored node-field pointer. Do not retain it across calls.
            for key in list(state):
                if key.startswith('%'):state.pop(key)
            for r in ('rax','rcx','rdx','r8','r9','r10','r11'):state.pop(r,None)
            returned=(return_aliases or {}).get(at)
            if returned:state['rax']=returned
            if m=='jmp':returns[at]=returned
        elif m=='ret':returns[at]=state.get('rax')
        else:
            try:
                for r in ins.regs_access()[1]:
                    key=reg(ins,r)
                    if key not in ('rflags','rsp'):state.pop(key,None)
            except capstone.CsError:pass
        next_=at+ins.size;targets=[]
        if m.startswith('j') and ops and ops[0].type==X86_OP_IMM:
            targets.append(ops[0].imm)
            if m!='jmp':targets.append(next_)
        elif m not in ('ret','int3','jmp'):targets.append(next_)
        for target in targets:
            if target not in instructions:continue
            if target not in states:states[target]=dict(state);todo.append(target)
            else:
                merged={k:v for k,v in states[target].items() if state.get(k)==v}
                if merged!=states[target]:states[target]=merged;todo.append(target)
    return dict(entry=start,arguments={k:list(v) for k,v in initial.items() if k!='rsp'},instructions=len(instructions),reachedInstructions=len(states),
                accesses=sorted(events.values(),key=lambda e:(e['rva'],e['kind'])),
                callBoundaries=sorted(boundaries.values(),key=lambda e:e['rva']),
                stackCallCandidates=sorted(stack_calls.values(),key=lambda e:e['rva']),
                incomingStackReads=[dict(offset=o,size=s) for o,s in sorted(incoming)],
                pointerEscapes=sorted(escapes.values(),key=lambda e:(e['rva'],e['kind'])),
                returnAliases=[dict(rva=at,value=list(value) if value else None) for at,value in sorted(returns.items())],
                flow=im.flowtext(start))

def main():
    im=Image();rows=[audit(im,r) for r in FUNCTIONS]
    result=dict(utc=datetime.now(timezone.utc).isoformat(),method='conservative CFG register/stack affine receiver tracking; differing aliases widened to unknown; calls retained as explicit unresolved boundaries; not a whole-program non-use proof',functions=rows)
    folder=HERE/'.cache'/'ai_attack_access';folder.mkdir(parents=True,exist_ok=True);path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json')
    path.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    for row in rows:print(hex(row['entry']),row['reachedInstructions'],'instructions',row['accesses'],len(row['callBoundaries']),'node argument call boundaries')
    print(path)
if __name__=='__main__':main()
