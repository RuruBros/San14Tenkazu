# Audit the real finalizer's caution cleanup and commander replacement before the record reset.
# All fixture writes and native list allocations occur on the oracle's copied memory.
import collections, copy, struct, time
from context import *
from unit_finalization import AUX_TABLES, auxiliary_snapshot, capture_native_pools
from engagement_tactic_sequence import PRESENTATION
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RAX, UC_X86_REG_RBX, UC_X86_REG_RSI, UC_X86_REG_RSP

TROOPS, CAUTION_TROOPS, CAUTION_UNITS = 0x7F8D8, 0x80BA8, 0x81B50
COVER = [0x282130, 0x32AE20, 0x335060, 0x2269F0, 0x22A110, 0x333350, 0x234540]


def generate(ctx):
    o = ctx.o
    capture_native_pools(ctx)
    auxiliary = auxiliary_snapshot(ctx)
    snapshot = copy.deepcopy(ctx.snap)
    snapshot['tables'].update(auxiliary)
    tables = {int(t, 16): (ctx.addr(int(t, 16), 0), data['size'], len(data['records']))
              for t, data in snapshot['tables'].items()}
    focus = [UNITS, TROOPS, CAUTION_TROOPS, CAUTION_UNITS]
    baseline = {t: bytes.fromhex(''.join(snapshot['tables'][hex(t)]['records'])) for t in tables}
    pool = ctx.g.exe + 0x27D10E0
    heads, tails, counts = [o.q(pool+at) for at in [0x10, 0x18, 0x28]]
    capacity = struct.unpack('<I', o.read(pool+0x40, 4))[0]
    unit_ptrs = {ctx.addr(UNITS, i): i for i in range(len(ctx.rec[UNITS]))}
    caution_ptrs = {ctx.addr(CAUTION_UNITS, i): i for i in range(1001)}
    def ref(p, pointers):
        return None if not p else pointers[p]
    def nodes(wrapper):
        if not wrapper: return None
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        if index >= capacity: return None
        node, previous, seen, out = o.q(heads+index*8), 0, set(), []
        while node:
            if node in seen: raise RuntimeError('Caution list cycle')
            seen.add(node)
            if o.q(node+16) != previous: raise RuntimeError('Caution list previous link differs')
            out.append((node, o.q(node)))
            previous, node = node, o.q(node+8)
        if previous != o.q(tails+index*8) or len(out) != o.q(counts+index*8):
            raise RuntimeError('Caution list head/tail/count differs')
        return out
    def members(wrapper, pointers):
        ns = nodes(wrapper)
        return None if ns is None else [ref(p, pointers) for _, p in ns]
    def state():
        return {'records': {t: o.read(first, size*count) for t, (first, size, count) in tables.items()},
                'caution_units': members(o.q(ctx.raw+0x130), caution_ptrs),
                'caution_troops': [members(o.q(ctx.addr(CAUTION_TROOPS, i)+0x30), unit_ptrs) for i in range(501)],
                'active': members(o.q(ctx.raw+0x60), unit_ptrs),
                'pending': members(o.q(ctx.raw+0x70), unit_ptrs),
                'free': o.q(pool+0x20), 'total': o.q(pool+0x30), 'wrappers': o.q(pool+0x78),
                'list_available': o.q(pool+0x78) != o.q(pool+0x80)}
    def changes(before, after):
        out = []
        for t, (_, size, count) in tables.items():
            if before[t] == after[t]: continue
            for i in range(count):
                b = after[t][i*size:(i+1)*size]
                if b != before[t][i*size:(i+1)*size]: out.append([hex(t), i, b.hex()])
        return out
    def check_changes(before, after, allowed):
        delta = changes(before['records'], after['records'])
        if any(int(t, 16) not in allowed for t, i, b in delta):
            raise RuntimeError(f'Unexpected table writes in finalizer branch: {delta}')
        return delta
    live = [i for i, r in enumerate(ctx.rec[UNITS]) if i and r[0x10] and ctx.u16(UNITS, i, 0x12)]
    groups = collections.defaultdict(list)
    for i in live: groups[ctx.u16(UNITS, i, 0x58)].append(i)
    leader = next(i for i in live if len(groups[ctx.u16(UNITS, i, 0x58)]) >= 3 and
                  ctx.u16(UNITS, i, 0x12) == struct.unpack_from('<H', baseline[TROOPS], ctx.u16(UNITS, i, 0x58)*0x40+0x2C)[0])
    group = ctx.u16(UNITS, leader, 0x58)
    peer, other = [i for i in groups[group] if i != leader][:2]
    extra = next(i for i in live if ctx.u16(UNITS, i, 0x58) != group)
    commander, replacement = ctx.u16(UNITS, leader, 0x12), ctx.u16(UNITS, peer, 0x12)
    null_ids = [i for i in range(1, 6000) if ctx.byid[i] < 0]
    missing = null_ids[-1]
    if ctx.byid[6000] >= 0: raise RuntimeError('Boundary fixture requires an unmapped ID 6000')
    fixtures = []
    def fixture(name, unit=leader, pokes=None, active=None, pending=None, caution_units=None, caution_troops=None):
        fixtures.append({'name': name, 'unit': unit, 'pokes': pokes or [], 'active': active,
                         'pending': [unit] if pending is None else pending,
                         'caution_units': caution_units, 'caution_troops': caution_troops or {}})
    fixture('snapshot_leader')
    fixture('snapshot_member', peer)
    cautioned = next(i for i in live if any(struct.unpack_from('<H', baseline[CAUTION_UNITS], n*0x18+0x10)[0] == i for n in range(1001)))
    fixture('snapshot_cautioned', cautioned)
    def word(t, i, at, value): return [hex(t), i, at, struct.pack('<H', value).hex()]
    def controlled(name, ids, group_id=group, lead_id=commander, troop_id=None, pending=None, candidates=None):
        candidates = [peer, other, extra][:len(ids)] if candidates is None else candidates
        p = [word(UNITS, leader, 0x58, group_id), word(UNITS, leader, 0x12, lead_id),
             word(TROOPS, group_id if group_id <= 500 else 0, 0x2C, lead_id if troop_id is None else troop_id),
             [hex(TROOPS), group_id if group_id <= 500 else 0, 0x2E, '3a71']]
        for i, oid in zip(candidates, ids): p += [word(UNITS, i, 0x58, group_id), word(UNITS, i, 0x12, oid)]
        fixture(name, pokes=p, active=[leader, *candidates], pending=pending)
        fixtures[-1]['controlled'] = True
    controlled('first_different', [commander, replacement, ctx.u16(UNITS, extra, 0x12)])
    controlled('first_candidate', [replacement, commander])
    controlled('all_same', [commander, commander])
    controlled('unmapped_id_below_6000', [commander, missing, replacement])
    controlled('id_zero_accepted', [commander, 0, replacement])
    controlled('id_6000_stops', [commander, 6000, replacement])
    controlled('id_6001_stops', [commander, 6001, replacement])
    controlled('id_65535_stops', [commander, 65535, replacement])
    controlled('different_unmapped_ids_share_null', [replacement], lead_id=missing, troop_id=6000)
    controlled('different_invalid_ids_share_officer_zero', [replacement], lead_id=65535, troop_id=6001)
    controlled('departing_id_6000', [replacement], lead_id=6000)
    controlled('leader_pointer_differs', [replacement], troop_id=ctx.u16(UNITS, extra, 0x12))
    controlled('first_remaining_officer_invalid', [0, replacement])
    controlled('no_remaining_members', [])
    controlled('pending_candidate_excluded', [replacement, ctx.u16(UNITS, extra, 0x12)], pending=[leader, peer])
    controlled('group_500', [replacement], group_id=500)
    controlled('group_501_falls_back', [replacement], group_id=501)
    controlled('group_zero_invalid', [replacement], group_id=0)
    controlled('invalid_unit_still_in_group_iterator', [replacement], candidates=[400])
    p = [word(CAUTION_UNITS, i, 0x10, leader) for i in [0, 1, 500, 1000]]
    p += [word(CAUTION_UNITS, i, 0x12, 65535 if i == 500 else 0) for i in [0, 1, 500, 1000]]
    p += [[hex(CAUTION_UNITS), i, 0x08, 'a7b8c9d0e1f20314'] for i in [0, 1, 500, 1000]]
    p += [[hex(CAUTION_UNITS), i, 0x14, '64758697'] for i in [0, 1, 500, 1000]]
    fixture('caution_duplicates_and_unregistered', pokes=p, caution_units=[0, 1, 1, 1000, 2],
            caution_troops={0: [leader, peer, leader], 1: [None, leader, leader, peer], 2: [peer], 500: [leader]})
    wrapper = o.alloc(0x1000); o.poke(wrapper, struct.pack('<I', capacity))
    fixture('caution_absent_and_out_of_range_wrappers',
            pokes=[[hex(CAUTION_TROOPS), 3, 0x30, bytes(8).hex()],
                   [hex(CAUTION_TROOPS), 4, 0x30, struct.pack('<Q', wrapper).hex()]],
            caution_troops={1: [leader, leader]})
    # Combine each boundary with duplicate caution entries, including retained null payloads.
    for f in copy.deepcopy([f for f in fixtures if f.get('controlled')]):
        f['name'] += '_with_cautions'
        f['pokes'] += [word(CAUTION_UNITS, 1000, 0x10, f['unit']), word(CAUTION_UNITS, 1000, 0x12, 61)]
        f['caution_units'] = [1000, 2, 1000]
        f['caution_troops'] = {1: [f['unit'], None, peer, f['unit']], 500: [f['unit'], f['unit']]}
        fixtures.append(f)
    fixture('temporary_wrapper_pool_exhausted', unit=400,
            pokes=[word(UNITS, 400, 0x58, group), word(UNITS, 400, 0x12, commander)], active=[400, peer, other])
    fixtures[-1]['exhaust_wrappers'] = True
    fixture('caution_registration_wrapper_absent',
            pokes=[word(CAUTION_UNITS, 1000, 0x10, leader), word(CAUTION_UNITS, 1000, 0x12, 61)])
    fixtures[-1]['absent_registered'] = True
    driver = o.alloc(0x10000)
    def emit(code, rva, args):
        for op, value in zip([b'\x48\xb9', b'\x48\xba', b'\x49\xb8', b'\x49\xb9'], args):
            code.extend(op+struct.pack('<Q', value))
        code.extend(b'\x48\xb8'+struct.pack('<Q', ctx.g.exe+rva)+b'\xff\xd0')
    def replace_list(code, wrapper, values, pointers):
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        emit(code, 0x18770, [pool, index])
        for i in values:
            emit(code, 0x18DF0, [pool, index, 1])
            code.extend(b'\x48\xba'+struct.pack('<Q', 0 if i is None else pointers[i])+b'\x48\x89\x10')
    active, before, cleaned, replaced, valid, candidates, visited, clears, removed, entry_wrappers = [False], [], [], [], [], [], [], [], [], []
    def entry(mu, address, size, data): entry_wrappers.append(o.q(pool+0x78))
    def begin(mu, address, size, data):
        active[0] = True; before.append(state())
    def after_cleanup(mu, address, size, data):
        s = state(); cleaned.append(s); active[0] = False
        free = s['free']
        for expected in reversed(removed):
            if free != expected: raise RuntimeError('Caution erasure free-chain order differs')
            free = o.q(free+8)
        if free != before[0]['free']: raise RuntimeError('Caution erasure free-chain tail differs')
    def validity(mu, address, size, data): valid.append(bool(mu.reg_read(UC_X86_REG_RAX)))
    def candidate_list(mu, address, size, data):
        candidates.extend(members(o.q(mu.reg_read(UC_X86_REG_RSP)+0x30), unit_ptrs) or [])
    def candidate(mu, address, size, data): visited.append(ref(o.q(mu.reg_read(UC_X86_REG_RBX)), unit_ptrs))
    def after_replacement(mu, address, size, data): replaced.append(state())
    def caution_clear(mu, address, size, data):
        if active[0]: clears.append(ref(mu.reg_read(UC_X86_REG_RDX), caution_ptrs))
    def erase(mu, address, size, data):
        if active[0] and mu.reg_read(UC_X86_REG_RCX) == pool: removed.append(mu.reg_read(UC_X86_REG_RDX))
    hooks = [o.mu.hook_add(UC_HOOK_CODE, fn, begin=ctx.g.exe+r, end=ctx.g.exe+r)
             for r, fn in [(0x282130, entry), (0x28218D, begin), (0x28229D, after_cleanup), (0x2822C1, validity),
                           (0x282387, candidate_list), (0x2823D0, candidate), (0x282496, after_replacement),
                           (0x32AE20, caution_clear), (0x18A50, erase)]]
    presentation = [*PRESENTATION, 0x1B4990]
    for r in presentation: o.stub(r)
    cases, stats = [], collections.Counter()
    try:
        for n, f in enumerate(fixtures):
            for x in [before, cleaned, replaced, valid, candidates, visited, clears, removed, entry_wrappers]: x.clear()
            active[0] = False
            code = bytearray(b'\x53\x48\x83\xec\x30')
            if f['active'] is not None: replace_list(code, o.q(ctx.raw+0x60), f['active'], {i: p for p, i in unit_ptrs.items()})
            if f['caution_units'] is not None: replace_list(code, o.q(ctx.raw+0x130), f['caution_units'], {i: p for p, i in caution_ptrs.items()})
            for i, values in f['caution_troops'].items():
                replace_list(code, o.q(ctx.addr(CAUTION_TROOPS, i)+0x30), values, {i: p for p, i in unit_ptrs.items()})
            for i in f['pending']: emit(code, 0x31ECC0, [ctx.raw, ctx.addr(UNITS, i)])
            if f.get('exhaust_wrappers'):
                # Consume every real free wrapper through its native allocator, without falsifying counts.
                code.extend(b'\x48\xb8'+struct.pack('<Q', pool)+b'\x48\x8b\x98\x80\0\0\0\x48\x2b\x58\x78')
                loop = len(code)
                emit(code, 0x18B60, [pool+0x48, 0, 0])
                code.extend(b'\x48\xff\xcb\x75'+struct.pack('<b', loop-(len(code)+5)))
            if f.get('absent_registered'):
                code.extend(b'\x48\xb8'+struct.pack('<Q', ctx.raw+0x130)+b'\x48\xc7\x00\0\0\0\0')
            emit(code, 0x282130, [ctx.addr(UNITS, f['unit'])])
            code.extend(b'\x48\x83\xc4\x30\x5b\xc3')
            o.poke(driver, bytes(code)); o.mu.ctl_remove_cache(driver, driver+0x10000)
            def inspect():
                if len(before) != 1 or len(cleaned) != 1 or len(replaced) != 1 or len(valid) != 1:
                    raise RuntimeError('Missing native branch checkpoint')
                if o.read(ctx.addr(UNITS, f['unit'])+0x10, 1) != b'\0': raise RuntimeError('Full finalizer did not reset the unit')
                if f['unit'] in members(o.q(ctx.raw+0x60), unit_ptrs): raise RuntimeError('Full finalizer did not remove the active unit')
                return []
            started = time.time()
            result = ctx.run(f['pokes'], lambda: o.call(driver-ctx.g.exe, read_after=inspect))
            if result is None: raise RuntimeError(f"Native branch case {f['name']} failed: {ctx.failures[-1]}")
            b, c, r = before[0], cleaned[0], replaced[0]
            if f.get('exhaust_wrappers') and b['list_available']:
                raise RuntimeError(f"Native wrapper exhaustion did not persist: entry={entry_wrappers}, cleanup={b['wrappers']}")
            cleanup_delta = check_changes(b, c, [CAUTION_UNITS])
            commander_delta = check_changes(c, r, [TROOPS])
            if b['active'] != c['active'] or b['pending'] != c['pending']: raise RuntimeError('Cleanup changed unit membership')
            if c['caution_units'] != r['caution_units'] or c['caution_troops'] != r['caution_troops']:
                raise RuntimeError('Commander replacement changed caution lists')
            removed_count = sum(len(a or [])-len(z or []) for a, z in zip(
                [b['caution_units'], *b['caution_troops']], [c['caution_units'], *c['caution_troops']]))
            if removed_count != len(removed) or c['total'] != b['total']-removed_count:
                raise RuntimeError('Caution erasure pool total differs')
            # The checkpoint hook checked the native free-chain prefix before oracle restoration.
            if r['total'] != c['total'] or r['wrappers'] != c['wrappers']:
                raise RuntimeError('Temporary commander list leaked nodes or wrappers')
            cases.append({'name': f['name'], 'unit': f['unit'], 'inputs': changes(baseline, b['records']),
                'active': b['active'], 'pending': b['pending'], 'caution_units': b['caution_units'],
                'caution_troops': b['caution_troops'], 'cleanup_records': cleanup_delta,
                'cleanup_units': c['caution_units'], 'cleanup_troops': c['caution_troops'],
                'commander_records': commander_delta, 'troops_valid': valid[0], 'candidates': list(candidates),
                'visited': list(visited), 'cleared': list(clears), 'removed_nodes': len(removed),
                'member_list_available': b['list_available'], 'entry_wrappers': entry_wrappers[0],
                'cleanup_wrappers': b['wrappers']})
            stats['cases'] += 1; stats['caution_records_cleared'] += len(clears)
            stats['caution_nodes_removed'] += len(removed); stats['commander_changes'] += len(commander_delta)
            stats['valid_groups'] += valid[0]; stats['candidate_visits'] += len(visited)
            stats['exhausted_wrapper_cases'] += not b['list_available']
            print(f"  branch {n+1}/{len(fixtures)} {f['name']} ({time.time()-started:.2f}s): "
                  f"valid={valid[0]}, candidates={candidates}, visited={visited}, cleared={clears}, "
                  f"removed={len(removed)}, commander={[row[:2] for row in commander_delta]}, "
                  f"wrappers={entry_wrappers[0]}->{b['wrappers']}, available={b['list_available']}", flush=True)
    finally:
        for h in hooks: o.mu.hook_del(h)
        o.unstub()
    if not ctx.frozen:
        from snapshot_guard import differences
        end = Context()
        changed = differences(ctx.snap, end.snap)
        if changed or auxiliary_snapshot(end) != auxiliary: raise RuntimeError(f'Live branch snapshot changed: {changed}')
    ctx.save('unit_finalizer_branches_world.json.gz', snapshot)
    ctx.save('unit_finalizer_branches.json.gz', {'world': 'unit_finalizer_branches_world.json.gz', 'cases': cases,
        'stats': dict(stats), 'native_finalizer': True, 'presentation': [hex(r) for r in presentation],
        'coverage': {hex(r): [hit, total] for r, (hit, total, missed) in o.coverage().items()},
        'scope': 'Actual 0x282130 checkpoints before cleanup, after cleanup and after commander replacement; all 29 table bytes and caution-list storage audited. Other finalizer engine effects remain outside the C# port.'})
    return f'commander replacement and caution cleanup: {dict(stats)}'
