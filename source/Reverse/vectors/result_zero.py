# Audit reason-0 immediate routing with the existing nine presentation boundaries.
# Engine INPUT pages are replayed independently; engine outputs never seed a C# implementation.
import copy
import struct
import time
from context import *
from engine_audit_state import EngineAuditState
from engagement_tactic_sequence import PRESENTATION
from unit_finalization import capture_native_pools
from destruction_overflow import require_unchanged_snapshot
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

COVER = [0x3789E0, 0x270550, 0x258EE0, 0x31ECC0, 0x27D4B0]


def generate(ctx):
    o = ctx.o
    o.insn_limit = 300_000_000
    capture_native_pools(ctx)
    state = EngineAuditState(ctx)
    initial_rng = o.read(ctx.g.exe + 0x1A47B90, 4)
    refs = {ctx.addr(t, i): [hex(t), i] for t in ctx.rec for i in range(len(ctx.rec[t]))}
    urefs = {ctx.addr(UNITS, i): i for i in range(501)}
    prefs = {ctx.addr(OFFICERS, i): i for i in range(len(ctx.rec[OFFICERS]))}
    people = [ctx.officer_by_id(ctx.u16(UNITS, i, 0x12)) for i in range(1, 501)
              if ctx.rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    people = [i for i in people if o.call32(0x32F500, ctx.addr(OFFICERS, i))
              and 1 <= ctx.officer_force(i) <= 45
              and not o.call32(0x32F500, o.call(0x22AA70, ctx.addr(OFFICERS, i)))]
    first = people[0]
    second = next(i for i in people if ctx.officer_force(i) != ctx.officer_force(first))
    persons = [ctx.addr(OFFICERS, i) for i in (first, second)]
    units = [o.call(0x22A070, p, 1) for p in persons]
    target = urefs[units[1]]
    base = [['worldstate', 0, 0x3A, '00'], ['worldstate', 0, 0x165C, '00'],
            ['worldstate', 0, 0x165D, '00'], ['exe', 'challenge_active', 0, '00000000'],
            [hex(UNITS), target, 0x1A, '01'], [hex(OFFICERS), second, 0xF1, '4a'],
            [hex(OFFICERS), second, 0x161, '00'], ['settings', 0, 0xE58, '01000000'],
            ['settings', 0, 0xD0, '00000000'], [hex(OFFICERS), first, 0x118, bytes(10).hex()]]
    fixtures = [dict(name='zero_selector_0', selector=0, pokes=base),
                dict(name='zero_selector_1', selector=1, pokes=base),
                dict(name='zero_player', selector=0, pokes=base +
                     [['worldstate', 0, 0x3A, bytes([ctx.officer_force(second)]).hex()],
                      ['settings', 0, 0x28, '00000000']])]
    trace, events, entry_state, exit_state, prefix, engine_args = {}, [], [None], [None], [None], [None]
    active = [False]
    hooks, return_hooks = [], {}
    registers = (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)
    def signed(value):
        value &= 0xFFFFFFFF
        return value - 0x100000000 if value >= 0x80000000 else value
    def root(mu, ret):
        return o.q(mu.reg_read(UC_X86_REG_RSP)) == ctx.g.exe + ret
    def morale(mu, address, size, data):
        if active[0] and root(mu, 0x378C70):
            trace['morale'].append([urefs[mu.reg_read(UC_X86_REG_RCX)], signed(mu.reg_read(UC_X86_REG_RDX)),
                                    refs[mu.reg_read(UC_X86_REG_R8)][1], signed(mu.reg_read(UC_X86_REG_R9)),
                                    struct.unpack('<i', o.read(mu.reg_read(UC_X86_REG_RSP) + 0x28, 4))[0]])
    def setter(mu, address, size, data):
        if active[0] and root(mu, 0x27064C):
            trace['setter'].append([urefs[mu.reg_read(UC_X86_REG_RCX)], signed(mu.reg_read(UC_X86_REG_RDX))])
    def injury(mu, address, size, data):
        if active[0] and root(mu, 0x378CBB):
            trace['injury'].append([prefs[mu.reg_read(UC_X86_REG_RCX)], signed(mu.reg_read(UC_X86_REG_RDX)),
                                    prefs[mu.reg_read(UC_X86_REG_R8)], signed(mu.reg_read(UC_X86_REG_R9))])
    def player(mu, address, size, data):
        if active[0] and root(mu, 0x378B18):
            trace['player'].append([signed(mu.reg_read(UC_X86_REG_RCX)), prefs[mu.reg_read(UC_X86_REG_RDX)]])
    def queue(mu, address, size, data):
        if active[0]:
            trace['queue'].append(hex(o.q(mu.reg_read(UC_X86_REG_RSP)) - ctx.g.exe))
    def engine_done(mu, address, size, data):
        if active[0] and exit_state[0] is None:
            exit_state[0] = state.read()
    def engine(mu, address, size, data):
        if not active[0]:
            return
        if entry_state[0] is not None:
            raise RuntimeError('Unexpected nested destruction engine in this narrow result fixture')
        entry_state[0] = state.read()
        args = [mu.reg_read(r) for r in registers]
        reason = struct.unpack('<i', o.read(mu.reg_read(UC_X86_REG_RSP) + 0x28, 4))[0]
        engine_args[0] = [*args, reason]
        trace['engine'].append([refs[args[0]], refs.get(args[1]), refs.get(args[2]), signed(args[3]), reason])
        prefix[0] = {page: bytes(mu.mem_read(page, 4096)) for page, baseline in o.pristine.items()
                     if page not in o.own and bytes(mu.mem_read(page, 4096)) != baseline}
        ret = o.q(mu.reg_read(UC_X86_REG_RSP))
        if ret not in return_hooks:
            return_hooks[ret] = o.mu.hook_add(UC_HOOK_CODE, engine_done, begin=ret, end=ret)
    def boundary(rva):
        def record():
            if active[0]:
                args = [o.mu.reg_read(r) for r in registers]
                request = dict(rva=hex(rva))
                if rva == 0x1A3C50:
                    request.update(object=refs.get(args[1]), related=refs.get(args[2]))
                elif rva == 0x1B4990:
                    request.update(unit=refs.get(args[1]), kind=signed(args[2]),
                                   payload=o.read(args[3], 32).hex() if args[3] else None)
                events.append(request)
            return 0
        return record
    for rva, callback in [(0x270550, morale), (0x241620, setter), (0x27D4B0, injury),
                          (0x378F00, player), (0x31EAB0, queue), (0x258EE0, engine)]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, callback, begin=ctx.g.exe + rva, end=ctx.g.exe + rva))
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation:
        o.stub(rva, fn=boundary(rva))
    def reset():
        trace.clear()
        trace.update(morale=[], setter=[], injury=[], player=[], queue=[], engine=[])
        events.clear()
        entry_state[0] = exit_state[0] = prefix[0] = engine_args[0] = None
    def run(fixture, direct=None):
        undo = []
        if direct is not None:
            for page, data in direct[0].items():
                undo.append((page, o.read(page, 4096)))
                o.poke(page, data)
        reset()
        before = state.read()
        result = []
        active[0] = True
        try:
            def inspect():
                result.append(state.read())
                return []
            if direct is None:
                o.call(0x3789E0, *persons, 0, fixture['selector'], read_after=inspect)
            else:
                args = direct[1]
                o.call(0x258EE0, *args[:4], stack_args=[args[4]], read_after=inspect)
                exit_state[0] = result[0]
            return dict(before=before, after=result[0], entry=entry_state[0], exit=exit_state[0],
                        trace=copy.deepcopy(trace), events=copy.deepcopy(events), prefix=prefix[0], args=engine_args[0])
        finally:
            active[0] = False
            for page, data in reversed(undo):
                o.poke(page, data)
    cases = []
    try:
        for fixture in fixtures:
            started = time.time()
            whole = ctx.run(fixture['pokes'], lambda: run(fixture))
            if whole is None:
                raise RuntimeError(f'Zero-morale caller did not complete: {fixture["name"]}: {ctx.failures[-1]}')
            if whole['entry'] is None or whole['exit'] is None:
                raise RuntimeError('Zero-morale engine frame missing')
            direct = ctx.run(fixture['pokes'], lambda: run(fixture, (whole['prefix'], whole['args'])))
            if direct is None:
                raise RuntimeError(f'Zero-morale direct engine did not complete: {ctx.failures[-1]}')
            if (direct['before'] != whole['entry'] or direct['after'] != whole['exit']
                    or direct['trace']['engine'] != whole['trace']['engine']
                    or direct['events'] != whole['events']):
                raise RuntimeError(f'Independent engine input/state/events differ: {fixture["name"]}')
            if whole['trace']['queue'] or whole['trace']['injury']:
                raise RuntimeError('Unexpected queue registration or injury call after zero morale')
            if whole['after'] != whole['exit']:
                raise RuntimeError('Unexpected gameplay-state writes after the route engine')
            cases.append(dict(fixture=fixture, trace=whole['trace'], events=whole['events'],
                              engine_inputs=state.delta(whole['before'], whole['entry']),
                              engine_after=state.delta(whole['entry'], whole['exit']),
                              after=state.delta(whole['before'], whole['after'])))
            print(f'  {fixture["name"]}: caller/direct input, state and presentation requests matched ({time.time() - started:.2f}s)', flush=True)
    finally:
        for hook in [*hooks, *return_hooks.values()]:
            o.mu.hook_del(hook)
        o.unstub()
    if not ctx.frozen:
        require_unchanged_snapshot(state.snapshot, initial_rng)
    ctx.save('result_zero_world.json.gz', state.snapshot)
    ctx.save('result_zero.json.gz', dict(cases=cases, world='result_zero_world.json.gz', officers=[first, second],
             units=[urefs[p] for p in units], presentation=[hex(r) for r in presentation],
             native_handlers=True, independent_native_match=True,
             scope='Reason-0 zero-morale request and immediate engine input/state audit. Existing nine presentation callbacks are excluded; 1A3C50 and 1B4990 request arguments are observed, not their stores/RNG/graphics effects. All 29 tables, world state, lists, 4509 unit lists, 501 caution lists, unit cache, queue, command actions, RNG, settings and captured flags are compared. Destruction body remains an explicit C# boundary.'))
    return f'Zero-morale immediate engine callers: {len(cases)}'
