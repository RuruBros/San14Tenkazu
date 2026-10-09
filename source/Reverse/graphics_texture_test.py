"""GPU resource experiment; this does not implement visible seasonal effects.

Requires a current binding trace to invoke D3D11 context methods on the observed
rendering thread. Transient command buffers are never mutation targets.
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

k32.VirtualFreeEx.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_size_t,ctypes.c_ulong]


def field_candidate(catalog):
    entries=json.loads(catalog.read_text())['entries']
    candidates=[e for e in entries if e.get('desc',{}).get('width')==4096 and
        e['desc']['height']==4096 and e['desc']['mips']==11 and e['desc']['format']==76 and
        e['candidate']['texture_index']==2]
    if len(candidates)!=1: raise RuntimeError('Field texture candidate is not unique')
    return candidates[0]


def identity(f,candidate):
    c=candidate['candidate'];g=f.g
    owner=int(c['wrapper'],16);vt=g.exe+int(c['vtable'],16)
    field=int(c['resource_field'],16);resource=int(candidate['resource'],16)
    if g.q(owner)!=vt or g.q(field)!=resource: raise RuntimeError('Asset owner/resource changed')
    return (owner,vt,field,resource,g.q(resource))


def render_copy_candidate(f,c,refs_path,expected_count=5,containers_path=None):
    """Find a native texture-set copy using its five GPU resource records.

    The 0x14BD800 container's count/+0x28 slots are established by 0x967790
    and 0x967C00. The selected record must equal all 72 source bytes. Every
    other record must share all three COM pointers and the metadata through
    offset 55; runtime differences in the remaining bytes are recorded.
    The scan is discovery input only; all pointers are revalidated.
    """
    refs=json.loads(refs_path.read_text());g=f.g
    if refs['pid']!=g.pid or int(refs['exe'],16)!=g.exe:
        raise RuntimeError('Reference scan belongs to another process')
    source=identity(f,c);owner,_,field,resource,_=source
    count=struct.unpack('<I',g.read(owner+0x10,4))[0]
    if count!=expected_count: raise RuntimeError('Unexpected field asset count')
    records=[g.read(owner+0x18+i*72,72) for i in range(count)]
    selected=c['candidate']['texture_index'];matches=[];seen=set()
    vt=g.exe+0x14BD800;pattern=struct.pack('<Q',vt)
    known_containers=None
    if containers_path:
        scan=json.loads(containers_path.read_text())
        if scan['state']['pid']!=g.pid or int(scan['state']['exe'],16)!=g.exe:
            raise RuntimeError('Container scan belongs to another process')
        known_containers=[int(e['address'],16) for e in scan['entries'] if e['count']==count]
    for row in refs['references']['texture']:
        address=int(row['address'],16)
        if address==field or g.read(address,72)!=records[selected]: continue
        if known_containers is not None:
            containers=known_containers
        else:
            page=address&~0xFFF;raw=g.read(page,0x1000)
            if raw is None: continue
            containers=[];at=raw.find(pattern)
            while at>=0:
                containers.append(page+at);at=raw.find(pattern,at+1)
        for container in containers:
            if container%8 or container in seen: continue
            seen.add(container)
            if g.q(container)!=vt: continue
            if g.q(container+0x10)!=0 or not g.q(container+8): continue
            if struct.unpack('<I',g.read(container+0x20,4))[0]!=count: continue
            slots=[g.q(container+0x28+i*8) for i in range(count)]
            if slots[selected]!=address: continue
            if any(not p or g.read(p,56)!=records[i][:56] for i,p in enumerate(slots)): continue
            matches.append((container,address,slots))
    distinct_records={r for _,r,_ in matches}
    if len(distinct_records)!=1:
        f.log('render_copy_candidates',matches=[dict(container=hex(p),record=hex(r),slots=[hex(s) for s in slots])
                                               for p,r,slots in matches])
        raise RuntimeError(f'Exact native field copy record is not unique: {len(distinct_records)}')
    if any(slots!=matches[0][2] for _,_,slots in matches):
        raise RuntimeError('Native field aliases disagree on their record slots')
    container,address,slots=matches[0]
    result=json.loads(json.dumps(c))
    result['view']=hex(container)
    result['candidate'].update(wrapper=hex(container),vtable='0x14bd800',resource_field=hex(address))
    differences={str(i):[j for j,(a,b) in enumerate(zip(records[i],g.read(p,72))) if a!=b]
                 for i,p in enumerate(slots)}
    f.log('render_copy_identified',source_owner=hex(owner),container=hex(container),record=hex(address),
          count=count,slots=[hex(p) for p in slots],selected_record_equal=True,
          all_resource_pointers_and_metadata_equal=True,record_byte_differences=differences,
          container_aliases=[hex(p) for p,_,_ in matches])
    return result


def read_pixels(f,obj,candidate,filename):
    code,entry=object_text(obj,'gpu_read');g=f.g;ident=identity(f,candidate)
    d=candidate['desc']
    capacity=sum(max(1,((d['width']>>m)+3)//4)*max(1,((d['height']>>m)+3)//4)*16
                 for m in range(d['mips']))*d['array']
    pixels=k32.VirtualAllocEx(ctypes.c_void_p(f.h),ctypes.c_void_p(0),ctypes.c_size_t(capacity),0x3000,0x04)
    if not pixels: raise ctypes.WinError(ctypes.get_last_error())
    p=driver_path(g)
    payload=struct.pack('<9Q',*ident,p['device'],p['immediate'],pixels,capacity)
    def args(page):
        f.write(page+0x3000,payload)
        return page+0x3000,page+0x4000
    page,result=f.call(code,entry,args)
    # call() only returns after the helper is complete; late dispatches skip it.
    try:
        meta=g.read(page+0x4000,64)
        complete,hresult=struct.unpack_from('<II',meta)
        count=struct.unpack_from('<Q',meta,56)[0]
        if result or not complete or count!=capacity:
            raise RuntimeError(f'GPU read failed: {result:#x}/{hresult:#x}, {count}/{capacity}')
        raw=g.read(pixels,count)
        if raw is None: raise RuntimeError('GPU readback buffer is unavailable')
        filename.write_bytes(raw)
        f.log('gpu_readback',candidate=candidate,bytes=count,sha256=hashlib.sha256(raw).hexdigest(),file=str(filename))
        return raw
    finally:
        if not k32.VirtualFreeEx(ctypes.c_void_p(f.h),ctypes.c_void_p(pixels),0,0x8000):
            raise ctypes.WinError(ctypes.get_last_error())


def archive_pixels(assets,ktid,index):
    meta=json.loads((assets/f'{ktid}.json').read_text())
    g1t=(assets/f'{ktid}.g1t').read_bytes()
    if hashlib.sha256(g1t).hexdigest()!=meta['sha256']: raise RuntimeError('Archive extraction changed')
    t=meta['textures'][index]
    return g1t[t['offset']:t['end']],meta


def array_gpu_order(raw,width,height,mips,layers):
    """G1T arrays group all layers of each mip; GPU subresources group mips per layer.

    The 19-layer E369753A ordering was validated byte-for-byte against GPU
    readback in 20261006-layer-trial/layout.json before any resource mutation.
    """
    parts=[[] for _ in range(layers)];offset=0
    for mip in range(mips):
        size=max(1,((width>>mip)+3)//4)*max(1,((height>>mip)+3)//4)*16
        for layer in range(layers):
            parts[layer].append(raw[offset:offset+size]);offset+=size
    if offset!=len(raw):raise RuntimeError('Archive array size differs from its descriptor')
    return b''.join(b''.join(layer) for layer in parts)


def live_trial(f,obj,c,original,changed,hold):
    if original==changed or len(original)!=len(changed): raise RuntimeError('Invalid replacement data')
    for i in range(0,len(original),16):
        if original[i:i+8]!=changed[i:i+8] or original[i+12:i+16]!=changed[i+12:i+16]:
            raise RuntimeError('Replacement changed alpha or colour indices')
    code,entry=object_text(obj,'gpu_swap');ident=identity(f,c);g=f.g
    old=g.read(ident[2],24);old_values=struct.unpack('<3Q',old)
    f.log('trial_ready',original_record=old.hex(),original_sha256=hashlib.sha256(original).hexdigest(),
          replacement_sha256=hashlib.sha256(changed).hexdigest(),hold=hold)
    pixels=k32.VirtualAllocEx(ctypes.c_void_p(f.h),ctypes.c_void_p(0),ctypes.c_size_t(len(changed)),0x3000,0x04)
    if not pixels: raise ctypes.WinError(ctypes.get_last_error())
    f.write(pixels,changed)
    d=c['desc'];p=driver_path(g);pages=[]
    keys=('width','height','mips','array','format','samples','sample_quality','usage','bind','cpu','misc')
    expected=struct.pack('<11I',*(d[k] for k in keys))
    def payload(page,command):
        return struct.pack('<9Q',*ident,p['device'],page+0x3100,old_values[1],old_values[2])+expected+struct.pack('<I',command)
    def args(page):
        pages.append(page)
        f.write(page+0x3000,payload(page,0))
        sub=bytearray();offset=0
        for m in range(d['mips']):
            pitch=max(1,((d['width']>>m)+3)//4)*16
            rows=max(1,((d['height']>>m)+3)//4)
            sub.extend(struct.pack('<QII',pixels+offset,pitch,pitch*rows));offset+=pitch*rows
        if offset!=len(changed): raise RuntimeError('Invalid mip-chain size')
        f.write(page+0x3100,sub)
        return page+0x3000,page+0x4000
    completed=False
    try:
        page,result=f.call(code,entry,args)
        completed=True
        session=page+0x4000
        phase,hr=struct.unpack('<II',g.read(session,8))
        if result or phase!=1: raise RuntimeError(f'GPU swap failed: {result:#x}/{hr:#x}, phase {phase}')
        replacement=g.read(session+32,24)
        if g.read(ident[2],24)!=replacement: raise RuntimeError('Replacement record differs')
        f.log('trial_applied',session=hex(session),original=old.hex(),replacement=replacement.hex())
        changed_candidate=json.loads(json.dumps(c))
        changed_candidate['resource']=hex(struct.unpack_from('<Q',replacement)[0])
        changed_candidate['candidate']['resource']=changed_candidate['resource']
        gpu=read_pixels(f,obj,changed_candidate,f.out/'field-modified-readback.bc3')
        if gpu!=changed: raise RuntimeError('Replacement GPU bytes differ from supplied mip chain')
        f.log('replacement_verified',sha256=hashlib.sha256(gpu).hexdigest())
        until=time.monotonic()+hold
        while time.monotonic()<until:
            f.assert_identity()
            if g.read(ident[2],24)!=replacement: raise RuntimeError('Texture record changed during trial')
            time.sleep(0.25)
        f.log('trial_hold_complete')
    finally:
        if pages:
            page=pages[0];session=page+0x4000
            reached=g.read(page+0x1000,1)[0]
            phase=struct.unpack('<I',g.read(session,4))[0]
            if reached==2:
                completed=True
                if phase==1:
                    def restore_args(newpage):
                        f.write(newpage+0x3000,payload(page,1))
                        return newpage+0x3000,session
                    _,result=f.call(code,entry,restore_args,check_clock=False)
                    phase=struct.unpack('<I',g.read(session,4))[0]
                    if result or phase!=2 or g.read(ident[2],24)!=old:
                        raise RuntimeError(f'Original texture restoration failed: {result:#x}, phase {phase}')
                    f.log('trial_restored',record=old.hex(),session_phase=phase,state=f.state())
                    gpu=read_pixels(f,obj,c,f.out/'field-restored-readback.bc3')
                    if gpu!=original: raise RuntimeError('Restored GPU texture bytes differ from original')
                    f.log('restoration_verified',sha256=hashlib.sha256(gpu).hexdigest())
            elif phase==1:
                raise RuntimeError('Native swap still executing; its memory must remain allocated')
        if completed:
            if not k32.VirtualFreeEx(ctypes.c_void_p(f.h),ctypes.c_void_p(pixels),0,0x8000):
                raise ctypes.WinError(ctypes.get_last_error())


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--obj',required=True,type=pathlib.Path)
    p.add_argument('--catalog',required=True,type=pathlib.Path)
    p.add_argument('--assets',required=True,type=pathlib.Path)
    p.add_argument('--out',required=True,type=pathlib.Path)
    p.add_argument('--replacement',type=pathlib.Path)
    p.add_argument('--render-copy-refs',type=pathlib.Path)
    p.add_argument('--render-packets',type=pathlib.Path)
    p.add_argument('--render-proof',type=pathlib.Path)
    p.add_argument('--dispatch-obj',type=pathlib.Path)
    p.add_argument('--hold',type=float,default=30)
    a=p.parse_args()
    if a.render_packets:raise RuntimeError('Transient render packet mutation was rejected by runtime results and has been removed')
    if not a.render_proof or not a.dispatch_obj:raise RuntimeError('A current --render-proof and --dispatch-obj are required')
    from graphics_gpu_render import Render
    f=Render(a.out,a.render_proof,a.dispatch_obj)
    if not 1<=a.hold<=60: raise RuntimeError('Hold must be 1..60 seconds')
    c=field_candidate(a.catalog)
    if a.render_copy_refs:
        c=render_copy_candidate(f,c,a.render_copy_refs)
    original=read_pixels(f,a.obj,c,a.out/'field-original.bc3')
    expected,meta=archive_pixels(a.assets,'f7c960b0',2)
    match=original==expected
    f.log('archive_comparison',ktid='f7c960b0',index=2,equal=match,expected_sha256=hashlib.sha256(expected).hexdigest())
    if not match: raise RuntimeError('GPU texture is not byte-for-byte equal to the field archive')
    proof=dict(state=f.state(),candidate=c,archive=meta,original_sha256=hashlib.sha256(original).hexdigest())
    (a.out/'identified.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
    if a.replacement:
        live_trial(f,a.obj,c,original,a.replacement.read_bytes(),a.hold)


if __name__=='__main__':main()
