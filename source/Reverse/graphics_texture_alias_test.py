"""Replace the two identified native resource records, then restore both.

Uses the existing validated COM swap helper. No archive writes, command buffer
patches or API argument substitution. A read-only binding trace can run during
the hold to establish which resource actually reaches the renderer.
"""
import argparse
import ctypes
import hashlib
import json
import pathlib
import struct
import time

from game import k32
from graphics_gpu import driver_path, object_text
from graphics_gpu_render import Render
from graphics_texture_test import (archive_pixels, array_gpu_order, field_candidate, identity,
                                   read_pixels, render_copy_candidate)


def main():
    p = argparse.ArgumentParser()
    for option in ('obj', 'catalog', 'assets', 'out', 'replacement', 'render-copy-refs',
                   'render-proof', 'dispatch-obj'):
        p.add_argument('--' + option, required=True, type=pathlib.Path)
    p.add_argument('--hold', type=float, default=40)
    p.add_argument('--detail-layers', action='store_true')
    p.add_argument('--containers',type=pathlib.Path)
    a = p.parse_args()
    if not 1 <= a.hold <= 60: raise RuntimeError('Hold must be 1..60 seconds')
    f = Render(a.out, a.render_proof, a.dispatch_obj)
    # Same native context and slot as the observed terrain bind. The one-shot
    # scheduler must remain usable after old SRVs cease to be submitted.
    f.bind_view = 0xFFFFFFFFFFFFFFFF
    f.log('slot_dispatch_selected',slot=4,count=1,any_view=True,
          native_arguments_unchanged=True)
    g = f.g
    if a.detail_layers:
        if not a.containers: raise RuntimeError('The recorded native container scan is required')
        matching=[e for e in json.loads(a.catalog.read_text())['entries']
                  if e.get('desc',{}).get('array')==19 and e['desc']['width']==1024
                  and e['desc']['height']==1024 and e['desc']['mips']==9
                  and e['desc']['format']==76 and e['candidate']['texture_index']==0]
        if len(matching)!=1: raise RuntimeError('Detail albedo array is not unique')
        source=matching[0]
    else:
        source = field_candidate(a.catalog)
    copy = render_copy_candidate(f,source,a.render_copy_refs,
                                 expected_count=3 if a.detail_layers else 5,containers_path=a.containers)
    candidates = [source, copy]
    identities = [identity(f, c) for c in candidates]
    old_records = [g.read(ident[2], 24) for ident in identities]
    if len({ident[2] for ident in identities}) != 2 or old_records[0] != old_records[1]:
        raise RuntimeError('Two distinct matching resource records are required')
    original = read_pixels(f, a.obj, source, a.out/'field-original.bc3')
    expected, _ = archive_pixels(a.assets,'e369753a' if a.detail_layers else 'f7c960b0',
                                0 if a.detail_layers else 2)
    if a.detail_layers:
        d=source['desc']
        expected=array_gpu_order(expected,d['width'],d['height'],d['mips'],d['array'])
    if original != expected: raise RuntimeError('Original GPU/archive bytes differ')
    changed = a.replacement.read_bytes()
    if len(changed) != len(original) or changed == original:
        raise RuntimeError('Invalid replacement mip chain')
    for offset in range(0, len(original), 16):
        if (original[offset:offset+8] != changed[offset:offset+8] or
                original[offset+12:offset+16] != changed[offset+12:offset+16]):
            raise RuntimeError('Replacement changed alpha or colour indices')
    code, entry = object_text(a.obj, 'gpu_swap')
    pixels = k32.VirtualAllocEx(ctypes.c_void_p(f.h),None,len(changed),0x3000,0x04)
    if not pixels: raise ctypes.WinError(ctypes.get_last_error())
    sessions = []
    f.write(pixels, changed)
    keys = ('width','height','mips','array','format','samples','sample_quality','usage','bind','cpu','misc')
    desc = struct.pack('<11I',*(source['desc'][key] for key in keys))
    device = driver_path(g)['device']
    subresources = bytearray()
    offset = 0
    for layer in range(source['desc']['array']):
        for mip in range(source['desc']['mips']):
            pitch = max(1,((source['desc']['width'] >> mip)+3)//4)*16
            rows = max(1,((source['desc']['height'] >> mip)+3)//4)
            subresources.extend(struct.pack('<QII', pixels+offset,pitch,pitch*rows))
            offset += pitch*rows
    if len(subresources)>0xF00: raise RuntimeError('Initial subresources exceed their data page')
    if offset != len(changed): raise RuntimeError('Subresource byte count differs')
    try:
        for index, (c, ident, old) in enumerate(zip(candidates, identities, old_records)):
            old_values = struct.unpack('<3Q', old)
            s = dict(index=index, candidate=c, identity=ident, old=old, page=None)
            sessions.append(s)
            def args(page):
                s['page'] = page
                payload = struct.pack('<9Q',*ident,device,page+0x3100,old_values[1],old_values[2])+desc+struct.pack('<I',0)
                s['payload'] = payload
                f.write(page+0x3000,payload)
                f.write(page+0x3100,subresources)
                return page+0x3000,page+0x4000
            page, result = f.call(code,entry,args)
            session = page+0x4000
            phase, hr = struct.unpack('<II',g.read(session,8))
            if result or phase != 1:
                raise RuntimeError(f'Swap {index} failed: {result:#x}/{hr:#x}, phase {phase}')
            replacement = g.read(session+32,24)
            if g.read(ident[2],24) != replacement:
                raise RuntimeError('Installed pointers differ from the swap session')
            s['replacement'] = replacement
            current = json.loads(json.dumps(c))
            current['resource'] = hex(struct.unpack_from('<Q',replacement)[0])
            current['candidate']['resource'] = current['resource']
            s['current'] = current
            f.log('alias_applied',index=index,field=hex(ident[2]),session=hex(session),
                  original=old.hex(),replacement=replacement.hex())
        for s in sessions:
            gpu = read_pixels(f,a.obj,s['current'],a.out/f"field-{s['index']}-modified-readback.bc3")
            if gpu != changed: raise RuntimeError('Replacement GPU bytes differ')
        applied = dict(state=f.state(),records=[dict(index=s['index'],field=hex(s['identity'][2]),
                       original=s['old'].hex(),replacement=s['replacement'].hex()) for s in sessions],
                       original_sha256=hashlib.sha256(original).hexdigest(),
                       replacement_sha256=hashlib.sha256(changed).hexdigest(),hold=a.hold)
        (a.out/'applied.json').write_text(json.dumps(applied,indent=2),encoding='utf-8')
        f.log('both_aliases_verified',**applied)
        print('BOTH ALIASES APPLIED AND GPU VERIFIED; HOLD STARTED',flush=True)
        until = time.monotonic()+a.hold
        while time.monotonic()<until:
            f.assert_identity()
            for s in sessions:
                if g.read(s['identity'][2],24)!=s['replacement']:
                    raise RuntimeError('A resource record changed during the hold')
            time.sleep(0.25)
        f.log('hold_complete')
    finally:
        errors=[]
        for s in reversed(sessions):
            page=s['page']
            if page is None: continue
            dispatch=struct.unpack('<I',g.read(page+0x1000,4))[0]
            phase=struct.unpack('<I',g.read(page+0x4000,4))[0]
            if dispatch==1:
                errors.append(f"Swap {s['index']} is still executing; retain its buffers")
                continue
            if phase!=1: continue
            try:
                payload=bytearray(s['payload']);struct.pack_into('<I',payload,116,1)
                def restore_args(newpage):
                    f.write(newpage+0x3000,payload)
                    return newpage+0x3000,page+0x4000
                _,result=f.call(code,entry,restore_args,check_clock=False)
                phase=struct.unpack('<I',g.read(page+0x4000,4))[0]
                if result or phase!=2 or g.read(s['identity'][2],24)!=s['old']:
                    raise RuntimeError(f'Restore failed: {result:#x}, phase {phase}')
                f.log('alias_restored',index=s['index'],session_phase=phase,record=s['old'].hex())
            except Exception as error:
                errors.append(f"Restore {s['index']}: {error}")
        if errors:
            f.log('restoration_requires_attention',errors=errors,pixels=hex(pixels))
            raise RuntimeError('; '.join(errors))
        if not k32.VirtualFreeEx(ctypes.c_void_p(f.h),ctypes.c_void_p(pixels),0,0x8000):
            raise ctypes.WinError(ctypes.get_last_error())
        for s in sessions:
            if g.read(s['identity'][2],24)!=s['old']:
                raise RuntimeError('An original record was not restored')
        gpu=read_pixels(f,a.obj,source,a.out/'field-restored-readback.bc3')
        if gpu!=original: raise RuntimeError('Restored GPU bytes differ')
        result=dict(state=f.state(),original_records_restored=True,
                    original_gpu_sha256=hashlib.sha256(gpu).hexdigest(),
                    ps_slot_restored=g.q(g.q(driver_path(g)['immediate'])+64)==0x7ffed25dbaf0,
                    sessions=[dict(index=s['index'],phase=struct.unpack('<I',g.read(s['page']+0x4000,4))[0])
                              for s in sessions if s['page'] is not None])
        if not result['ps_slot_restored']: raise RuntimeError('Native API slot was not restored')
        (a.out/'final.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
        f.log('restoration_verified',**result)
        print('ORIGINAL RECORDS, GPU BYTES AND NATIVE API SLOT RESTORED',flush=True)


if __name__=='__main__': main()
