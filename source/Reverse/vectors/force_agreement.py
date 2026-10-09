# Complete agreement/reset bodies with player flag policy; GUI refresh remains an observed host request.
import copy
import struct
from context import FORCES
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RDX, UC_X86_REG_RCX

COVER = [0x2EBCC0, 0x226030, 0x333240]


def generate(ctx, retained=()):
    if not ctx.frozen: raise RuntimeError('Player agreement collection requires a coherent frozen input')
    o = ctx.o
    capture_native_pools(ctx)
    audit = EngineAuditState(ctx)
    audit.wrappers.extend((f'force:{i}:{at:x}', ctx.addr(FORCES, i) + at) for i in range(52) for at in (0x250, 0x260))
    original = audit.read()
    snapshot = copy.deepcopy(audit.snapshot)
    snapshot['nested'] = original['nested']
    gui_pointer = ctx.g.exe + 0x1B91DA8
    gui_original = o.read(gui_pointer, 8)
    if not o.q(gui_pointer): raise RuntimeError('This input needs an initialized refresh manager')
    if any(o.q(ctx.addr(FORCES, i) + 0x1A0) for i in range(52)):
        raise RuntimeError('Force-vector heap release requires separate inputs')
    fixtures = []
    for value in (-2147483648, -1, 0, 1, 2, 3, 4, 48, 49, 2147483647):
        for a, b in ((45, 3), (3, 45)):
            fixtures.append(dict(name=f'player_value_{value}_{a}_{b}', kind='agreement', target=a, other=b,
                value=value, refresh=False, pokes=[['worldstate', 0, 0x3A, '2d'],
                    [hex(FORCES), a, 0xEA+b, 'fd'], [hex(FORCES), b, 0xEA+a, 'e3']]))
    for player in (0, 45, 52):
        fixtures.append(dict(name=f'same_force_player_{player}', kind='agreement', target=45, other=45,
            value=4, refresh=False, pokes=[['worldstate', 0, 0x3A, bytes([player]).hex()],
                [hex(FORCES), 45, 0xEA+45, 'fd']]))
    fixtures.append(dict(name='zero_force_and_player', kind='agreement', target=0, other=45, value=4,
        refresh=False, pokes=[['worldstate', 0, 0x3A, '2d'], [hex(FORCES), 45, 0xEA, 'fd']]))
    for a, b in ((45, 3), (3, 45)):
        fixtures.append(dict(name=f'actual_refresh_order_{a}_{b}', kind='agreement', target=a, other=b,
            value=4, refresh=True, pokes=[['worldstate', 0, 0x3A, '2d'],
                [hex(FORCES), a, 0xEA+b, 'fd'], [hex(FORCES), b, 0xEA+a, 'e3']]))
    fixtures.append(dict(name='same_force_actual_refresh_twice', kind='agreement', target=45, other=45,
        value=2, refresh=True, pokes=[['worldstate', 0, 0x3A, '2d'], [hex(FORCES), 45, 0xEA+45, 'fd']]))
    for target, refresh in ((3, False), (45, False), (0, False), (51, False), (3, True)):
        fixtures.append(dict(name=f'configured_player_reset_{target}_refresh_{refresh}', kind='reset',
            target=target, refresh=refresh, pokes=[['worldstate', 0, 0x3A, '2d']]))
    city_refs = {ctx.addr(0xDD38, i): i for i in range(len(ctx.rec[0xDD38]))}
    wrapper_indices = {}
    for i in range(52):
        for at in (0x250, 0x260):
            wrapper = o.q(ctx.addr(FORCES, i)+at)
            if wrapper: wrapper_indices[struct.unpack('<I', o.read(wrapper, 4))[0]] = (i, at)
    refreshes, clears, hooks, cases = [], [], [], []
    active = [None]
    def refresh(mu, address, size, data):
        fixture = active[0]
        if not fixture['refresh']: raise RuntimeError('Disabled refresh manager still emitted a refresh')
        city = city_refs[mu.reg_read(UC_X86_REG_RDX)]
        force_bytes = [o.read(ctx.addr(FORCES, i), len(ctx.rec[FORCES][i])).hex()
                       for i in sorted({fixture['target'], fixture.get('other', fixture['target'])})]
        refreshes.append(dict(city=city, forces=force_bytes))
    def clear(mu, address, size, data):
        if mu.reg_read(UC_X86_REG_RCX) != ctx.g.exe+0x27D10E0:
            raise RuntimeError('Unexpected clear pool')
        clears.append(list(wrapper_indices[mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF]))
    prior = {c['fixture']['name']: c for c in retained}
    try:
        for rva, fn in ((0x1A3DE0, refresh), (0x18770, clear)):
            hooks.append(o.mu.hook_add(UC_HOOK_CODE, fn, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
        for fixture in fixtures:
            if fixture['name'] in prior:
                saved = prior[fixture['name']]
                if saved['fixture'] != fixture: raise RuntimeError('Retained agreement fixture differs')
                cases.append(saved)
                continue
            active[0] = fixture
            refreshes.clear(); clears.clear()
            if not fixture['refresh']: o.poke(gui_pointer, bytes(8))
            def run():
                before = audit.read()
                result = []
                def inspect():
                    after = audit.read()
                    delta = audit.delta(before, after)
                    if any(k not in ('records', 'nested') for k in delta):
                        raise RuntimeError('Agreement/reset changed unexpected engine state')
                    if any(int(r[0], 16) != FORCES for r in delta['records']):
                        raise RuntimeError('Agreement/reset changed a non-force table')
                    result.append(dict(fixture=fixture, after=delta, refreshes=copy.deepcopy(refreshes), clears=list(clears)))
                    return []
                if fixture['kind'] == 'reset':
                    o.call(0x226030, ctx.addr(FORCES, fixture['target']), read_after=inspect)
                else:
                    o.call(0x2EBCC0, ctx.addr(FORCES, fixture['target']), ctx.addr(FORCES, fixture['other']),
                        fixture['value'], read_after=inspect)
                return result[0]
            try:
                result = ctx.run(fixture['pokes'], run)
                if result is None: raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
                cases.append(result)
                ctx.save('force_agreement_progress.json.gz', dict(cases=cases))
                print('  player agreement', fixture['name'], 'refreshes', len(result['refreshes']), 'clears', result['clears'], flush=True)
            finally: o.poke(gui_pointer, gui_original)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    if audit.read() != original: raise RuntimeError('Agreement/reset fixtures did not restore the frozen audit state')
    ctx.save('force_agreement_world.json.gz', snapshot)
    ctx.save('force_agreement.json.gz', dict(world='force_agreement_world.json.gz', cases=cases,
        gameplay_stubs=False, ui_body_native=True, ui_effects_external=True, empty_vectors_only=True))
    return f'{len(cases)} complete native agreement/reset calls; GUI requests observed, physical GUI bytes not C# modeled'
