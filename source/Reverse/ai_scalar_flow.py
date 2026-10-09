# Conservative scalar/stack constants for saved local x64 CFGs. No execution.
# Unknown reads/call returns stay unknown. Only ordinary ABI callee-saved values survive calls.
from collections import deque
import capstone
from capstone.x86_const import X86_OP_IMM, X86_OP_MEM, X86_OP_REG
from ai_attack_access import reg

NONVOLATILE={'rbx','rbp','rsi','rdi','r12','r13','r14','r15','rsp'}


def flags_for(left,right,width,logical=False):
    mask=(1<<(width*8))-1;left&=mask;right&=mask
    result=(left&right) if logical else (left-right)&mask
    sign=1<<(width*8-1)
    return dict(z=result==0,s=bool(result&sign),c=False if logical else left<right,
                o=False if logical else bool((left^right)&(left^result)&sign),p=(result&255).bit_count()%2==0)


def branch(m,flags):
    if flags is None:return None
    z,s,c,o,p=(flags[k] for k in ('z','s','c','o','p'))
    return {'je':z,'jz':z,'jne':not z,'jnz':not z,'ja':not c and not z,'jbe':c or z,
            'jb':c,'jae':not c,'jg':not z and s==o,'jge':s==o,'jl':s!=o,'jle':z or s!=o,
            'js':s,'jns':not s,'jo':o,'jno':not o,'jp':p,'jnp':not p}.get(m)


def specialize(im,entry,arguments,stack_arguments=None):
    im.md.detail=True;code={i.address:i for i in im.flow(entry)}
    if entry not in code:raise ValueError('entry has no decoded CFG')
    initial={**arguments,'rsp':('stack',0)}
    # The anchored caller writes these integer arguments as dwords, not qwords.
    for offset,value in (stack_arguments or {}).items():initial['@'+str(offset)]=(value,4)
    states={entry:initial};pending=deque([entry]);iterations=0

    def address(ins,operand,state):
        if operand.type!=X86_OP_MEM or operand.mem.index:return None
        base=state.get(reg(ins,operand.mem.base))
        return ('stack',base[1]+operand.mem.disp) if isinstance(base,tuple) and base[0]=='stack' else None

    def value(ins,operand,state):
        if operand.type==X86_OP_IMM:return operand.imm&((1<<(operand.size*8))-1)
        if operand.type==X86_OP_REG:
            v=state.get(reg(ins,operand.reg))
            return v&((1<<(operand.size*8))-1) if isinstance(v,int) else v if operand.size==8 else None
        a=address(ins,operand,state)
        item=state.get('@'+str(a[1])) if a else None
        if item is None or item[1]<operand.size:return None
        v=item[0]
        return v&((1<<(operand.size*8))-1) if isinstance(v,int) else v if operand.size==8 else None

    def transfer(ins,incoming):
        state=dict(incoming);ops=ins.operands;m=ins.mnemonic;written=set()
        def store(operand,v):
            if operand.type==X86_OP_REG:
                key=reg(ins,operand.reg);written.add(key)
                if operand.size not in (4,8):state.pop(key,None);return
            else:
                a=address(ins,operand,state)
                if not a:
                    for other in list(state):
                        if other.startswith('@'):state.pop(other)
                    return
                key='@'+str(a[1]);written.add(key)
                for other in list(state):
                    if other.startswith('@') and int(other[1:])<a[1]+operand.size and a[1]<int(other[1:])+8:state.pop(other)
            if v is None:state.pop(key,None)
            else:
                v=v&((1<<(operand.size*8))-1) if isinstance(v,int) else v
                state[key]=(v,operand.size) if key.startswith('@') else v
        if m in ('mov','movabs','movzx','movsx','movsxd') and len(ops)==2:
            v=value(ins,ops[1],state)
            if isinstance(v,int) and m in ('movsx','movsxd'):
                bits=ops[1].size*8;v=(v&((1<<bits)-1))-(1<<bits) if v&(1<<(bits-1)) else v
            store(ops[0],v)
        elif m=='lea' and len(ops)==2:
            a=address(ins,ops[1],state);v=a
            mem=ops[1].mem
            if mem.base and not mem.index:
                base=state.get(reg(ins,mem.base))
                if isinstance(base,int):v=base+mem.disp
            store(ops[0],v)
        elif m in ('add','sub','xor','and','or') and len(ops)==2:
            a,b=value(ins,ops[0],state),value(ins,ops[1],state);v=None
            if m=='xor' and ops[0].type==ops[1].type==X86_OP_REG and reg(ins,ops[0].reg)==reg(ins,ops[1].reg):v=0
            elif isinstance(a,int) and isinstance(b,int):v={'add':lambda:a+b,'sub':lambda:a-b,'xor':lambda:a^b,'and':lambda:a&b,'or':lambda:a|b}[m]()
            elif isinstance(a,tuple) and isinstance(b,int) and m in ('add','sub'):v=(a[0],a[1]+b*(1 if m=='add' else -1))
            store(ops[0],v);state.pop('flags',None)
        elif m=='push':
            v=value(ins,ops[0],state);sp=state.get('rsp');written.add('rsp')
            if isinstance(sp,tuple):
                state['rsp']=('stack',sp[1]-8);key='@'+str(sp[1]-8)
                if v is None:state.pop(key,None)
                else:state[key]=(v,8)
        elif m=='pop':
            sp=state.get('rsp');written.add('rsp');item=state.get('@'+str(sp[1])) if isinstance(sp,tuple) else None
            v=item[0] if item and item[1]==8 else None
            store(ops[0],v)
            if isinstance(sp,tuple):state['rsp']=('stack',sp[1]+8)
        elif m=='call':
            # Opaque callees supply no memory alias proof, including stack arguments.
            state={k:v for k,v in state.items() if k in NONVOLATILE}
            return state
        elif m in ('cmp','test') and len(ops)==2:
            a,b=value(ins,ops[0],state),value(ins,ops[1],state)
            if isinstance(a,int) and isinstance(b,int):state['flags']=flags_for(a,b,ops[0].size,m=='test')
            else:state.pop('flags',None)
        else:
            # Unmodelled stores invalidate cached memory; unknown destinations may alias stack.
            if any(op.type==X86_OP_MEM and op.access&capstone.CS_AC_WRITE for op in ops) or m.startswith(('stos','movs')):
                state={k:v for k,v in state.items() if not k.startswith('@')}
        for register in ins.regs_access()[1]:
            name=reg(ins,register)
            if name not in written and name!='rflags':state.pop(name,None)
        if m not in ('cmp','test') and any(ins.reg_name(r) in ('rflags','eflags') for r in ins.regs_access()[1]):state.pop('flags',None)
        return state

    def successors(ins,state):
        m=ins.mnemonic;ops=ins.operands
        if m in ('ret','int3'):return []
        if m.startswith('j'):
            if not ops or ops[0].type!=X86_OP_IMM:raise ValueError('unresolved local jump')
            target=ops[0].imm
            if target not in code:raise ValueError('external local jump')
            if m=='jmp':return [target]
            taken=branch(m,state.get('flags'))
            if taken is None:return [target,ins.address+ins.size]
            return [target if taken else ins.address+ins.size]
        return [ins.address+ins.size]

    while pending:
        iterations+=1
        if iterations>100000:raise ValueError('scalar CFG did not converge')
        pc=pending.popleft();state=transfer(code[pc],states[pc])
        for target in successors(code[pc],state):
            if target not in code:raise ValueError('decoded local CFG is incomplete')
            if target not in states:states[target]=dict(state);pending.append(target)
            else:
                joined={k:v for k,v in states[target].items() if k in state and state[k]==v}
                if joined!=states[target]:states[target]=joined;pending.append(target)
    decisions=[]
    for pc,state in sorted(states.items()):
        ins=code[pc]
        if ins.mnemonic.startswith('j') and ins.mnemonic!='jmp':
            taken=branch(ins.mnemonic,state.get('flags'))
            if taken is not None:decisions.append(dict(rva=pc,text=ins.mnemonic+' '+ins.op_str,taken=taken,flags=state['flags']))
    reachable=set();todo=[entry]
    while todo:
        pc=todo.pop()
        if pc in reachable:continue
        reachable.add(pc);todo+=successors(code[pc],transfer(code[pc],states[pc]))
    calls=[dict(rva=pc,target=code[pc].operands[0].imm if code[pc].operands[0].type==X86_OP_IMM else None,
                text=code[pc].mnemonic+' '+code[pc].op_str) for pc in sorted(reachable) if code[pc].mnemonic=='call']
    return dict(entry=entry,arguments=arguments,stackArguments=stack_arguments or {},iterations=iterations,
                instructionCount=len(code),reachable=sorted(reachable),decisions=decisions,calls=calls,
                nativeObserved=False,scope='specified scalar inputs, local CFG and ordinary Windows x64 callee-saved ABI; opaque calls do not supply constant return values')
