"""Bounded reader for the exported tree G1M 0x37 / G1MG 0x44 layout.

Keep unknown render-state/type fields raw. Name/layout correlation with DXBC
does not prove the game's material-to-constant-buffer upload or Pass choice.
"""
import hashlib
import math
import re
import struct


def sha(data):return hashlib.sha256(data).hexdigest()


def require(data,at,size,end=None):
    end=len(data) if end is None else end
    if not 0<=at<=end<=len(data) or not 0<=size<=end-at:
        raise ValueError('G1M material range exceeds container')


def read_material_sections(data):
    require(data,0,24)
    if data[:8]!=b'_M1G7300' or len(data)>64*1024*1024:
        raise ValueError('Unsupported inspected G1M version/size')
    length,first,raw_word,count=struct.unpack_from('<4I',data,8)
    if length!=len(data) or first!=24 or not 1<=count<=64:
        raise ValueError('G1M chunk header rejected')
    chunks=[];at=first;geometries=[]
    for index in range(count):
        require(data,at,12);size=struct.unpack_from('<I',data,at+8)[0]
        if size<12:raise ValueError('G1M chunk has no complete header')
        require(data,at,size)
        chunks.append(dict(index=index,offset=at,size=size,tag=data[at:at+8].decode('ascii'),sha256=sha(data[at:at+size])))
        if data[at:at+4]==b'GM1G':geometries.append((at,size))
        at+=size
    if at!=len(data) or len(geometries)!=1:raise ValueError('G1M chunk coverage/geometry count differs')
    geometry,size=geometries[0];end=geometry+size;require(data,geometry,48,end)
    if data[geometry:geometry+8]!=b'GM1G4400':raise ValueError('Unsupported inspected G1MG version')
    count=struct.unpack_from('<I',data,geometry+44)[0]
    if not 1<=count<=64:raise ValueError('G1MG section count rejected')
    at=geometry+48;sections=[];selected={}
    for index in range(count):
        require(data,at,12,end);kind,size,records=struct.unpack_from('<3I',data,at)
        if size<12 or records>65536:raise ValueError('G1MG section size/count rejected')
        require(data,at,size,end)
        section=dict(index=index,type=f'{kind:08X}',offset=at,size=size,records=records,sha256=sha(data[at:at+size]))
        sections.append(section)
        if kind in (0x10001,0x10002,0x10003):
            if kind in selected:raise ValueError('Duplicate inspected material section')
            selected[kind]=section
        at+=size
    if at!=end or len(selected)!=3:raise ValueError('G1MG material section coverage differs')
    counts={selected[k]['records'] for k in selected}
    if len(counts)!=1 or not 1<=next(iter(counts))<=4096:
        raise ValueError('G1MG material/attribute/state record counts differ')
    total=next(iter(counts));bindings=[];section=selected[0x10002]
    at=section['offset']+12;end=section['offset']+section['size']
    for material in range(total):
        require(data,at,16,end);words=struct.unpack_from('<4I',data,at);n=words[1];at+=16
        if n>256:raise ValueError('G1MG texture binding count rejected')
        require(data,at,n*12,end)
        records=[list(struct.unpack_from('<6H',data,at+12*i)) for i in range(n)];at+=n*12
        bindings.append(dict(index=material,rawHeader=list(words),rawTextureBindings=records))
    if at!=end:raise ValueError('G1MG material bindings do not cover section')
    section=selected[0x10001];at=section['offset']+12;end=section['offset']+section['size']
    # Keep the entire bounded section body. Its per-record layout is not yet
    # decoded; count does not imply a stride or an alpha-state field offset.
    if (end-at)%4:raise ValueError('G1MG raw state section is not word-aligned')
    states=dict(offset=at,bytes=end-at,declaredRecords=total,rawHex=data[at:end].hex(),
                rawWords=list(struct.unpack_from(f'<{(end-at)//4}I',data,at)),recordLayoutVerified=False)
    section=selected[0x10003];at=section['offset']+12;end=section['offset']+section['size'];attributes=[]
    budget=0
    for material in range(total):
        require(data,at,4,end);n=struct.unpack_from('<I',data,at)[0];at+=4
        if n>1024 or budget+n>65536:raise ValueError('G1MG named attribute count rejected')
        budget+=n;values=[];names=set()
        for index in range(n):
            require(data,at,16,end)
            length,name_length,reserved,raw_kind,raw_count=struct.unpack_from('<3I2H',data,at)
            if length<20 or length%4 or not 4<=name_length<=256 or name_length%4 or 16+name_length>=length:
                raise ValueError('G1MG named attribute shape rejected')
            require(data,at,length,end)
            name_bytes=data[at+16:at+16+name_length];name,separator,tail=name_bytes.partition(b'\0')
            if not separator or any(tail) or not re.fullmatch(rb'[A-Za-z_][A-Za-z0-9_]*',name):
                raise ValueError('G1MG attribute name is not bounded/padded ASCII')
            name=name.decode('ascii')
            if name in names:raise ValueError('Duplicate G1MG material attribute name')
            names.add(name);payload=data[at+16+name_length:at+length]
            if len(payload)>65536 or len(payload)%4:raise ValueError('G1MG attribute value shape rejected')
            values.append(dict(index=index,name=name,offset=at,size=length,nameBytes=name_length,
                rawReserved=reserved,rawType=raw_kind,rawCount=raw_count,valueBytes=len(payload),
                valueHex=payload.hex(),recordSha256=sha(data[at:at+length]),typeSemanticsVerified=False))
            at+=length
        attributes.append(dict(index=material,attributes=values))
    if at!=end:raise ValueError('G1MG named attributes do not cover section')
    return dict(chunks=chunks,geometryOffset=geometry,sections=sections,materials=bindings,
                rawStates=states,namedAttributes=attributes,rawHeaderWord=raw_word,
                runtimeMaterialUploadVerified=False,alphaThresholdVerified=False)


def correlate_attribute(attribute,variable):
    """Exact name/size and reflected scalar/vector type; no game upload claim."""
    if attribute['name']!=variable['name']:raise ValueError('Attribute/reflected name differs')
    kind=variable['type'];payload=bytes.fromhex(attribute['valueHex'])
    scalar=kind['variableClass']==0 and kind['rows']==1 and kind['columns']==1
    vector=kind['variableClass']==1 and kind['rows']==1 and 1<=kind['columns']<=4
    matched=len(payload)==variable['size']==kind['rows']*kind['columns']*4 and (scalar or vector) and not kind['elements'] and not kind['members']
    values=None
    if matched and kind['variableType'] in (2,3):
        values=list(struct.unpack(f'<{len(payload)//4}{"i" if kind["variableType"]==2 else "f"}',payload))
        if any(isinstance(v,float) and not math.isfinite(v) for v in values):
            raise ValueError('Matched material attribute has nonfinite float value')
    return dict(nameAndByteSizeMatched=matched,interpretationFromReflectedType=values,
                actualUploadVerified=False,actualGamePassVerified=False)
