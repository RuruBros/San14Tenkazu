"""Recognize two bounded discard patterns in saved PS assembly, offline only.

This is a syntactic resource/parameter dependency receipt. It does not identify
a game pass, current bindings/values, texture addressing, or runtime selection.
Unknown writers, control flow, modifiers and dynamic operands are rejected.
"""
import re
from season_shader_instructions import instruction_facts

SAMPLE = 'sample_indexable(texture2d)(float,float,float,float)'
SCALAR = re.compile(r'r\d+\.[xyzw]')
DESTINATION = re.compile(r'([ro]\d+)\.([xyzw]{1,4})')
CB = re.compile(r'cb(\d+)\[(\d+)\]\.([xyzw])')
INPUT = re.compile(r'v(\d+)\.([xyzw]{4})')
TEXTURE = re.compile(r't(\d+)\.([xyzw]{4})')
CONTROL = {'if_nz','if_z','else','endif','loop','endloop','switch','endswitch',
           'case','default','break','breakc_nz','breakc_z','continue','continuec_nz',
           'continuec_z','call','callc_nz','callc_z','label','ret','retc_nz','retc_z'}
SINGLE_WRITERS={'mov':2,'add':3,'lt':3,'mad_sat':4,SAMPLE:4}


def operands(text):
    """Split at top-level commas, preserving literal and indexed expressions."""
    result=[];start=0;stack=[];pairs={')':'(',']':'['}
    for at,ch in enumerate(text):
        if ch in '([':stack.append(ch)
        elif ch in ')]':
            if not stack or stack.pop()!=pairs[ch]:raise ValueError('Unbalanced operand')
        elif ch==',' and not stack:result.append(text[start:at].strip());start=at+1
    if stack:raise ValueError('Unbalanced operand')
    result.append(text[start:].strip())
    if any(not x for x in result):raise ValueError('Empty operand')
    return result


def scalar(value):
    if not SCALAR.fullmatch(value):raise ValueError('Only direct scalar temporaries are supported')
    return value


def cb(value):
    match=CB.fullmatch(value)
    if not match:raise ValueError('Only direct scalar constant-buffer operands are supported')
    slot,vector,component=int(match[1]),int(match[2]),match[3]
    if slot>=14 or vector>=4096:raise ValueError('Constant-buffer operand bounds rejected')
    return dict(operand=value,slot=slot,vector=vector,component=component,
                byteOffset=16*vector+4*'xyzw'.index(component))


def operation(record):
    fields=record['text'].split(None,1)
    return dict(**record,token=fields[0],operands=operands(fields[1]) if len(fields)>1 else [])


def require(row,token,count):
    if row['token']!=token or len(row['operands'])!=count:
        raise ValueError('Unsupported cutout instruction at line '+str(row['line']))
    return row['operands']


def writer(rows,before,value):
    register,component=scalar(value).split('.')
    for at in range(before-1,-1,-1):
        row=rows[at]
        if row['token'] in CONTROL or row['opcode'].startswith(('discard','store_','atomic_','imm_atomic_')):
            raise ValueError('Control flow or side effect crosses a dependency')
        if row['token'] not in SINGLE_WRITERS or len(row['operands'])!=SINGLE_WRITERS[row['token']]:
            raise ValueError('Unknown or multi-destination writer crosses a dependency')
        if not row['operands']:raise ValueError('Unknown writer shape')
        dest=DESTINATION.fullmatch(row['operands'][0])
        if not dest:raise ValueError('Unsupported intervening destination')
        if dest[1]==register and component in dest[2]:return at
    raise ValueError('Cutout temporary has no supported definition')


def outside_branch(rows,before):
    stack=[]
    for row in rows[:before]:
        token=row['token']
        if token in {'if_nz','if_z'}:stack.append(False)
        elif token=='else':
            if not stack or stack[-1]:raise ValueError('Invalid conditional structure')
            stack[-1]=True
        elif token=='endif':
            if not stack:raise ValueError('Invalid conditional structure')
            stack.pop()
        elif token in CONTROL:raise ValueError('Unsupported prefix control flow')
    if stack:raise ValueError('Cutout is inside an unverified conditional')


def sampled(row,value):
    dest,coordinate,texture,sampler=require(row,SAMPLE,4)
    mask=DESTINATION.fullmatch(dest);uv=INPUT.fullmatch(coordinate);tex=TEXTURE.fullmatch(texture)
    reg,component=scalar(value).split('.')
    if not mask or mask[1]!=reg or component not in mask[2] or len(set(mask[2]))!=len(mask[2]):
        raise ValueError('Sample destination does not define the consumed component')
    if not uv or not tex or not re.fullmatch(r's\d+',sampler):
        raise ValueError('Sample resource/input is not a direct supported operand')
    return dict(line=row['line'],textureSlot=int(tex[1]),textureOperand=texture,
        sampledComponent=tex[2]['xyzw'.index(component)],samplerSlot=int(sampler[1:]),
        inputRegister=int(uv[1]),coordinateOperand=coordinate,coordinateComponents=uv[2][:2])


def recognize_cutouts(assembly):
    if len(assembly)>4*1024*1024:raise ValueError('Assembly size rejected')
    facts=instruction_facts(assembly)
    if facts['profile']['stage']!=0:raise ValueError('Cutout inspection requires a pixel shader')
    # instruction_facts intentionally keeps critical windows, not all operations.
    rows=[]
    for number,line in enumerate(assembly.splitlines(),1):
        text=line.strip()
        if not text or text.startswith('//') or text==facts['profile']['text'] or text.startswith('dcl_'):continue
        rows.append(operation(dict(line=number,opcode=text.split(None,1)[0].split('(',1)[0],text=text)))
    if len(rows)>65536:raise ValueError('Instruction count rejected')
    results=[]
    for at,row in enumerate(rows):
        if not row['opcode'].startswith('discard'):continue
        predicate,=require(row,'discard',1);scalar(predicate)
        comparison=writer(rows,at,predicate)
        dest,value,zero=require(rows[comparison],'lt',3)
        if dest!=predicate or zero not in {'l(0.000000)','l(0)'}:
            raise ValueError('Only scalar value < zero discard predicates are supported')
        subtract=writer(rows,comparison,value)
        target,alpha,threshold=require(rows[subtract],'add',3)
        if target!=value or not threshold.startswith('-'):
            raise ValueError('Cutout subtraction is not a direct scalar threshold')
        scalar(alpha);threshold=cb(threshold[1:]);outside_branch(rows,subtract)
        adjustments=None
        if subtract>0 and rows[subtract-1]['token']=='endif':
            # Exact conditional block: gate > 0 ? saturate(sample*scale+bias) : 1.
            start=subtract-7
            if start<0:raise ValueError('Incomplete gated cutout block')
            guard,branch,sample,adjust,otherwise,fallback,end=rows[start:subtract]
            outside_branch(rows,start)
            guard_dest,guard_zero,enable=require(guard,'lt',3)
            scalar(guard_dest)
            if guard_zero not in {'l(0.000000)','l(0)'}:raise ValueError('Cutout gate comparison is unsupported')
            if require(branch,'if_nz',1)!=[guard_dest]:raise ValueError('Cutout gate predicate differs')
            if require(adjust,'mad_sat',4)[:2]!=[alpha,alpha]:raise ValueError('Adjusted alpha does not use the sample')
            require(otherwise,'else',0);require(end,'endif',0)
            if require(fallback,'mov',2) not in ([alpha,'l(1.000000)'],[alpha,'l(1)']):
                raise ValueError('Gated cutout fallback is unsupported')
            adjustments=dict(enable=cb(enable),scale=cb(adjust['operands'][2]),bias=cb(adjust['operands'][3]),
                             fallbackLiteral=fallback['operands'][1],gateLine=guard['line'])
            sample_info=sampled(sample,alpha);dependency_rows=rows[start:subtract]
            kind='gatedAdjustedSample'
        else:
            sample_index=writer(rows,subtract,alpha);outside_branch(rows,sample_index)
            sample_info=sampled(rows[sample_index],alpha);dependency_rows=[rows[sample_index]]
            kind='directSample'
        results.append(dict(pattern=kind,sample=sample_info,threshold=threshold,adjustments=adjustments,
            predicate=predicate,discardLine=row['line'],instructions=dependency_rows+[rows[subtract],rows[comparison],row],
            syntacticPatternVerified=True,actualBindingValuesVerified=False,gamePassVerified=False,
            actualAlphaCoverageVerified=False))
    return results


def bind_declarations(pattern,layout):
    """Match exact reflected slots/bytes, without filling in current values."""
    metadata=layout['metadata'];resources=metadata['resources'];sample=pattern['sample']
    def resource(kind,slot):
        found=[r for r in resources if r['kind']==kind and r['slot']==slot and r['count']==1]
        if len(found)!=1:raise ValueError('Cutout binding declaration is absent or ambiguous')
        return found[0]
    texture=resource(2,sample['textureSlot']);sampler=resource(3,sample['samplerSlot'])
    if texture['dimension']!=4 or texture['result']!=5:raise ValueError('Cutout texture declaration is unsupported')
    inputs=[v for v in metadata['inputs'] if v['register']==sample['inputRegister'] and v['stream']==0]
    if len(inputs)!=1 or any(not inputs[0]['mask']&(1<<'xyzw'.index(c)) for c in sample['coordinateComponents']):
        raise ValueError('Cutout coordinates exceed the input signature')
    def constant(reference):
        buffers=[b for b in layout['constantBuffers'] if b['binding'] is not None and b['binding']['slot']==reference['slot']]
        if len(buffers)!=1:raise ValueError('Cutout constant-buffer declaration is ambiguous')
        buffer=buffers[0];offset=reference['byteOffset']
        if offset+4>buffer['size']:raise ValueError('Cutout constant exceeds buffer bounds')
        variables=[v for v in buffer['variables'] if v['offset']<=offset and offset+4<=v['offset']+v['size']]
        if len(variables)!=1:raise ValueError('Cutout constant does not have a unique variable declaration')
        return dict(**reference,bufferName=buffer['name'],variableName=variables[0]['name'],
                    variableOffset=variables[0]['offset'],variableSize=variables[0]['size'],currentValueVerified=False)
    return dict(texture=texture,sampler=sampler,inputSignature=inputs[0],threshold=constant(pattern['threshold']),
        adjustments={role:constant(value) for role,value in (pattern['adjustments'] or {}).items()
                     if role in {'enable','scale','bias'}},gameResourceIdentityVerified=False)
