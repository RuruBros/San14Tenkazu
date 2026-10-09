# Execute force reset and bilateral agreement setters only on coherent frozen Oracle memory.
import copy
import random
import struct
from context import FORCES
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unit_lifecycle import GeneralListOracle
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX

COVER = [0x226030, 0x2EBCC0, 0x18770]


def generate(ctx):
    if not ctx.frozen:
        raise RuntimeError('Force-reset collection requires an explicitly supplied coherent frozen input')
    o = ctx.o
    capture_native_pools(ctx)
    audit = EngineAuditState(ctx)
    audit.wrappers.extend((f'force:{i}:{at:x}', ctx.addr(FORCES, i) + at)
        for i in range(52) for at in (0x250, 0x260))
    snapshot = copy.deepcopy(audit.snapshot)
    original = audit.read()
    snapshot['nested'] = original['nested']
    pool = ctx.g.exe + 0x27D10E0
    if not o.q(pool + 8):
        raise RuntimeError('The saved reset input must have an initialized general-list pool')
    if any(o.q(ctx.addr(FORCES, i) + 0x1A0) for i in range(52)):
        raise RuntimeError('This collection covers empty vectors only; a native heap-release input is separate')
    rng = random.Random(0x226030)
    fixtures = []

    def case(name, target=45, enabled=True, present=(True, True), counts=(3, 2), randomize=False, player=0):
        a = ctx.addr(FORCES, target)
        b = bytearray(ctx.rec[FORCES][target])
        if randomize:
            b[0x10:] = rng.randbytes(len(b) - 0x10)
        struct.pack_into('<Q', b, 0x1A0, 0)
        for at, exists in zip((0x250, 0x260), present):
            struct.pack_into('<Q', b, at, o.q(a + at) if exists else 0)
        # Distinct input values expose padding, high-bit preservation and bilateral clearing.
        pokes = [[hex(FORCES), target, 0, b.hex()], ['worldstate', 0, 0x3A, bytes([player]).hex()]]
        for i in range(52):
            pokes.extend([[hex(FORCES), i, 0x13 + target, bytes([(i * 13 + 7) & 255]).hex()],
                [hex(FORCES), i, 0xEA + target, bytes([(i * 11 + 0x83) & 255]).hex()]])
        fixtures.append(dict(name=name, kind='reset', target=target, enabled=enabled,
            present=list(present), counts=list(counts), pokes=pokes))

    case('actual_force_with_nonempty_lists')
    case('inactive_force', target=3)
    case('zero_slot', target=0)
    case('special_force', target=46)
    case('force_slot_51', target=51)
    case('both_lists_empty', counts=(0, 0))
    case('first_list_absent', present=(False, True))
    case('second_list_absent', present=(True, False))
    case('both_lists_absent', present=(False, False))
    case('pool_disabled', enabled=False)
    case('pool_disabled_absent_first', enabled=False, present=(False, True))
    case('out_of_range_player', player=52)
    for i in range(6):
        case(f'complete_record_padding_{i}', target=[0, 3, 13, 45, 46, 51][i],
            randomize=True, enabled=i != 5, counts=(i % 4, (i + 1) % 4))
    for value in (-2147483648, -1, 0, 1, 3, 4, 47, 48, 49, 255, 2147483647):
        for same in (False, True):
            fixtures.append(dict(name=f'agreement_{value}_same_{same}', kind='agreement', target=45,
                other=45 if same else 46, value=value, pokes=[['worldstate', 0, 0x3A, '00'],
                    [hex(FORCES), 45, 0xEA + (45 if same else 46), 'fd'],
                    [hex(FORCES), 45 if same else 46, 0xEA + 45, '73']]))
    fixtures.append(dict(name='agreement_zero_and_invalid', kind='agreement', target=0, other=3, value=48,
        pokes=[['worldstate', 0, 0x3A, '00'], [hex(FORCES), 0, 0xED, 'ff'], [hex(FORCES), 3, 0xEA, '83']]))
    calls, cases = [], []
    expected_indices = {}

    def cleared(mu, address, size, data):
        if mu.reg_read(UC_X86_REG_RCX) != pool:
            raise RuntimeError('Reset used an unexpected native list pool')
        index = mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF
        if index not in expected_indices:
            raise RuntimeError(f'Unexpected native clear index: {index}')
        calls.append(expected_indices[index])

    hook = o.mu.hook_add(UC_HOOK_CODE, cleared, begin=ctx.g.exe + 0x18770, end=ctx.g.exe + 0x18770)
    try:
        for fixture in fixtures:
            a = ctx.addr(FORCES, fixture['target'])
            lists = []
            old_enabled = o.read(pool + 8, 8)
            if fixture['kind'] == 'reset':
                lists = [GeneralListOracle(ctx, o.q(a + at), capacity=8) for at in (0x250, 0x260)]
                expected_indices = {f.index: at for at, f in zip((0x250, 0x260), lists)}
                for f, count in zip(lists, fixture['counts']):
                    f.prepare([ctx.addr(FORCES, 45), ctx.addr(FORCES, 46), ctx.addr(FORCES, 45)][:count])
                if not fixture['enabled']:
                    o.poke(pool + 8, bytes(8))
            calls.clear()

            def run():
                before = audit.read()
                total, free = o.q(pool + 0x30), o.q(pool + 0x20)
                after = []

                def inspect():
                    after.append(audit.read())
                    if lists:
                        returned = []
                        for at, f, count, present in zip((0x250, 0x260), lists, fixture['counts'], fixture['present']):
                            removed = fixture['enabled'] and present
                            expected = [0, 0, 0] if removed else [f.nodes if count else 0,
                                f.nodes + (count - 1) * 24 if count else 0, count]
                            if [o.q(slot) for slot in f.slots] != expected:
                                raise RuntimeError('Reset list head/tail/count differs')
                            if removed:
                                returned = [f.nodes + i * 24 for i in range(count)] + returned
                        if o.q(pool + 0x30) != total - len(returned):
                            raise RuntimeError('Reset pool active total differs')
                        if o.q(pool + 0x20) != (returned[0] if returned else free):
                            raise RuntimeError('Reset pool free head differs')
                        for i, node in enumerate(returned):
                            if o.q(node + 8) != (returned[i + 1] if i + 1 < len(returned) else free):
                                raise RuntimeError('Reset pool free chain differs')
                    return []

                if fixture['kind'] == 'reset':
                    o.call(0x226030, a, read_after=inspect)
                else:
                    o.call(0x2EBCC0, a, ctx.addr(FORCES, fixture['other']), fixture['value'], read_after=inspect)
                delta = audit.delta(before, after[0])
                if delta.keys() - {'records', 'nested'}:
                    raise RuntimeError(f'Unexpected reset state changes: {delta.keys()}')
                if any(int(t, 16) != FORCES for t, _, _ in delta['records']):
                    raise RuntimeError('Reset changed an unrelated record table')
                wanted = [at for at, present in zip((0x250, 0x260), fixture.get('present', []))
                    if present and fixture['enabled']] if lists else []
                if calls != wanted:
                    raise RuntimeError(f'Native clear order differs: {calls} != {wanted}')
                return dict(fixture=fixture, after=delta, clear_calls=list(calls),
                    nested_before={name: members for name, members in before['nested'].items() if name.startswith('force:')})

            try:
                result = ctx.run(fixture['pokes'], run)
                if result is None:
                    raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
                cases.append(result)
                print('  force reset', fixture['name'], 'records', len(result['after']['records']),
                    'clear calls', result['clear_calls'], flush=True)
            finally:
                o.poke(pool + 8, old_enabled)
                for f in reversed(lists):
                    f.restore()
    finally:
        o.mu.hook_del(hook)
        o.unstub()
    if audit.read() != original:
        raise RuntimeError('Force reset did not restore the complete frozen audit state')
    ctx.save('force_reset_world.json.gz', snapshot)
    ctx.save('force_reset.json.gz', dict(world='force_reset_world.json.gz', entry='0x226030', cases=cases,
        no_stubs=True, empty_vectors_only=True, player_refresh=False, native_pool=True))
    return f'{len(cases)} complete native calls; empty vectors and no player refresh'
