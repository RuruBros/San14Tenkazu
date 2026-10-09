# Native kinship and candidate ordering; this does not execute the ruler-succession caller.
import copy
import struct
from context import OFFICERS, FORCES
from engine_audit_state import EngineAuditState
from unit_lifecycle import GeneralListOracle
from unit_finalization import capture_native_pools

COVER = [0x2C8A70, 0x2C5C40, 0x2C5780, 0x2C6EF0, 0x2561A0, 0x24F400, 0x2A5EA0]


def generate(ctx, names=None):
    if not ctx.frozen:
        raise RuntimeError('Candidate collection requires an explicitly supplied coherent frozen input')
    o = ctx.o
    capture_native_pools(ctx)
    audit = EngineAuditState(ctx)
    original = audit.read()
    priority_bytes = o.read(ctx.g.exe + 0x13A7C60, 88)
    if priority_bytes != ctx.im.img[0x13A7C60:0x13A7CB8]:
        raise RuntimeError('The frozen priority table differs from the analysis image')
    priority = list(struct.unpack('<22I', priority_bytes))
    people = [i for i, b in enumerate(ctx.rec[OFFICERS]) if i and b[0xE6] in (1, 2, 3, 4)][:24]
    if len(people) != 24:
        raise RuntimeError('Twenty-four actual officer records are required')
    dying, a, b, father, mother = people[:5]
    ids = {i: ctx.u16(OFFICERS, i, 0x10) for i in people}
    p = lambda t, i, at, value, fmt='H': [hex(t), i, at, struct.pack('<' + fmt, value).hex()]
    def word(i, at, value): return p(OFFICERS, i, at, value)
    common = [['worldstate', 0, 0x34, struct.pack('<h', 210).hex()], ['worldstate', 0, 0x44, bytes(4).hex()]]
    for i in people:
        common.extend([word(i, 0x15E, ctx.u16(OFFICERS, i, 0x15E) & ~0x4000),
            p(OFFICERS, i, 0xE6, 4, 'B'), p(OFFICERS, i, 0xA9, 0, 'B'), word(i, 0xAC, 200),
            word(i, 0xB4, 0), word(i, 0xB6, 0), p(OFFICERS, i, 0xE7, 0, 'B')])
    fixtures = []
    def kin(name, entry=0x2C5C40, person=a, relative=dying, extra=()):
        fixtures.append(dict(name=name, kind='kinship', entry=hex(entry), person=person,
            relative=relative, pokes=common + list(extra)))
    for first, second, label in ((0xB4, 0xB4, 'father_father'), (0xB4, 0xB6, 'father_mother'),
        (0xB6, 0xB4, 'mother_father'), (0xB6, 0xB6, 'mother_mother')):
        kin(label, extra=[word(a, first, ids[father]), word(father, second, ids[dying])])
    kin('no_grandparent')
    kin('same_pointer', relative=a, extra=[word(a, 0xB4, ids[father]), word(father, 0xB4, ids[a])])
    kin('same_id', extra=[word(a, 0x10, ids[dying]), word(a, 0xB4, ids[father]), word(father, 0xB4, ids[dying])])
    kin('null_person', person=None)
    kin('null_relative', relative=None)
    kin('zero_slot', person=0)
    kin('invalid_person', extra=[p(OFFICERS, a, 0xE6, 0, 'B')])
    kin('invalid_relative', extra=[p(OFFICERS, dying, 0xE6, 0, 'B')])
    kin('invalid_parent_falls_through_to_mother', extra=[word(a, 0xB4, ids[father]), word(a, 0xB6, ids[mother]),
        p(OFFICERS, father, 0xE6, 0, 'B'), word(mother, 0xB4, ids[dying])])
    kin('inactive_marker_parent', extra=[word(a, 0xB4, ids[father]), word(father, 0xB4, ids[dying]),
        word(father, 0x15E, ctx.u16(OFFICERS, father, 0x15E) | 0x4000), p(OFFICERS, father, 0x1C5, 0, 'B')])
    kin('active_marker_parent', extra=[word(a, 0xB4, ids[father]), word(father, 0xB4, ids[dying]),
        word(father, 0x15E, ctx.u16(OFFICERS, father, 0x15E) | 0x4000), p(OFFICERS, father, 0x1C5, 1, 'B')])
    for pid in (0, 6000, 6001, 65535):
        kin(f'parent_lookup_{pid}', extra=[word(a, 0xB4, pid)])
    holes = [pid for pid in range(1, 6001) if ctx.officer_by_id(pid) is None or ctx.officer_by_id(pid) < 0]
    if holes:
        kin('unmapped_parent_id', extra=[word(a, 0xB4, holes[0])])
    for entry, name, at in ((0x2C8A70, 'parent_father', 0xB4), (0x2C8A70, 'parent_mother', 0xB6)):
        kin(name, entry=entry, extra=[word(a, at, ids[dying])])
    kin('parent_same_id', entry=0x2C8A70, extra=[word(a, 0x10, ids[dying]), word(a, 0xB4, ids[dying])])
    kin('parent_zero_id', entry=0x2C8A70, extra=[word(dying, 0x10, 0)])
    kin('mother_lookup_6001', extra=[word(a, 0xB6, 6001)])

    def sort(name, members=None, extra=()):
        fixtures.append(dict(name=name, kind='sort', dying=dying, members=[b, a] if members is None else members,
            pokes=common + list(extra)))
    sort('empty', [])
    sort('singleton', [a])
    sort('unsigned_id')
    sort('all_special_priorities', people[1:23][::-1],
        [word(i, 0x10, pid) for i, pid in zip(people[1:23], priority)])
    sort('priority_before_gender_and_age', extra=[word(a, 0x10, 132), p(OFFICERS, a, 0xA9, 255, 'B'),
        word(a, 0xAC, 210), word(b, 0x10, 1), word(b, 0xAC, 100)])
    sort('priority_table_before_numeric_id', extra=[word(a, 0x10, 521), word(b, 0x10, 35)])
    sort('male_before_older_female', extra=[word(a, 0xAC, 210), p(OFFICERS, b, 0xA9, 7, 'B'), word(b, 0xAC, 100)])
    sort('age_before_dying_parent', extra=[word(a, 0xAC, 100), word(dying, 0xB4, ids[b])])
    sort('dying_father_priority', extra=[word(dying, 0xB4, ids[b])])
    sort('dying_mother_priority', extra=[word(dying, 0xB6, ids[b])])
    sort('grandchild_priority', extra=[word(b, 0xB4, ids[father]), word(father, 0xB6, ids[dying])])
    sort('parent_before_grandchild', extra=[word(dying, 0xB4, ids[a]), word(b, 0xB4, ids[father]),
        word(father, 0xB4, ids[dying])])
    sort('office_before_numeric_id', extra=[p(OFFICERS, a, 0xE7, 1, 'B'), p(OFFICERS, b, 0xE7, 2, 'B'),
        p(0x784F8, 1, 0x37, 200, 'B'), p(0x784F8, 2, 0x37, 1, 'B')])
    sort('office_255_lookup', extra=[p(OFFICERS, a, 0xE7, 255, 'B'), p(0x784F8, 255, 0x37, 0, 'B'),
        p(0x784F8, 0, 0x37, 255, 'B')])
    sort('unsigned_id_high_bit', extra=[word(a, 0x10, 65535), word(b, 0x10, 32768)])
    sort('equal_id_preserves_node_order', [b, a, father], extra=[word(i, 0x10, ids[a]) for i in (b, father)])
    sort('duplicate_pointers', [b, a, b, father, a])
    sort('female_values_are_boolean', extra=[p(OFFICERS, a, 0xA9, 1, 'B'), p(OFFICERS, b, 0xA9, 255, 'B')])
    sort('negative_current_year_and_unsigned_birth', extra=[['worldstate', 0, 0x34, 'ffff'],
        word(a, 0xAC, 0), word(b, 0xAC, 65535)])
    sort('future_birth_clamps_age_zero', extra=[word(a, 0xAC, 65535), word(b, 0xAC, 211)])
    sort('mode3_without_special_age', extra=[['worldstate', 0, 0x44, struct.pack('<I', 3).hex()],
        ['worldstate', 0, 0x50, struct.pack('<I', 1).hex()], word(b, 0xAC, 100)])
    sort('earlier_priority_keeps_previous_node', extra=[word(a, 0x10, 35), word(b, 0x10, 521)])
    sort('grandchild_moves_before_unrelated', extra=[word(a, 0xB4, ids[father]), word(father, 0xB6, ids[dying])])
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names):
            raise ValueError('Unknown candidate fixture')
    native_list = GeneralListOracle(ctx, o.q(ctx.addr(FORCES, 45) + 0x250), capacity=32)
    refs = {ctx.addr(OFFICERS, i): i for i in range(len(ctx.rec[OFFICERS]))}
    cases = []
    try:
        for fixture in fixtures:
            is_sort = fixture['kind'] == 'sort'
            if is_sort:
                native_list.prepare([ctx.addr(OFFICERS, i) for i in fixture['members']])
            def run():
                before, outputs = audit.read(), []
                def inspect():
                    if audit.read() != before:
                        raise RuntimeError('Kinship or candidate sorting changed audited gameplay state')
                    if is_sort:
                        outputs.extend(refs[int(p, 16)] for p in audit.members(o.q(ctx.addr(FORCES, 45) + 0x250)))
                        if len(outputs) != len(fixture['members']):
                            raise RuntimeError('Native candidate count differs')
                        node, previous = o.q(native_list.slots[0]), 0
                        while node:
                            if o.q(node + 16) != previous:
                                raise RuntimeError('Native sorted previous link differs')
                            previous, node = node, o.q(node + 8)
                        if previous != o.q(native_list.slots[1]):
                            raise RuntimeError('Native sorted tail differs')
                    return []
                if is_sort:
                    o.call(0x24F400, ctx.g.exe + 0x27D10E0, native_list.index,
                        ctx.addr(OFFICERS, fixture['dying']), read_after=inspect)
                    result = outputs
                else:
                    result = bool(o.call(int(fixture['entry'], 16),
                        ctx.addr(OFFICERS, fixture['person']) if fixture['person'] is not None else 0,
                        ctx.addr(OFFICERS, fixture['relative']) if fixture['relative'] is not None else 0,
                        read_after=inspect))
                return dict(fixture=fixture, result=result)
            try:
                result = ctx.run(fixture['pokes'], run)
                if result is None:
                    raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
                cases.append(result)
                print('  candidate helper', fixture['name'], result['result'], flush=True)
            finally:
                if is_sort:
                    native_list.restore()
    finally:
        o.unstub()
    if audit.read() != original:
        raise RuntimeError('Candidate fixtures did not restore the full frozen audit state')
    ctx.save('ruler_candidates_world.json.gz', copy.deepcopy(audit.snapshot))
    ctx.save('ruler_candidates.json.gz', dict(world='ruler_candidates_world.json.gz', cases=cases,
        priority_ids=priority, no_stubs=True, native_sort=True, succession_caller=False, special_age_modes=False))
    return f'{len(cases)} complete native helper calls; succession caller and special age modes are separate'
