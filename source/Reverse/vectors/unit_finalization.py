# Execute the real World+0x70 finalizer after destruction/capture, and replay its native calls independently.
# Core tests cover the callback traversal and reset leaf; other engine effects remain native audit evidence.
import collections, copy, struct, time
from context import *
from destruction_consumer import ConsumerOracle
from engagement_tactic_sequence import PRESENTATION
from tactic_targets import CITIES
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX

COVER = [0x28E290, 0x282130, 0x226830, 0x32ADA0, 0x31ECC0]
AUX_TABLES = {0x7F8D8: ('troops', 0x40, 501), 0x80BA8: ('cautiontroops', 0x48, 501),
              0x81B50: ('cautionunit', 0x18, 1001)}


def capture_native_pools(ctx, extra_pools=()):
    # UI threads use the same pool while gameplay is paused. Capture each array in one read and require
    # a complete, disjoint active/free partition before executing any native allocator on the copy.
    o, g = ctx.o, ctx.g
    for rva, stride, next_at in [(0x27D10E0, 24, 8), (0x2144570, 64, 48), *extra_pools]:
        manager = g.exe + rva
        def validate(header, blocks):
            q = struct.unpack('<18Q', header)
            base, allocated, capacity = q[1], q[7], q[8]
            nodes, head_data, tail_data, count_data = blocks[base], blocks[q[2]], blocks[q[3]], blocks[q[5]]
            heads = struct.unpack(f'<{capacity}Q', head_data)
            tails = struct.unpack(f'<{capacity}Q', tail_data)
            counts = struct.unpack(f'<{capacity}Q', count_data)
            used = set()
            def node_at(p):
                if p < base or p >= base+allocated*stride or (p-base) % stride:
                    raise RuntimeError(f'Pool {rva:#x} node outside its native array: {p:#x}')
                return p-base
            for index, (head, tail, count) in enumerate(zip(heads, tails, counts)):
                node, previous, length = head, 0, 0
                while node:
                    if node in used: raise RuntimeError(f'Pool {rva:#x} active node shared/cyclic at index {index}')
                    used.add(node); offset = node_at(node)
                    if struct.unpack_from('<Q', nodes, offset+next_at+8)[0] != previous:
                        raise RuntimeError(f'Pool {rva:#x} active prev link differs at index {index}')
                    previous, node = node, struct.unpack_from('<Q', nodes, offset+next_at)[0]
                    length += 1
                if previous != tail or length != count:
                    raise RuntimeError(f'Pool {rva:#x} head/tail/count differs at index {index}')
            if len(used) != q[6]: raise RuntimeError(f'Pool {rva:#x} active total differs')
            active = len(used)
            node = q[4]
            while node:
                if node in used: raise RuntimeError(f'Pool {rva:#x} free node shared/cyclic: {node:#x}')
                used.add(node); node = struct.unpack_from('<Q', nodes, node_at(node)+next_at)[0]
            if len(used) != allocated: raise RuntimeError(f'Pool {rva:#x} node partition is incomplete')
            wrappers, wrapper_base, wrapper_count = blocks[q[10]], q[10], q[16]
            wrapper_used, node, previous = set(), q[11], 0
            def wrapper_at(p):
                if p < wrapper_base or p >= wrapper_base+wrapper_count*24 or (p-wrapper_base) % 24:
                    raise RuntimeError(f'Pool {rva:#x} wrapper outside its native array')
                return p-wrapper_base
            while node:
                if node in wrapper_used: raise RuntimeError(f'Pool {rva:#x} active wrapper cycle')
                wrapper_used.add(node); offset = wrapper_at(node)
                if struct.unpack_from('<Q', wrappers, offset+16)[0] != previous:
                    raise RuntimeError(f'Pool {rva:#x} wrapper prev link differs')
                previous, node = node, struct.unpack_from('<Q', wrappers, offset+8)[0]
            if previous != q[12] or len(wrapper_used) != q[15]:
                raise RuntimeError(f'Pool {rva:#x} wrapper tail/count differs')
            node = q[13]
            while node:
                if node in wrapper_used: raise RuntimeError(f'Pool {rva:#x} free wrapper shared/cyclic')
                wrapper_used.add(node); node = struct.unpack_from('<Q', wrappers, wrapper_at(node)+8)[0]
            if len(wrapper_used) != wrapper_count: raise RuntimeError(f'Pool {rva:#x} wrapper partition is incomplete')
            return active, allocated-active
        if ctx.frozen:
            header = o.read(manager, 0x90); q = struct.unpack('<18Q', header)
            blocks = {q[1]: o.read(q[1], q[7]*stride),
                      **{q[k]: o.read(q[k], q[8]*8) for k in [2, 3, 5]}}
            blocks[q[10]] = o.read(q[10], q[16]*24)
            active, free = validate(header, blocks)
        else:
            last_error = None
            for attempt in range(8):
                header = g.read(manager, 0x90); q = struct.unpack('<18Q', header)
                blocks = {q[1]: g.read(q[1], q[7]*stride),
                          **{q[k]: g.read(q[k], q[8]*8) for k in [2, 3, 5]}}
                # The wrapper allocator uses 24-byte nodes and is needed by 0x282130's temporary lists.
                blocks[q[10]] = g.read(q[10], q[16]*24)
                if g.read(manager, 0x90) != header:
                    last_error = 'manager changed during read'; continue
                try: active, free = validate(header, blocks)
                except RuntimeError as e:
                    last_error = str(e); continue
                for address, data in blocks.items(): o.poke(address, data)
                o.poke(manager, header)
                break
            else: raise RuntimeError(f'No consistent native pool snapshot: {last_error}')
        print(f'  pool {rva:#x}: complete partition, active {active}, free {free}', flush=True)
    from snapshot_guard import differences
    changed = differences(ctx.snap, world.snapshot(o))
    if changed: raise RuntimeError(f'Pool capture changed gameplay snapshot: {changed}')


def auxiliary_snapshot(ctx):
    # These three pointer-array limits and record types were checked in the native loops and RTTI.
    out = {}
    for table, (name, size, count) in AUX_TABLES.items():
        first = ctx.addr(table, 0)
        if any(ctx.addr(table, i) != first + size*i for i in range(count)):
            raise RuntimeError(f'{name} table is not contiguous')
        data = ctx.o.read(first, size*count)
        out[hex(table)] = {'name': name, 'size': size, 'address': hex(first), 'extra': {},
                          'records': [data[size*i:size*(i+1)].hex() for i in range(count)]}
    return out


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    o.insn_limit = 300_000_000
    capture_native_pools(ctx)
    auxiliary = auxiliary_snapshot(ctx)
    snapshot = copy.deepcopy(ctx.snap)
    snapshot['tables'].update(auxiliary)
    tables = {int(t, 16): (ctx.addr(int(t, 16), 0), data['size'], len(data['records']))
              for t, data in snapshot['tables'].items()}
    native = ConsumerOracle(ctx)
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation: o.stub(rva)
    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    force_of = lambda i: ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, i, 0x12)))
    unit = live[0]
    attacker = next(i for i in live if force_of(i) != force_of(unit))
    companion = next(i for i in live if i != unit and force_of(i) == force_of(unit))
    city = next(i for i in range(1, 52) if rec[CORPS][rec[CITIES][i][0x30]][0x10] not in [0, force_of(attacker), 46, 47, 48, 49, 50])
    ur, vr, ar, cr = [hex(UNITS), unit], [hex(UNITS), companion], [hex(UNITS), attacker], [hex(CITIES), city]
    request = lambda target: [target, None, ar, None, 0, 4, 0]
    fixtures = [([], []), ([request(ur)], []), ([request(ur), request(ur)], []),
                ([request(ur), request(vr)], []), ([[ur, None, None, cr, 1, 3, 0]], []),
                ([[None, cr, ar, None, 0, 4, 0]], []), ([], [None]), ([], [[hex(UNITS), 400]])]
    if ctx.snap['lists']['0x70']['items']: raise RuntimeError('Engine audit requires an initially empty pending list')
    pool = ctx.g.exe + 0x27D10E0
    heads, tails, counts = [o.q(pool + at) for at in [0x10, 0x18, 0x28]]
    bound = struct.unpack('<I', o.read(pool + 0x40, 4))[0]
    pending_index = struct.unpack('<I', o.read(o.q(ctx.raw + 0x70), 4))[0]
    unit_refs = {ctx.addr(UNITS, i): [hex(UNITS), i] for i in range(len(rec[UNITS]))}
    unit_ref = lambda p: unit_refs[p] if p else None
    def list_nodes(wrapper):
        if not wrapper: return []
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        if index >= bound: return []
        node, seen, out = o.q(heads + 8*index), set(), []
        while node:
            if node in seen: raise RuntimeError('General-list node cycle')
            seen.add(node); out.append((node, o.q(node))); node = o.q(node + 8)
        return out
    nested_wrappers = [(f'unit:{i}:{at:x}', ctx.addr(UNITS, i)+at)
                       for i in range(len(rec[UNITS])) for at in range(0x160, 0x1F0, 16)]
    nested_wrappers += [(f'caution:{i}', ctx.addr(0x80BA8, i)+0x30) for i in range(501)]
    nested_wrappers.append(('world:130', ctx.raw+0x130))
    ws = o.q(ctx.raw + world.WORLDSTATE_RAW)
    def unit_cache():
        # 0xA2000 erases a unit-pointer key from the manager's map at +0xB0.
        manager = o.q(ctx.g.exe + 0x1B9A4A0)
        header = o.q(manager + 0xB0)
        todo, seen, keys = [o.q(header + 8)], set(), []
        while todo:
            node = todo.pop()
            if o.read(node + 0x19, 1)[0]: continue
            if node in seen: raise RuntimeError('Unit cache tree cycle')
            seen.add(node); keys.append(hex(o.q(node + 0x20)))
            todo.extend([o.q(node), o.q(node + 0x10)])
        if len(keys) != o.q(manager + 0xB8): raise RuntimeError('Unit cache tree count differs')
        return sorted(keys)
    def state():
        records = {t: o.read(first, size*count) for t, (first, size, count) in tables.items()}
        lists = {hex(h): world.list_items(o, ctx.raw, ctx.g.exe, h, spec) for h, spec in world.LISTS.items() if h != 0x140}
        nested = {name: [hex(p) for _, p in list_nodes(o.q(address))] for name, address in nested_wrappers}
        flags = {name: o.read(address, 8 if how == 'set' else 1 if how.startswith('ptrbyte') else 4) if address else None
                 for name, (rva, how) in world.EXE_VALUES.items() if name in ctx.snap['flags']
                 for address in [world.exe_value_address(o, ctx.g.exe, rva, how)]}
        return (records, o.read(ws, world.WORLDSTATE_SIZE), lists, nested, unit_cache(), world.cmd_actions(o),
                o.read(ctx.g.exe + 0x1A47B90, 4), o.read(ctx.g.exe + world.SETTINGS_RVA, world.SETTINGS_SIZE), flags)
    def delta(before, after):
        changed = []
        for t, (_, size, count) in tables.items():
            if before[0][t] == after[0][t]: continue
            for i in range(count):
                data = after[0][t][size*i:size*(i+1)]
                if data != before[0][t][size*i:size*(i+1)]: changed.append([hex(t), i, data.hex()])
        return {'records': changed, 'worldstate': after[1].hex() if before[1] != after[1] else None,
                'lists': {h: x for h, x in after[2].items() if x != before[2][h]},
                'nested': {h: x for h, x in after[3].items() if x != before[3][h]},
                'unit_cache': after[4] if before[4] != after[4] else None,
                'cmdactions': after[5] if before[5] != after[5] else None,
                'rng': after[6].hex() if before[6] != after[6] else None,
                'settings': after[7].hex() if before[7] != after[7] else None,
                'flags': {k: v.hex() if v is not None else None for k, v in after[8].items() if v != before[8][k]}}
    active, before_final, last_frame, current_reset, clear_check = [None], [None], [None], [None], [None]
    calls, frames, reset_inputs, cleared_offsets, hooks = [], [], [], [], []
    def begin_final(mu, address, size, data):
        before_final[0] = state(); last_frame[0] = before_final[0]
        active[0] = 'finalizing'
    def entered(mu, address, size, data):
        if active[0] == 'finalizing': calls.append(unit_ref(mu.reg_read(UC_X86_REG_RCX)))
    def returned(mu, address, size, data):
        if active[0] != 'finalizing': return
        after = state(); frames.append(delta(last_frame[0], after)); last_frame[0] = after
    def reset_entry(mu, address, size, data):
        if active[0] != 'finalizing': return
        pointer = mu.reg_read(UC_X86_REG_RCX)
        current_reset[0] = [unit_ref(pointer), bool(o.q(pool+8)), o.read(pointer, 0x200).hex()]
        cleared_offsets.clear()
    def reset_return(mu, address, size, data):
        if active[0] != 'finalizing' or current_reset[0] is None: return
        r = current_reset[0]
        pointer = native.ptr(r[0])
        reset_inputs.append([*r, o.read(pointer, 0x200).hex(), list(cleared_offsets)])
        current_reset[0] = None
    def pool_clear(mu, address, size, data):
        if active[0] != 'finalizing' or mu.reg_read(UC_X86_REG_RCX) != pool: return
        index = mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF
        if current_reset[0] is not None:
            pointer = native.ptr(current_reset[0][0])
            matches = [at for at in range(0x160, 0x1F0, 16) if o.q(pointer+at)
                       and struct.unpack('<I', o.read(o.q(pointer+at), 4))[0] == index]
            if len(matches) != 1: raise RuntimeError('Unknown/aliased list cleared during native reset')
            cleared_offsets.append(matches[0])
        if index == pending_index:
            nodes = list_nodes(o.q(ctx.raw+0x70))
            if len(nodes) != o.q(counts+8*index): raise RuntimeError('Pending list count differs before clear')
            clear_check[0] = (nodes, o.q(pool+0x20), o.q(pool+0x30))
    for rva, callback in [(0x282130, entered), (0x28E318, returned), (0x226830, reset_entry),
                          (0x2824AE, reset_return), (0x18770, pool_clear)]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, callback, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    driver = o.alloc(0x10000)
    def emit(code, rva, args):
        for op, value in zip([b'\x48\xb9', b'\x48\xba', b'\x49\xb8', b'\x49\xb9'], args):
            code.extend(op + struct.pack('<Q', value))
        code.extend(b'\x48\xb8' + struct.pack('<Q', ctx.g.exe+rva) + b'\xff\xd0')
    def make_driver(seeds, sequence=None):
        code = bytearray(b'\x48\x83\xec\x38')
        for r in seeds: emit(code, 0x31ECC0, [ctx.raw, native.ptr(r)])
        emit(code, 0x44F060, [])
        marker = driver + len(code); returns = []
        if sequence is None: emit(code, 0x28E290, [])
        else:
            for r in sequence:
                emit(code, 0x282130, [native.ptr(r)]); returns.append(driver + len(code))
            emit(code, 0x18770, [pool, pending_index])
        code.extend(b'\x48\x83\xc4\x38\xc3')
        o.poke(driver, bytes(code)); o.mu.ctl_remove_cache(driver, driver+0x10000)
        return marker, returns
    def run(requests):
        native.prepare(requests); native.initial_count = len(requests)
        calls.clear(); frames.clear(); reset_inputs.clear(); cleared_offsets.clear()
        active[0] = None; current_reset[0] = None; clear_check[0] = None
        initial = state(); result = []
        def inspect():
            native.inspect_clear()
            if clear_check[0] is None: raise RuntimeError('Pending-list clear was not reached')
            nodes, free, total = clear_check[0]
            if [o.q(a+8*pending_index) for a in [heads, tails, counts]] != [0, 0, 0]:
                raise RuntimeError('Native pending-list storage was not cleared')
            if o.q(pool+0x30) != total-len(nodes): raise RuntimeError('Native pending-list total count differs')
            if nodes and (o.q(pool+0x20) != nodes[0][0] or o.q(nodes[-1][0]+8) != free):
                raise RuntimeError('Native pending-list nodes were not freed')
            after = state()
            result.append((before_final[0], after, list(calls), list(frames), list(reset_inputs), delta(initial, after)))
            return []
        try:
            o.call(driver-ctx.g.exe, read_after=inspect)
            return result[0]
        finally: active[0] = None; native.restore()
    cases, stats = [], collections.Counter()
    try:
        for n, (requests, seeds) in enumerate(fixtures):
            p = [['worldstate', 0, 0x3A, '00'], ['worldstate', 0, 0x165C, '00'], ['worldstate', 0, 0x165D, '00'],
                 ['exe', 'challenge_active', 0, '00000000']]
            started = time.time()
            marker, _ = make_driver(seeds)
            marker_hook = o.mu.hook_add(UC_HOOK_CODE, begin_final, begin=marker, end=marker)
            try: full = ctx.run(p, lambda: run(requests))
            finally: o.mu.hook_del(marker_hook)
            if full is None: raise RuntimeError(f'Full native finalizer failed at case {n}: {ctx.failures[-1]}')
            pending = [row[0] for row in full[0][2]['0x70']]
            if full[2] != pending: raise RuntimeError('Real finalizer callback order differs from the pending list')
            marker, returns = make_driver(seeds, pending)
            replay_hooks = [o.mu.hook_add(UC_HOOK_CODE, begin_final, begin=marker, end=marker)]
            replay_hooks += [o.mu.hook_add(UC_HOOK_CODE, returned, begin=a, end=a) for a in returns]
            try: direct = ctx.run(p, lambda: run(requests))
            finally:
                for hook in replay_hooks: o.mu.hook_del(hook)
            if direct is None: raise RuntimeError(f'Direct native finalizers failed at case {n}: {ctx.failures[-1]}')
            if direct != full:
                ctx.save('unit_finalization_mismatch.json.gz', {'case': n, 'full': full[5], 'direct': direct[5],
                    'full_calls': full[2], 'direct_calls': direct[2], 'full_frames': full[3], 'direct_frames': direct[3]})
                raise RuntimeError(f'Full/direct native finalization differs at case {n}')
            if len(full[2]) != len(full[3]): raise RuntimeError('A finalizer return frame was not captured')
            for r in pending:
                if r is None: continue
                t, i = int(r[0], 16), r[1]
                first, size, _ = tables[t]
                after = full[1][0][t][size*i:size*(i+1)]
                if after[0x10] or struct.unpack_from('<H', after, 0x12)[0]: raise RuntimeError('Finalized unit remained valid')
                if [r] in full[1][2]['0x60']: raise RuntimeError('Finalized unit remained in the active list')
            stats['cases'] += 1; stats['requests'] += len(requests); stats['finalizers'] += len(full[2])
            stats['resets'] += len(full[4]); stats['null_finalizers'] += full[2].count(None)
            for t, i, data in full[5]['records']: stats[f'records_{t}'] += 1
            cases.append({'requests': requests, 'seeds': seeds, 'pokes': p, 'pending': pending,
                'calls': full[2], 'frames': full[3], 'resets': full[4], 'after': full[5],
                'before_finalization': {'units': [[r, full[0][0][UNITS][0x200*r[1]:0x200*(r[1]+1)].hex()] for r in pending if r is not None],
                                        'active_units': full[0][2]['0x60']}})
            print(f'  real finalization {n+1}/{len(fixtures)} equals native sequence ({time.time()-started:.1f}s): {dict(stats)}', flush=True)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    if not ctx.frozen:
        # Guard the additional engine tables as well as the runner's standard snapshot.
        from snapshot_guard import differences
        end = Context()
        changed = differences(ctx.snap, end.snap)
        if changed or auxiliary_snapshot(end) != auxiliary:
            raise RuntimeError(f'Live engine snapshot changed during finalization: {changed}')
        print('snapshot: standard and three auxiliary tables remained unchanged', flush=True)
    ctx.save('unit_finalization_world.json.gz', snapshot)
    ctx.save('unit_finalization.json.gz', {'world': 'unit_finalization_world.json.gz', 'cases': cases, 'stats': dict(stats),
        'native_finalizer': True, 'direct_native_match': True, 'presentation': [hex(r) for r in presentation],
        'scope': 'All 29 table bytes, world state, captured lists, nine lists per unit, caution lists and World+0x130, unit-cache keys, command kinds/officer lists, RNG, settings and captured flags; other engine effects are not ported'})
    return f'real unit finalization equals sequential native finalizers: {dict(stats)}'
