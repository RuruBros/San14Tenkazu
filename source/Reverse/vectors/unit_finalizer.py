# Replay the whole native finalizer and pending consumer; every fixture writes only oracle copies.
import collections, copy, struct, time
from unit_group_cache import *
from unicorn.x86_const import UC_X86_REG_RIP

COVER = [0x282130, 0x28E290, 0x2288B0, 0x32ADA0, 0x22A240, 0x226830, 0x282540]
FOCUS = [UNITS, TROOPS, 0x80BA8, 0x81B50]


def validate_pool(o, pool):
    # Validate the complete native active/free partitions, not only lists touched by a fixture.
    q = struct.unpack('<18Q', o.read(pool, 0x90))
    base, allocated, capacity = q[1], q[7], q[8]
    data = o.read(base, allocated*24)
    heads, tails, counts = [struct.unpack(f'<{capacity}Q', o.read(q[k], capacity*8)) for k in [2, 3, 5]]
    seen = set()
    def offset(p, first, count):
        if not first <= p < first+count*24 or (p-first) % 24: raise RuntimeError('Pool node outside native array')
        return p-first
    for head, tail, count in zip(heads, tails, counts):
        p, previous, n = head, 0, 0
        while p:
            if p in seen: raise RuntimeError('Pool active node shared/cyclic')
            seen.add(p); at = offset(p, base, allocated)
            if struct.unpack_from('<Q', data, at+16)[0] != previous: raise RuntimeError('Pool previous link differs')
            previous, p = p, struct.unpack_from('<Q', data, at+8)[0]; n += 1
        if previous != tail or n != count: raise RuntimeError('Pool head/tail/count differs')
    if len(seen) != q[6]: raise RuntimeError('Pool active total differs')
    p = q[4]
    while p:
        if p in seen: raise RuntimeError('Pool free node shared/cyclic')
        seen.add(p); p = struct.unpack_from('<Q', data, offset(p, base, allocated)+8)[0]
    if len(seen) != allocated: raise RuntimeError('Pool node partition incomplete')
    base, allocated = q[10], q[16]
    data = o.read(base, allocated*24); seen.clear(); p, previous = q[11], 0
    while p:
        if p in seen: raise RuntimeError('Pool wrapper cycle')
        seen.add(p); at = offset(p, base, allocated)
        if struct.unpack_from('<Q', data, at+16)[0] != previous: raise RuntimeError('Pool wrapper previous link differs')
        previous, p = p, struct.unpack_from('<Q', data, at+8)[0]
    if previous != q[12] or len(seen) != q[15]: raise RuntimeError('Pool wrapper tail/count differs')
    p = q[13]
    while p:
        if p in seen: raise RuntimeError('Pool free wrapper shared/cyclic')
        seen.add(p); p = struct.unpack_from('<Q', data, offset(p, base, allocated)+8)[0]
    if len(seen) != allocated: raise RuntimeError('Pool wrapper partition incomplete')


def generate(ctx):
    o = ctx.o; o.insn_limit = 300_000_000
    capture_native_pools(ctx)
    auxiliary = auxiliary_snapshot(ctx)
    manager = capture_native_caches(ctx)
    snapshot = copy.deepcopy(ctx.snap); snapshot['tables'].update(auxiliary)
    tables = {int(t, 16): (ctx.addr(int(t, 16), 0), d['size'], len(d['records'])) for t, d in snapshot['tables'].items()}
    baseline = {t: bytes.fromhex(''.join(snapshot['tables'][hex(t)]['records'])) for t in tables}
    pool = ctx.g.exe+0x27D10E0
    heads, counts = o.q(pool+0x10), o.q(pool+0x28)
    capacity = struct.unpack('<I', o.read(pool+0x40, 4))[0]
    refs = {ctx.addr(UNITS, i): i for i in range(501)}
    troops = {ctx.addr(TROOPS, i): i for i in range(501)}
    alerts = {ctx.addr(0x81B50, i): i for i in range(1001)}
    def ref(p, lookup=refs): return None if not p else lookup[p]
    def nodes(wrapper):
        if not wrapper: return None
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        if index >= capacity: return None
        out, seen, p = [], set(), o.q(heads+index*8)
        while p:
            if p in seen: raise RuntimeError('List cycle')
            seen.add(p); out.append((p, o.q(p))); p = o.q(p+8)
        if len(out) != o.q(counts+index*8): raise RuntimeError('List count differs')
        return out
    def members(wrapper, lookup=refs):
        ns = nodes(wrapper)
        return None if ns is None else [ref(p, lookup) for n, p in ns]
    def unit_lists(indices):
        return {f'{i}:{at:x}': None if (ns := nodes(o.q(ctx.addr(UNITS, i)+at))) is None else [hex(p) for n, p in ns]
                for i in indices for at in range(0x160, 0x1F0, 16)}
    def records(focus=FOCUS): return {t: o.read(tables[t][0], tables[t][1]*tables[t][2]) for t in focus}
    def delta(a, b):
        out = []
        for t, data in b.items():
            if a[t] == data: continue
            size = tables[t][1]
            out += [[hex(t), i, data[i*size:(i+1)*size].hex()] for i in range(tables[t][2])
                    if a[t][i*size:(i+1)*size] != data[i*size:(i+1)*size]]
        return out
    def caches(): return {name: [[hex(k), hex(v)] for n, k, v in cache_tree(o.read, o.q, manager, at)[0]]
                          for name, at in [('unit', 0xB0), ('troop', 0xA0)]}
    native_cache = caches()
    owners = {v: hex(o.q(int(v, 16)+8)) for k, v in native_cache['troop']}
    live = [i for i in range(1, 501) if ctx.rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    leader, peer, other = live[:3]; group = ctx.u16(UNITS, leader, 0x58)
    watched = [0, *live, 400]
    all_lists = unit_lists(range(501))
    def state():
        return {'records': records(), 'active': members(o.q(ctx.raw+0x60)), 'pending': members(o.q(ctx.raw+0x70)),
                'cautions': members(o.q(ctx.raw+0x130), alerts),
                'caution_troops': [members(o.q(ctx.addr(0x80BA8, i)+0x30)) for i in range(501)],
                'unit_lists': unit_lists(watched), 'caches': caches()}
    def packed(s, b): return {**s, 'records': delta(b['records'], s['records'])}
    listener_slot = ctx.g.exe+0x1B91DC0
    listener = o.q(listener_slot)
    listener_fn = o.q(o.q(listener)+0x150) if listener else 0
    fixtures = []
    def fixture(name, kind='unit', target=leader, pokes=None, active=None, pending=None, **kw):
        fixtures.append(dict(name=name, kind=kind, target=target, pokes=pokes or [], active=active, pending=pending, **kw))
    def word(t, i, at, value): return [hex(t), i, at, struct.pack('<H', value).hex()]
    fixture('null', target=None)
    fixture('null_with_null_active', target=None, active=[None, peer])
    fixture('entry_zero', target=0)
    fixture('invalid_slot', target=400)
    for i in live: fixture(f'snapshot_{i}', target=i)
    fixture('listener_absent', no_listener=True)
    fixture('ui_association_absent', pokes=[[hex(UNITS), leader, 0x148, bytes(8).hex()]])
    def controlled(name, ids=None, pending=None, active=None, target=leader, **kw):
        ids = [ctx.u16(UNITS, i, 0x12) for i in [leader, peer, other]] if ids is None else ids
        p = [word(UNITS, i, 0x58, group) for i in [leader, peer, other]]
        p += [word(UNITS, i, 0x12, oid) for i, oid in zip([leader, peer, other], ids)]
        p += [word(TROOPS, group, 0x2C, ids[0]), [hex(TROOPS), group, 0x2E, 'bcd3']]
        fixture(name, target=target, pokes=p, active=[leader, peer, other] if active is None else active,
                pending=[leader] if pending is None else pending, **kw)
    controlled('commander_replaced')
    controlled('all_same_officer', ids=[ctx.u16(UNITS, leader, 0x12)]*3)
    controlled('first_remaining_invalid', ids=[ctx.u16(UNITS, leader, 0x12), 0, ctx.u16(UNITS, other, 0x12)])
    controlled('candidate_6000', ids=[ctx.u16(UNITS, leader, 0x12), ctx.u16(UNITS, peer, 0x12), 6000], pending=[leader, peer])
    controlled('first_candidate_same_pointer_then_6000', ids=[ctx.u16(UNITS, leader, 0x12)]*2+[6000])
    controlled('all_pending', pending=[leader, peer, other])
    controlled('duplicate_active_pointer', active=[leader, peer, leader, other, leader])
    controlled('target_not_active', active=[peer, other])
    controlled('wrapper_exhaustion', target=400, exhaust=True)
    fixtures[-1]['pokes'] += [word(UNITS, 400, 0x58, group), word(UNITS, 400, 0x12, ctx.u16(UNITS, leader, 0x12))]
    fixtures[-1]['active'] = [400, peer, other]; fixtures[-1]['pending'] = [400]
    for old in [0, 500, 501, 65535]: fixture(f'old_group_{old}', pokes=[word(UNITS, leader, 0x58, old)])
    fixture('duplicate_cautions', pokes=[word(0x81B50, i, 0x10, leader) for i in [0, 1, 500, 1000]],
            cautions=[0, 1, 1, None, 1000], caution_groups={0: [leader, peer, leader], 1: [None, leader, leader], 500: [leader]})
    fixture('record_padding_and_absent_unit_lists', target=400, active=[400], pending=[400],
            pokes=[[hex(UNITS), 400, at, '916ab73d'] for at in [8, 0x5A, 0x13A, 0x154, 0x1FC]]+
                  [[hex(UNITS), 400, at, bytes(8).hex()] for at in [0x160, 0x190, 0x1E0]])
    for values in [[], [leader], [leader, leader], [leader, peer], [peer, leader], [None, 0, 400, leader], live,
                   list(reversed(live)), [leader, None, leader, 400, 0]]:
        fixture(f'pending_{len(fixtures)}', 'pending', pending=values)
    controlled('pending_shared_group', kind='pending', pending=[leader, peer, other])
    # The real body's external notification return changes a live payload or appends through native 0x31ECC0.
    if listener:
        fixture('pending_replace_next', 'pending', pending=[leader, peer], actions=[['replace', 1, other]])
        fixture('pending_append', 'pending', pending=[leader], actions=[['append', peer]])
    for target, values in [(None, [None, leader, None, peer]), (leader, [peer, leader, leader, None]),
                           (leader, [peer]), (400, []), (0, [0, 0])]:
        fixture(f'active_erase_{len(fixtures)}', 'erase', target, active=values)
    for target, values, pending in [(0, [None, leader], []), (500, [], []),
                                     (group, [leader, leader, peer], []), (group, [leader, leader, peer], [leader])]:
        fixture(f'member_count_{len(fixtures)}', 'count', target, active=values, pending=pending)
    driver, marker, action_driver = o.alloc(0x20000), o.alloc(1), o.alloc(0x1000)
    o.poke(marker, b'\xc3')
    def emit(code, rva, args):
        for op, v in zip([b'\x48\xb9', b'\x48\xba', b'\x49\xb8', b'\x49\xb9'], args): code.extend(op+struct.pack('<Q', v))
        code.extend(b'\x48\xb8'+struct.pack('<Q', ctx.g.exe+rva)+b'\xff\xd0')
    def replace_list(code, wrapper, values, lookup=refs):
        emit(code, 0x18770, [pool, struct.unpack('<I', o.read(wrapper, 4))[0]])
        inv = {v: p for p, v in lookup.items()}
        for i in values:
            emit(code, 0x18DF0, [pool, struct.unpack('<I', o.read(wrapper, 4))[0], 1])
            code.extend(b'\x48\xba'+struct.pack('<Q', 0 if i is None else inv[i])+b'\x48\x89\x10')
    armed, current, reset_unit, input_state, events, phases, availability, action_done = [False], [None], [None], [], [], [], [], [False]
    active_fixture = [None]
    def event(kind, **kw): events.append(dict(kind=kind, **kw, state=packed(state(), input_state[0])))
    def begin(mu, address, size, data): armed[0] = True; input_state.append(state())
    def code_hook(mu, address, size, data):
        if not armed[0]: return
        r = address-ctx.g.exe; rcx, rdx = mu.reg_read(UC_X86_REG_RCX), mu.reg_read(UC_X86_REG_RDX)
        if r == 0x282130:
            current[0] = ref(rcx); phases.append(['unit', current[0]])
        elif address == listener_fn: event('notify', unit=ref(rdx))
        elif r == 0x1C3D90: event('detach', unit=ref(rdx))
        elif r == 0x282185 and not action_done[0] and current[0] is not None:
            action_done[0] = True
            for action in active_fixture[0].get('actions', []):
                if action[0] == 'replace':
                    node = nodes(o.q(ctx.raw+0x70))[action[1]][0]
                    o.emu_write(node, struct.pack('<Q', ctx.addr(UNITS, action[2])))
                elif action[0] == 'append':
                    code = bytearray(b'\x48\x83\xec\x20'); emit(code, 0x31ECC0, [ctx.raw, ctx.addr(UNITS, action[1])])
                    code.extend(b'\x48\x83\xc4\x20\x48\xb8'+struct.pack('<Q', address)+b'\xff\xe0')
                    o.poke(action_driver, bytes(code)); o.mu.ctl_remove_cache(action_driver, action_driver+0x1000)
                    mu.reg_write(UC_X86_REG_RIP, action_driver)
        elif r == 0x28218D:
            # UI callbacks may manage their own objects, but do not supply opaque record outputs to C#.
            changed = delta(input_state[0]['records'], records()) if len(phases) == 1 else []
            if any(int(t, 16) != UNITS for t, i, b in changed): raise RuntimeError('UI detachment wrote another gameplay table')
        elif r == 0x282337: availability.append(o.q(pool+0x78) != o.q(pool+0x80))
        elif r == 0x226830: reset_unit[0] = ref(rcx); phases.append(['reset', reset_unit[0]])
        elif r == 0x18770 and reset_unit[0] is not None and rcx == pool:
            index = rdx & 0xFFFFFFFF
            matches = [at for at in range(0x160, 0x1F0, 16) if o.q(ctx.addr(UNITS, reset_unit[0])+at) and
                       struct.unpack('<I', o.read(o.q(ctx.addr(UNITS, reset_unit[0])+at), 4))[0] == index]
            if len(matches) != 1: raise RuntimeError('Unknown unit reset list')
            event('clear_unit_list', unit=reset_unit[0], offset=matches[0])
        elif r == 0x2824AE: reset_unit[0] = None
        elif r == 0x2824BD: phases.append(['active_removed', current[0], bool(mu.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF)])
        elif r == 0x2824C5: phases.append(['count', current[0], mu.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF])
        elif r == 0xA22A0: event('troop_cache', troop=troops[rdx])
        elif r == 0xD37B0: event('properties', object=hex(rdx), type=mu.reg_read(UC_X86_REG_R8))
        elif r == 0xB27610: event('destroy', object=hex(o.q(rdx)), owner=hex(rcx))
    hook_addresses = [0x282130, 0x282185, 0x28218D, 0x282337, 0x1C3D90, 0x226830, 0x18770, 0x2824AE,
                      0x2824BD, 0x2824C5, 0xA22A0, 0xD37B0, 0xB27610]
    hooks = [o.mu.hook_add(UC_HOOK_CODE, begin, begin=marker, end=marker)]
    hooks += [o.mu.hook_add(UC_HOOK_CODE, code_hook, begin=ctx.g.exe+r, end=ctx.g.exe+r) for r in hook_addresses]
    if listener_fn: hooks.append(o.mu.hook_add(UC_HOOK_CODE, code_hook, begin=listener_fn, end=listener_fn))
    presentation = [*PRESENTATION, 0x1B4990]
    for r in presentation: o.stub(r)
    cases, stats = [], collections.Counter()
    try:
        for n, f in enumerate(fixtures):
            input_state.clear(); events.clear(); phases.clear(); availability.clear()
            armed[0] = action_done[0] = False; current[0] = reset_unit[0] = None; active_fixture[0] = f
            code = bytearray(b'\x53\x48\x83\xec\x30')
            for at, values in [(0x60, f['active']), (0x70, f['pending']), (0x130, f.get('cautions'))]:
                if values is not None: replace_list(code, o.q(ctx.raw+at), values, alerts if at == 0x130 else refs)
            for i, values in f.get('caution_groups', {}).items(): replace_list(code, o.q(ctx.addr(0x80BA8, i)+0x30), values)
            if f.get('no_listener'): code.extend(b'\x48\xb8'+struct.pack('<Q', listener_slot)+b'\x48\xc7\x00\0\0\0\0')
            if f.get('exhaust'):
                code.extend(b'\x48\xb8'+struct.pack('<Q', pool)+b'\x48\x8b\x98\x80\0\0\0\x48\x2b\x58\x78')
                loop = len(code); emit(code, 0x18B60, [pool+0x48, 0, 0])
                code.extend(b'\x48\xff\xcb\x75'+struct.pack('<b', loop-(len(code)+5)))
            emit(code, marker-ctx.g.exe, [])
            kind = f['kind']; target = 0 if f['target'] is None else ctx.addr(TROOPS if kind == 'count' else UNITS, f['target'])
            emit(code, {'unit': 0x282130, 'pending': 0x28E290, 'erase': 0x32ADA0, 'count': 0x22A240}[kind],
                 [] if kind == 'pending' else [ctx.raw, target] if kind == 'erase' else [target])
            code.extend(b'\x48\x83\xc4\x30\x5b\xc3')
            o.poke(driver, bytes(code)); o.mu.ctl_remove_cache(driver, driver+0x20000)
            result = []
            def inspect():
                validate_pool(o, pool); result.append(state())
                untouched = delta(baseline, records(tables.keys()))
                if any(int(t, 16) not in FOCUS for t, i, b in untouched): raise RuntimeError('Finalizer wrote an unmodeled gameplay table')
                nested = unit_lists(range(501))
                if any(v != all_lists[k] for k, v in nested.items() if int(k.split(':')[0]) not in watched):
                    raise RuntimeError('Finalizer changed an unmodeled unit list')
                return []
            started = time.time()
            ret = ctx.run(f['pokes'], lambda: o.call(driver-ctx.g.exe, read_after=inspect))
            if ret is None: raise RuntimeError(f"Finalizer {f['name']} failed: {ctx.failures[-1]}")
            b, a = input_state[0], result[0]
            case = {'name': f['name'], 'kind': kind, 'target': f['target'], 'inputs': packed(b, {'records': baseline}),
                    'after': packed(a, b), 'events': copy.deepcopy(events), 'phases': list(phases),
                    'availability': list(availability), 'listener': bool(listener) and not f.get('no_listener', False),
                    'actions': f.get('actions', []), 'return': ret & 0xFFFFFFFF if kind in ['erase', 'count'] else None}
            cases.append(case); stats['cases'] += 1; stats[kind] += 1
            stats['finalizers'] += sum(row[0] == 'unit' for row in phases)
            stats['resets'] += sum(row[0] == 'reset' for row in phases)
            stats['ui_detaches'] += sum(e['kind'] == 'detach' for e in events)
            stats['unit_list_clears'] += sum(e['kind'] == 'clear_unit_list' for e in events)
            stats['troop_cache_calls'] += sum(e['kind'] == 'troop_cache' for e in events)
            stats['exhausted_allocations'] += availability.count(False)
            print(f"  finalizer {n+1}/{len(fixtures)} {f['name']} ({time.time()-started:.2f}s): "
                  f"bodies={sum(p[0]=='unit' for p in phases)}, events={len(events)}, availability={availability}", flush=True)
    finally:
        for h in hooks: o.mu.hook_del(h)
        o.unstub()
    if not ctx.frozen:
        from snapshot_guard import differences
        end = Context()
        changed = differences(ctx.snap, end.snap)
        if changed or auxiliary_snapshot(end) != auxiliary: raise RuntimeError(f'Live finalizer snapshot changed: {changed}')
        em = end.g.q(end.g.exe+0x1B9A4A0)
        ec = {name: [[hex(k), hex(v)] for n, k, v in cache_tree(end.g.read, end.g.q, em, at)[0]]
              for name, at in [('unit', 0xB0), ('troop', 0xA0)]}
        if ec != native_cache: raise RuntimeError('Live cache changed during finalizer audit')
    ctx.save('unit_finalizer_world.json.gz', snapshot)
    ctx.save('unit_finalizer.json.gz', {'world': 'unit_finalizer_world.json.gz', 'cases': cases, 'stats': dict(stats),
             'ui_owners': owners, 'unit_lists': all_lists, 'native_full_body': True,
             'presentation': [hex(r) for r in presentation], 'coverage': {hex(r): [h, t] for r, (h, t, m) in o.coverage().items()},
             'scope': 'Complete 0x282130 and 0x28E290; 29 tables, all unit external lists, cautions, active/pending lists, cache trees and full general-pool partitions audited. UI object bodies and allocator storage remain explicit C# engine boundaries.'})
    return f'whole native unit finalizer: {dict(stats)}'
