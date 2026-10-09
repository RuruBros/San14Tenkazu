# Execute real destruction/capture handlers and compare the consumer with sequential native handler calls.
# This is an engine-boundary audit, not a C# implementation of officer capture or territorial ownership.
# Generate separately: run.py destruction_engine --out=<new-directory> --dump=<new-frozen-file>.
import collections, struct, time
from context import *
from destruction_consumer import ConsumerOracle, HANDLERS
from tactic_targets import CITIES, GATES
from engagement_tactic_sequence import PRESENTATION
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RBX, UC_X86_REG_RCX, UC_X86_REG_RDX

COVER = [0x44F060, 0x325AF0, *HANDLERS, 0x31ECC0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    o.insn_limit = 300_000_000
    native = ConsumerOracle(ctx)
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation: o.stub(rva)
    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    unit = live[0]
    force = ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, unit, 0x12)))
    attacker = next(i for i in live if ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, i, 0x12))) != force)
    af = ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, attacker, 0x12)))
    city = next(i for i in range(1, 52) if rec[CORPS][rec[CITIES][i][0x30]][0x10] not in [0, af, 46, 47, 48, 49, 50])
    gate = next(i for i in range(1, 11) if o.call32(0x22CD80, ctx.addr(GATES, i)) not in [0, af, 46, 47, 48, 49, 50])
    ur, ar, cr, gr = [hex(UNITS), unit], [hex(UNITS), attacker], [hex(CITIES), city], [hex(GATES), gate]
    unit_request = [ur, None, ar, None, 0, 4, 0]
    city_request = [None, cr, ar, gr, 0, 4, 0]
    gate_request = [None, gr, ar, cr, 0, 4, 0]
    fixtures = [[], [unit_request], [unit_request, unit_request],
                [[ur, None, None, cr, 1, 3, 0]], [city_request, city_request], [gate_request, gate_request],
                [unit_request, [ur, cr, ar, gr, 0, 4, 0]],
                [[None, cr, ar, gr, 0, 4, 0]]]
    starts = {int(t, 16): ctx.addr(int(t, 16), 0) for t in ctx.snap['tables']}
    sizes = {int(t, 16): (data['size'], len(data['records'])) for t, data in ctx.snap['tables'].items()}
    ws = o.q(ctx.raw + world.WORLDSTATE_RAW)
    flags = {name: (world.exe_value_address(o, ctx.g.exe, rva, how),
                    8 if how == 'set' else 1 if how.startswith('ptrbyte') else 4)
             for name, (rva, how) in world.EXE_VALUES.items() if name in ctx.snap['flags']}
    def state():
        records = {t: o.read(starts[t], size * count) for t, (size, count) in sizes.items()}
        lists = {hex(h): world.list_items(o, ctx.raw, ctx.g.exe, h, spec) for h, spec in world.LISTS.items() if h != 0x140}
        return (records, o.read(ws, world.WORLDSTATE_SIZE), lists, world.cmd_actions(o),
                o.read(ctx.g.exe + 0x1A47B90, 4), o.read(ctx.g.exe + world.SETTINGS_RVA, world.SETTINGS_SIZE),
                {name: o.read(address, size) if address else None for name, (address, size) in flags.items()})
    def delta(before, after):
        changed = []
        for t, (size, count) in sizes.items():
            if before[0][t] == after[0][t]: continue
            for i in range(count):
                data = after[0][t][size*i:size*(i+1)]
                if data != before[0][t][size*i:size*(i+1)]: changed.append([hex(t), i, data.hex()])
        return {'records': changed, 'worldstate': after[1].hex() if before[1] != after[1] else None,
                'lists': {h: items for h, items in after[2].items() if items != before[2][h]},
                'cmdactions': after[3] if before[3] != after[3] else None,
                'rng': after[4].hex() if before[4] != after[4] else None,
                'settings': after[5].hex() if before[5] != after[5] else None,
                'flags': {name: value.hex() if value is not None else None for name, value in after[6].items() if value != before[6][name]}}
    active = [None]
    pending = [None]
    calls, frames, inputs, hooks = [], [], [], []
    initial = [None]
    def request_entry(mu, address, size, data):
        if active[0] != 'consumer': return
        payload = o.read(mu.reg_read(UC_X86_REG_RBX), 48)
        pointers = struct.unpack_from('<4Q', payload)
        capture_mode, mode, reason = struct.unpack_from('<3i', payload, 32)
        request = [*[native.refs[p] if p else None for p in pointers], mode, reason, capture_mode]
        inputs.append({'request': request, 'state': delta(initial[0], state()), 'call': None})
    def entry(kind):
        def trace(mu, address, size, data):
            if active[0] is None: return
            calls.append(native.call_args(kind)); pending[0] = state()
            if active[0] == 'consumer': inputs[-1]['call'] = calls[-1]
        return trace
    def returned(mu, address, size, data):
        if active[0] is None or pending[0] is None: return
        frames.append(delta(pending[0], state())); pending[0] = None
    for kind, rva in enumerate(HANDLERS):
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, entry(kind), begin=ctx.g.exe + rva, end=ctx.g.exe + rva))
    def check_pool(mu, address, size, data):
        pool, index = mu.reg_read(UC_X86_REG_RCX), mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF
        if o.q(pool + 0x10) and index < struct.unpack('<I', o.read(pool + 0x40, 4))[0]:
            head, tail = [o.q(o.q(pool + at) + index * 8) for at in [0x10, 0x18]]
            if head and not tail:
                raise RuntimeError(f'Invalid native pool before clear: {pool:#x} index {index}, head {head:#x}, tail {tail:#x}, calls {calls}')
    hooks.append(o.mu.hook_add(UC_HOOK_CODE, check_pool, begin=ctx.g.exe + 0x18770, end=ctx.g.exe + 0x18770))
    hooks.append(o.mu.hook_add(UC_HOOK_CODE, returned, begin=ctx.g.exe + 0x44F1B1, end=ctx.g.exe + 0x44F1B1))
    hooks.append(o.mu.hook_add(UC_HOOK_CODE, request_entry, begin=ctx.g.exe + 0x44F0C0, end=ctx.g.exe + 0x44F0C0))
    driver = o.alloc(0x1000)
    def emit_call(code, rva, args, fifth=0):
        for opcode, value in zip([b'\x48\xb9', b'\x48\xba', b'\x49\xb8', b'\x49\xb9'], args):
            code.extend(opcode + struct.pack('<Q', value & 0xFFFFFFFFFFFFFFFF))
        code.extend(b'\xc7\x44\x24\x20' + struct.pack('<I', fifth & 0xFFFFFFFF))
        code.extend(b'\x48\xb8' + struct.pack('<Q', ctx.g.exe + rva) + b'\xff\xd0')
    def make_driver(expected):
        code = bytearray(b'\x48\x83\xec\x38'); returns = []
        for c in expected:
            kind, target, au, ab, corps, mode, reason, capture_mode, suzerain = c
            args = [native.ptr(target), native.ptr(au), native.ptr(ab), mode] if kind == 0 else (
                [native.ptr(target), native.ptr(corps), native.ptr(au), capture_mode] if kind == 1 else
                [native.ptr(target), native.ptr(au), int(suzerain), 0])
            emit_call(code, HANDLERS[kind], args, reason if kind == 0 else 0)
            returns.append(driver + len(code))
        emit_call(code, 0x325AF0, [ctx.raw, 0, 0, 0])
        code.extend(b'\x48\x83\xc4\x38\xc3'); o.poke(driver, bytes(code))
        # The same scratch address previously held a different driver; discard its translated instructions.
        o.mu.ctl_remove_cache(driver, driver + 0x1000)
        return returns
    cases, stats = [], collections.Counter()
    try:
        for n, requests in enumerate(fixtures):
            p = [['worldstate', 0, 0x3A, '00'], ['worldstate', 0, 0x165C, '00'], ['worldstate', 0, 0x165D, '00'],
                 ['exe', 'challenge_active', 0, '00000000'], ['listdata', '0x70', [], '']]
            # The last fixture executes the native tribe handler's non-player early return.
            if n == len(fixtures) - 1:
                p += [[hex(CITIES), city, 0x30, '33'], [hex(CORPS), 51, 0x10, '2e010100'],
                      [hex(FORCES), 46, 0x10, '0100'], [hex(FORCES), 46, 0x194, bytes([af]).hex()]]
            def run():
                native.prepare(requests); native.initial_count = len(requests)
                before = state(); initial[0] = before; result = []
                calls.clear(); frames.clear(); inputs.clear(); pending[0] = None; active[0] = 'consumer'
                try:
                    def inspect():
                        native.inspect_clear(); result.append(state()); return []
                    o.call(0x44F060, read_after=inspect)
                    return [before, result[0], list(calls), list(frames), list(inputs)]
                finally: active[0] = None; native.restore()
            started = time.time(); observed = ctx.run(p, run)
            if observed is None: raise RuntimeError(f'Actual engine consumer failed at case {n}: {ctx.failures[-1]}')
            returns = make_driver(observed[2])
            return_hooks = [o.mu.hook_add(UC_HOOK_CODE, returned, begin=a, end=a) for a in returns]
            def replay():
                native.prepare(requests); native.initial_count = len(requests)
                result = []; calls.clear(); frames.clear(); pending[0] = None; active[0] = 'direct'
                try:
                    def inspect():
                        native.inspect_clear(); result.append(state()); return []
                    o.call(driver - ctx.g.exe, read_after=inspect)
                    return result[0], list(calls), list(frames)
                finally: active[0] = None; native.restore()
            try: direct = ctx.run(p, replay)
            finally:
                for hook in return_hooks: o.mu.hook_del(hook)
            if direct is None: raise RuntimeError(f'Direct native engine failed at case {n}: {ctx.failures[-1]}')
            if direct != (observed[1], observed[2], observed[3]):
                ctx.save('destruction_engine_mismatch.json.gz', {'case': n, 'consumer': delta(observed[0], observed[1]),
                    'direct': delta(observed[0], direct[0]), 'consumer_calls': observed[2], 'direct_calls': direct[1],
                    'consumer_frames': observed[3], 'direct_frames': direct[2]})
                raise RuntimeError(f'Consumer/direct native engine differs at case {n}')
            changed = delta(observed[0], observed[1])
            if len(observed[2]) != len(observed[3]): raise RuntimeError('An engine return frame was not captured')
            for call in observed[2]: stats[['unit', 'base', 'tribe'][call[0]]] += 1
            for t, index, data in changed['records']: stats[f'records_{t}'] += 1
            stats['cases'] += 1; stats['requests'] += len(requests)
            cases.append({'requests': requests, 'pokes': p, 'calls': observed[2], 'frames': observed[3], 'inputs': observed[4], 'after': changed})
            print(f'  actual engine case {n + 1}/{len(fixtures)} matched direct native handlers ({time.time() - started:.1f}s): {dict(stats)}', flush=True)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    ctx.save('destruction_engine_world.json.gz', ctx.snap)
    ctx.save('destruction_engine.json.gz', {'cases': cases, 'stats': dict(stats), 'world': 'destruction_engine_world.json.gz',
        'presentation': [hex(r) for r in presentation], 'native_handlers': True, 'direct_native_match': True,
        'scope': 'All snapshot table bytes, world state, captured lists, command kinds/officer lists, RNG, settings and captured flags; tribe non-player early return only'})
    return f'actual engine consumption equals sequential native handlers: {dict(stats)}'
