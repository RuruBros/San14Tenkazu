"""Offline native save/load regression; pass an output JSON path. No live game access.

Requires the pinned, previously captured Reverse/.cache/image.bin and Unicorn.
Runs original field transport helpers and normalization blocks, not file I/O or a full load.
"""
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'source/Reverse'))
from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import *

im = Image()
digest = hashlib.sha256(im.img).hexdigest()
assert digest == 'aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
u = Uc(UC_ARCH_X86, UC_MODE_64)
u.mem_map(im.base, (len(im.img)+4095)&~4095)
u.mem_write(im.base, im.img)
stack, data = 0x20000000000, 0x21000000000
u.mem_map(stack, 0x10000)
u.mem_map(data, 0x100000)
person, stream, buffer, world = data, data+0x1000, data+0x2000, data+0x10000
stop = stack+0x100

def w64(p, v): u.mem_write(p, struct.pack('<Q',v))
def w32(p, v): u.mem_write(p, struct.pack('<I',v))
def reg(r,v): u.reg_write(r,v)
def call(rva, rcx=0, rdx=0, r8=0, r9=0):
    rsp=stack+0x8008
    w64(rsp,stop)
    for r,v in [(UC_X86_REG_RSP,rsp),(UC_X86_REG_RCX,rcx),(UC_X86_REG_RDX,rdx),(UC_X86_REG_R8,r8),(UC_X86_REG_R9,r9)]:reg(r,v)
    u.emu_start(im.base+rva,stop,count=100000)
    assert u.reg_read(UC_X86_REG_RIP)==stop, hex(u.reg_read(UC_X86_REG_RIP))
    return u.reg_read(UC_X86_REG_RAX)

results=[]
patches=json.loads((ROOT/'source/Core/RuntimeRules/patches.json').read_text())

def mode(name):
    for p in patches:
        key='Traits' if p['Traits']!=p['Before'] and name!='original' else {'original':'Before','traits9-cap200':'Cap','traits9-growth200':'Growth'}[name]
        u.mem_write(im.base+p['Rva'],bytes.fromhex(p[key]))
    u.ctl_remove_cache(im.base,im.base+len(im.img))
def transport(rva, raw):
    u.mem_write(stream,bytes(0x100))
    u.mem_write(person,raw)
    w64(stream+0x30,buffer); w64(stream+0x38,buffer+0x1000)
    call(rva,stream,person)
    written=bytes(u.mem_read(buffer,len(raw)))
    assert written==raw
    u.mem_write(person,bytes(len(raw)))
    w32(stream+0x20,1); w64(stream+0x30,buffer)
    call(rva,stream,person)
    actual=bytes(u.mem_read(person,len(raw)))
    assert actual==raw
    assert struct.unpack('<I',u.mem_read(stream+0x50,4))[0]==0
    return actual

for values in ([1,99,100,101,200],[127,128,150,199,255]):
    actual=transport(0x202ef0,bytes(values))
    results.append(dict(case='ability-native-serialization-roundtrip',before=values,after=list(actual)))
traits=[1,2,3,4,5,255,256,300,355]
actual=transport(0x203250,struct.pack('<9H',*traits))
results.append(dict(case='nine-traits-native-serialization-roundtrip',before=traits,after=list(struct.unpack('<9H',actual))))

# Test the serializer's actual five-int experience block in both directions.
experience=[0,1660500,3712500,4525000,2147483647]
raw=struct.pack('<5i',*experience)
u.mem_write(stream,bytes(0x100))
u.mem_write(person+0x104,raw)
w64(stream+0x30,buffer);w64(stream+0x38,buffer+0x1000)
for direction in (0,1):
    w32(stream+0x20,direction);w64(stream+0x30,buffer)
    if direction==1:u.mem_write(person+0x104,bytes(20))
    rsp=stack+0x8000;w64(rsp+0x30,5)
    for r,v in [(UC_X86_REG_RSP,rsp),(UC_X86_REG_R12,person),(UC_X86_REG_RBX,stream),(UC_X86_REG_R15,0)]:reg(r,v)
    u.emu_start(im.base+0x23a51b,im.base+0x23a56f,count=10000)
    assert u.reg_read(UC_X86_REG_RIP)==im.base+0x23a56f
    assert bytes(u.mem_read(buffer if direction==0 else person+0x104,20))==raw
results.append(dict(case='experience-native-serialization-block-roundtrip',before=experience,after=list(struct.unpack('<5i',u.mem_read(person+0x104,20)))))

# Execute the original count check at entry to the sixth valid personality.
# Earlier lookup/validity/duplicate checks are deliberately outside this block test.
for name,count in [(name,count) for name in ('original','traits9-cap200','traits9-growth200') for count in (5,6,9)]:
    mode(name)
    u.mem_write(person,bytes(0x1f0))
    u.mem_write(person+0x118,struct.pack('<9H',*traits))
    u.mem_write(person+0x184,struct.pack('<H',0x1ff))
    for r,v in [(UC_X86_REG_R12,count),(UC_X86_REG_RSI,0),(UC_X86_REG_RDI,person),(UC_X86_REG_R14,person+0x118)]:reg(r,v)
    # First execute cmp/jg only. A count of five falls through; six/nine clear all slots.
    u.emu_start(im.base+0x22558f,im.base+0x225599,count=2)
    branch=u.reg_read(UC_X86_REG_RIP)-im.base
    if branch==0x225711:
        u.emu_start(im.base+branch,im.base+0x22574d,count=1000)
        assert u.reg_read(UC_X86_REG_RIP)==im.base+0x22574d
    actual=list(struct.unpack('<9H',u.mem_read(person+0x118,18)))
    assert actual==([0]*9 if name=='original' and count>5 else traits)
    results.append(dict(case='native-trait-count-normalization-block',mode=name,valid_count=count,before=traits,after=actual))

# Execute the original ability clamp loop, including the real native cap decoder.
w64(im.base+0x277df18,world)
w64(world+0x73a50,person-0x1f0)
w64(person,im.base+0x13a6b88)
for name in ('original','traits9-cap200','traits9-growth200'):
    mode(name)
    u.mem_write(person+0x10,struct.pack('<H',1))
    for r,v in [(UC_X86_REG_RDI,person),(UC_X86_REG_RCX,person),(UC_X86_REG_RSP,stack+0x8000)]:reg(r,v)
    u.emu_start(im.base+0x2253a7,im.base+0x22545e,count=10000)
    assert u.reg_read(UC_X86_REG_RIP)==im.base+0x22545e
    encoded=u.mem_read(person+0x1ed,1)[0]
    values=[99,100,101,150,200]
    u.mem_write(person+0xec,bytes(values))
    u.mem_write(person+0x104,raw)
    for r,v in [(UC_X86_REG_RDI,person),(UC_X86_REG_RSP,stack+0x8000),(UC_X86_REG_RSI,0)]:reg(r,v)
    u.emu_start(im.base+0x2258a7,im.base+0x225947,count=10000)
    assert u.reg_read(UC_X86_REG_RIP)==im.base+0x225947
    actual=list(u.mem_read(person+0xec,5))
    assert actual==([99,100,100,100,100] if name=='original' else values)
    assert bytes(u.mem_read(person+0x104,20))==raw
    results.append(dict(case='native-ability-normalization-loop',mode=name,before=values,after=actual,encoded_cap=encoded,cap_source='original native normalization prelude'))

covered=[p['Rva'] for p in patches if 0x225380<=p['Rva']<0x225f30]
assert sorted(covered)==[0x22558f,0x22591c]
report=dict(image_sha256=digest,live_game_access=False,product_changes=True,
    scope='Native field serializer helpers and normalization blocks, not the complete save file loader',
    normalization_patch_sites=covered,results=results)
Path(sys.argv[1]).write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
