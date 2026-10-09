"""Run a relocation-free documented D3D11 API probe once on the game's frame.

The temporary CApp::Update dispatch uses the already verified frame stub. Its
code pages remain allocated for a possibly late-dispatched frame, as in Core.
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import hashlib
import json
import pathlib
import struct
import time

from game import Game, k32, psapi
from graphics_probe import observe
from graphics_live_test import (VERIFIED_EXE_HASH, UPDATE, UPDATE_SLOT, UPDATE_START,
                                frame_stub)
from stub import run as verify_stub
from season_trace_contract import check_identity, validate_snapshot, restore_slots


def object_text(path, symbol):
    data = path.read_bytes()
    machine, n, _, symoff, symcount, opt, _ = struct.unpack_from('<HHIIIHH', data)
    if machine != 0x8664 or opt: raise RuntimeError('Expected x64 COFF object')
    strings = data[symoff+symcount*18:]
    sections = []
    for i in range(n):
        row = struct.unpack_from('<8sIIIIIIHHI', data, 20+i*40)
        name = row[0].rstrip(b'\0').decode()
        sections.append((name, data[row[4]:row[4]+row[3]], row[7]))
    i = 0
    while i < symcount:
        row = data[symoff+i*18:symoff+(i+1)*18]
        nm = row[:8]
        if nm[:4] == bytes(4):
            p = struct.unpack_from('<I',nm,4)[0]
            name = strings[p:].split(b'\0')[0].decode()
        else: name = nm.rstrip(b'\0').decode()
        value, section, typ, storage, aux = struct.unpack_from('<IhHBB',row,8)
        if name == symbol:
            sname, code, relocs = sections[section-1]
            if not sname.startswith('.text') or relocs:
                raise RuntimeError(f'Function has relocations or is not code: {sname}, {relocs}')
            return code, value
        i += 1+aux
    raise RuntimeError(f'Missing function: {symbol}')


def driver_path(g):
    manager = g.q(g.exe+0x1B9FB70)
    if not manager: raise RuntimeError('Scene manager is unreadable')
    scene = g.q(manager+0x210)
    if not scene: raise RuntimeError('Game scene is unavailable')
    parent = g.q(scene+0x50)
    if not parent: raise RuntimeError('Scene renderer is unavailable')
    driver = g.q(parent+0x18)
    if not driver: raise RuntimeError('D3D11 driver is unavailable')
    return dict(driver=driver, device=g.q(driver+0x3078), immediate=g.q(driver+0x10))


class Frame:
    def __init__(self, out, check_clock=True):
        self.g = Game()
        self.out = out
        self.out.mkdir(parents=True, exist_ok=True)
        self.h = k32.OpenProcess(0x0438,False,self.g.pid)
        if not self.h: raise ctypes.WinError(ctypes.get_last_error())
        path = ctypes.create_unicode_buffer(32768)
        if not psapi.GetModuleFileNameExW(ctypes.c_void_p(self.g.h),ctypes.c_void_p(self.g.exe),path,len(path)):
            raise ctypes.WinError(ctypes.get_last_error())
        if hashlib.sha256(pathlib.Path(path.value).read_bytes()).hexdigest()!=VERIFIED_EXE_HASH:
            raise RuntimeError('Unverified game executable')
        if self.g.read(self.g.exe+UPDATE,len(UPDATE_START))!=UPDATE_START:
            raise RuntimeError('Unverified CApp::Update code')
        self.baseline = self.state()
        validate_snapshot(self.baseline)
        time.sleep(1)
        self.assert_identity(check_clock)
        self.log('preflight',baseline=self.baseline,exe_hash=VERIFIED_EXE_HASH)

    def state(self):
        d=observe(self.g)
        d.update({k:hex(v or 0) for k,v in driver_path(self.g).items()})
        if observe(self.g) != {k:v for k,v in d.items() if k not in ('driver','device','immediate')}:
            raise RuntimeError('Game state changed during snapshot read')
        return d

    def assert_identity(self,check_clock=True):
        current=self.state()
        try: return check_identity(self.baseline,current,check_clock)
        except ValueError as error: raise RuntimeError(str(error)) from error

    def log(self,event,**data):
        record=dict(event=event,monotonic=time.monotonic(),**data)
        with (self.out/'events.jsonl').open('a',encoding='utf-8') as f:
            f.write(json.dumps(record)+'\n');f.flush()
        print(json.dumps(record),flush=True)

    def write(self,address,data):
        data=bytes(data)
        buf=ctypes.create_string_buffer(data);got=ctypes.c_size_t()
        if not k32.WriteProcessMemory(ctypes.c_void_p(self.h),ctypes.c_void_p(address),buf,len(data),ctypes.byref(got)) or got.value!=len(data):
            raise ctypes.WinError(ctypes.get_last_error())
        if self.g.read(address,len(data))!=data: raise RuntimeError('Write readback mismatch')

    def protect(self,address,size,value):
        old=wt.DWORD()
        if not k32.VirtualProtectEx(ctypes.c_void_p(self.h),ctypes.c_void_p(address),size,value,ctypes.byref(old)):
            raise ctypes.WinError(ctypes.get_last_error())
        return old.value

    def call(self,code,entry,args,size=0xA000,check_clock=True):
        self.assert_identity(check_clock);g=self.g;slot=g.exe+UPDATE_SLOT
        if g.q(slot)!=g.exe+UPDATE: raise RuntimeError('CApp::Update is already hooked')
        if not 0 < len(code) <= 0x1000 or not 0 <= entry < len(code): raise RuntimeError('Invalid helper code page/entry')
        if type(size) is not int or size < 0x3000: raise RuntimeError('Frame allocation omits code/control pages')
        page=k32.VirtualAllocEx(ctypes.c_void_p(self.h),ctypes.c_void_p(0),ctypes.c_size_t(size),0x3000,0x04)
        if not page: raise ctypes.WinError(ctypes.get_last_error())
        data=page+0x1000;helper=page+0x2000
        rcx,rdx=args(page)
        stub=frame_stub(page,helper+entry,rcx,rdx)
        problems=verify_stub(stub,page,helper+entry,[rcx,rdx])
        if problems: raise RuntimeError('; '.join(problems))
        self.write(data+8,struct.pack('<Q',g.exe+UPDATE))
        self.write(page,stub);self.write(helper,code)
        for address in (page,helper):
            self.protect(address,0x1000,0x20)
            if not k32.FlushInstructionCache(ctypes.c_void_p(self.h),ctypes.c_void_p(address),0x1000):
                raise ctypes.WinError(ctypes.get_last_error())
        self.log('dispatch_ready',page=hex(page),size=size,helper_sha256=hashlib.sha256(code).hexdigest(),args=[hex(rcx),hex(rdx)])
        old=self.protect(slot,8,0x04);reached=0
        try:
            if g.q(slot)!=g.exe+UPDATE:raise RuntimeError('CApp::Update changed before dispatch installation')
            self.write(slot,struct.pack('<Q',page))
            until=time.monotonic()+5
            while time.monotonic()<until:
                reached=g.read(data,1)[0]
                if reached==2: break
                time.sleep(0.015)
        finally:
            errors=restore_slots(self,[(slot,g.exe+UPDATE,page,old)])
            if errors:
                self.log('frame_restore_failed',errors=errors,page=hex(page))
                raise RuntimeError('Frame dispatch restoration failed: '+json.dumps(errors))
        result=struct.unpack('<I',g.read(data+4,4))[0]
        self.log('frame_call',status=reached,result=result,slot_restored=g.q(slot)==g.exe+UPDATE,page=hex(page))
        if reached!=2: raise RuntimeError(f'Frame call did not finish: {reached}')
        self.assert_identity(check_clock)
        return page,result


def decode(data):
    complete,count=struct.unpack_from('<II',data)
    if complete!=1 or count>256: raise RuntimeError('Invalid probe output')
    entries=[]
    keys=('width','height','mips','array','format','samples','sample_quality','usage','bind','cpu','misc')
    for i in range(count):
        o=8+i*72
        view,resource,dimension,stage,slot=struct.unpack_from('<QQIII',data,o)
        e=dict(view=hex(view),resource=hex(resource),dimension=dimension,stage=stage,slot=slot)
        if dimension==3: e['desc']=dict(zip(keys,struct.unpack_from('<11I',data,o+28)))
        entries.append(e)
    return entries


def probe(obj,out):
    code,entry=object_text(obj,'gpu_probe')
    f=Frame(out);p=driver_path(f.g)
    page,result=f.call(code,entry,lambda page:(p['immediate'],page+0x4000))
    data=f.g.read(page+0x4000,8+256*72)
    (out/'output.bin').write_bytes(data)
    entries=decode(data)
    final=dict(state=f.state(),entries=entries)
    (out/'textures.json').write_text(json.dumps(final,indent=2),encoding='utf-8')
    f.log('probe_complete',textures=len(entries))
    print(json.dumps(entries,indent=2))


def inspect(obj,out,input_path):
    code,entry=object_text(obj,'gpu_inspect')
    f=Frame(out)
    candidates=json.loads(input_path.read_text())
    all_entries=[]
    for offset in range(0,len(candidates),256):
        batch=candidates[offset:offset+256]
        payload=bytearray(struct.pack('<II',len(batch),0))
        for c in batch:
            wrapper=int(c['wrapper'],16);vtable=f.g.exe+int(c['vtable'],16);resource=int(c['resource'],16)
            field=int(c.get('resource_field',hex(wrapper+0x10)),16)
            if f.g.q(wrapper)!=vtable or f.g.q(field)!=resource:
                raise RuntimeError('Texture wrapper changed before inspection')
            vt=f.g.q(resource)
            if not vt or not f.g.q(vt+0x38): raise RuntimeError('Unavailable COM resource vtable')
            payload.extend(struct.pack('<5Q',wrapper,vtable,resource,vt,field))
        def args(page):
            f.write(page+0x3000,payload)
            return page+0x3000,page+0x6000
        page,result=f.call(code,entry,args,size=0xB000)
        if result: raise RuntimeError(f'Native object identity guard rejected batch: {result:#x}')
        data=f.g.read(page+0x6000,8+256*72)
        (out/f'output-{offset:04d}.bin').write_bytes(data)
        entries=decode(data)
        for e,c in zip(entries,batch): e['candidate']=c
        all_entries.extend(entries)
    (out/'textures.json').write_text(json.dumps(dict(state=f.state(),entries=all_entries),indent=2),encoding='utf-8')
    f.log('inspect_complete',textures=len(all_entries))
    for e in all_entries:
        d=e.get('desc',{})
        if d.get('array')==19 or d.get('width') in (2049,4096):
            print(json.dumps(e))


if __name__=='__main__':
    p=argparse.ArgumentParser()
    p.add_argument('--obj',required=True,type=pathlib.Path)
    p.add_argument('--out',required=True,type=pathlib.Path)
    p.add_argument('--input',type=pathlib.Path)
    a=p.parse_args()
    if a.input: inspect(a.obj,a.out,a.input)
    else: probe(a.obj,a.out)
