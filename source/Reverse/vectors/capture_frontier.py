# Execute only the remaining player-tribe and gate-mode-1 engine conditions.
# The force-removal caller's gate filter/zero-record arguments are audited separately from this direct body.
import struct
import time
from context import *
from tactic_targets import CITIES, GATES
from engine_audit_state import EngineAuditState
from engagement_tactic_sequence import PRESENTATION
from unit_finalization import capture_native_pools
from destruction_overflow import require_unchanged_snapshot

COVER = [0x284010, 0x284D60]


def generate(ctx):
    o = ctx.o
    o.insn_limit = 300_000_000
    capture_native_pools(ctx)
    state = EngineAuditState(ctx)
    initial_rng = o.read(ctx.g.exe + 0x1A47B90, 4)
    valid = lambda t, i: o.call32(0x32F500, ctx.addr(t, i)) != 0
    force_of = lambda i: ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, i, 0x12)))
    attacker = next(i for i in range(1, 501) if valid(UNITS, i) and 1 <= force_of(i) <= 45)
    player = force_of(attacker)
    city = next(i for i in range(1, 52) if valid(CITIES, i)
                and 46 <= o.call32(0x22CD10, ctx.addr(CITIES, i)) <= 50)
    tribe = o.call32(0x22CD10, ctx.addr(CITIES, city))
    gate = next(i for i in range(1, 11) if valid(GATES, i)
                and o.call32(0x22CD80, ctx.addr(GATES, i)) != 0)
    zero_corps, zero_unit = ctx.addr(CORPS, 0), ctx.addr(UNITS, 0)
    if valid(CORPS, 0) or valid(UNITS, 0):
        raise RuntimeError('Mode-1 caller needs the actual invalid zero records, not null pointers')
    common = [['worldstate', 0, 0x165C, '00'], ['worldstate', 0, 0x165D, '00'],
              ['exe', 'challenge_active', 0, '00000000']]
    fixtures = [dict(name='player_tribe', rva=0x284010,
                     args=[ctx.addr(CITIES, city), ctx.addr(UNITS, attacker), 0],
                     target=[hex(CITIES), city], attacker=[hex(UNITS), attacker], force=tribe,
                     pokes=common + [['worldstate', 0, 0x3A, bytes([player]).hex()]]),
                dict(name='gate_mode_1', rva=0x284D60,
                     args=[ctx.addr(GATES, gate), zero_corps, zero_unit, 1],
                     target=[hex(GATES), gate], attacker=[hex(UNITS), 0], force=0, pokes=common)]
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation:
        o.stub(rva)
    cases = []
    try:
        for fixture in fixtures:
            started = time.time()
            def execute():
                before = state.read()
                after = []
                def inspect():
                    after.append(state.read())
                    return []
                o.call(fixture['rva'], *fixture['args'], read_after=inspect)
                return before, after[0]
            result = ctx.run(fixture['pokes'], execute)
            if result is None:
                raise RuntimeError(f'Capture frontier did not complete: {fixture["name"]}: {ctx.failures[-1]}')
            before, after = result
            if fixture['name'] == 'player_tribe':
                bit = 1 << (tribe - 46 + 5)
                flags_before = struct.unpack_from('<I', before['worldstate'], 0x169C)[0]
                flags_after = struct.unpack_from('<I', after['worldstate'], 0x169C)[0]
                force_size = state.tables[FORCES][1]
                suzerain = after['records'][FORCES][force_size * tribe + 0x194]
                if flags_after != flags_before | bit or suzerain != player:
                    raise RuntimeError('Player tribe flags/suzerain differ from the native code contract')
                expected = dict(flag_bit=bit, flags_before=flags_before, flags_after=flags_after, suzerain=suzerain)
            else:
                size = state.tables[GATES][1]
                owner = after['records'][GATES][size * gate + 0x30]
                if owner != 0:
                    raise RuntimeError('Gate mode-1 did not clear its owner corps')
                expected = dict(owner_corps=owner, invalid_corps_pointer=hex(zero_corps),
                                invalid_unit_pointer=hex(zero_unit), capture_mode=1)
            cases.append(dict(fixture=fixture, expected=expected, after=state.delta(before, after)))
            print(f'  {fixture["name"]}: complete native body and code-derived fields verified ({time.time() - started:.2f}s)', flush=True)
    finally:
        o.unstub()
    if not ctx.frozen:
        require_unchanged_snapshot(state.snapshot, initial_rng)
    ctx.save('capture_frontier_world.json.gz', state.snapshot)
    ctx.save('capture_frontier.json.gz', dict(cases=cases, world='capture_frontier_world.json.gz',
             native_handlers=True, presentation=[hex(r) for r in presentation],
             scope='Player tribe and actual gate mode-1 direct engine inputs. Complete native bodies except the existing nine presentation callbacks. Code-derived conquest flag/suzerain and gate owner fields are verified; 29 tables and surrounding engine state are recorded. This does not claim full 293790 caller replay or a C# engine-body port.'))
    return f'Unverified capture conditions completed: {len(cases)}'
