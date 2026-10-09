"""Read-only KTGL access matching the editor's KtglArchive reader."""
import pathlib
import re
import struct
import zlib

def index(folder, name):
    b=(pathlib.Path(folder)/(name+'.rdb')).read_bytes()
    if b[:8]!=b'_DRK0000':raise ValueError('Unsupported archive header')
    p=struct.unpack_from('<I',b,8)[0];rows=[]
    while p+56<=len(b) and b[p:p+8]==b'IDRK0000':
        size,data_size=struct.unpack_from('<2Q',b,p+8)
        extra,ktid,kind=struct.unpack_from('<3I',b,p+32)
        links=struct.unpack_from('<I',b,p+52)[0]
        location=b[p+56+links*12+extra:p+56+links*12+extra+data_size].rstrip(b'\0').decode('ascii')
        match=re.fullmatch(r'([0-9A-Fa-f]+)@([0-9A-Fa-f]+)(?:#([0-9A-Fa-f]+)(?:&(.+))?)?',location)
        if match:
            offset,n,bin_index,sub=match.groups()
            suffix=(str(int(bin_index,16))+('_'+sub if sub else '')) if bin_index else ''
            rows.append(dict(type=kind,ktid=ktid,bin=name+'.rdb.bin'+suffix,offset=int(offset,16),size=int(n,16)))
        if size<56:raise ValueError('Invalid archive entry size')
        p=(p+size+3)&~3
    return rows

def read(folder, entry):
    with (pathlib.Path(folder)/entry['bin']).open('rb') as f:
        f.seek(entry['offset']);b=f.read(entry['size'])
    if b[:8]!=b'IDRK0000':raise ValueError('Unsupported resource header')
    n,file_size=struct.unpack_from('<2Q',b,16)
    extra=struct.unpack_from('<I',b,32)[0];flags=struct.unpack_from('<I',b,44)[0]
    links=struct.unpack_from('<I',b,52)[0];p=56+12*links+extra
    if not flags&0x100000:return b[p:p+n]
    end=min(p+n,len(b));parts=[];got=0
    while got<file_size and p+4<=end:
        count=struct.unpack_from('<I',b,p)[0];p+=4
        raw=zlib.decompress(b[p:p+count]);parts.append(raw);got+=len(raw);p+=count
    if got!=file_size:raise ValueError('Decompressed resource size differs')
    return b''.join(parts)

def textures(b):
    if b[:4]!=b'GT1G':raise ValueError('Unsupported G1T')
    header,n=struct.unpack_from('<2I',b,12)
    offsets=[header+struct.unpack_from('<I',b,header+i*4)[0] for i in range(n)]
    rows=[]
    for i,p in enumerate(offsets):
        extended=struct.unpack_from('<I',b,p+8)[0] if b[p+7]&0xf0 else 0
        if not 0<=extended<=256:extended=0
        w=1<<(b[p+2]&15);h=1<<(b[p+2]>>4)
        if extended>=20 and b[p+2]==0:w,h=struct.unpack_from('<2I',b,p+20)
        rows.append(dict(format=b[p+1],width=w,height=h,mips=max(1,b[p]>>4),
                         offset=p+8+extended,end=offsets[i+1] if i+1<n else len(b)))
    return rows
