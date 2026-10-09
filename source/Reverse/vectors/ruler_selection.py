# Execute the actual succession caller only up to its selection/dissolution/player-choice boundary.
import copy
import struct
from context import OFFICERS, CORPS
from engine_audit_state import EngineAuditState
from unit_lifecycle import GeneralListOracle
from unit_finalization import capture_native_pools
from ruler_predicates import ENTRIES
from oracle import SENTINEL
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RIP, UC_X86_REG_RSP, UC_X86_REG_RBP, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9

CITIES = 0xDD38
COVER = [0x28C180, 0x230410, 0x3332C0, 0x232DE0, 0x231A10, 0x2A5EA0, 0x24F400]
BOUNDARIES = {'selected': 0x28CB07, 'dissolve': 0x293790, 'player': 0x28C458}


def generate(ctx, names=None, retained=()):
    if not ctx.frozen:
        raise RuntimeError('Succession selection requires a separate coherent frozen input')
    o = ctx.o
    capture_native_pools(ctx)
    audit = EngineAuditState(ctx)
    original = audit.read()
    special_ids = list(struct.unpack('<5I', o.read(ctx.g.exe + 0x13AC9F0, 20)))
    if special_ids != [231, 247, 271, 631, 5121]:
        raise RuntimeError('Special-age IDs differ from the formula constants')
    dying, a, b, c = 551, 553, 526, 945
    # Retain the rejected 35/32 fixture as well; the actual packed pair is 0x22_00000023 (35/34).
    pairs = [(557, 553), (521, 526), (952, 945), (35, 32), (953, 946), (638, 625)]
    actors = sorted({dying, a, b, c, *(ctx.officer_by_id(pid) for _, pid in pairs)})
    if any(i is None or i <= 0 for i in actors):
        raise RuntimeError('Historical successors are not mapped in this frozen input')
    def p(table, index, at, value, fmt='H'):
        return [hex(table), index, at, struct.pack('<' + fmt, value).hex()]
    def word(index, at, value): return p(OFFICERS, index, at, value)
    def byte(index, at, value): return p(OFFICERS, index, at, value, 'B')
    def ws(at, value, fmt='I'): return ['worldstate', 0, at, struct.pack('<' + fmt, value).hex()]
    common = [ws(0x34, 210, 'h'), ws(0x44, 0), ws(0x50, 0), ws(0x3A, 45, 'B'),
        p(CORPS, 1, 0x10, 3, 'B'), p(CORPS, 1, 0x11, 1, 'B'), p(CORPS, 1, 0x12, 3000),
        p(CORPS, 2, 0x10, 6, 'B'), p(CORPS, 2, 0x11, 1, 'B'), p(CORPS, 2, 0x12, 3000)]
    common += [p(CITIES, i, 0x30, 0, 'B') for i in range(len(ctx.rec[CITIES]))]
    common += [p(CITIES, 1, 0x30, 1, 'B')]
    for n, i in enumerate(actors):
        # Keep record pointers/ID-map entries from this snapshot; all changed field bytes are explicit fixtures.
        cap = 100 ^ (i & 255)
        encoded = ((cap >> 3) | (cap << 5)) & 255
        common += [word(i, 0x10, 3000 if i == dying else 1000 + n), byte(i, 0xE6, 4),
            byte(i, 0xE0, 1), word(i, 0xE2, 1), word(i, 0x15E, 0), byte(i, 0xA9, 0),
            word(i, 0xAC, 200), word(i, 0xB2, 0), word(i, 0xB4, 0), word(i, 0xB6, 0),
            word(i, 0xBA, 0), word(i, 0xBC, 0), word(i, 0x17C, 0), word(i, 0x166, 200),
            byte(i, 0xE7, 0), byte(i, 0x161, 0), byte(i, 0x1BC, 0), byte(i, 0x14C, 0),
            byte(i, 0x1EC, encoded), byte(i, 0x1ED, encoded),
            [hex(OFFICERS), i, 0xEC, bytes([40] * 5).hex()], [hex(OFFICERS), i, 0xC0, bytes(16).hex()]]
    ids = {i: 3000 if i == dying else 1000 + actors.index(i) for i in actors}
    fixtures = []
    def case(name, extra=(), members=None):
        fixtures.append(dict(name=name, dying=dying, members=[dying, a, b] if members is None else members,
            pokes=common + list(extra)))
    case('ability_tie_keeps_first')
    case('ability_sum_all_five', [[hex(OFFICERS), a, 0xEC, bytes([90, 1, 1, 1, 1]).hex()],
        [hex(OFFICERS), b, 0xEC, bytes([30] * 5).hex()]])
    case('ability_injury_applies', [byte(a, 0x161, 3), [hex(OFFICERS), a, 0xEC, bytes([100] * 5).hex()]])
    case('single_candidate')
    fixtures[-1]['members'] = [dying, a]
    case('single_before_player_and_historical', [ws(0x3A, 3, 'B'), ws(0x50, 1), word(dying, 0x10, 521)], [dying, a])
    case('player_choice_requested', [ws(0x3A, 3, 'B')])
    case('last_counted_officer_requests_dissolution', members=[dying])
    case('no_city_requests_dissolution', [p(CITIES, 1, 0x30, 0, 'B')])
    case('same_id_candidates_excluded', [word(a, 0x10, 3000), word(b, 0x10, 3000)])
    case('duplicate_candidate_nodes_preserved', members=[dying, b, a, b])
    for status in (0, 5, 6, 8, 9, 255):
        case(f'nonserving_status_{status}', [byte(b, 0xE6, status)])
    case('foreign_force_filtered', [byte(b, 0xE0, 2)])
    case('invalid_corps_filtered', [byte(b, 0xE0, 255)])
    case('marker_filtered', [word(b, 0x15E, 0x4000), byte(b, 0x1C5, 1)])
    case('special_officer_filtered', [word(b, 0x10, 4075)])
    case('counted_filter_is_stricter_for_tribe_outside', [p(CORPS, 1, 0x10, 46, 'B'),
        word(a, 0xE2, 0), word(b, 0xE2, 62)])
    for predicate in range(8):
        extra = ([word(a, 0xB4, 3000)] if predicate == 0 else
            [word(a, 0xB4, 90), word(dying, 0xB4, 90)] if predicate == 1 else
            [word(dying, 0xB4, ids[a])] if predicate == 2 else
            [word(a, 0xBC, 90), word(dying, 0xBC, 90)] if predicate == 3 else
            [word(a, 0xBA, 3000)] if predicate == 4 else
            [word(a, 0x10, 132)] if predicate == 5 else
            [word(a, 0x17C, 10)] if predicate == 6 else [word(dying, 0xC0, ids[a])])
        case(f'predicate_{predicate}_selected', extra)
    case('female_child_skipped_for_male_priority', [byte(a, 0xA9, 1), word(a, 0xB4, 3000), word(b, 0x10, 132)])
    case('male_child_precedes_later_priority', [word(a, 0xB4, 3000), word(b, 0x10, 132)])
    case('last_predicate_retains_female_only', [byte(a, 0xA9, 1), byte(b, 0xA9, 255),
        word(dying, 0xC0, ids[a]), word(dying, 0xC2, ids[b]), word(b, 0xAC, 100)])
    case('female_child_discarded_when_later_lists_empty', [byte(a, 0xA9, 1), word(a, 0xB4, 3000),
        [hex(OFFICERS), b, 0xEC, bytes([90] * 5).hex()]])
    case('predicate_sort_priority_before_age', [word(a, 0x10, 35), word(b, 0x10, 521), word(a, 0xAC, 100)])
    for mode in (2, 3):
        for pid in special_ids:
            case(f'special_age_mode{mode}_id{pid}', [ws(0x44, mode), word(a, 0x10, pid),
                word(dying, 0xC0, pid), word(dying, 0xC2, ids[b]), word(a, 0xAC, 209), word(b, 0xAC, 100)])
    case('mode3_special_age_disabled', [ws(0x44, 3), ws(0x50, 1), word(a, 0x10, 231),
        word(dying, 0xC0, 231), word(dying, 0xC2, ids[b]), word(a, 0xAC, 209), word(b, 0xAC, 100)])
    for ruler, successor in pairs:
        target = ctx.officer_by_id(successor)
        case(f'historical_pair_{ruler}_{successor}', [ws(0x50, 1), word(dying, 0x10, ruler), word(target, 0x10, successor)])
    case('historical_can_select_filtered_marker', [ws(0x50, 1), word(dying, 0x10, 557), word(a, 0x10, 553),
        word(a, 0x15E, 0x4000), byte(a, 0x1C5, 1)], [dying, a, b, c])
    case('historical_can_select_off_list', [ws(0x50, 1), word(dying, 0x10, 638), word(ctx.officer_by_id(625), 0x10, 625)])
    case('historical_foreign_falls_back', [ws(0x50, 1), word(dying, 0x10, 557), word(a, 0x10, 553), byte(a, 0xE0, 2)], [dying, a, b, c])
    case('historical_nonserving_falls_back', [ws(0x50, 1), word(dying, 0x10, 557), word(a, 0x10, 553), byte(a, 0xE6, 5)], [dying, a, b, c])
    case('historical_invalid_marker_falls_back', [ws(0x50, 1), word(dying, 0x10, 557), word(a, 0x10, 553),
        word(a, 0x15E, 0x4000), byte(a, 0x1C5, 0)], [dying, a, b, c])
    target = ctx.officer_by_id(34)
    case('historical_actual_pair_35_34', [ws(0x50, 1), word(dying, 0x10, 35), word(target, 0x10, 34),
        byte(target, 0xE6, 4), byte(target, 0xE0, 1), word(target, 0x15E, 0)])
    zero_caps = []
    for i in (a, b, c):
        encoded = (((i & 255) >> 3) | ((i & 255) << 5)) & 255
        zero_caps += [byte(i, 0x1EC, encoded), byte(i, 0x1ED, encoded)]
    case('all_zero_ability_keeps_null', zero_caps)
    case('invalid_historical_retained_when_ability_is_zero', zero_caps + [ws(0x50, 1),
        word(dying, 0x10, 557), word(a, 0x10, 553), word(a, 0x15E, 0x4000), byte(a, 0x1C5, 0)], [dying, a, b, c])
    case('out_of_range_dying_corps_requests_dissolution', [byte(dying, 0xE0, 255)])
    case('invalid_force_skips_null_officer_node', [byte(dying, 0xE0, 255)], [None])
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names): raise ValueError('Unknown selection fixture')

    native_list = GeneralListOracle(ctx, o.q(ctx.raw + 0x100), capacity=16)
    refs = {ctx.addr(OFFICERS, i): i for i in range(len(ctx.rec[OFFICERS]))}
    selected, candidates, events, hooks = [], [], [], []
    def stop(mu, address, size, data):
        rva = address - ctx.g.exe
        if rva == BOUNDARIES['selected']:
            pointer = o.q(mu.reg_read(UC_X86_REG_RSP) + 0x70)
            selected.append(refs[pointer] if pointer else None)
            events.append(['selected'])
        elif rva == BOUNDARIES['dissolve']:
            args = [mu.reg_read(r) for r in (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)]
            if args not in [[ctx.addr(0xDF30, force), 0, 0, 0] for force in (0, 3, 46)]:
                raise RuntimeError('Unexpected native dissolution request')
            events.append(['DissolveForce'])
        else: events.append(['PlayerChoice'])
        mu.reg_write(UC_X86_REG_RIP, SENTINEL)
    def read_candidates(mu, address, size, data):
        wrapper = o.q(mu.reg_read(UC_X86_REG_RBP) - 0x48)
        candidates.extend(refs[int(pointer, 16)] for pointer in audit.members(wrapper))
    def trace(mu, address, size, data):
        rva = address - ctx.g.exe
        if rva in ENTRIES: events.append(['predicate', ENTRIES.index(rva)])
        elif rva == 0x28C43B: events.append(['Single'])
        elif rva == 0x28C7ED: events.append(['historical_attempt'])
        elif rva == 0x24F400: events.append(['Predicate'])
        elif rva == 0x231A10: events.append(['Ability'])
    cases = []
    prior = {case['fixture']['name']: case for case in retained}
    try:
        for rva in BOUNDARIES.values():
            hooks.append(o.mu.hook_add(UC_HOOK_CODE, stop, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, read_candidates, begin=ctx.g.exe+0x28C408, end=ctx.g.exe+0x28C408))
        for rva in [*ENTRIES, 0x28C43B, 0x28C7ED, 0x24F400, 0x231A10]:
            hooks.append(o.mu.hook_add(UC_HOOK_CODE, trace, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
        for fixture in fixtures:
            if fixture['name'] in prior:
                saved = prior[fixture['name']]
                if saved['fixture'] != fixture: raise RuntimeError('Retained selection fixture differs')
                cases.append(saved)
                continue
            native_list.prepare([ctx.addr(OFFICERS, i) if i is not None else 0 for i in fixture['members']])
            selected.clear(); candidates.clear(); events.clear()
            def run():
                before = audit.read()
                captured = []
                def inspect():
                    if audit.read() != before: raise RuntimeError('Selection prefix changed audited gameplay state')
                    if not events or events[-1][0] not in ('selected', 'DissolveForce', 'PlayerChoice'):
                        raise RuntimeError('Selection did not reach a declared boundary')
                    if events[-1][0] != 'selected': kind = events[-1][0]
                    else:
                        kinds = [e[0] for e in events if e[0] in ('Single', 'Predicate', 'Ability')]
                        kind = kinds[-1] if kinds else ('Historical' if any(e[0] == 'historical_attempt' for e in events) else 'Ability')
                    preds = [e[1] for e in events if e[0] == 'predicate']
                    captured.append(dict(kind=kind, selected=selected[0] if selected else None,
                        predicate=preds[-1] if kind == 'Predicate' else -1, candidates=list(candidates), events=copy.deepcopy(events)))
                    return []
                o.call(0x28C180, ctx.addr(OFFICERS, fixture['dying']), read_after=inspect)
                return dict(fixture=fixture, result=captured[0])
            try:
                result = ctx.run(fixture['pokes'], run)
                if result is None: raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
                cases.append(result)
                ctx.save('ruler_selection_progress.json.gz', dict(cases=cases))
                print('  selection prefix', fixture['name'], result['result']['kind'], result['result']['selected'], result['result']['predicate'], flush=True)
            finally: native_list.restore()
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    if audit.read() != original: raise RuntimeError('Selection fixtures did not restore the frozen audit state')
    ctx.save('ruler_selection_world.json.gz', copy.deepcopy(audit.snapshot))
    ctx.save('ruler_selection.json.gz', dict(world='ruler_selection_world.json.gz', cases=cases,
        boundaries={key: hex(value) for key, value in BOUNDARIES.items()}, special_age_ids=special_ids,
        gameplay_stubs=False, complete_succession=False, prefix_only=True))
    return f'{len(cases)} native selection prefixes; succession/history/dissolution/player dialog bodies not executed'
