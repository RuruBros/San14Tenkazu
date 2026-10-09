# Execute complete combat callers at the 128-request boundary, with real engine bodies.
# Compare them with independently assembled native registration/handler sequences.
# Use a separate output and frozen dump; never merge snapshots from different scenes.
import collections, copy, struct, time
from context import *
from destruction_consumer import ConsumerOracle, HANDLERS
from engagement_tactic_sequence import PRESENTATION
from tactic_targets import CITIES, GATES
from unit_finalization import capture_native_pools, auxiliary_snapshot
from oracle import OracleError
from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_UNMAPPED, UC_MEM_READ_UNMAPPED
from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RDX, UC_X86_REG_RIP, UC_X86_REG_RSP

COVER = [0x174520, 0x270550, 0x29E840, 0x3789E0, 0x31EAB0, *HANDLERS, 0x241620, 0x22EB50, 0x31ECC0]


def require_unchanged_snapshot(snapshot, rng, extra_tables=()):
    # Include the auxiliary tables in the live end guard before publishing engine evidence.
    from snapshot_guard import differences
    end = Context()
    candidate = copy.deepcopy(end.snap)
    candidate['tables'].update(auxiliary_snapshot(end))
    for table, size, count in extra_tables:
        first = end.addr(table, 0)
        if any(end.addr(table, i) != first + i * size for i in range(count)):
            raise RuntimeError(f'Extra guard table {table:#x} is not contiguous')
        data = end.o.read(first, size * count)
        candidate['tables'][hex(table)] = dict(size=size, address=hex(first), extra={},
            records=[data[i*size:(i+1)*size].hex() for i in range(count)])
    changed = differences(snapshot, candidate)
    if end.g.read(end.g.exe+0x1A47B90, 4) != rng: changed.append('rng')
    if changed: raise RuntimeError(f'Overflow snapshot changed during generation: {changed}')
    print(f'overflow snapshot: all {len(snapshot["tables"])} initial tables, vectors, flags, lists, settings and RNG remained unchanged', flush=True)


class OverflowQueue(ConsumerOracle):
    def prepare(self, requests):
        # Use the actual native node array and free partition, including the 127->128 allocation.
        o = self.o
        q = struct.unpack('<18Q', o.read(self.pool, 0x90))
        if q[6] or o.q(self.slots[2]): raise RuntimeError('An empty native destruction pool is required')
        free, seen, p = [], set(), q[4]
        while p:
            if p in seen or not q[1] <= p < q[1]+q[7]*64 or (p-q[1]) % 64:
                raise RuntimeError('Invalid destruction free partition')
            seen.add(p); free.append(p); p = o.q(p+48)
        if len(free) != q[7] or len(requests) > len(free):
            raise RuntimeError('Insufficient native destruction nodes')
        self.old = [(a, o.read(a, n)) for a, n in [(self.pool, 0x90), (q[1], q[7]*64),
                    *[(slot, 8) for slot in self.slots]]]
        for i, request in enumerate(requests):
            o.poke(free[i], self.payload(request)+struct.pack('<QQ',
                   free[i+1] if i+1 < len(requests) else 0, free[i-1] if i else 0))
        for a, value in zip(self.slots, [free[0] if requests else 0,
                    free[len(requests)-1] if requests else 0, len(requests)]):
            o.poke(a, struct.pack('<Q', value))
        o.poke(self.pool+0x20, struct.pack('<Q', free[len(requests)] if len(requests) < len(free) else 0))
        o.poke(self.pool+0x30, struct.pack('<Q', len(requests)))

    def items(self):
        o = self.o; out, seen, previous, p = [], set(), 0, o.q(self.slots[0])
        q = struct.unpack('<18Q', o.read(self.pool, 0x90))
        def valid(p):
            if not q[1] <= p < q[1]+q[7]*64 or (p-q[1]) % 64:
                raise RuntimeError('Destruction node outside its native array')
        while p:
            valid(p)
            if p in seen or o.q(p+56) != previous: raise RuntimeError('Destruction active links differ')
            seen.add(p); data = o.read(p, 48)
            refs = [self.refs[x] if x else None for x in struct.unpack_from('<4Q', data)]
            capture, mode, reason = struct.unpack_from('<3i', data, 32)
            out.append([*refs, mode, reason, capture]); previous, p = p, o.q(p+48)
        if previous != o.q(self.slots[1]) or len(out) != o.q(self.slots[2]) or len(out) != q[6]:
            raise RuntimeError('Destruction head/tail/count/total differs')
        p = q[4]
        while p:
            valid(p)
            if p in seen: raise RuntimeError('Destruction free/active nodes overlap or cycle')
            seen.add(p); p = o.q(p+48)
        if len(seen) != q[7]: raise RuntimeError('Destruction node partition is incomplete')
        return out


def generate(ctx, additional_only=False, routes_only=False):
    o = ctx.o; o.insn_limit = 300_000_000
    capture_native_pools(ctx)
    snapshot = copy.deepcopy(ctx.snap); snapshot['tables'].update(auxiliary_snapshot(ctx))
    initial_rng = o.read(ctx.g.exe+0x1A47B90, 4)
    tables = {int(t, 16): (ctx.addr(int(t, 16), 0), d['size'], len(d['records']))
              for t, d in snapshot['tables'].items()}
    native = OverflowQueue(ctx)
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation: o.stub(rva)
    refs = native.refs; ptr = native.ptr
    ref = lambda t, i: [hex(t), i]
    valid = lambda r: bool(o.call32(0x32F500, ptr(r)))
    force_of = lambda i: ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, i, 0x12)))
    live = [i for i in range(1, 501) if ctx.rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    unit = live[0]; attacker = next(i for i in live if force_of(i) != force_of(unit))
    city = next(i for i in range(1, 46) if ctx.rec[CORPS][ctx.rec[CITIES][i][0x30]][0x10]
                not in [0, force_of(attacker), 46, 47, 48, 49, 50])
    gate = next(i for i in range(1, 11) if o.call32(0x22CD80, ctx.addr(GATES, i))
                not in [0, force_of(attacker), 46, 47, 48, 49, 50])
    ur, ar, cr, gr = ref(UNITS, unit), ref(UNITS, attacker), ref(CITIES, city), ref(GATES, gate)
    source_city = ref(CITIES, next(i for i in range(1, 46) if ctx.rec[CORPS][ctx.rec[CITIES][i][0x30]][0x10] == force_of(attacker)))
    # Inputs use the real object's tile; record the native tile resolver separately below.
    sources = [(255, -1), (27, attacker), (5, source_city[1]), (6, gate)]
    fixtures = []
    def fixture(name, target, source, count, duplicate=False, pokes=None, route=False, reason_flag=0, defer=1, pending=False):
        fixtures.append(dict(name=name, target=target, source=source, count=count, duplicate=duplicate,
                pokes=pokes or [], route=route, reason_flag=reason_flag, defer=defer, pending=pending))
    for target in [(27, unit), (5, city), (6, gate)]:
        for source in sources:
            label = f'{target[0]}_{source[0]}'
            fixture(label+'_127_append', target, source, 127)
            fixture(label+'_127_duplicate', target, source, 127, duplicate=True)
            fixture(label+'_128', target, source, 128)
            fixture(label+'_128_duplicate', target, source, 128, duplicate=True)
    for force in [45, 46, 47, 48, 49, 50, 51]:
        for suzerain in [False, True] if 46 <= force <= 50 else [False]:
            p = [[hex(CITIES), city, 0x30, '33'], [hex(CORPS), 51, 0x10, bytes([force, 1, 1, 0]).hex()],
                 [hex(FORCES), force, 0x10, '0100'],
                 [hex(FORCES), force, 0x194, bytes([force_of(attacker) if suzerain else 0]).hex()]]
            fixture(f'force_{force}_suzerain_{int(suzerain)}', (5, city), (27, attacker), 128, pokes=p)
    fixture('tribe_without_attacker_uses_general_handler', (5, city), (255, -1), 128,
            pokes=[[hex(CITIES), city, 0x30, '33'], [hex(CORPS), 51, 0x10, '2e010100'], [hex(FORCES), 46, 0x10, '0100']])
    fixture('already_pending_unit', (27, unit), (27, attacker), 128, pending=True)
    for target in [(27, 0), (27, 400), (27, 501), (5, 0), (5, 52), (6, 0), (6, 11), (4, unit)]:
        fixture(f'invalid_target_{target[0]}_{target[1]}', target, (27, attacker), 128)
    for reason_flag in [0, 1]:
        for source in sources[1:]:
            for count, duplicate in [(127, False), (127, True), (128, False), (128, True)]:
                fixture(f'route_{source[0]}_{reason_flag}_{count}_{int(duplicate)}', (27, unit), source,
                        count, duplicate, route=True, reason_flag=reason_flag)
        fixture(f'route_without_defer_{reason_flag}', (27, unit), (27, attacker), 128,
                route=True, reason_flag=reason_flag, defer=0)
    # Keep the original unit-on-base inputs and also cover a real unit slot with no city/gate precedence.
    route_attacker = next(i for i in live if force_of(i) != force_of(unit)
        and ctx.u16(UNITS, i, 0x2A) < 48400
        and ctx.u16(TILES, ctx.u16(UNITS, i, 0x2A), 0x1C) == i
        and not (1 <= ctx.u16(TILES, ctx.u16(UNITS, i, 0x2A), 0x1A) <= 61
                 and o.call32(0x32F500, ctx.addr(0x6DA98, ctx.u16(TILES, ctx.u16(UNITS, i, 0x2A), 0x1A)))))
    for reason_flag in [0, 1]:
        for count, duplicate in [(127, False), (127, True), (128, False), (128, True)]:
            fixture(f'route_unit_on_hex_{reason_flag}_{count}_{int(duplicate)}', (27, unit), (27, route_attacker),
                    count, duplicate, route=True, reason_flag=reason_flag)
    additional_start = len(fixtures)
    for target in [(5, city), (6, gate)]:
        for source in sources[:2]:
            for count, duplicate in [(127, False), (127, True), (128, False), (128, True)]:
                fixture(f'bombard_{target[0]}_{source[0]}_{count}_{int(duplicate)}', target, source, count, duplicate)
                fixtures[-1]['bombard'] = True
    for reason in [1, 2]:
        for count, duplicate in [(127, False), (127, True), (128, False), (128, True)]:
            fixture(f'result_{reason}_{count}_{int(duplicate)}', (27, unit), (27, attacker), count, duplicate)
            fixtures[-1]['command_reason'] = reason
    if additional_only: fixtures = fixtures[additional_start:]
    if routes_only: fixtures = [f for f in fixtures if f['route']]
    ws = o.q(ctx.raw+world.WORLDSTATE_RAW)
    pool = ctx.g.exe+0x27D10E0
    heads, counts = o.q(pool+0x10), o.q(pool+0x28)
    bound = struct.unpack('<I', o.read(pool+0x40, 4))[0]
    def members(wrapper):
        if not wrapper: return None
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        if index >= bound: return None
        p, seen, out = o.q(heads+8*index), set(), []
        while p:
            if p in seen: raise RuntimeError('Nested list cycle')
            seen.add(p); out.append(hex(o.q(p))); p = o.q(p+8)
        if len(out) != o.q(counts+8*index): raise RuntimeError('Nested list count differs')
        return out
    def state():
        flags = {name: o.read(a, 8 if how == 'set' else 1 if how.startswith('ptrbyte') else 4) if a else None
                 for name, (rva, how) in world.EXE_VALUES.items() if name in ctx.snap['flags']
                 for a in [world.exe_value_address(o, ctx.g.exe, rva, how)]}
        return {'records': {t: o.read(first, size*count) for t, (first, size, count) in tables.items()},
                'worldstate': o.read(ws, world.WORLDSTATE_SIZE),
                'lists': {hex(h): world.list_items(o, ctx.raw, ctx.g.exe, h, spec)
                          for h, spec in world.LISTS.items() if h != 0x140},
                'nested': {f'{i}:{at:x}': members(o.q(ctx.addr(UNITS, i)+at))
                           for i in range(501) for at in range(0x160, 0x1F0, 16)},
                'queue': native.items(), 'cmdactions': world.cmd_actions(o),
                'rng': o.read(ctx.g.exe+0x1A47B90, 4),
                'settings': o.read(ctx.g.exe+world.SETTINGS_RVA, world.SETTINGS_SIZE), 'flags': flags}
    def delta(a, b):
        out = {'records': []}
        for t, (_, size, count) in tables.items():
            if a['records'][t] != b['records'][t]:
                out['records'] += [[hex(t), i, b['records'][t][size*i:size*(i+1)].hex()]
                                  for i in range(count) if a['records'][t][size*i:size*(i+1)] != b['records'][t][size*i:size*(i+1)]]
        for name in b.keys()-{'records'}:
            if a[name] == b[name]: continue
            value = b[name]
            out[name] = value.hex() if isinstance(value, bytes) else (
                {k: v.hex() if isinstance(v, bytes) else v for k, v in value.items()} if name == 'flags' else value)
        return out
    def resolve(kind, number):
        if kind == 27: return ref(UNITS, number if number <= 500 else 0), None
        if kind in [5, 6]:
            n = number if kind == 5 and number < 52 else number+51 if kind == 6 and 1 <= number <= 10 else 0
            p = o.call(0x32C130, ctx.raw, n)
            return None, refs[p] if p else None
        return None, None
    def route_source(h):
        # 0x270550 resolves a supplied hex itself: a city/gate building precedes its unit slot.
        # Unlike 0x16EA90, this branch does not reject a unit pending final removal.
        tile = ctx.addr(TILES, h)
        if not o.call32(0x32F500, tile): raise RuntimeError('Route fixture needs a valid supplied hex')
        building = o.call(0x22FB10, tile)
        if o.call32(0x32F500, building):
            is_city, is_gate = o.call32(0x232650, building), o.call32(0x232BD0, building)
            if is_city or is_gate:
                n = o.call32(0x22DEA0, building)
                n = (n if n <= 51 else 0) if is_city else (n if 52 <= n <= 61 else 0)
                base = o.call(0x32C130, ctx.raw, n)
                return None, refs[base] if base else None
        n = o.call32(0x231500, tile)
        return (ref(UNITS, n), None) if 1 <= n <= 500 else (None, None)
    def expected(f):
        if 'command_reason' in f:
            f['officers'] = [ctx.officer_by_id(ctx.u16(UNITS, i, 0x12)) for i in [attacker, unit]]
            au, target_unit = [refs[o.call(0x22A070, ctx.addr(OFFICERS, i), 1)] for i in f['officers']]
            request = [target_unit, None, au, None, 0, f['command_reason'], 0]
            return request, [0, target_unit, au, None, None, 0, f['command_reason'], 0, False], []
        target_unit, target_base = resolve(*f['target'])
        if not valid(target_unit or target_base): return None, None, []
        if f['route']:
            source_unit, source_base = resolve(*f['source'])
            h = ctx.u16(UNITS, f['source'][1], 0x2A) if source_unit else o.call32(0x22D740, ptr(source_base))
            au, ab = route_source(h)
            f['source_hex'], f['resolved_source'] = h, [au, ab]
            reason = 0 if f['reason_flag'] else 3
            request = [target_unit, None, au, ab, 1, reason, 0]
            # Morale and delta are both zero, so the real setter is the complete write prefix.
            return request, [0, target_unit, au, ab, None, 1, reason, 0, False], [(0x241620, [ptr(target_unit), 0], 0)]
        au, ab = resolve(*f['source']); has_attacker = valid(au) or valid(ab)
        if not has_attacker: au = ab = None
        prefixes = []
        reason = 0 if target_unit else 4
        request = [target_unit, target_base, au, ab, 1 if target_unit else 0, reason, 0]
        if target_unit:
            return request, [0, target_unit, au, ab, None, 1, reason, 0, False], prefixes
        corps = None
        if has_attacker:
            if valid(au):
                p = o.call(0x22B980, ptr(au)); corps = refs[p] if p else None
                prefixes.append((0x22EB50, [ptr(au)], 0))
            elif valid(ab):
                p = ptr(ab); fn = o.q(o.q(p)+0x80)
                p = o.call(fn-ctx.g.exe, p); corps = refs[p] if p else None
            force_ptr = o.call(0x22C990, ptr(target_base))
            if o.call32(0x32F500, force_ptr):
                # Force references are outside ConsumerOracle's argument pointer map.
                fi = next(i for i in range(52) if ctx.addr(FORCES, i) == force_ptr)
                if 46 <= fi <= 50:
                    suzerain = o.read(force_ptr+0x194, 1)[0] == o.call32(0x22CC40, ptr(au))
                    return request, [2, target_base, au, None, None, 0, 0, 0, suzerain], prefixes
        return request, [1, target_base, au, None, corps, 0, 0, 0, False], prefixes
    driver, request_buffer = o.alloc(0x1000), o.alloc(48)
    active = [False]; fault_state = [None]; expected_fault = [None]
    capture_prefix = [False]; prefix_pages = [None]
    queue_inputs, accepted, calls, frames, engine_inputs, root_calls, hooks = [], [], [], [], [], [], []
    queue_stack, engine_stack, return_hooks = [], [], {}
    def queue_entry(mu, address, size, data):
        if not active[0]: return
        if capture_prefix[0] and prefix_pages[0] is None:
            # These are actual caller INPUTS at the registration boundary, before any engine execution.
            # Replay only the prefix's dirty non-scratch pages; never copy handler outputs into its input.
            prefix_pages[0] = {p: bytes(mu.mem_read(p, 4096)) for p, baseline in o.pristine.items()
                               if p not in o.own and bytes(mu.mem_read(p, 4096)) != baseline}
        p = mu.reg_read(UC_X86_REG_RDX); b = o.read(p, 48)
        rs = [refs[x] if x else None for x in struct.unpack_from('<4Q', b)]
        capture, mode, reason = struct.unpack_from('<3i', b, 32)
        queue_inputs.append([*rs, mode, reason, capture])
        address = o.q(mu.reg_read(UC_X86_REG_RSP)); queue_stack.append(address); watch_return(address)
    def queue_return(mu, address, size, data):
        if active[0] and queue_stack and queue_stack[-1] == address:
            queue_stack.pop(); accepted.append(mu.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF)
    def engine_entry(kind):
        def trace(mu, address, size, data):
            if active[0]:
                call = native.call_args(kind)
                if not engine_stack: root_calls.append(call)
                address = o.q(mu.reg_read(UC_X86_REG_RSP))
                engine_stack.append((address, len(calls))); watch_return(address)
                calls.append(call); engine_inputs.append(state()); frames.append(None)
        return trace
    def engine_return(mu, address, size, data):
        if active[0] and engine_stack and engine_stack[-1][0] == address:
            _, ordinal = engine_stack.pop(); frames[ordinal] = state()
    def faulted(mu, access, address, size, value, data):
        if active[0] and expected_fault[0] is not None:
            fault = {'rva': hex(mu.reg_read(UC_X86_REG_RIP)-ctx.g.exe), 'address': hex(address), 'access': access}
            if fault == expected_fault[0]: fault_state[0] = (state(), fault)
        return False
    def add_hook(address, fn):
        return o.mu.hook_add(UC_HOOK_CODE, fn, begin=address, end=address)
    def watch_return(address):
        if address not in return_hooks:
            def returned(mu, address, size, data):
                queue_return(mu, address, size, data); engine_return(mu, address, size, data)
            return_hooks[address] = add_hook(address, returned)
    hooks.append(add_hook(ctx.g.exe+0x31EAB0, queue_entry))
    for kind, r in enumerate(HANDLERS): hooks.append(add_hook(ctx.g.exe+r, engine_entry(kind)))
    hooks.append(o.mu.hook_add(UC_HOOK_MEM_UNMAPPED, faulted))
    def emit(code, rva, args, fifth=0):
        for op, value in zip([b'\x48\xb9', b'\x48\xba', b'\x49\xb8', b'\x49\xb9'], args):
            code.extend(op+struct.pack('<Q', value & 0xFFFFFFFFFFFFFFFF))
        code.extend(b'\xc7\x44\x24\x20'+struct.pack('<I', fifth & 0xFFFFFFFF))
        code.extend(b'\x48\xb8'+struct.pack('<Q', ctx.g.exe+rva)+b'\xff\xd0')
    def make_driver(request, call, prefixes, register, process):
        code = bytearray(b'\x48\x83\xec\x38')
        for r, args, fifth in prefixes: emit(code, r, args, fifth)
        if register and request:
            o.poke(request_buffer, native.payload(request)); emit(code, 0x31EAB0, [ctx.raw, request_buffer])
        if process:
            kind, target, au, ab, corps, mode, reason, capture, suzerain = call
            args = [ptr(target), ptr(au), ptr(ab), mode] if kind == 0 else (
                [ptr(target), ptr(corps), ptr(au), capture] if kind == 1 else [ptr(target), ptr(au), int(suzerain)])
            emit(code, HANDLERS[kind], args, reason if kind == 0 else 0)
        code.extend(b'\x48\x83\xc4\x38\xc3'); o.poke(driver, bytes(code))
        o.mu.ctl_remove_cache(driver, driver+0x1000)
    cases, stats = [], collections.Counter()
    try:
        for n, f in enumerate(fixtures):
            p = [['worldstate', 0, 0x3A, '00'], ['worldstate', 0, 0x165C, '00'], ['worldstate', 0, 0x165D, '00'],
                 ['exe', 'challenge_active', 0, '00000000'], *f['pokes']]
            if f['route']: p.append([hex(UNITS), unit, 0x1A, '00'])
            if f.get('bombard'):
                _, target_base = resolve(*f['target'])
                building = ctx.u16(int(target_base[0], 16), target_base[1], 0x4E)
                f['building'] = building
                p.append([hex(0x6DA98), building, 0x14, '0000'])
                if f['source'][0] == 27:
                    # 0x29E840 preserves one durability outside the exact city/gate attack distance.
                    center = ctx.u16(0x6DA98, building, 0x10)
                    distance = 2 if f['target'][0] == 5 else 1
                    h = next(h for h in range(max(0, center-442), min(48400, center+443))
                             if o.call32(0x2AA850, h, center) == distance)
                    f['unit_hex'] = h
                    p.append([hex(UNITS), attacker, 0x2A, struct.pack('<H', h).hex()])
            computed = ctx.run(p, lambda: expected(f))
            if computed is None: raise RuntimeError(f'Cannot resolve overflow fixture {n}: {ctx.failures[-1]}')
            request, engine, prefixes = computed
            initial = [[None, None, None, None, 0, 0, 0] for _ in range(f['count'])]
            if request and f['duplicate']: initial[0] = request[:]
            registers = request is not None and (not f['route'] or f['defer'] != 0)
            expected_accepted = int(f['count'] < 128) if registers else None
            process = request is not None and (not registers or not expected_accepted)
            if f['pending']: prefixes.insert(0, (0x31ECC0, [ctx.raw, ptr(ur)], 0))
            expected_fault[0] = None
            if process and engine[0] == 1:
                if engine[4] is None:
                    expected_fault[0] = {'rva': '0x284dd1', 'address': '0x10', 'access': UC_MEM_READ_UNMAPPED}
                elif engine[2] is None:
                    # The force-equality early return precedes the null attacker-unit read.
                    target_force = ctx.run(p, lambda: o.call32(0x22CD10 if request[1][0] == hex(CITIES) else 0x22CD80, ptr(request[1])))
                    attacker_force = ctx.run(p, lambda: o.read(ptr(engine[4])+0x10, 1)[0])
                    if target_force != attacker_force:
                        expected_fault[0] = {'rva': '0x22cc46', 'address': '0x12', 'access': UC_MEM_READ_UNMAPPED}
            def run(rva, args, stack=(), prefix=None):
                native.prepare(initial); before = state(); result = []
                undo_prefix = []
                if prefix is not None:
                    for page, data in prefix.items():
                        undo_prefix.append((page, o.read(page, 4096))); o.poke(page, data)
                queue_inputs.clear(); accepted.clear(); calls.clear(); frames.clear(); engine_inputs.clear(); root_calls.clear()
                queue_stack.clear(); engine_stack.clear()
                fault_state[0] = None
                active[0] = True
                try:
                    def inspect(): result.append(state()); return []
                    try:
                        value = o.call(rva, *args, stack_args=stack, read_after=inspect)
                        if expected_fault[0] is not None: raise RuntimeError('Expected native null read did not occur')
                        fault = None
                    except OracleError:
                        if fault_state[0] is None: raise
                        after, fault = fault_state[0]; result.append(after); value = None
                    return before, result[0], list(queue_inputs), list(accepted), list(calls), list(engine_inputs), list(frames), value, fault, list(root_calls)
                finally:
                    active[0] = False
                    for page, data in reversed(undo_prefix): o.poke(page, data)
                    native.restore()
            started = time.time()
            capture_prefix[0] = bool(f.get('bombard')); prefix_pages[0] = None
            if f.get('bombard'):
                observed = ctx.run(p, lambda: run(0x29E840, [ctx.addr(0x6DA98, f['building']), 1, ptr(request[2])]))
            elif 'command_reason' in f:
                # Literal result-side selector 2 bypasses 0x3790C0 in the real code; the whole remaining caller runs.
                observed = ctx.run(p, lambda: run(0x3789E0,
                    [*[ctx.addr(OFFICERS, i) for i in f['officers']], f['command_reason'], 2]))
            elif f['route']:
                args = [ptr(ur), 0, ctx.addr(TILES, f['source_hex']), f['reason_flag']]
                observed = ctx.run(p, lambda: run(0x270550, args, [f['defer']]))
            elif f['pending']:
                # Seed the real pending list through its native allocator, within the same caller execution.
                code = bytearray(b'\x48\x83\xec\x38')
                emit(code, 0x31ECC0, [ctx.raw, ptr(ur)])
                emit(code, 0x174520, [*f['target'], *f['source']])
                code.extend(b'\x48\x83\xc4\x38\xc3'); o.poke(driver, bytes(code))
                o.mu.ctl_remove_cache(driver, driver+0x1000)
                observed = ctx.run(p, lambda: run(driver-ctx.g.exe, []))
            else: observed = ctx.run(p, lambda: run(0x174520, [*f['target'], *f['source']]))
            captured_prefix = prefix_pages[0]; capture_prefix[0] = False
            if observed is None: raise RuntimeError(f'Whole overflow caller failed at {f["name"]}: {ctx.failures[-1]}')
            expected_queue = [request] if registers else []
            expected_returns = [expected_accepted] if registers else []
            expected_calls = [engine] if process else []
            if (observed[2][:len(expected_queue)] != expected_queue or observed[3][:len(expected_returns)] != expected_returns
                or observed[9] != expected_calls or (not process and len(observed[2]) != len(expected_queue))):
                raise RuntimeError(f'Independent overflow dispatch differs at {f["name"]}: {(observed[2], observed[3], observed[9])} != {(expected_queue, expected_returns, expected_calls)}')
            if len(observed[2]) != len(observed[3]): raise RuntimeError('A native queue registration return is missing')
            expected_items = initial+([request] if registers and expected_accepted and not f['duplicate'] else [])
            if observed[1]['queue'] != expected_items: raise RuntimeError(f'Overflow queue changed unexpectedly: {f["name"]}')
            if f['route'] and observed[7] & 0xFFFFFFFF != 1: raise RuntimeError('Native route did not report success')
            if f.get('bombard') and captured_prefix is None: raise RuntimeError('Bombard registration boundary was not captured')
            make_driver(request, engine, [] if f.get('bombard') else prefixes, registers, process)
            direct = ctx.run(p, lambda: run(driver-ctx.g.exe, [], prefix=captured_prefix))
            if direct is None: raise RuntimeError(f'Direct native overflow sequence failed at {f["name"]}: {ctx.failures[-1]}')
            # Registers and stack addresses are not engine state; all captured gameplay state and handler frames are.
            if direct[:7] != observed[:7] or direct[8:] != observed[8:]:
                ctx.save('destruction_overflow_mismatch.json.gz', {'name': f['name'], 'whole': delta(observed[0], observed[1]),
                         'direct': delta(direct[0], direct[1]), 'whole_calls': observed[4], 'direct_calls': direct[4]})
                raise RuntimeError(f'Whole/direct native overflow state differs at {f["name"]}')
            if sum(s is None for s in observed[6]) != (expected_fault[0] is not None): raise RuntimeError('An engine return frame is missing')
            stats['cases'] += 1; stats['full'] += f['count'] == 128
            stats['full_duplicate'] += f['count'] == 128 and f['duplicate']
            stats['accepted'] += bool(expected_accepted); stats['invalid'] += request is None
            stats['route'] += f['route']; stats['already_pending'] += f['pending']
            stats['native_null_read'] += observed[8] is not None
            stats['bombard'] += bool(f.get('bombard')); stats['command_result'] += 'command_reason' in f
            for c in observed[4]: stats[['unit', 'base', 'tribe'][c[0]]] += 1
            stats['nested_handlers'] += len(observed[4])-len(observed[9])
            cases.append({'fixture': f, 'pokes': p, 'initial': initial, 'request': request,
                'queue_inputs': observed[2], 'accepted': observed[3], 'calls': observed[4], 'root_calls': observed[9],
                'engine_inputs': [delta(observed[0], s) for s in observed[5]],
                'frames': [delta(a, b) if b is not None else None for a, b in zip(observed[5], observed[6])], 'after': delta(observed[0], observed[1]),
                'queue_after': observed[1]['queue'], 'native_fault': observed[8],
                'prefix_input_replay': bool(f.get('bombard'))})
            print(f'  overflow {n+1}/{len(fixtures)} {f["name"]}: native/direct matched'
                  f'{" (null read)" if observed[8] else ""} ({time.time()-started:.1f}s)', flush=True)
    except Exception:
        # Preserve a diagnostic copy, explicitly separate from a guarded/published snapshot.
        if not ctx.frozen:
            diagnostic = os.path.join(os.path.dirname(__file__), '..', '.cache', f'overflow_incomplete_{time.time_ns()}.pkl')
            print(f'  incomplete diagnostic snapshot: {diagnostic} ({o.save_pages(diagnostic)} pages)', flush=True)
        raise
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        for hook in return_hooks.values(): o.mu.hook_del(hook)
        o.unstub()
    if not ctx.frozen: require_unchanged_snapshot(snapshot, initial_rng)
    ctx.save('destruction_overflow_world.json.gz', snapshot)
    ctx.save('destruction_overflow.json.gz', {'cases': cases, 'stats': dict(stats), 'world': 'destruction_overflow_world.json.gz',
        'presentation': [hex(r) for r in presentation], 'native_handlers': True, 'independent_native_match': True,
        'scope': 'Complete 0x174520, zero-morale 0x270550, 0x3789E0 (result-side selector 2) callers; whole 0x29E840 with captured pre-registration INPUT pages for independent boundary replay. 29 tables, world state, lists, 4509 unit lists, queue partitions, commands, RNG, settings and captured flags. Tribe non-player early return; no final removal or dynamic object byte audit.'})
    return f'128-slot combat queue overflow, independent real native handlers: {dict(stats)}'
