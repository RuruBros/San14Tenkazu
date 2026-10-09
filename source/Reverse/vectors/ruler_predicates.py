# Complete native succession predicates, with candidate/dying argument direction from the caller.
import copy
import struct
from context import OFFICERS, CORPS, OFFICES
from engine_audit_state import EngineAuditState

ENTRIES = [0x276900, 0x2768E0, 0x2767E0, 0x282110, 0x27BF80, 0x280D90, 0x2750F0, 0x27A640]
COVER = ENTRIES + [0x2C3B50, 0x2C9BD0, 0x2C8AC0, 0x22CE20, 0x232330]


def generate(ctx, names=None):
    if not ctx.frozen:
        raise RuntimeError('Succession predicates require an explicitly supplied coherent frozen input')
    o = ctx.o
    audit = EngineAuditState(ctx)
    original = audit.read()
    merit, grade = [struct.unpack('<i', o.read(ctx.g.exe + rva, 4))[0] for rva in (0x1A49A34, 0x1A49A28)]
    if (merit, grade) != (10, 2):
        raise RuntimeError('Succession rules differ from the code constants')
    priority = list(struct.unpack('<22I', o.read(ctx.g.exe + 0x13A7C60, 88)))
    people = [i for i, b in enumerate(ctx.rec[OFFICERS]) if i and b[0xE6] in (1, 2, 3, 4)]
    a, b = people[:2]
    aid, bid = [ctx.u16(OFFICERS, i, 0x10) for i in (a, b)]
    def p(table, index, at, value, fmt='H'):
        return [hex(table), index, at, struct.pack('<' + fmt, value).hex()]
    def word(index, at, value): return p(OFFICERS, index, at, value)
    def byte(index, at, value): return p(OFFICERS, index, at, value, 'B')
    common = [['worldstate', 0, 0x34, struct.pack('<h', 210).hex()]]
    for index in (a, b):
        common += [byte(index, 0xE6, 4), byte(index, 0xE0, 1), word(index, 0x15E, 0),
                   word(index, 0xB2, 0), word(index, 0xB4, 0), word(index, 0xB6, 0),
                   word(index, 0xBA, 0), word(index, 0xBC, 0), word(index, 0x17C, 0),
                   word(index, 0x166, 200), byte(index, 0xE7, 0), [hex(OFFICERS), index, 0xC0, bytes(16).hex()]]
    for index in (1, 2):
        common += [p(CORPS, index, 0x10, 3, 'B'), p(CORPS, index, 0x11, 1, 'B'), p(CORPS, index, 0x12, bid)]
    fixtures = []
    def case(name, predicate, extra=(), candidate=a, dying=b):
        fixtures.append(dict(name=name, predicate=predicate, candidate=candidate, dying=dying,
                             entry=hex(ENTRIES[predicate]), pokes=common + list(extra)))
    for predicate in range(8):
        case(f'predicate_{predicate}_unmatched', predicate)
    case('child_father_zero_surname', 0, [word(a, 0xB4, bid)])
    case('child_mother_same_surname', 0, [word(a, 0xB6, bid), word(a, 0xB2, 7), word(b, 0xB2, 7)])
    case('child_different_surname', 0, [word(a, 0xB4, bid), word(a, 0xB2, 1), word(b, 0xB2, 2)])
    case('parent_direction_reversed', 0, [word(b, 0xB4, aid)])
    case('child_same_id', 0, [word(a, 0x10, bid), word(a, 0xB4, bid)])
    case('child_unsigned_parent_id', 0, [word(b, 0x10, 65535), word(a, 0xB4, 65535)])
    for predicate in range(5):
        relation = [word(a, 0xB4, bid)] if predicate in (0, 2) else (
            [word(a, 0xB4, 90), word(b, 0xB4, 90)] if predicate == 1 else (
                [word(a, 0xBC, 90), word(b, 0xBC, 90)] if predicate == 3 else [word(a, 0xBA, bid)]))
        case(f'predicate_{predicate}_valid_relation', predicate, relation)
        case(f'predicate_{predicate}_invalid_candidate', predicate, relation + [byte(a, 0xE6, 0)])
        case(f'predicate_{predicate}_invalid_dying', predicate, relation + [byte(b, 0xE6, 0)])
        case(f'predicate_{predicate}_null_candidate', predicate, candidate=None)
        case(f'predicate_{predicate}_null_dying', predicate, dying=None)
    case('sibling_shared_mother', 1, [word(a, 0xB6, 90), word(b, 0xB6, 90)])
    case('sibling_same_id', 1, [word(a, 0x10, bid), word(a, 0xB4, 90), word(b, 0xB4, 90)])
    for at, label in ((0xB4, 'father'), (0xB6, 'mother'), (0xBA, 'spouse')):
        case('related_' + label, 2, [word(a, at, bid)])
    for at, label in ((0xB4, 'child_father'), (0xB6, 'child_mother')):
        case('related_' + label, 2, [word(b, at, aid)])
    case('related_sibling_mother', 2, [word(a, 0xB6, 90), word(b, 0xB6, 90)])
    case('related_sworn', 2, [word(a, 0xBC, 90), word(b, 0xBC, 90)])
    case('related_same_nonzero_surname', 2, [word(a, 0xB2, 7), word(b, 0xB2, 7)])
    case('related_same_id_surname', 2, [word(a, 0x10, bid), word(a, 0xB2, 7), word(b, 0xB2, 7)])
    case('related_foreign_serving', 2, [word(a, 0xB4, bid), byte(b, 0xE0, 2), p(CORPS, 2, 0x10, 6, 'B')])
    case('related_foreign_free_candidate', 2, [word(a, 0xB4, bid), byte(a, 0xE6, 5), byte(b, 0xE0, 2), p(CORPS, 2, 0x10, 6, 'B')])
    case('related_foreign_dead_dying', 2, [word(a, 0xB4, bid), byte(b, 0xE6, 9), byte(b, 0xE0, 2), p(CORPS, 2, 0x10, 6, 'B')])
    for force1, force2 in ((52, 53), (52, 52), (255, 52)):
        case(f'related_raw_force_{force1}_{force2}', 2, [word(a, 0xB4, bid), byte(b, 0xE0, 2),
            p(CORPS, 1, 0x10, force1, 'B'), p(CORPS, 2, 0x10, force2, 'B')])
    case('related_both_corps_out_of_range', 2, [word(a, 0xB4, bid), byte(a, 0xE0, 52), byte(b, 0xE0, 255)])
    case('related_invalid_corps', 2, [word(a, 0xB4, bid), byte(b, 0xE0, 2), p(CORPS, 2, 0x11, 0, 'B')])
    case('related_inactive_marker', 2, [word(a, 0xB4, bid), word(a, 0x15E, 0x4000), byte(a, 0x1C5, 0)])
    case('related_active_marker', 2, [word(a, 0xB4, bid), word(a, 0x15E, 0x4000), byte(a, 0x1C5, 1), byte(a, 0xE6, 0)])
    case('sworn_same_id', 3, [word(a, 0x10, bid), word(a, 0xBC, 90), word(b, 0xBC, 90)])
    case('spouse_reverse_only', 4, [word(b, 0xBA, aid)])
    case('spouse_same_id', 4, [word(a, 0x10, bid), word(a, 0xBA, bid)])
    for pid in priority:
        case(f'priority_id_{pid}', 5, [word(a, 0x10, pid)])
    case('priority_invalid_officer', 5, [word(a, 0x10, 132), byte(a, 0xE6, 0)])
    case('priority_zero_slot', 5, [word(0, 0x10, 132)], candidate=0)
    case('priority_unsigned_65535', 5, [word(a, 0x10, 65535)])
    senior = [word(a, 0x17C, merit), byte(a, 0xE7, 1), p(OFFICES, 1, 0x34, 1), p(OFFICES, 1, 0x37, 1, 'B')]
    for value in (-32768, merit - 1, merit, 32767):
        case(f'senior_merit_{value}', 6, senior + [p(OFFICERS, a, 0x17C, value, 'h')])
    for value in (0, 1, grade, 255):
        case(f'senior_office_grade_{value}', 6, senior + [p(OFFICES, 1, 0x37, value, 'B')])
    case('senior_invalid_office_ignored', 6, senior + [p(OFFICES, 1, 0x34, 0), p(OFFICES, 1, 0x37, 255, 'B')])
    case('senior_office_zero_ignored', 6, senior + [byte(a, 0xE7, 0), p(OFFICES, 0, 0x37, 255, 'B')])
    case('senior_office_255', 6, senior + [byte(a, 0xE7, 255), p(OFFICES, 255, 0x34, 1), p(OFFICES, 255, 0x37, 1, 'B')])
    for began in (205, 206, 65535):
        case(f'senior_service_began_{began}', 6, senior + [word(a, 0x166, began)])
    case('senior_negative_year', 6, senior + [['worldstate', 0, 0x34, 'ffff'], word(a, 0x166, 0)])
    case('senior_invalid_officer_still_matches', 6, senior + [byte(a, 0xE6, 0)])
    case('liked_first_slot', 7, [word(b, 0xC0, aid)])
    case('liked_last_slot', 7, [word(b, 0xCE, aid)])
    case('liked_reverse_only', 7, [word(a, 0xC0, bid)])
    case('liked_invalid_source', 7, [word(b, 0xC0, aid), byte(b, 0xE6, 0)])
    case('liked_invalid_target', 7, [word(b, 0xC0, aid), byte(a, 0xE6, 0)])
    case('liked_null_target', 7, candidate=None)
    case('liked_zero_id_valid_target', 7, [word(a, 0x10, 0)])
    case('liked_invalid_zero_source', 7, [word(0, 0xC0, aid)], dying=0)
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names):
            raise ValueError('Unknown predicate fixture')
    cases = []
    try:
        for fixture in fixtures:
            def run():
                before = audit.read()
                def inspect():
                    if audit.read() != before:
                        raise RuntimeError('A succession predicate changed audited gameplay state')
                    return []
                args = [ctx.addr(OFFICERS, fixture[k]) if fixture[k] is not None else 0 for k in ('candidate', 'dying')]
                result = o.call(int(fixture['entry'], 16), *args, read_after=inspect) & 0xFF != 0
                return {'fixture': fixture, 'result': result}
            result = ctx.run(fixture['pokes'], run)
            if result is None:
                raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
            cases.append(result)
            print('  predicate', fixture['name'], result['result'], flush=True)
    finally:
        o.unstub()
    if audit.read() != original:
        raise RuntimeError('Predicate fixtures did not restore the full frozen audit state')
    ctx.save('ruler_predicates_world.json.gz', copy.deepcopy(audit.snapshot))
    ctx.save('ruler_predicates.json.gz', {'world': 'ruler_predicates_world.json.gz', 'cases': cases,
        'entries': list(map(hex, ENTRIES)), 'senior_merit': merit, 'senior_office_grade': grade,
        'no_stubs': True, 'succession_caller': False})
    return f'{len(cases)} complete native predicate calls; caller selection and mutations remain separate'
