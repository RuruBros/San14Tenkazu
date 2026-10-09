"""Record PS bindings for discovery; this is not a production season backend.

Dynamic D3D11 Draw slots are intentionally unavailable for live tracing.
Native context ownership and lease/watchdog recovery must be verified before
using this observer across scene/device teardown or controller termination.
"""
import argparse
import ctypes
import json
import pathlib
import struct
import time
from game import k32
from graphics_gpu import Frame, driver_path, object_text
from graphics_gpu_trace import d3d11_range
from season_trace_contract import TraceLayout, HEADER_SIZE, RECORD_SIZE, CANARY, restore_slots


SYMBOLS = ('season_trace_indexed', 'season_trace_draw', 'season_trace_indexed_instanced', 'season_trace_instanced')
ARGUMENT_COUNTS = (4, 3, 6, 5)


def wrapper(helper, data, argument_count):
    prefix = bytes.fromhex('4883ec48')
    # Copy native stack arguments before appending the observer arguments.
    for i in range(4, argument_count):
        prefix += bytes.fromhex('488b4424') + bytes([0x48+8+8*i])
        prefix += bytes.fromhex('48894424') + bytes([8*i])
    if argument_count == 3:
        prefix += b'\x49\xb9' + struct.pack('<Q', data)
    else:
        prefix += b'\x48\xb8' + struct.pack('<Q', data)
        prefix += bytes.fromhex('48894424') + bytes([8*argument_count])
    prefix += bytes.fromhex('488b44244848894424') + bytes([8*(argument_count+1)])
    return prefix + b'\x48\xb8' + struct.pack('<Q', helper) + bytes.fromhex('ffd04883c448c3')


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--out', required=True, type=pathlib.Path)
    p.add_argument('--seconds', type=float, default=0.5)
    p.add_argument('--bindings', action='store_true', help='Explicitly select the bounded diagnostic PS binding observer')
    a = p.parse_args()
    if not a.bindings:
        raise RuntimeError('Live Draw tracing is unavailable: D3D11 changes its dispatch slots. Use verified PS binding diagnostics only.')
    if not 0.05 <= a.seconds <= 2: raise ValueError('Trace must be 0.05..2 seconds')
    # Observation supports date progression; executable, world, scene and device remain guarded.
    f = Frame(a.out, check_clock=False); g = f.g
    context = driver_path(g)['immediate']; vtable = g.q(context)
    indices=(8,9)
    symbols=('season_trace_views','season_trace_shader')
    arg_counts=(4,4)
    slots = [vtable + i*8 for i in indices]
    original = [g.q(s) for s in slots]
    module, size = d3d11_range(g)
    if any(not module <= x < module + size for x in original):
        raise RuntimeError('Draw API is already redirected')
    obj = pathlib.Path(__file__).resolve().parent.parent/'Seasons/Native/Trace.obj'
    functions = [object_text(obj, symbol) for symbol in symbols]
    layout = TraceLayout(8192);capacity=layout.capacity
    allocation = layout.allocation_size
    page = k32.VirtualAllocEx(ctypes.c_void_p(f.h), None, ctypes.c_size_t(allocation), 0x3000, 0x04)
    if not page: raise ctypes.WinError(ctypes.get_last_error())
    if any(len(code)>0x2000 for code,entry in functions): raise RuntimeError('Trace code exceeded its allocation')
    data = page+0x10000
    def write_buffer(offset,payload):
        layout.check_range(offset,len(payload));f.write(page+offset,payload)
    write_buffer(0x10000, struct.pack('<4Q4I', *(original+[0]*(4-len(original))), 0, 0, capacity, capacity))
    write_buffer(layout.records_end,CANARY)
    for i, (code,entry) in enumerate(functions):
        helper=page+0x4000+i*0x2000
        if not 0<=entry<len(code):raise RuntimeError('Invalid observer entry offset')
        write_buffer(helper-page,code)
        write_buffer(i*0x1000,wrapper(helper+entry,data,arg_counts[i]))
    f.protect(page, 0xc000, 0x20)
    if not k32.FlushInstructionCache(ctypes.c_void_p(f.h), ctypes.c_void_p(page), 0xc000):
        raise ctypes.WinError(ctypes.get_last_error())
    leases=[];recovery_errors=[]
    f.log('trace_ready', context=hex(context), vtable=hex(vtable), indices=indices,
          original=[hex(p) for p in original], page=hex(page), mode='bindings',
          allocationBytes=allocation,headerBytes=HEADER_SIZE,recordBytes=RECORD_SIZE,capacity=capacity)
    try:
        f.assert_identity(check_clock=False)
        for slot, target, expected in zip(slots, (page+i*0x1000 for i in range(len(indices))),original):
            if g.q(slot)!=expected:raise RuntimeError('PS dispatch changed before installation')
            old=f.protect(slot,8,0x04);leases.append((slot,expected,target,old))
            f.write(slot,struct.pack('<Q',target))
        write_buffer(0x10000+32,struct.pack('<I',1))
        deadline=time.monotonic()+a.seconds
        while time.monotonic()<deadline:
            time.sleep(min(0.01,max(0,deadline-time.monotonic())))
            f.assert_identity(check_clock=False)
    finally:
        try:write_buffer(0x10000+32,struct.pack('<I',0))
        except Exception as error:recovery_errors.append(dict(stage='disable',error=str(error)))
        recovery_errors.extend(restore_slots(f,leases))
        f.log('trace_restore',errors=recovery_errors,
              slotRestored=all(g.q(s)==o for s,o in zip(slots,original)))
        if recovery_errors:raise RuntimeError('Native observer recovery failed: '+json.dumps(recovery_errors))
    f.assert_identity(check_clock=False)
    time.sleep(0.05)
    if g.read(page+layout.records_end,len(CANARY))!=CANARY:
        raise RuntimeError('Native trace buffer end guard changed')
    header=g.read(data,HEADER_SIZE);count,saturated=layout.decode_count(header)
    raw=g.read(data+HEADER_SIZE,count*RECORD_SIZE) if count else b''
    (a.out/'draw-records.bin').write_bytes(raw)
    rows=[]
    for i in range(count):
        r=raw[i*RECORD_SIZE:(i+1)*RECORD_SIZE]
        if struct.unpack_from('<I',r,336)[0]!=1:continue
        q=struct.unpack_from('<36Q',r)
        kind,n,start,base,instances,first_instance=struct.unpack_from('<3Ii2I',r,288)
        rows.append(dict(sequence=i,caller=hex(q[0]),callerRva=hex(q[0]-g.exe),
                         ps=hex(q[1]),vs=hex(q[2]),targets=[hex(x) for x in q[3:7]],depth=hex(q[7]),
                         views=[hex(x) for x in q[8:20]],psConstants=[hex(x) for x in q[20:28]],
                         vsConstants=[hex(x) for x in q[28:36]],kind=kind,count=n,start=start,base=base,
                         instances=instances,firstInstance=first_instance,
                         viewport=list(struct.unpack_from('<6f',r,312))))
    output=dict(state=f.state(),records=rows,slotRestored=all(g.q(s)==o for s,o in zip(slots,original)),
                capacity=capacity,saturated=saturated,coverage='Bounded PS bindings; not a complete draw/frame trace',
                original=[hex(p) for p in original],current=[hex(g.q(s)) for s in slots],indices=indices,
                codePage=hex(page),codeLifetime='Retained for late native calls until game exit')
    (a.out/'draws.json').write_text(json.dumps(output,indent=2),encoding='utf-8')
    unique={}
    for r in rows:
        key=(r['ps'],r['vs'],tuple(r['targets']),r['depth'],tuple(r['views']))
        if key not in unique:unique[key]=dict(r,draws=0)
        unique[key]['draws']+=1
    (a.out/'passes.json').write_text(json.dumps(list(unique.values()),indent=2),encoding='utf-8')
    print(json.dumps(dict(records=len(rows),passes=len(unique),saturated=saturated,slotRestored=output['slotRestored'])))
    if not output['slotRestored']:raise RuntimeError('Native draw slots were not restored')


if __name__=='__main__':main()
