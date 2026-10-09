# Audit cancellation, refund writes, return requests and immediate removal on copied memory.
# Return construction/pathfinding and heap/UI destruction are recorded native bodies, not C# substitutes.
import collections
import copy
import struct
import time
from context import *
from engine_audit_state import EngineAuditState
from engagement_tactic_sequence import PRESENTATION
from unit_finalization import capture_native_pools
from destruction_overflow import require_unchanged_snapshot
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8

COVER = [0x334780, 0x1F1F70, 0x1F1F80, 0x1F1B30, 0x1F1FC0, 0x235620,
         0x224670, 0x2289D0, 0x32A5E0, 0x1F22C0, 0x1DE410, 0x323ED0, 0x1EC240, 0x1F6720]
# 0x1F1380 allocates one shared diplomatic class and one shared strategic class for these kinds.
SIZES = {1: 0x78, 2: 0x80, 3: 0x78, 15: 0x80, 16: 0x78, 17: 0x88, 18: 0x88,
         21: 0x78, 22: 0x80, 23: 0x80, 25: 0x88,
         **{k: 0x90 for k in [4, 5, 6, 7, 8, 9, 19]}, **{k: 0x80 for k in [10, 11, 12, 13, 14, 20, 24]}}
DIPLOMACY = {4, 5, 6, 7, 8, 9, 19}
STRATEGIC = {10, 11, 12, 13, 14, 20, 24}


def generate(ctx, names=None):
    o = ctx.o
    o.insn_limit = 300_000_000
    capture_native_pools(ctx, [(0x1B5C990, 24, 8)])
    audit = EngineAuditState(ctx)
    snapshot = audit.snapshot
    guard_snapshot = copy.deepcopy(snapshot)
    initial_rng = o.read(ctx.g.exe + 0x1A47B90, 4)
    for head in [0xA0, 0xB0, 0xD0]:
        snapshot['lists'][hex(head)] = {'items': world.list_items(o, ctx.raw, ctx.g.exe, head,
            {'pool': world.POOL_RVA, 'next': 1, 'fields': 1})}
    mgr = o.q(ctx.raw + 0x871E0)
    addresses = [int(p, 16) for p in audit.members(o.q(mgr + 0x18))]
    if len(addresses) != len(snapshot['cmdactions']): raise RuntimeError('Command catalog and active list differ')
    commands = []
    for address, info in zip(addresses, snapshot['cmdactions']):
        kind = info['kind']
        if kind not in SIZES: raise RuntimeError(f'Unanalyzed command class: {kind}')
        vt = o.q(address)
        handlers = {hex(at): hex(o.q(vt + at) - ctx.g.exe) for at in [0x68, 0x90, 0xF0, 0x110]}
        if handlers['0x68'] != '0x14d350' or handlers['0xf0'] != '0x235620' or handlers['0x110'] != '0x2289d0':
            raise RuntimeError('Command dispatch differs from the analyzed constant-zero phase contract')
        cancel = 0x1F1B30 if kind in DIPLOMACY else 0x1F1FC0 if kind in STRATEGIC else 0x1F1F80 if kind == 1 else 0x1F1F70
        if int(handlers['0x90'], 16) != cancel:
            raise RuntimeError(f'Unanalyzed cancel handler: kind {kind}, {handlers}')
        commands.append(dict(info, address=hex(address), data=o.read(address, SIZES[kind]).hex(), handlers=handlers))
    snapshot['commands'] = commands
    original = audit.read()
    selected = {}
    for i, command in enumerate(commands): selected.setdefault(command['kind'], i)
    valid_force = next(i for i in range(1, 46) if ctx.u16(FORCES, i, 0x10))
    item = next(r[0][1] for r in snapshot['lists']['0x110']['items'] if r[0][1])
    common = [['worldstate', 0, 0x3A, '00']]
    fixtures = []
    def fixture(name, index, no_serving=True, command_pokes=None, pokes=None):
        p = []
        if no_serving:
            p = [[hex(OFFICERS), ctx.officer_by_id(pid), 0xE6, '06'] for pid in commands[index]['officers']]
        fixtures.append(dict(name=name, command=index, command_pokes=command_pokes or [],
            pokes=common + p + (pokes or [])))
    def cp(at, value, fmt='i'): return [at, struct.pack('<' + fmt, value).hex()]
    for kind, index in selected.items():
        fixture(f'no_serving_kind_{kind}', index,
            command_pokes=[cp(0x75, valid_force, 'B')] if kind in DIPLOMACY else [])
    ret = selected[15]
    fixture('normal_return', ret, no_serving=False)
    fixture('already_removed_flag', ret, command_pokes=[cp(0x54, 1)])
    fixture('negative_removed_flag', ret, command_pokes=[cp(0x54, -1)])
    strategic = selected[14]
    for value in [0, -1, 1234, 2147483647]:
        fixture(f'strategic_budget_{value}', strategic, command_pokes=[cp(0x78, value)])
    fixture('strategic_invalid_force', strategic, command_pokes=[cp(0x78, 1234)],
        pokes=[[hex(OFFICERS), ctx.officer_by_id(commands[strategic]['officers'][0]), 0xE0, '00']])
    diplomacy = selected[8]
    fixture('diplomacy_refund', diplomacy, command_pokes=[cp(0x75, valid_force, 'B'), cp(0x76, item, 'H'),
        cp(0x78, 12345), cp(0x7C, 23456)], pokes=[[hex(ITEMS), item, 0x4F, '08']])
    fixture('diplomacy_overflow', diplomacy, command_pokes=[cp(0x75, valid_force, 'B'), cp(0x76, 0, 'H'),
        cp(0x78, 2147483647), cp(0x7C, 2147483647)])
    fixture('diplomacy_invalid_force_no_item', diplomacy, command_pokes=[cp(0x75, 0, 'B'), cp(0x76, 0, 'H'),
        cp(0x78, 12345), cp(0x7C, 23456)])
    fixture('diplomacy_unowned_item', diplomacy, command_pokes=[cp(0x75, valid_force, 'B'), cp(0x76, item, 'H'),
        cp(0x78, 12345), cp(0x7C, 23456)], pokes=[[hex(ITEMS), item, 0x50, '02'], [hex(ITEMS), item, 0x4F, '08']])
    ruler = ctx.officer_by_id(ctx.u16(FORCES, valid_force, 0x10))
    fixture('diplomacy_invalid_ruler_base', diplomacy, command_pokes=[cp(0x75, valid_force, 'B'), cp(0x76, item, 'H'),
        cp(0x78, 12345), cp(0x7C, 23456)], pokes=[[hex(OFFICERS), ruler, 0xE2, '0000']])
    many = next(i for i, c in enumerate(commands) if len(c['officers']) >= 4 and c['kind'] == 1)
    fixture('ordered_repeated_return', many, command_pokes=[cp(0x50, -7)])
    ids = commands[many]['officers'].copy()
    ids[-1] = ids[0]
    fixtures[-1]['officer_ids'] = ids
    fixtures[-1]['pokes'] += [[hex(OFFICERS), ctx.officer_by_id(pid), 0xE6, bytes([status]).hex()]
                              for pid, status in zip(ids[:4], [1, 2, 3, 4])]
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names): raise ValueError('Unknown cancellation fixture')
    active, hooks = [None], []
    refs = {ctx.addr(t, i): [hex(t), i] for t, (_, _, count) in audit.tables.items() for i in range(count)}
    def trace(rva):
        def entry(mu, address, size, data):
            if active[0] is None: return
            pointer = mu.reg_read(UC_X86_REG_RCX)
            event = dict(rva=hex(rva), args=[refs.get(mu.reg_read(r), hex(mu.reg_read(r)))
                for r in [UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8]])
            active[0]['events'].append(event)
            if rva == 0x224670:
                active[0]['prefix'].append(audit.read())
                # Capture original ordered input IDs and serving records at the actual helper entry.
                index = struct.unpack('<I', o.read(o.q(pointer + 0x20), 4))[0]
                node = o.q(o.q(ctx.g.exe + world.CMD_POOL_RVA) + index * 8)
                serving = []
                while node:
                    pid = struct.unpack('<i', o.read(node, 4))[0]
                    p = ctx.officer_by_id(pid)
                    status = o.read(ctx.addr(OFFICERS, p) + 0xE6, 1)[0]
                    if 1 <= status <= 4: serving.append([hex(OFFICERS), p])
                    node = o.q(node + 8)
                active[0]['return_requests'].append(dict(command=hex(pointer), officers=serving,
                    notify=struct.unpack('<i', o.read(pointer + 0x50, 4))[0] != 0))
            if rva == 0x32A5E0:
                active[0]['removal'].append(dict(command=hex(pointer), immediate=mu.reg_read(UC_X86_REG_RDX),
                    data=o.read(pointer, active[0]['size']).hex()))
        return entry
    for rva in COVER:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, trace(rva), begin=ctx.g.exe + rva, end=ctx.g.exe + rva))
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation: o.stub(rva)
    cases, stats = [], collections.Counter()
    try:
        for f in fixtures:
            began = time.time()
            info = commands[f['command']]
            pointer, n = int(info['address'], 16), SIZES[info['kind']]
            undo = []
            if 'officer_ids' in f:
                # Replace payloads in existing integer-list nodes only; do not synthesize a list or vtable.
                index = struct.unpack('<I', o.read(o.q(pointer + 0x20), 4))[0]
                node = o.q(o.q(ctx.g.exe + world.CMD_POOL_RVA) + index * 8)
                for pid in f['officer_ids']:
                    if not node: raise RuntimeError('Officer ID replacement exceeds the native list')
                    undo.append((node, o.read(node, 4)))
                    o.poke(node, struct.pack('<i', pid))
                    node = o.q(node + 8)
                if node: raise RuntimeError('Officer ID replacement truncates the native list')
            for at, h in f['command_pokes']:
                b = bytes.fromhex(h)
                undo.append((pointer + at, o.read(pointer + at, len(b))))
                o.poke(pointer + at, b)
            try:
                def run():
                    before, after = audit.read(), []
                    frame = dict(events=[], prefix=[], return_requests=[], removal=[], size=n)
                    active[0] = frame
                    def inspect(): after.append(audit.read()); return []
                    try:
                        o.call(0x334780, pointer, read_after=inspect)
                        if len(frame['prefix']) != 1: raise RuntimeError('Expected exactly one native return request entry')
                        return before, after[0], frame
                    finally: active[0] = None
                value = ctx.run(f['pokes'], run)
                if value is None: raise RuntimeError(f'Cancellation failed: {f["name"]}: {ctx.failures[-1]}')
                before, after, frame = value
                prefix = audit.delta(before, frame['prefix'][0])
                cases.append(dict(fixture=f, prefix=prefix, after=audit.delta(before, after),
                    return_requests=frame['return_requests'], removal=frame['removal'], events=frame['events']))
                for e in frame['events']: stats[e['rva']] += 1
                stats['cases'] += 1
                print(f'  cancel {f["name"]}: prefix {len(prefix["records"])} records, removal {len(frame["removal"])}, '
                      f'return officers {len(frame["return_requests"][0]["officers"])} ({time.time()-began:.2f}s)', flush=True)
            finally:
                for a, b in reversed(undo): o.poke(a, b)
    finally:
        o.unstub()
        for h in hooks: o.mu.hook_del(h)
    if audit.read() != original: raise RuntimeError('Cancellation fixtures did not restore the original frozen input')
    if not ctx.frozen: require_unchanged_snapshot(guard_snapshot, initial_rng)
    ctx.save('command_cancel_world.json.gz', snapshot)
    ctx.save('command_cancel.json.gz', dict(world='command_cancel_world.json.gz', cases=cases, stats=dict(stats),
        presentation=[hex(r) for r in presentation], gameplay_stubs=[],
        scope='Refund prefix, ordered serving-officer return requests, removal flag and immediate-delete requests; full native bodies recorded. Return construction/A* and native command storage remain separate C# bodies.'))
    return f'command cancellation: {dict(stats)}'
