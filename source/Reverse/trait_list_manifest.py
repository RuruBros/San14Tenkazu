"""Reproduce list metadata from the pinned saved image; no game/process access."""
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(__file__).resolve().parent / '.cache'
b = (CACHE / 'image.bin').read_bytes()
assert hashlib.sha256(b).hexdigest() == 'aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
base = int((CACHE / 'image.meta').read_text(),16)
q = lambda at: struct.unpack_from('<Q',b,at)[0]
pe=struct.unpack_from('<I',b,0x3c)[0]; opt=pe+24
start,size=struct.unpack_from('<II',b,opt+112+5*8); fix=[]; at=start
while at<start+size:
    page,n=struct.unpack_from('<II',b,at)
    if n<8: break
    for p in range(at+8,at+n,2):
        v=struct.unpack_from('<H',b,p)[0]
        if v>>12==10:fix.append(page+(v&4095))
    at+=n
# Native CRT initializer 0x2260 fills these absolute pointers using RIP-relative LEAs
# and the relocated pointer at 0x1A47B98. They are absent from the PE relocation table.
# trait_list_relocation_audit.py executes that initializer at independent module bases.
runtime_fix=[0x1a2b850+c*96+o for c in (22,69,99,100) for o in (0x18,0x28)]+[0x1a2b850+212*96+8]
assert not set(runtime_fix)&set(fix)
fix=sorted(fix+runtime_fix)
def block(r,n): return dict(Rva=r,Bytes=b[r:r+n].hex().upper(),Fixups=[p-r for p in fix if r<=p<r+n])
def table(r):
    if not 0x1a30000<=r<0x1a34000:return
    rows=[]
    for n in range(16):
        p=q(r+n*24)
        if not p:return rows
        if not 0x1a28000<=p-base<0x1a2b850:return
        ids=[]
        for k in range(60):
            v=struct.unpack_from('<i',b,p-base+k*4)[0];ids.append(v)
            if v==-1:break
            if not 0<=v<213:return
        if ids[-1]!=-1:return
        rows.append((p-base,ids))
tabs={}
for i in range(0x1000,0x133fa00):
    if b[i] not in (0x48,0x4c) or b[i+1]!=0x8d or b[i+2]&0xc7!=5:continue
    t=i+7+struct.unpack_from('<i',b,i+3)[0]; rows=table(t)
    if rows:
        for n,(ptr,ids) in enumerate(rows):
            for first in (61,170):
                if ids==[0,*range(first,first+5),-1]:tabs[t]=(rows,n,first)
tables=[]; vectors=set()
for r,(rows,n,first) in sorted(tabs.items()):
    entry=block(r,len(rows)*24+8);entry.update(TraitIndex=n,TraitBase=first);tables.append(entry);vectors.add(rows[n][0])
manifest=dict(OriginalBase=base,ImageSize=struct.unpack_from('<I',b,opt+56)[0],Columns=block(0x1a2b850,213*96),Tables=tables,
              Guards=[block(0x2bf02e,36),block(0x2bf120,29),block(0x2260,0x291),*[block(v,28) for v in sorted(vectors)],
                      block(0x35eec0,78),block(0x353810,0x2d3),block(0x35f020,0xc7)])
path=ROOT/'source/Core/RuntimeRules/trait-list.json'
if '--write' in sys.argv:path.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf8')
else:assert json.loads(path.read_text())==manifest
print('Verified manifest:',len(tables),'tab layouts;',sum(t['TraitBase']==170 for t in tables),'alternate-name layouts')
