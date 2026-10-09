# Audit native group dissolution, record reset and pointer-keyed cache removal on copied memory only.
import collections, copy, struct, time
from context import *
from unit_finalization import auxiliary_snapshot, capture_native_pools
from engagement_tactic_sequence import PRESENTATION
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_RAX

TROOPS = 0x7F8D8
COVER = [0x282540, 0x241C90, 0x227D80, 0xA2000, 0xA22A0, 0xD4B80, 0xD5170, 0x3078E0]


def cache_tree(read, q, manager, at):
    # The native maps share the std::map node layout and black sentinel. Validate storage after every call.
    head, count = q(manager+at), q(manager+at+8)
    blocks, rows, seen = {}, [], set()
    def node(p):
        if p not in blocks:
            blocks[p] = read(p, 0x30)
            if blocks[p] is None: raise RuntimeError(f'Unreadable cache node {p:#x}')
        return blocks[p]
    h = node(head)
    left, root, right = struct.unpack_from('<QQQ', h)
    if h[0x18:0x1A] != b'\1\1': raise RuntimeError('Invalid cache sentinel')
    def visit(p, parent, low=-1, high=2**64):
        if p == head: return 1
        if p in seen: raise RuntimeError('Cache tree cycle/shared node')
        seen.add(p)
        b = node(p)
        l, pa, r = struct.unpack_from('<QQQ', b)
        key, value = struct.unpack_from('<QQ', b, 0x20)
        if b[0x19] or b[0x18] not in [0, 1] or pa != parent or not low < key < high:
            raise RuntimeError('Cache parent/key/color differs')
        a = visit(l, p, low, key)
        rows.append((p, key, value))
        z = visit(r, p, key, high)
        if a != z: raise RuntimeError('Cache black height differs')
        if b[0x18] == 0 and (node(l)[0x18] == 0 or node(r)[0x18] == 0):
            raise RuntimeError('Cache red node has a red child')
        return a + int(b[0x18] == 1)
    visit(root, head)
    if root != head and node(root)[0x18] != 1: raise RuntimeError('Cache root is red')
    if len(rows) != count or (rows and (rows[0][0] != left or rows[-1][0] != right)) or (not rows and (left != head or right != head)):
        raise RuntimeError('Cache count/extrema differ')
    return rows, blocks


def capture_native_caches(ctx):
    o, g = ctx.o, ctx.g
    manager = g.q(g.exe+0x1B9A4A0) if not ctx.frozen else o.q(g.exe+0x1B9A4A0)
    if not manager: raise RuntimeError('Cache manager is unavailable')
    for at in [0xA0, 0xB0]:
        if ctx.frozen:
            rows, blocks = cache_tree(o.read, o.q, manager, at)
        else:
            for attempt in range(8):
                pair = g.read(manager+at, 16)
                try:
                    rows, blocks = cache_tree(g.read, g.q, manager, at)
                    if g.q(g.exe+0x1B9A4A0) != manager or g.read(manager+at, 16) != pair or any(g.read(p, len(b)) != b for p, b in blocks.items()):
                        raise RuntimeError('Cache changed during capture')
                except RuntimeError as error:
                    print(f'  cache capture retry {at:#x}: {error}', flush=True)
                    continue
                for p, b in blocks.items(): o.poke(p, b)
                o.poke(manager+at, pair)
                break
            else: raise RuntimeError('No coherent cache snapshot')
        print(f'  cache {at:#x}: {len(rows)} keys, parent/order/color/black height/count/extrema verified', flush=True)
    return manager


def generate(ctx):
    o = ctx.o
    capture_native_pools(ctx)
    auxiliary = auxiliary_snapshot(ctx)
    snapshot = copy.deepcopy(ctx.snap)
    snapshot['tables'].update(auxiliary)
    tables = {int(t, 16): (ctx.addr(int(t, 16), 0), data['size'], len(data['records']))
              for t, data in snapshot['tables'].items()}
    baseline = {t: bytes.fromhex(''.join(snapshot['tables'][hex(t)]['records'])) for t in tables}
    manager = capture_native_caches(ctx)
    pool = ctx.g.exe+0x27D10E0
    heads, tails, counts = [o.q(pool+at) for at in [0x10, 0x18, 0x28]]
    capacity = struct.unpack('<I', o.read(pool+0x40, 4))[0]
    unit_ptrs = {ctx.addr(UNITS, i): i for i in range(501)}
    troop_ptrs = {ctx.addr(TROOPS, i): i for i in range(501)}
    def list_values(wrapper):
        if not wrapper: return []
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        if index >= capacity: return []
        node, previous, seen, out = o.q(heads+index*8), 0, set(), []
        while node:
            if node in seen or o.q(node+16) != previous: raise RuntimeError('Unit list links differ')
            seen.add(node); out.append(unit_ptrs[o.q(node)])
            previous, node = node, o.q(node+8)
        if previous != o.q(tails+index*8) or len(out) != o.q(counts+index*8): raise RuntimeError('Unit list storage differs')
        return out
    def records(): return {t: o.read(first, size*count) for t, (first, size, count) in tables.items()}
    def delta(before, after):
        out = []
        for t, (_, size, count) in tables.items():
            if before[t] == after[t]: continue
            for i in range(count):
                b = after[t][i*size:(i+1)*size]
                if b != before[t][i*size:(i+1)*size]: out.append([hex(t), i, b.hex()])
        return out
    def caches():
        return {name: [[hex(k), hex(v)] for _, k, v in cache_tree(o.read, o.q, manager, at)[0]]
                for name, at in [('troop', 0xA0), ('unit', 0xB0)]}
    def state(): return {'records': records(), 'caches': caches(),
                         'active': list_values(o.q(ctx.raw+0x60)), 'pending': list_values(o.q(ctx.raw+0x70))}
    native_cache = caches()
    ui_owners = {v: hex(o.q(int(v, 16)+8)) for k, v in native_cache['troop']}
    unit_keys = [int(k, 16) for k, v in native_cache['unit']]
    cached_groups = [troop_ptrs[int(k, 16)] for k, v in native_cache['troop']]
    if not cached_groups or not unit_keys: raise RuntimeError('Cache fixtures require actual native entries')
    group = cached_groups[0]
    group_members = [i for i in list_values(o.q(ctx.raw+0x60)) if ctx.u16(UNITS, i, 0x58) == group]
    valid_units = [i for i in range(1, 501) if ctx.rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12) and
                   1 <= ctx.rec[OFFICERS][ctx.officer_by_id(ctx.u16(UNITS, i, 0x12))][0xE0] <= 51]
    chosen = valid_units[:3]
    if len(chosen) != 3 or not group_members: raise RuntimeError('Group fixtures require three valid officers and a cached member')
    fixtures = []
    def fixture(name, kind, target=None, pokes=None, active=None, pending=None, **kw):
        fixtures.append(dict(name=name, kind=kind, target=target, pokes=pokes or [], active=active, pending=pending, **kw))
    def word(t, i, at, n): return [hex(t), i, at, struct.pack('<H', n).hex()]
    fixture('dissolve_null', 'dissolve')
    fixture('dissolve_zero', 'dissolve', 0)
    fixture('dissolve_empty_uncached_500', 'dissolve', 500, active=[])
    fixture('dissolve_cached_no_active_members', 'dissolve', group, active=[])
    for i in cached_groups: fixture(f'dissolve_snapshot_{i}', 'dissolve', i)
    def controlled(name, kind='dissolve', target=group, ids=None, active=None, pending=None, assigned=0):
        ids = [ctx.u16(UNITS, i, 0x12) for i in chosen] if ids is None else ids
        p = [word(UNITS, i, 0x58, group) for i in chosen]
        p += [word(UNITS, i, 0x12, oid) for i, oid in zip(chosen, ids)]
        fixture(name, kind, target, p, chosen if active is None else active, [] if pending is None else pending, assigned=assigned)
    controlled('dissolve_three_valid')
    controlled('dissolve_three_invalid', ids=[0, 0, 0])
    controlled('dissolve_invalid_then_valid', ids=[0, ctx.u16(UNITS, chosen[1], 0x12), 0])
    controlled('dissolve_invalid_tail', ids=[ctx.u16(UNITS, chosen[0], 0x12), 0, 0])
    controlled('dissolve_duplicate_active', active=[chosen[0], chosen[0], chosen[1], chosen[2]])
    controlled('dissolve_all_pending', pending=chosen)
    controlled('dissolve_middle_pending', pending=[chosen[1]])
    controlled('dissolve_invalid_unit_with_valid_officer', active=[400, chosen[1]], ids=[])
    fixtures[-1]['pokes'] += [word(UNITS, 400, 0x58, group), word(UNITS, 400, 0x12, ctx.u16(UNITS, chosen[0], 0x12))]
    for assigned in [-2147483648, -1, 0, group, 499, 500, 501, 65535, 2147483647]:
        controlled(f'set_valid_remaining_{assigned}', 'set', chosen[0], assigned=assigned)
        controlled(f'set_invalid_remaining_{assigned}', 'set', chosen[0], ids=[ctx.u16(UNITS, chosen[0], 0x12), 0, 0], assigned=assigned)
    for old in [0, 500, 501, 65535]:
        fixture(f'set_old_{old}', 'set', chosen[0], [word(UNITS, chosen[0], 0x58, old)], active=[chosen[0]], pending=[], assigned=0)
    ui = int(native_cache['troop'][0][1], 16)
    other_ui = int(native_cache['troop'][1][1], 16)
    for name, association, orphan in [('mapped', ui, False), ('mapped_pointer_zero', 0, False),
                                      ('mapped_pointer_differs', other_ui, False), ('orphan', ui, True),
                                      ('absent', 0, True)]:
        fixture(f'troop_cache_{name}', 'troop_cache', group,
                [[hex(TROOPS), group, 0x30, struct.pack('<Q', association).hex()]], orphan=orphan)
    fixture('troop_cache_uncached_empty', 'troop_cache', 500)
    for name, order in [('ascending', cached_groups), ('descending', list(reversed(cached_groups))),
                        ('alternating', cached_groups[::2]+cached_groups[1::2])]:
        fixture(f'troop_cache_all_{name}', 'troop_cache', targets=order)
    for i in [0, group, 500]:
        for enabled in [False, True]:
            # Every padding byte is nonzero; retain a real association only for its owning group.
            b = bytearray((i*17+at*29+7) % 256 or 0xA9 for at in range(8, 0x40))
            b[0x30-8:0x38-8] = struct.pack('<Q', ui if i == group else 0)
            fixture(f'reset_troop_{i}_{enabled}', 'reset', i, [[hex(TROOPS), i, 8, b.hex()]], enabled=enabled)
    for key in [0, ctx.addr(UNITS, 0), ctx.addr(UNITS, 500), unit_keys[0]+1, 2**63, 2**64-1, *unit_keys]:
        fixture(f'unit_cache_{key:x}', 'unit_cache', keys=[key])
    fixture('unit_cache_repeat', 'unit_cache', keys=[unit_keys[0], unit_keys[0], 0, unit_keys[0]])
    fixture('unit_cache_all_ascending', 'unit_cache', keys=unit_keys)
    fixture('unit_cache_all_descending', 'unit_cache', keys=list(reversed(unit_keys)))
    fixture('unit_cache_all_alternating', 'unit_cache', keys=unit_keys[::2]+unit_keys[1::2])
    # Isolated cache calls also traverse an empty tree after real native erasure, without fabricating nodes.
    fixture('unit_cache_empty', 'unit_cache', keys=[0, unit_keys[0], 2**64-1], empty_units=True)
    driver, key_slot = o.alloc(0x10000), o.alloc(8)
    def emit(code, rva, args):
        for op, value in zip([b'\x48\xb9', b'\x48\xba', b'\x49\xb8', b'\x49\xb9'], args):
            code.extend(op+struct.pack('<Q', value & (2**64-1)))
        code.extend(b'\x48\xb8'+struct.pack('<Q', ctx.g.exe+rva)+b'\xff\xd0')
    def replace_list(code, wrapper, values):
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        emit(code, 0x18770, [pool, index])
        for i in values:
            emit(code, 0x18DF0, [pool, index, 1])
            code.extend(b'\x48\xba'+struct.pack('<Q', ctx.addr(UNITS, i))+b'\x48\x89\x10')
    before, after, trace, checkpoints, ui_checkpoints, armed = [], [], [], [], [], [False]
    def begin(mu, address, size, data): armed[0] = True; before.append(state())
    def record_call(mu, address, size, data):
        if not armed[0]: return
        rva, rcx, rdx = address-ctx.g.exe, mu.reg_read(UC_X86_REG_RCX), mu.reg_read(UC_X86_REG_RDX)
        if rva == 0x282540: trace.append(['dissolve', troop_ptrs.get(rcx)])
        elif rva == 0x241C90: trace.append(['set', unit_ptrs[rcx], rdx & 0xFFFFFFFF])
        elif rva == 0x227D80: trace.append(['reset', troop_ptrs[rcx]])
        elif rva == 0xA22A0:
            trace.append(['troop_cache', troop_ptrs[rdx]])
            checkpoints.append({'troop': troop_ptrs[rdx], 'records': delta(before[0]['records'], records()), 'caches': caches()})
        elif rva in [0xD37B0, 0xB27610]:
            if rva == 0xD37B0: trace.append(['properties', hex(rdx), mu.reg_read(UC_X86_REG_R8)])
            else: trace.append(['destroy', hex(o.q(rdx)), hex(rcx)])
            ui_checkpoints.append({'records': delta(before[0]['records'], records()), 'caches': caches()})
        elif rva == 0xA2000: trace.append(['unit_cache', hex(rdx)])
        elif (rva == 0xD4B80 and rcx == manager+0xB0) or (rva == 0xD5170 and rcx == manager+0xA0):
            trace.append(['erase', 'unit' if rva == 0xD4B80 else 'troop', hex(o.q(rdx))])
    begin_marker = driver+0xF000
    o.poke(begin_marker, b'\xc3')
    hooks = [o.mu.hook_add(UC_HOOK_CODE, begin, begin=begin_marker, end=begin_marker)]
    hooks += [o.mu.hook_add(UC_HOOK_CODE, record_call, begin=ctx.g.exe+r, end=ctx.g.exe+r)
              for r in [0x282540, 0x241C90, 0x227D80, 0xA22A0, 0xD37B0, 0xB27610, 0xA2000, 0xD4B80, 0xD5170]]
    presentation = [*PRESENTATION, 0x1B4990]
    for r in presentation: o.stub(r)
    cases, stats = [], collections.Counter()
    try:
        for n, f in enumerate(fixtures):
            for values in [before, after, trace, checkpoints, ui_checkpoints]: values.clear()
            armed[0] = False
            code = bytearray(b'\x48\x83\xec\x28')
            for at, values in [(0x60, f['active']), (0x70, f['pending'])]:
                if values is not None: replace_list(code, o.q(ctx.raw+at), values)
            if f.get('orphan'):
                o.poke(key_slot, struct.pack('<Q', ctx.addr(TROOPS, f['target'])))
                emit(code, 0xD5170, [manager+0xA0, key_slot])
            if f.get('empty_units'): emit(code, 0xD47C0, [manager+0xB0])
            if f['kind'] == 'reset' and not f['enabled']:
                code.extend(b'\x48\xb8'+struct.pack('<Q', ctx.g.exe+0x1B9A4A0)+b'\x48\xc7\x00\0\0\0\0')
            emit(code, begin_marker-ctx.g.exe, [])
            target = 0 if f['target'] is None else ctx.addr(UNITS if f['kind'] == 'set' else TROOPS, f['target'])
            kind = f['kind']
            if kind == 'dissolve': emit(code, 0x282540, [target])
            elif kind == 'set': emit(code, 0x241C90, [target, f['assigned']])
            elif kind == 'reset': emit(code, 0x227D80, [target])
            elif kind == 'troop_cache':
                for i in f.get('targets', [f['target']]): emit(code, 0xA22A0, [manager, ctx.addr(TROOPS, i)])
            elif kind == 'unit_cache':
                for key in f['keys']: emit(code, 0xA2000, [manager, key])
            code.extend(b'\x48\x83\xc4\x28\xc3')
            o.poke(driver, bytes(code)); o.mu.ctl_remove_cache(driver, driver+0x10000)
            def inspect(): after.append(state()); return []
            started = time.time()
            result = ctx.run(f['pokes'], lambda: o.call(driver-ctx.g.exe, read_after=inspect))
            if result is None: raise RuntimeError(f"Native cache case {f['name']} failed: {ctx.failures[-1]}")
            if len(before) != 1 or len(after) != 1: raise RuntimeError('Missing cache checkpoint')
            b, a = before[0], after[0]
            changes = delta(b['records'], a['records'])
            if any(int(t, 16) not in [UNITS, TROOPS] for t, i, v in changes): raise RuntimeError(f'Unexpected native table writes: {changes}')
            if b['active'] != a['active'] or b['pending'] != a['pending']: raise RuntimeError('Cache/group operation changed unit lists')
            releases = sum(row[0] == 'properties' for row in trace)
            destroys = sum(row[0] == 'destroy' for row in trace)
            if releases != destroys: raise RuntimeError('Native property/destruction callback count differs')
            cases.append({'name': f['name'], 'kind': kind, 'target': f['target'], 'assigned': f.get('assigned', 0),
                          'keys': [hex(k) for k in f.get('keys', [])], 'manager_available': f.get('enabled', True),
                          'inputs': delta(baseline, b['records']), 'active': b['active'], 'pending': b['pending'],
                          'caches_before': b['caches'], 'records_after': changes, 'caches_after': a['caches'],
                          'trace': list(trace), 'cache_checkpoints': list(checkpoints), 'ui_checkpoints': list(ui_checkpoints),
                          'targets': f.get('targets', [f['target']]) if kind == 'troop_cache' else [],
                          'return': bool(result & 0xFFFFFFFF) if kind == 'dissolve' else None})
            stats['cases'] += 1; stats[kind] += 1; stats['ui_releases'] += releases
            stats['recursive_dissolves'] += max(0, sum(row[0] == 'dissolve' for row in trace)-int(kind == 'dissolve'))
            stats['unit_erases'] += sum(row[:2] == ['erase', 'unit'] for row in trace)
            stats['troop_erases'] += sum(row[:2] == ['erase', 'troop'] for row in trace)
            print(f"  cache {n+1}/{len(fixtures)} {f['name']} ({time.time()-started:.2f}s): "
                  f"records={len(changes)}, group calls={[row[1] for row in trace if row[0] == 'troop_cache']}, "
                  f"UI={releases}, caches={len(b['caches']['troop'])}/{len(b['caches']['unit'])}->"
                  f"{len(a['caches']['troop'])}/{len(a['caches']['unit'])}", flush=True)
    finally:
        for h in hooks: o.mu.hook_del(h)
        o.unstub()
    if not ctx.frozen:
        from snapshot_guard import differences
        end = Context()
        changed = differences(ctx.snap, end.snap)
        if changed or auxiliary_snapshot(end) != auxiliary: raise RuntimeError(f'Live cache snapshot changed: {changed}')
        end_manager = end.o.q(end.g.exe+0x1B9A4A0)
        end_caches = {name: [[hex(k), hex(v)] for _, k, v in cache_tree(end.g.read, end.g.q, end_manager, at)[0]]
                      for name, at in [('troop', 0xA0), ('unit', 0xB0)]}
        if end_caches != native_cache: raise RuntimeError('Live cache keys/values changed during oracle execution')
    ctx.save('unit_group_cache_world.json.gz', snapshot)
    ctx.save('unit_group_cache.json.gz', {'world': 'unit_group_cache_world.json.gz', 'cases': cases, 'stats': dict(stats), 'ui_owners': ui_owners,
             'presentation': [hex(r) for r in presentation], 'native_cache_and_ui': True,
             'coverage': {hex(r): [hit, total] for r, (hit, total, missed) in o.coverage().items()},
             'scope': 'Real 0x282540/0x241C90/0x227D80/0xA2000/0xA22A0, including native UI cleanup/destruction and tree erase; 29 complete tables and validated cache storage. C# preserves UI callbacks as an external engine boundary.'})
    return f'group dissolution and cache removal: {dict(stats)}'
