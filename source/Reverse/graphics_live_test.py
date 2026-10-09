"""One reversible native colour-setter call on CApp::Update, with automatic restoration.

This test changes only one channel of the vector written by exe+0xCE7870.
It does not change the game archives or gameplay records. The meaning of the
vector must be judged from captured frames, not from its offset or name.
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
from image import Image
from stub import run as verify_stub

VERIFIED_EXE_HASH = '87ed4e46590e0939c9efcaef14fa0f892917604dfda86e0a4be319fccffa2d6a'
UPDATE, UPDATE_SLOT, SETTER = 0x14B600, 0x1387728, 0xCE7870
UPDATE_START = bytes.fromhex('48895C241048896C241848897424205741544155415641574883EC30448BA9C8')
SETTER_START = bytes.fromhex('0F2802660F7F8120100000C3')

k32.VirtualAllocEx.restype = ctypes.c_void_p
k32.VirtualProtectEx.argtypes = [ctypes.c_void_p,ctypes.c_void_p,ctypes.c_size_t,wt.DWORD,ctypes.POINTER(wt.DWORD)]
k32.WriteProcessMemory.argtypes = [ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_size_t,ctypes.POINTER(ctypes.c_size_t)]
k32.FlushInstructionCache.argtypes = [ctypes.c_void_p,ctypes.c_void_p,ctypes.c_size_t]


def frame_stub(code, function, rcx, rdx):
    # Same no-player-guard, two-argument path as GameMemory.FrameStub.
    data = code+0x1000
    b = bytearray()
    def add(h): b.extend(bytes.fromhex(h))
    def q(value): b.extend(struct.pack('<Q',value))
    def rip(target,after=0): b.extend(struct.pack('<i',target-code-(len(b)+4+after)))
    def status(value):
        add('C605'); rip(data,1); b.append(value)
    def next_update():
        add('FF25'); rip(data+8)
    add('803D'); rip(data,1); b.append(0)
    add('7406'); next_update()
    status(1)
    add('5152415041514883EC68')
    for r in range(4): b.extend((0xF3,0x0F,0x7F,0x44|(r<<3),0x24,0x20+r*16))
    add('48B9'); q(rcx)
    add('48BA'); q(rdx)
    add('48B8'); q(function)
    add('FFD08905'); rip(data+4)
    for r in range(4): b.extend((0xF3,0x0F,0x6F,0x44|(r<<3),0x24,0x20+r*16))
    add('4883C468415941585A59')
    status(2); next_update()
    return bytes(b)


class Test:
    def __init__(self,out):
        self.out=out
        self.g=Game()
        self.log_path=out/'events.jsonl'
        self.h=k32.OpenProcess(0x0438,False,self.g.pid)
        if not self.h: raise ctypes.WinError(ctypes.get_last_error())
        path=ctypes.create_unicode_buffer(32768)
        if not psapi.GetModuleFileNameExW(ctypes.c_void_p(self.g.h),ctypes.c_void_p(self.g.exe),path,len(path)):
            raise ctypes.WinError(ctypes.get_last_error())
        self.exe_hash=hashlib.sha256(pathlib.Path(path.value).read_bytes()).hexdigest()
        if self.exe_hash!=VERIFIED_EXE_HASH: raise RuntimeError('Unverified game executable')
        im=Image()
        for rva,code in ((UPDATE,UPDATE_START),(SETTER,SETTER_START)):
            if self.g.read(self.g.exe+rva,len(code))!=code: raise RuntimeError(f'Unexpected native code: {rva:#x}')
        for rva,end in ((0x18C8B0,im.func(0x18C8B0)[1]),(0x14B854,0x14B868),(0x188A40,0x188A81)):
            if self.g.read(self.g.exe+rva,end-rva)!=im.img[rva:end]: raise RuntimeError(f'Preserved code differs: {rva:#x}')
        self.renderer=self.g.q(self.g.exe+0x1BB3D00)
        self.context=self.g.q(self.renderer+0x2F8A0)
        self.target=self.g.q(self.context+0x20)
        if not self.target or self.target%16: raise RuntimeError('Unaligned/unavailable renderer target')
        self.original=self.g.read(self.target+0x1020,16)
        self.baseline=self.state()
        if self.baseline['clock'] is None or self.baseline['manager_mode']!=0 or self.baseline['manager_scene']=='0x0':
            raise RuntimeError('A loaded game scene and valid date are required')
        time.sleep(2)
        self.assert_identity()
        if self.g.read(self.target+0x1020,16)!=self.original: raise RuntimeError('Colour changed during preflight')
        self.log('preflight',exe_hash=self.exe_hash,baseline=self.baseline,original=self.original.hex())
        (out/'original.json').write_text(json.dumps(dict(pid=self.g.pid,exe=hex(self.g.exe),target=hex(self.target),
                      address=hex(self.target+0x1020),bytes=self.original.hex(),values=struct.unpack('<4f',self.original),
                      state=self.baseline),indent=2),encoding='utf-8')

    def log(self,event,**data):
        record=dict(event=event,monotonic=time.monotonic(),**data)
        with self.log_path.open('a',encoding='utf-8') as f:
            f.write(json.dumps(record)+'\n'); f.flush()
        print(json.dumps(record),flush=True)

    def state(self):
        d=observe(self.g)
        d.update(renderer=hex(self.g.q(self.g.exe+0x1BB3D00) or 0),
                 context=hex(self.g.q(self.renderer+0x2F8A0) or 0),
                 target=hex(self.g.q(self.context+0x20) or 0),
                 camera=self.g.read(self.g.exe+0x1B62450+0x50+0x50,0x78).hex())
        return d

    def assert_identity(self):
        if self.state()!=self.baseline: raise RuntimeError('Game date, scene, renderer, or camera changed')

    def write(self,address,data):
        buf=ctypes.create_string_buffer(data)
        got=ctypes.c_size_t()
        if not k32.WriteProcessMemory(ctypes.c_void_p(self.h),ctypes.c_void_p(address),buf,len(data),ctypes.byref(got)) or got.value!=len(data):
            raise ctypes.WinError(ctypes.get_last_error())
        if self.g.read(address,len(data))!=data: raise RuntimeError('Write readback mismatch')

    def protect(self,address,size,value):
        old=wt.DWORD()
        if not k32.VirtualProtectEx(ctypes.c_void_p(self.h),ctypes.c_void_p(address),size,value,ctypes.byref(old)):
            raise ctypes.WinError(ctypes.get_last_error())
        return old.value

    def call(self,vector):
        self.assert_identity()
        g=self.g
        slot=g.exe+UPDATE_SLOT
        if g.q(slot)!=g.exe+UPDATE: raise RuntimeError('CApp::Update is already hooked')
        page=k32.VirtualAllocEx(ctypes.c_void_p(self.h),ctypes.c_void_p(0),ctypes.c_size_t(8192),0x3000,0x04)
        if not page: raise ctypes.WinError(ctypes.get_last_error())
        data=page+0x1000
        stub=frame_stub(page,g.exe+SETTER,self.target,data+0x40)
        problems=verify_stub(stub,page,g.exe+SETTER,[self.target,data+0x40])
        if problems: raise RuntimeError('; '.join(problems))
        self.write(data+8,struct.pack('<Q',g.exe+UPDATE))
        self.write(data+0x40,vector)
        self.write(page,stub)
        self.protect(page,0x1000,0x20)
        if not k32.FlushInstructionCache(ctypes.c_void_p(self.h),ctypes.c_void_p(page),len(stub)):
            raise ctypes.WinError(ctypes.get_last_error())
        old=self.protect(slot,8,0x04)
        reached=0
        try:
            self.write(slot,struct.pack('<Q',page))
            until=time.monotonic()+3
            while time.monotonic()<until:
                reached=g.read(data,1)[0]
                if reached==2: break
                time.sleep(0.015)
        finally:
            self.write(slot,struct.pack('<Q',g.exe+UPDATE))
            self.protect(slot,8,old)
        # Keep the 8 KiB pages alive, as the editor does: an already-dispatched late frame
        # can enter the completed stub, which goes straight to the original Update.
        self.log('frame_call',status=reached,page=hex(page),slot_restored=g.q(slot)==g.exe+UPDATE,
                 vector=vector.hex(),readback=g.read(self.target+0x1020,16).hex())
        if reached!=2: raise RuntimeError(f'Frame call did not finish (status={reached})')
        if g.read(self.target+0x1020,16)!=vector: raise RuntimeError('Colour setter readback differs')


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--out',required=True)
    parser.add_argument('--hold',type=float,default=45)
    parser.add_argument('--channel',choices=('green','fourth'),default='green')
    parser.add_argument('--value',type=float,default=0.0)
    args=parser.parse_args()
    if not 1<=args.hold<=60: raise ValueError('Hold must be 1..60 seconds')
    if not 0<=args.value<=1: raise ValueError('Channel value must be 0..1')
    out=pathlib.Path(args.out)
    out.mkdir(parents=True,exist_ok=True)
    t=Test(out)
    # Change one float; preserve all other components bit-for-bit.
    offset=4 if args.channel=='green' else 12
    changed=t.original[:offset]+struct.pack('<f',args.value)+t.original[offset+4:]
    if changed==t.original: raise RuntimeError('Channel already has the requested value')
    armed=False
    try:
        armed=True
        t.call(changed)
        t.log('applied',hold_seconds=args.hold,channel=args.channel,values=struct.unpack('<4f',changed))
        until=time.monotonic()+args.hold
        while time.monotonic()<until:
            t.assert_identity()
            actual=t.g.read(t.target+0x1020,16)
            if actual!=changed:
                t.log('trial_overwritten',expected=changed.hex(),actual=actual.hex() if actual else None,
                      seconds_since_applied=args.hold-(until-time.monotonic()),state=t.state())
                raise RuntimeError('Trial colour was overwritten')
            time.sleep(0.1)
    finally:
        if armed:
            # Restore only when the same renderer and scene still own this vector.
            if (t.g.q(t.g.exe+0x1BB3D00)!=t.renderer or t.g.q(t.renderer+0x2F8A0)!=t.context
                    or t.g.q(t.context+0x20)!=t.target or observe(t.g)['manager_scene']!=t.baseline['manager_scene']):
                t.log('restore_aborted',reason='Renderer lifetime changed')
                raise RuntimeError('Renderer lifetime changed; do not write a stale address')
            try:
                t.call(t.original)
            except Exception as e:
                # The setter is precisely a 16-byte store. If the frame is unavailable,
                # restore its original data directly in the still-live same object.
                t.log('restore_frame_failed',error=str(e))
                t.write(t.target+0x1020,t.original)
                raise
            time.sleep(1)
            final=t.g.read(t.target+0x1020,16)
            t.log('restored',bytes_match=final==t.original,state=t.state(),
                  date_scene_camera_unchanged=t.state()==t.baseline)
            if final!=t.original: raise RuntimeError('Original colour was not restored')
        k32.CloseHandle(ctypes.c_void_p(t.h))


if __name__=='__main__': main()
