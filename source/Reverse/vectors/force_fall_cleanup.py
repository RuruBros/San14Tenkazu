# Complete force teardown dependencies on frozen memory; native pools execute without gameplay stubs.
import copy
import struct
from context import FORCES, OFFICERS, ITEMS
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unit_lifecycle import GeneralListOracle

COVER = [0x292070, 0x24AF70, 0x2EA9E0, 0x32B060, 0x335060]


def generate(ctx, retained=(), standings_only=False):
    if not ctx.frozen: raise RuntimeError('Force cleanup collection requires a coherent frozen input')
    o = ctx.o
    capture_native_pools(ctx, [(0x1B5C990, 24, 8)] if standings_only else [])
    audit = EngineAuditState(ctx)
    original = audit.read()
    snapshot = copy.deepcopy(audit.snapshot)
    cases, fixtures = [], []
    patterns = [[], [0], [1], [65535], [0, 0], [1, 1], [0, 1], [2, 1], [1, 2],
        [3, 2, 1], [1, 3, 2], [4, 3, 2, 1], [4, 3, 2, 1, 1], [4, 4, 3, 2, 1],
        [1, 4, 2, 3], [65535, 32768, 32767, 0], [65535, 0, 32768, 0], [0, 0, 0, 0],
        list(range(45)), list(reversed(range(45)))]
    for n, scores in enumerate(patterns):
        pokes = []
        for i in range(52):
            ruler = 7 if i == 0 or i >= 46 or 1 <= i <= len(scores) else 0
            score = scores[i-1] if 1 <= i <= len(scores) else 1234
            pokes += [[hex(FORCES), i, 0x10, struct.pack('<H', ruler).hex()],
                      [hex(FORCES), i, 0x1BC, struct.pack('<H', score).hex()],
                      [hex(FORCES), i, 0x1C4, 'a5']]
        fixtures.append(dict(name=f'standings_{n}', kind='standings', scores=scores, pokes=pokes))
    fixtures.append(dict(name='standings_original', kind='standings', pokes=[]))
    person = next(i for i, b in enumerate(ctx.rec[OFFICERS]) if i and b[0xE6] and ctx.u16(OFFICERS, i, 0x10))
    item_indices = list(dict.fromkeys(ctx.owned))[:5]
    if len(item_indices) < 5: raise RuntimeError('Need five existing owned-item nodes')
    item_pokes = [[hex(OFFICERS), person, 0x10, struct.pack('<H', 551).hex()]]
    for item, flags, kind, owner in zip(item_indices, (8, 9, 0xFE, 0x80, 8), (1, 1, 1, 2, 1), (551, 551, 551, 551, 552)):
        item_pokes += [[hex(ITEMS), item, 0x4A, struct.pack('<H', owner).hex()],
                      [hex(ITEMS), item, 0x4F, bytes([flags, kind]).hex()]]
    for ruler in (0, 1, 6000, 6001, 65535, 551):
        fixtures.append(dict(name=f'items_ruler_{ruler}', kind='items', person=person, victor=45,
            pokes=item_pokes+[[hex(FORCES), 45, 0x10, struct.pack('<H', ruler).hex()]]))
    for name, source, victor, extra in [
        ('null_person', None, 45, []), ('null_victor', person, None, []),
        ('person_zero', 0, 45, []), ('force_zero', person, 0, []),
        ('person_inactive', person, 45, [[hex(OFFICERS), person, 0xE6, '00']]),
        ('person_id_zero', person, 45, [[hex(OFFICERS), person, 0x10, '0000']]),
        ('person_id_6000', person, 45, [[hex(OFFICERS), person, 0x10, '7017']]),
        ('person_id_6001', person, 45, [[hex(OFFICERS), person, 0x10, '7117']]),
    ]:
        fixtures.append(dict(name='items_'+name, kind='items', person=source, victor=victor,
            pokes=item_pokes+[[hex(FORCES), 45, 0x10, '7102']]+extra))
    for name, members, target in [('empty', [], 3), ('present', [3, 45], 3), ('absent', [45], 3),
        ('duplicates', [45, 3, 45, 3, 3], 3), ('null_aliases', [None, 45, None], None),
        ('invalid_force_pointer', [0, 45, 0], 0)]:
        fixtures.append(dict(name='unregister_'+name, kind='unregister', members=members, target=target, pokes=[]))
    fixtures = [f for f in fixtures if (f['kind'] == 'standings') == standings_only]
    stem = 'force_standings' if standings_only else 'force_fall_cleanup'
    force_list = GeneralListOracle(ctx, o.q(ctx.raw+0xC0), capacity=8)
    prior = {c['fixture']['name']: c for c in retained}
    try:
        for fixture in fixtures:
            if fixture['name'] in prior:
                saved = prior[fixture['name']]
                if saved['fixture'] != fixture: raise RuntimeError('Retained cleanup fixture differs')
                cases.append(saved)
                continue
            replaced = fixture['kind'] == 'unregister'
            if replaced:
                force_list.prepare([ctx.addr(FORCES, i) if i is not None else 0 for i in fixture['members']])
            def run():
                before, collected = audit.read(), []
                def inspect():
                    after = audit.read()
                    delta = audit.delta(before, after)
                    if any(k not in ('records', 'lists') for k in delta):
                        raise RuntimeError('Force cleanup changed unexpected engine state')
                    table = FORCES if fixture['kind'] == 'standings' else ITEMS
                    if any(int(r[0], 16) != table for r in delta['records']):
                        raise RuntimeError('Force cleanup changed an unexpected table')
                    if fixture['kind'] != 'unregister' and 'lists' in delta:
                        raise RuntimeError('Standings/item transfer changed a list')
                    if 'lists' in delta and any(v != before['lists'][k] for k, v in delta['lists'].items() if k != '0xc0'):
                        raise RuntimeError('Force unregister changed an unrelated list')
                    collected.append(delta)
                    return []
                if fixture['kind'] == 'standings': result = o.call(0x292070, read_after=inspect)
                elif fixture['kind'] == 'items':
                    result = o.call(0x2EA9E0, ctx.addr(OFFICERS, fixture['person']) if fixture['person'] is not None else 0,
                        ctx.addr(FORCES, fixture['victor']) if fixture['victor'] is not None else 0, read_after=inspect)
                else:
                    result = o.call(0x32B060, ctx.raw,
                        ctx.addr(FORCES, fixture['target']) if fixture['target'] is not None else 0, read_after=inspect)
                return dict(fixture=fixture, result=result if fixture['kind'] != 'items' else None, after=collected[0])
            try:
                result = ctx.run(fixture['pokes'], run)
                if result is None: raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
                cases.append(result)
                ctx.save(stem+'_progress.json.gz', dict(cases=cases))
                print('  force cleanup', fixture['name'], 'records', len(result['after']['records']),
                      'return', result['result'], flush=True)
            finally:
                if replaced: force_list.restore()
    finally: o.unstub()
    if audit.read() != original: raise RuntimeError('Force cleanup did not restore the frozen audit state')
    ctx.save(stem+'_world.json.gz', snapshot)
    ctx.save(stem+'.json.gz', dict(world=stem+'_world.json.gz', cases=cases,
        gameplay_stubs=False, available_pools=True))
    return f'{len(cases)} complete native standings/item transfer/unregister calls'
