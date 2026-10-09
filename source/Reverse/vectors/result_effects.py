# Trace complete native result notifications without replacing localization, history or event bodies.
# The C# boundary reproduces the semantic requests, not those external stores or the caller's suffix.
import collections, copy, struct, time
from context import *
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

COVER = [0x3789E0, 0x3790C0, 0x2767E0]


def generate(ctx):
    o = ctx.o
    capture_native_pools(ctx)
    candidates = [ctx.officer_by_id(ctx.u16(UNITS, i, 0x12)) for i in range(1, 501)
                  if ctx.rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    candidates = [i for i in candidates if 1 <= ctx.officer_force(i) <= 45
                  and o.call32(0x32F500, ctx.addr(OFFICERS, i))
                  and not o.call32(0x32F500, o.call(0x22AA70, ctx.addr(OFFICERS, i)))]
    first = candidates[0]
    second = next(i for i in candidates if ctx.officer_force(i) != ctx.officer_force(first))
    pair = [first, second]
    refs = {ctx.addr(OFFICERS, i): i for i in range(len(ctx.rec[OFFICERS]))}
    forces = {ctx.addr(FORCES, i): i for i in range(52)}
    source = ctx.addr(OFFICERS, second)
    ruler = refs[o.call(0x22E900, source)]
    if not o.call32(0x32F500, ctx.addr(OFFICERS, ruler)):
        raise RuntimeError('The selector-1 relation fixture needs a valid ruler')
    fixtures = []
    def put(i, at, value): return [hex(OFFICERS), i, at, value.hex()]
    def fixture(name, selector, reason, pokes=None, officers=None, caller=False):
        fixtures.append(dict(name=name, selector=selector, reason=reason, pokes=pokes or [],
                             officers=officers or pair, caller=caller))
    for selector in [0, 1]:
        for reason in [1, 2]:
            fixture(f'caller_{selector}_{reason}', selector, reason, caller=True)
        marked = pair[1]
        flags = ctx.u16(OFFICERS, marked, 0x15E) ^ 0x4000
        fixture(f'flag_{selector}', selector, 2,
                [put(marked, 0x15E, struct.pack('<H', flags)), put(marked, 0x1C5, b'\x01'), put(marked, 0xE6, b'\x02')])
        unit = next(i for i in range(1, 501) if ctx.u16(UNITS, i, 0x12) == ctx.u16(OFFICERS, pair[1], 0x10))
        for morale in [50, 51]:
            fixture(f'morale_{selector}_{morale}', selector, 0, [[hex(UNITS), unit, 0x1A, bytes([morale]).hex()]])
    # Isolate the positive relation predicates without changing the actual forces or rulers.
    clean = [put(i, 0xB2, bytes(12)) for i in [ruler, second]]
    clean += [put(ruler, 0xC0, bytes(16))]
    for kind in ['sworn', 'spouse', 'clan', 'like']:
        p = copy.deepcopy(clean)
        if kind == 'sworn': p += [put(i, 0xBC, b'\x21\x00') for i in [ruler, second]]
        elif kind == 'spouse': p += [put(ruler, 0xBA, struct.pack('<H', ctx.u16(OFFICERS, second, 0x10)))]
        elif kind == 'clan': p += [put(i, 0xB2, b'\x21\x00') for i in [ruler, second]]
        else: p += [put(ruler, 0xC0, struct.pack('<H', ctx.u16(OFFICERS, second, 0x10)))]
        fixture(f'relation_{kind}', 1, 1, p)
    fixture('invalid_first', 0, 1, officers=[0, second], caller=True)
    fixture('invalid_second', 1, 1, officers=[first, 0], caller=True)
    idle = next(i for i in range(1, len(ctx.rec[OFFICERS]))
                if o.call32(0x32F500, ctx.addr(OFFICERS, i))
                and not o.call32(0x32F500, o.call(0x22A070, ctx.addr(OFFICERS, i), 1)))
    fixture('no_unit', 0, 1, officers=[idle, second])
    observed, hooks = [None], []
    def stack(mu): return mu.reg_read(UC_X86_REG_RSP)
    def word(address): return struct.unpack('<I', o.read(address, 4))[0]
    def selected(mu, address, size, data):
        observed[0]['messages'].append(mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF)
    def format_argument(mu, address, size, data):
        observed[0]['format_args'].append([mu.reg_read(UC_X86_REG_R8) & 0xFFFFFFFF,
                                           mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF])
    def enter(mu, address, size, data):
        observed[0]['entries'].append([mu.reg_read(r) & 0xFFFFFFFF for r in [UC_X86_REG_RCX, UC_X86_REG_RDX]]
            + [refs[mu.reg_read(r)] for r in [UC_X86_REG_R8, UC_X86_REG_R9]])
    def history(mu, address, size, data):
        if o.q(stack(mu)) != ctx.g.exe+0x379679: return
        s = stack(mu)
        args = [mu.reg_read(r) & 0xFFFFFFFF for r in [UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9]]
        args += [word(s+0x28+i*8) for i in range(9)]
        observed[0]['history'].append(args[:5]+args[7:])
    def event(mu, address, size, data):
        if o.q(stack(mu)) != ctx.g.exe+0x379434: return
        p = mu.reg_read(UC_X86_REG_RDX)
        observed[0]['events'].append([word(p), word(p+4), word(p+8), word(p+0x1C), word(p+0x20),
                                      word(p+0x80), mu.reg_read(UC_X86_REG_R8) & 0xFFFFFFFF])
    def event_force(mu, address, size, data):
        if o.q(stack(mu)) == ctx.g.exe+0x3793D4:
            observed[0]['event_forces'].append(forces[mu.reg_read(UC_X86_REG_RDX)])
    def command(mu, address, size, data):
        if o.q(stack(mu)) == ctx.g.exe+0x37961B:
            observed[0]['locations'].append(refs[mu.reg_read(UC_X86_REG_RCX)])
    for rva, fn in [(0x3790C0, enter), (0x934400, history), (0x8C7A60, event), (0x8ED280, event_force), (0x22D580, command)]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, fn, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    for rva in [0x3792A3, 0x3792FD, 0x379469, 0x3795C8]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, selected, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    for rva in [0x3791BC, 0x3791D2, 0x3794E4, 0x379500, 0x379516, 0x37952C]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, format_argument, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    def execute(f, caller):
        observed[0] = dict(entries=[], messages=[], format_args=[], history=[], events=[], event_forces=[], locations=[])
        a, b = f['officers']
        if caller: o.call(0x3789E0, ctx.addr(OFFICERS, a), ctx.addr(OFFICERS, b), f['reason'], f['selector'])
        else:
            a, b = (a, b) if f['selector'] == 0 else (b, a)
            o.call(0x3790C0, f['selector'], f['reason'], ctx.addr(OFFICERS, a), ctx.addr(OFFICERS, b))
        return copy.deepcopy(observed[0])
    cases, stats = [], collections.Counter()
    ctx.save('result_effects_progress_world.json.gz',ctx.snap)
    progress = os.path.join(ctx.output,'result_effects_progress.jsonl')
    if os.path.exists(progress): raise RuntimeError('Use a new result-effects output directory')
    try:
        for f in fixtures:
            started = time.time()
            direct = ctx.run(f['pokes'], lambda: execute(f, False))
            if direct is None: raise RuntimeError(f'Native notification failed: {f["name"]}: {ctx.failures[-1]}')
            if f['caller']:
                actual = ctx.run(f['pokes'], lambda: execute(f, True))
                if actual is None: raise RuntimeError(f'Native caller failed: {f["name"]}: {ctx.failures[-1]}')
                # Invalid officers are rejected before the caller enters 0x3790C0 at all.
                if {k: v for k, v in actual.items() if k != 'entries'} != {k: v for k, v in direct.items() if k != 'entries'}:
                    raise RuntimeError(f'Caller notification requests differ: {f["name"]}')
                if actual['entries'] and actual['entries'] != direct['entries']:
                    raise RuntimeError(f'Caller notification routing differs: {f["name"]}')
                stats['callers'] += 1
            stats['cases'] += 1
            stats['event_requests'] += len(direct['events'])
            cases.append({'fixture': f, 'observed': direct, 'caller_observed': actual if f['caller'] else None})
            with open(progress,'a',encoding='utf-8') as log: log.write(__import__('json').dumps(cases[-1])+'\n')
            print(f'  result effects {f["name"]}: complete native matched ({time.time()-started:.2f}s)', flush=True)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    ctx.save('result_effects_world.json.gz', ctx.snap)
    ctx.save('result_effects.json.gz', {'cases': cases, 'stats': dict(stats), 'world': 'result_effects_world.json.gz',
        'native_full_body': True, 'native_stubs': [], 'scope': 'Selector 0/1 semantic message/history/event requests; full native localization and stores execute. Caller 1/2 notification prefix only; no C# localization, history/event storage or caller suffix.'})
    return f'Complete result notification bodies and caller prefixes: {dict(stats)}'
