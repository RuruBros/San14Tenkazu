# Freeze complete pages of the native object pool, and optionally the integer pool.
# This changes no game memory and does not replace native allocation or list operations.
import hashlib
import struct
from game import Game


class ListPoolSnapshot(Game):
    def __init__(self, integer_pool=False):
        super().__init__()
        self._pool_ranges=[]
        self.pool_snapshot=self._capture(0x27D10E0)
        if integer_pool:self.pool_snapshot['integerPool']=self._capture(0x1B5C990)

    def _capture(self, base_rva):
        header_address=(self.exe+base_rva)&~0xFFF
        offset=self.exe+base_rva-header_address
        for attempt in range(30):
            before=super().read(header_address,0x1000)
            q=lambda field:struct.unpack_from('<Q',before,offset+field)[0]
            start=min(q(8),q(0x50))&~0xFFF
            end=max(q(8)+q(0x38)*24,q(0x50)+q(0x80)*24,
                    *(q(field)+q(0x40)*8 for field in [0x10,0x18,0x28]))
            end=(end+0xFFF)&~0xFFF
            if not 0<end-start<32*1024*1024:raise AssertionError('unexpected native pool dimensions')
            data=super().read(start,end-start)
            after=super().read(header_address,0x1000)
            if data is None or before[offset:offset+0x88]!=after[offset:offset+0x88]:continue
            heads=struct.unpack_from('<'+'Q'*q(0x40),data,q(0x10)-start)
            tails=struct.unpack_from('<'+'Q'*q(0x40),data,q(0x18)-start)
            counts=struct.unpack_from('<'+'Q'*q(0x40),data,q(0x28)-start)
            node_start=q(8);node_end=node_start+q(0x38)*24
            readq=lambda address:struct.unpack_from('<Q',data,address-start)[0]
            active=set();valid=True
            for head,tail,count in zip(heads,tails,counts):
                node=head;previous=0;actual=0
                while node:
                    if not node_start<=node<node_end or (node-node_start)%24 or node in active or readq(node+16)!=previous:
                        valid=False;break
                    active.add(node);actual+=1;previous=node;node=readq(node+8)
                if not valid or previous!=tail or actual!=count:valid=False;break
            if not valid or len(active)!=q(0x30):continue
            free=set();node=q(0x20)
            while node:
                if not node_start<=node<node_end or (node-node_start)%24 or node in active or node in free:
                    valid=False;break
                free.add(node);node=readq(node+8)
            if not valid or len(active)+len(free)!=q(0x38):continue
            index_start=q(0x50);index_end=index_start+q(0x80)*24
            index_active=set();indices=set();node=q(0x58);previous=0
            while node:
                if not index_start<=node<index_end or (node-index_start)%24 or node in index_active or readq(node+16)!=previous:
                    valid=False;break
                index=struct.unpack_from('<I',data,node-start)[0]
                if index>=len(heads) or index in indices:valid=False;break
                index_active.add(node);indices.add(index);previous=node;node=readq(node+8)
            if not valid or previous!=q(0x60) or len(index_active)!=q(0x78):continue
            if any(head and i not in indices for i,head in enumerate(heads)):continue
            index_free=set();node=q(0x68)
            while node:
                if not index_start<=node<index_end or (node-index_start)%24 or node in index_active or node in index_free:
                    valid=False;break
                index_free.add(node);node=readq(node+8)
            if not valid or len(index_active)+len(index_free)!=q(0x80):continue
            self._pool_ranges.extend([(header_address,before),(start,data)])
            return {'attempts':attempt+1,'start':start,'length':len(data),
                    'sha256':hashlib.sha256(data).hexdigest(),'validatedLists':len(heads),
                    'validatedActiveNodes':len(active),'validatedFreeNodes':len(free),
                    'validatedIndices':len(index_active),'validatedFreeIndices':len(index_free)}
        raise AssertionError('native list pool did not yield a consistent read in 30 attempts')

    def read(self,address,length):
        for start,data in getattr(self,'_pool_ranges',[]):
            if start<=address and address+length<=start+len(data):return data[address-start:address-start+length]
            if address<start+len(data) and start<address+length:
                raise AssertionError('read crosses a frozen native pool boundary')
        return super().read(address,length)
