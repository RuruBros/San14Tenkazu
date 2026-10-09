# Validate an existing frozen game before offline path execution. Never opens Game.
# Missing mutable data is never filled. Optional text fill uses the saved original PE
# with its declared DIR64 relocations, after comparing every captured text byte.
import argparse
import hashlib
import json
from pathlib import Path
import struct

from game import FrozenGame
from image import Image

POOL = 0x27D10E0
PAGE = 4096


def validate_pool(reader, address):
    header = reader(address, 0x88)
    if header is None:return dict(valid=False,complete=False,reason='missing pool header')
    q = lambda field:struct.unpack_from('<Q',header,field)[0]
    start=min(q(8),q(0x50)) & -PAGE
    end=max(q(8)+q(0x38)*24,q(0x50)+q(0x80)*24,*(q(f)+q(0x40)*8 for f in (0x10,0x18,0x28)))
    end=(end+PAGE-1)&-PAGE
    result=dict(valid=False,complete=False,start=start,length=end-start,capacity=q(0x40),
                nodeCapacity=q(0x38),indexCapacity=q(0x80))
    if not start or not 0 < end-start < 32*1024*1024 or not 0 < q(0x40) <= 0x14000:
        return dict(result,reason='unexpected pool dimensions')
    missing=[p for p in range(start,end,PAGE) if reader(p,PAGE) is None]
    if missing:return dict(result,reason='missing pool pages',missingPages=missing)
    data=reader(start,end-start)
    if data is None:return dict(result,reason='pool range unreadable despite page checks')
    result.update(complete=True,sha256=hashlib.sha256(data).hexdigest())
    def readq(at):
        if not start <= at <= end-8:raise ValueError('pool pointer outside captured range')
        return struct.unpack_from('<Q',data,at-start)[0]
    def array(field):
        at=q(field); n=q(0x40)*8
        if not start <= at <= end-n:raise ValueError('pool array outside captured range')
        return struct.unpack_from('<'+'Q'*q(0x40),data,at-start)
    def walk(node,lower,count,occupied,previous_links):
        upper=lower+count*24; seen=set(); previous=0
        while node:
            if not lower <= node < upper or (node-lower)%24 or node in seen or node in occupied:
                raise ValueError('cycle, shared node or invalid node address')
            if previous_links and readq(node+16)!=previous:raise ValueError('broken previous link')
            seen.add(node);previous=node;node=readq(node+8)
        return seen,previous
    try:
        heads,tails,counts=[array(f) for f in (0x10,0x18,0x28)]
        active=set()
        for head,tail,count in zip(heads,tails,counts):
            nodes,last=walk(head,q(8),q(0x38),active,True)
            if last!=tail or len(nodes)!=count:raise ValueError('list tail or count differs')
            active.update(nodes)
        if len(active)!=q(0x30):raise ValueError('active node total differs')
        free,_=walk(q(0x20),q(8),q(0x38),active,False)
        if len(active)+len(free)!=q(0x38):raise ValueError('node allocation coverage differs')
        index_active,last=walk(q(0x58),q(0x50),q(0x80),set(),True)
        if last!=q(0x60) or len(index_active)!=q(0x78):raise ValueError('index tail or active count differs')
        indices=[readq(node)&0xFFFFFFFF for node in index_active]
        if len(set(indices))!=len(indices) or any(i>=q(0x40) for i in indices):raise ValueError('duplicate or out-of-range index')
        if any(head and i not in indices for i,head in enumerate(heads)):raise ValueError('list without allocated index')
        index_free,_=walk(q(0x68),q(0x50),q(0x80),index_active,False)
        if len(index_active)+len(index_free)!=q(0x80):raise ValueError('index allocation coverage differs')
        result.update(valid=True,activeNodes=len(active),freeNodes=len(free),activeIndices=len(index_active),freeIndices=len(index_free))
    except (ValueError,struct.error) as error:result['reason']=str(error)
    return result


def relocated_text(im, base):
    begin,size=im.sections['.text'];end=begin+size;text=bytearray(im.img[begin:end]);relocations=[]
    pe=struct.unpack_from('<I',im.img,0x3C)[0]
    rva,length=struct.unpack_from('<II',im.img,pe+24+112+5*8)
    at=rva
    while at < rva+length:
        page,block=struct.unpack_from('<II',im.img,at)
        if block < 8 or block%2 or at+block > rva+length:raise ValueError('invalid PE relocation block')
        for off in range(at+8,at+block,2):
            item=struct.unpack_from('<H',im.img,off)[0];kind=item>>12;target=page+(item&0xFFF)
            if kind==0 or not begin <= target < end:continue
            if kind!=10 or target+8>end:raise ValueError('unsupported text relocation')
            old=struct.unpack_from('<Q',text,target-begin)[0]
            new=(old+base-im.base)&0xFFFFFFFFFFFFFFFF
            struct.pack_into('<Q',text,target-begin,new)
            relocations.append(dict(rva=target,before=old,after=new))
        at+=block
    return begin,bytes(text),relocations


class FrozenImage(FrozenGame):
    def __init__(self, path, im, fill_text=False):
        self.snapshot_sha256=hashlib.sha256(path.read_bytes()).hexdigest()
        super().__init__(path)
        if hashlib.sha256(path.read_bytes()).hexdigest()!=self.snapshot_sha256:
            raise ValueError('frozen snapshot file changed while loading')
        if self.exe_size!=len(im.img):raise ValueError('saved image size differs from frozen executable')
        begin,text,relocations=relocated_text(im,self.exe);end=begin+len(text);compared=0
        for page,data in self.pages.items():
            lo=max(page-self.exe,begin);hi=min(page-self.exe+len(data),end)
            if lo>=hi:continue
            if data[lo-(page-self.exe):hi-(page-self.exe)]!=text[lo-begin:hi-begin]:
                raise ValueError(f'captured text differs at page RVA {page-self.exe:X}')
            compared+=hi-lo
        if not compared:raise ValueError('no captured text bytes to compare')
        added=[]
        if fill_text:
            # Only complete text pages: never globals, vtables, IAT, heap or saved inputs.
            for rva in range((begin+PAGE-1)&-PAGE,end-PAGE+1,PAGE):
                page=self.exe+rva
                if page not in self.pages:
                    self.pages[page]=text[rva-begin:rva-begin+PAGE];added.append(rva)
        self.code_source=dict(imageSha256=hashlib.sha256(im.img).hexdigest(),imageBase=im.base,
                              frozenBase=self.exe,comparedTextBytes=compared,textRelocations=relocations,
                              addedTextPageRvas=added,mutablePagesAdded=0,
                              scope='all captured text bytes compared after declared PE relocation; only missing complete text pages copied from the saved original image; missing mutable data remains unreadable')
        self.pool_snapshot=validate_pool(self.read,self.exe+POOL)


def inspect(path,im,fill_text=False):
    record=dict(file=str(path),sha256=hashlib.sha256(path.read_bytes()).hexdigest(),nativeExecuted=False)
    try:
        g=FrozenImage(path,im,fill_text);w=g.q(g.exe+0x277DF18)
        record.update(pages=len(g.pages),codeSource=g.code_source,pool=g.pool_snapshot,world=w,
                      tilePointersReadable=bool(w and g.read(w+0xE270,48400*8)),
                      searchCodeReadable={hex(r):g.read(g.exe+r,16) is not None for r in (0x183A30,0x184EB0)},
                      fireCodeReadable={hex(r):g.read(g.exe+r,16) is not None for r in (0x2E39E0,0x2EDD40)})
    except (ValueError,struct.error) as error:record['error']=str(error)
    return record


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--snapshot',type=Path,action='append',required=True)
    parser.add_argument('--fill-text',action='store_true');parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    im=Image();rows=[inspect(p,im,args.fill_text) for p in args.snapshot]
    result=dict(snapshots=rows,analyzerSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                gameAccess=False,method='existing frozen files only; no native call or live process fallback')
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    for row in rows:
        print(Path(row['file']).name,row.get('error') or row['pool'])


if __name__=='__main__':main()
