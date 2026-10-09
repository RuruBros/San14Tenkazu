"""Disassemble local DXBC and index instructions; never opens a GPU or game.

Instruction facts are archive evidence, not a render-pass or alpha dataflow
analysis. Preserve the exact assembly around discard and resource writes.
"""
from collections import Counter
import ctypes as c
import re


STAGES={'ps':0,'vs':1,'gs':2,'hs':3,'ds':4,'cs':5}
PROFILE=re.compile(r'^(ps|vs|gs|hs|ds|cs)_([45])_(\d+)$')
OPCODE=re.compile(r'^([a-zA-Z][a-zA-Z0-9_]*)\b')
CB_REFERENCE=re.compile(r'\bcb(\d+)\[([^\]]+)\](?:\.([xyzw]+))?')


def blob_bytes(reflector,blob):
    pointer=reflector.method(blob,3,(),c.c_void_p)(blob)
    size=reflector.method(blob,4,(),c.c_size_t)(blob)
    if not pointer or size>64*1024*1024:raise ValueError('Local blob shape rejected')
    return c.string_at(pointer,size)


def disassemble(reflector,code):
    function=reflector.dll.D3DDisassemble
    function.argtypes=(c.c_void_p,c.c_size_t,c.c_uint32,c.c_char_p,c.POINTER(c.c_void_p))
    function.restype=c.c_int32
    storage=c.create_string_buffer(code);blob=c.c_void_p()
    status=function(storage,len(code),0,None,c.byref(blob))
    try:
        if status<0 or not blob:raise ValueError('Local disassembly failed')
        return blob_bytes(reflector,blob).rstrip(b'\0').decode('ascii')
    finally:
        if blob:reflector.method(blob,2)(blob)


def resource_write(opcode):
    return opcode.startswith(('store_','atomic_','imm_atomic_'))


def instruction_facts(assembly):
    lines=assembly.splitlines();profile=None;instructions=[]
    for number,raw in enumerate(lines,1):
        line=raw.strip()
        if not line or line.startswith('//'):continue
        if (match:=PROFILE.fullmatch(line)):
            if profile is not None:raise ValueError('Multiple assembly profiles rejected')
            profile=dict(text=line,stage=STAGES[match[1]],major=int(match[2]),minor=int(match[3]))
            continue
        if profile is None:raise ValueError('Instruction precedes assembly profile')
        match=OPCODE.match(line)
        if not match:raise ValueError('Unrecognized local assembly line: '+line)
        instructions.append(dict(line=number,opcode=match[1],text=line))
    if profile is None:raise ValueError('Local assembly has no profile')
    operations=[r for r in instructions if not r['opcode'].startswith('dcl_')]
    counts=Counter(r['opcode'] for r in operations)
    critical=[];references=[]
    for index,record in enumerate(operations):
        if record['opcode'].startswith('discard') or resource_write(record['opcode']):
            critical.append(dict(**record,precedingInstructions=operations[max(0,index-8):index],
                                 followingInstructions=operations[index+1:index+3]))
        for match in CB_REFERENCE.finditer(record['text']):
            # Direct register operands only. Dynamic expressions remain literal;
            # no inferred value, array stride, or matrix transform is supplied.
            direct=match[2].isdigit();components=match[3] or 'xyzw'
            vector=int(match[2]) if direct else None
            references.append(dict(line=record['line'],slot=int(match[1]),expression=match[2],
                directIndex=direct,vector=vector,components=components,
                byteOffsets=sorted({16*vector+4*'xyzw'.index(k) for k in components}) if direct else None))
    return dict(profile=profile,instructionCount=len(operations),opcodeCounts=dict(sorted(counts.items())),
        declarations=[r for r in instructions if r['opcode'].startswith('dcl_')],
        discardInstructionCount=sum(n for op,n in counts.items() if op.startswith('discard')),
        resourceWriteInstructionCount=sum(n for op,n in counts.items() if resource_write(op)),
        criticalInstructions=critical,constantBufferReferences=references,
        constantBufferValuesVerified=False,alphaDataflowVerified=False,gamePassVerified=False)
