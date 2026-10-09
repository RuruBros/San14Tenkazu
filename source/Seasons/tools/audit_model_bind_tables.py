"""Saved image emulation and installed-file reads only. No live process access."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

import ktgl
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_PROT_READ, UC_PROT_WRITE, UC_PROT_EXEC
from unicorn.x86_const import UC_X86_REG_RSP, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RAX, UC_X86_REG_RIP


# This is an already saved image, not the encrypted on-disk executable Hash.
IMAGE_SHA='aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
DESCRIPTORS_RVA=0xC21F50
EXPECTED_NAMES={'AD260326':'KTGLShaderBindTableResourceHash',
                '8AB68B3F':'KTGLModelDataResourceHash',
                '3BBFD9A5':'KTGLPartsModelGroupBindTableResourceHash',
                '7F0DE9A3':'KTGLMaterialGroupBindTableResourceHash'}


def sha(data):return hashlib.sha256(data).hexdigest()


def descriptors(data,base,rva=DESCRIPTORS_RVA,count=44,expected_names=None):
    if sha(data)!=IMAGE_SHA:raise ValueError('Saved executable image differs from inspected version')
    if base&4095:raise ValueError('Image base is not aligned')
    stack=0x500000000;output=0x600000000;stop=0x700000000
    uc=Uc(UC_ARCH_X86,UC_MODE_64)
    uc.mem_map(base,(len(data)+4095)&~4095,UC_PROT_READ|UC_PROT_EXEC)
    uc.mem_write(base,data)
    uc.mem_map(stack,0x10000,UC_PROT_READ|UC_PROT_WRITE)
    uc.mem_map(output,0x1000,UC_PROT_READ|UC_PROT_WRITE)
    uc.mem_map(stop,0x1000,UC_PROT_READ|UC_PROT_EXEC)
    sp=stack+0xFF00;uc.mem_write(sp,struct.pack('<Q',stop))
    uc.reg_write(UC_X86_REG_RSP,sp);uc.reg_write(UC_X86_REG_RDX,output)
    uc.reg_write(UC_X86_REG_R8,count);uc.reg_write(UC_X86_REG_R9,0)
    # No patched calls/stubs. This descriptor enumerator has no external calls.
    uc.emu_start(base+rva,stop,count=10000)
    if uc.reg_read(UC_X86_REG_RAX)!=count or uc.reg_read(UC_X86_REG_RIP)!=stop:
        raise ValueError('Descriptor enumerator did not finish all fields')
    result=[]
    for index in range(count):
        flags,identity,name,aux=struct.unpack('<2I2Q',uc.mem_read(output+index*24,24))
        offset=name-base
        if not 0<=offset<len(data):raise ValueError('Descriptor name pointer is outside saved image')
        end=data.find(b'\0',offset,min(offset+256,len(data)))
        if end<0:raise ValueError('Descriptor name is not bounded')
        result.append(dict(index=index,property=f'{identity:08X}',name=data[offset:end].decode('ascii'),
                           rawFlags=f'{flags:08X}',nameRva=f'{offset:X}',auxRva=f'{aux-base:X}'))
    for identity,expected in (EXPECTED_NAMES if expected_names is None else expected_names).items():
        if len([item for item in result if item['property']==identity and item['name']==expected])!=1:
            raise ValueError('Inspected property name evidence differs')
    return result


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--directory',required=True,type=Path)
    parser.add_argument('--image',required=True,type=Path)
    parser.add_argument('--image-base',required=True,type=lambda value:int(value,0))
    parser.add_argument('--links',required=True,type=Path)
    parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args();raw=args.links.read_bytes();links=json.loads(raw)
    fields=descriptors(args.image.read_bytes(),args.image_base)
    by_id={item['property']:item for item in fields}
    tree_shapes=[]
    for tree in links['models']:
        props=tree['model']['props']
        if len(props)!=44 or [p['name'] for p in props]!=[f['property'] for f in fields]:
            raise ValueError('Model property order differs from saved-image descriptors: '+tree['name'])
        # Preserve flags without assigning semantics beyond the recorded names.
        tree_shapes.append(dict(name=tree['name'],modelId=tree['model']['id'],matchedDescriptors=len(props)))
    folder=args.directory/'0002';catalog=ktgl.index(folder,'FieldEditor4');resources={};bindings=[]
    for tree in links['models']:
        for prop in tree['model']['props']:
            if prop['name'] not in ('3BBFD9A5','7F0DE9A3'):continue
            if prop['kind']!=5 or prop['count']!=1:raise ValueError('Bind-table reference shape differs')
            identity=prop['rawUint32'][0]
            if identity not in resources:
                hits=[row for row in catalog if f'{row["ktid"]:08X}'==identity]
                if len(hits)!=1:raise ValueError('Bind-table archive reference is ambiguous or absent')
                entry=hits[0];payload=ktgl.read(folder,entry)
                if len(payload)%4:raise ValueError('Bind-table payload is not word-aligned')
                resources[identity]=dict(source=entry,payloadBytes=len(payload),sha256=sha(payload),
                    rawUint32=[f'{word:08X}' for word in struct.unpack(f'<{len(payload)//4}I',payload)],
                    parsedLayoutVerified=False)
            bindings.append(dict(modelId=tree['model']['id'],name=tree['name'],property=prop['name'],
                                 propertyName=by_id[prop['name']]['name'],resourceKtid=identity))
    group_hashes={int(name[1:],16) for name in links['meshGroupNames']}
    matches=[dict(resourceKtid=identity,rawWordIndex=index,word=word)
             for identity,item in resources.items() for index,word in enumerate(item['rawUint32'])
             if int(word,16) in group_hashes]
    result=dict(mode='Unicorn on pinned saved image; installed archive read only',gameProcessAccess=False,
        savedImagePath=str(args.image.resolve()),savedImageSha256=IMAGE_SHA,imageBase=f'{args.image_base:X}',
        descriptorFunctionRva=f'{DESCRIPTORS_RVA:X}',dokLinksSha256=sha(raw),descriptors=fields,
        treeDescriptorMatches=tree_shapes,bindings=bindings,resources=resources,
        exactGroupWordMatches=matches,gamePassIdentityVerified=False,shaderBytecodeIdentityVerified=False,
        runtimeCallbackVerified=False,productionReady=False)
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(descriptors=len(fields),treeModels=len(tree_shapes),bindResources=len(resources),
                         bindings=len(bindings),exactGroupWordMatches=len(matches),gameProcessAccess=False)))


if __name__=='__main__':main()
