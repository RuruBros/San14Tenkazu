"""Run original policy effects and personality predicates without any live process.

Declared mechanical boundaries: CRT mutex lock/unlock and memset. Gameplay
visitors, bonuses, condition handlers, target filters and effect decoding execute
original code. Fixtures are controlled inputs, not a complete historical save.
"""
import argparse
import json
from pathlib import Path
import struct

from editor_consumer_audit import recorded_images, read_json, sha, policy_members
from editor_roundtrip_log import verify_rows
from editor_effects_audit import EffectsNative, mappings
from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, require
from version_audit import MemoryImage


def word(native, at, value):
    native.put(at, struct.pack('<H', value))


def integer(native, at, value):
    native.put(at, struct.pack('<i', value))


def evidence(native):
    return dict(nativeTraceSha256=sha(struct.pack('<' + 'I' * len(native.trace), *native.trace)),
                nativeInstructions=len(native.trace),
                reads=[dict(address=at, size=size) for at, size in sorted(native.reads)],
                ownedWrites=len(native.writes), inputAndNeighborWrites=0)


def policy_fixture(n, people, slots=(), strategist=0, wait=0, encoded=False):
    n.reset_pool()
    n.table(0xDF30, n.forces, 0x268, 52, 'CForceData')
    n.table(0x78D50, n.policies, 0xA8, 121, 'CPolicyData')
    n.table(0x80880, n.effects, 0xB0, 101, 'CPolicyEffectData')
    force = bytearray(0x268)
    struct.pack_into('<H', force, 0x10, 42)
    struct.pack_into('<H', force, 0x52, strategist)
    struct.pack_into('<H', force, 0x16C, wait)
    for field, slot, person, days in slots:
        at = (0x54, 0x72, 0x90, 0xAE, 0xCC)[field] + slot * 2
        struct.pack_into('<H', force, at, person)
        struct.pack_into('<H', force, at + 14, days)
    n.put(n.force, n.record(force, 'CForceData'))
    for person, spec in people.items():
        data = bytearray(0x1F0)
        struct.pack_into('<H', data, 0x10, person)
        data[0xE0] = spec.get('force', 1)
        data[0xE6] = spec.get('status', 3)
        data[0x14D] = spec.get('policy', 1)
        data[0x182] = spec['level']
        data[0x1BC] = spec.get('merit', 0)
        data[0x1C2], data[0x1C3] = spec.get('primary', 0), spec.get('secondary', 0)
        if spec.get('marker'):
            struct.pack_into('<H', data, 0x15E, 0x4000)
            data[0x1C5] = 1
        n.put(n.officers + person * 0x1F0, n.record(data, 'CPersonData'))
        n.put(n.corps + data[0xE0] * 0x30 + 0x10, bytes([data[0xE0], 1]))
        word(n, n.corps + data[0xE0] * 0x30 + 0x12, person)
        if data[0xE0] != 1:
            word(n, n.forces + data[0xE0] * 0x268 + 0x10, person)
        kinds = spec.get('kinds', [1])
        n.put(n.policies + data[0x14D] * 0xA8 + 0x98, bytes(kinds).ljust(8, b'\0'))
    for kind in range(1, 101):
        vec = bytearray(40)
        for level in range(10):
            value = kind * 100 + level + 1
            word(n, n.effects + kind * 0xB0 + 0x82 + level * 2, value)
            coded = value ^ kind
            coded = ((coded >> 11) | (coded << 5)) & 65535
            struct.pack_into('<H', vec, level * 4, coded)
        if encoded:
            at = n.vectors + kind * 64
            n.put(at, vec)
            n.put(n.effects + kind * 0xB0 + 0x98, struct.pack('<QQQ', at, at + 40, at + 40))


def policy_expected(people, slots, strategist, wait, kind):
    selected = [42] + ([strategist] if strategist and wait == 0 else [])
    selected += [p for _, _, p, days in slots if days == 0 and p]
    total = 0
    for person in selected:
        spec = people[person]
        if not 1 <= spec.get('policy', 1) <= 120:
            continue
        bonus = 0
        if spec.get('merit') and spec.get('status', 3) != 1 and not spec.get('marker') and spec.get('force', 1) not in range(46, 51):
            bonus = sum(i if i <= 6 else 0 for i in range(1, spec.get('primary', 0) + 1))
            bonus += sum(i if i <= 6 else 0 for i in range(4, 4 + spec.get('secondary', 0)))
        own = min(10, spec['level'] + bonus)
        total += own * sum(k == kind and 1 <= k <= 100 for k in spec.get('kinds', [1]))
    level = min(10, total)
    return level, kind * 100 + level if level > 0 and 1 <= kind <= 100 else 0


def policy_cases(n):
    results = []

    def check(name, people, slots=(), strategist=0, wait=0, encoded=False, kinds=(1,)):
        policy_fixture(n, people, slots, strategist, wait, encoded)
        for kind in kinds:
            level, effect = policy_expected(people, slots, strategist, wait, kind)
            got_level = n.policy_effect(kind, True)
            require((n.officers + 42 * 0x1F0 + 0x14D, 1) in n.reads, 'Native policy byte witness')
            if people[42].get('policy', 1) != 0:
                require((n.officers + 42 * 0x1F0 + 0x182, 1) in n.reads, 'Native policy level byte witness')
            level_proof = evidence(n)
            got_effect = n.policy_effect(kind)
            require((got_level, got_effect) == (level, effect), f'Policy {name}/{kind}: {(got_level, got_effect)} != {(level, effect)}')
            results.append(dict(name=name, people=people, slots=slots, strategist=strategist, wait=wait,
                                encoded=encoded, kind=kind, level=level, effect=effect,
                                levelProof=level_proof, effectProof=evidence(n)))

    for encoded in (False, True):
        for level in (0, 1, 2, 3, 9, 10, 11, 255):
            for kind in (1, 2, 50, 100):
                check(f'level-{level}', {42: dict(level=level, status=1, kinds=[kind])}, encoded=encoded, kinds=(kind,))
    for field in range(5):
        for slot in range(7):
            for days in (0, 1, 32767):
                check(f'slot-{field}-{slot}-{days}', {42: dict(level=1, status=1), 43: dict(level=4)}, [(field, slot, 43, days)])
    for wait in (0, 1, 32767):
        check('strategist-wait', {42: dict(level=2, status=1), 43: dict(level=3)}, strategist=43, wait=wait)
    for status in (1, 3):
        for primary in (0, 1, 3, 6, 7):
            for secondary in (0, 1, 3, 4):
                check('merit', {42: dict(level=0, status=1), 43: dict(level=1, status=status, merit=1, primary=primary, secondary=secondary)}, [(0, 0, 43, 0)])
    for extra in (dict(marker=True), dict(force=46), dict(merit=0)):
        spec = dict(level=2, merit=1, primary=3); spec.update(extra)
        check('merit-exclusion', {42: dict(level=0, status=1), 43: spec}, [(0, 0, 43, 0)])
    check('multiple-kinds-duplicates', {42: dict(level=3, status=1, kinds=[1, 1, 2, 100, 0, 255])}, kinds=(0, 1, 2, 3, 100, 101, 255))
    check('combined-over-cap', {42: dict(level=9, status=1), 43: dict(level=9), 44: dict(level=9)}, [(0, 0, 44, 0)], strategist=43, encoded=True)
    return results


def distance(left, right):
    x1, x2 = left % 220, right % 220
    y1 = 2 * (left // 220) + (1 if x1 % 2 == 0 else 0)
    y2 = 2 * (right // 220) + (1 if x2 % 2 == 0 else 0)
    dx, dy = abs(x1 - x2), abs(y1 - y2)
    return (dx + dy) // 2 if dx <= dy else dx


def trait_state(n, state):
    # Reset all unit/terrain/formation and county inputs between scenarios.
    n.table(0x7E838, n.units, 0x200, 501, 'CArmyUnitData')
    n.table(0x73860, n.terrains, 0x48, 31, 'CTerrainData')
    n.table(0x77428, n.formations, 0xC8, 21, 'CFormationData')
    for tile in (10000, 9999, 10001, 10220, 9780, 10002, 9998, 10010):
        word(n, n.tiles + tile * 0x20 + 0x1C, 0)
    n.setup_unit()
    n.put(n.unit + 0x10, bytes([state.get('kind', 1)]))
    n.put(n.unit + 0x26, bytes([2 if state.get('supply') else 0]))
    n.put(n.unit + 0x23, bytes(state.get('ailments', (0, 0, 0))))
    word(n, n.unit + 0x16, state.get('troops', 4000))
    n.put(n.unit + 0x37, bytes([state.get('order', 0)]))
    n.put(n.officers + 42 * 0x1F0 + 0xA9, bytes([state.get('gender', 0)]))
    terrain = state.get('terrainId', 1)
    n.put(n.tiles + 10000 * 0x20 + 0x10, bytes([terrain]))
    n.put(n.terrains + terrain * 0x48 + 0x42, bytes([state.get('terrain', 1)]))
    n.put(n.formations + 0xC8 + 0xA6, bytes([2 if state.get('siege') else 0]))
    n.put(n.tiles + 10000 * 0x20 + 0x14, bytes([state.get('territory', 1)]))
    for slot, tile, force, gender in state.get('neighbors', []):
        n.add_unit(slot, tile, force, gender)


def trait_expected(condition, value, state, personality=256, commander_filter=0):
    kind, terrain = state.get('kind', 1), state.get('terrain', 1)
    ailments = state.get('ailments', (0, 0, 0))
    radius = value if personality >= 256 and value > 0 else 3
    neighbors = state.get('neighbors', [])
    if condition in (2, 3, 9, 10, 21, 22, 23, 24, 38):
        if condition in (21, 22, 23, 24) and state.get('gender', 0) != 1:
            return False, []
        reach = 1 if condition in (2, 3, 23, 24) else radius if condition in (9, 10) else 3
        hostile = condition in (3, 10, 22, 24, 38)
        selected = [slot for slot, tile, force, gender in neighbors
                    if distance(10000, tile) <= reach and (force != 1) == hostile
                    and (condition != 38 or force in range(46, 51))
                    and (condition not in (21, 22, 23, 24) or gender == 0)
                    and (not commander_filter or 50 + slot == commander_filter)]
        return bool(selected), selected
    tests = {0: False, 1: kind == 1, 4: not state.get('supply', False), 5: any(ailments),
             6: ailments[0] > 0, 7: ailments[1] > 0, 8: ailments[2] > 0, 11: True,
             12: state.get('siege', False), 13: kind == 2, 14: terrain == 4,
             15: terrain == 3, 16: terrain == 2, 17: terrain in (3, 5), 18: terrain == 6,
             19: terrain != 4, 20: state.get('troops', 4000) <= value,
             25: state.get('order', 0) in (11, 12),
             26: sum(force != 1 and distance(10000, tile) <= 1 for _, tile, force, _ in neighbors) >= 2,
             28: state.get('terrainId', 1) in (15, 16, 17, 18),
             31: sum(force == 1 and distance(10000, tile) <= 3 for _, tile, force, _ in neighbors) >= 3,
             32: sum(force != 1 and distance(10000, tile) <= 3 for _, tile, force, _ in neighbors) >= 3,
             34: state.get('territory', 1) == 1, 36: True,
             37: kind == 1 and terrain != 4 and not state.get('siege', False),
             38: any(force in range(46, 51) and distance(10000, tile) <= 3 for _, tile, force, _ in neighbors),
             41: kind == 1 and state.get('order', 0) in (11, 12)}
    result = bool(tests.get(condition, False))
    return result, [1] if result else []


def trait_cases(n):
    results = []
    def check(name, state, condition, value=None, mode=1, filter_=11, personality=256, record=None):
        if value is None:
            value = 3 if condition in (9, 10) else 4000
        try:
            got, targets = n.condition(condition, value, filter_, mode, personality, record)
        except Exception as error:
            raise RuntimeError(f'Trait {name}/{condition}/{mode}: {error}; {n.last_fault}') from error
        actual = condition if mode == 1 else filter_
        number = value if mode == 1 else 0
        expected, selected = trait_expected(actual, number, state, personality)
        if mode == 2:
            selected = []
        require((bool(got), sorted(targets)) == (expected, sorted(selected)),
                f'Trait {name}/{actual}/mode{mode}/value{number}/state{state}: {(got, targets)} != {(expected, selected)}')
        at = n.traits + personality * 0xE8
        require((at + (0xA4 if mode == 1 else 0xAC), 1) in n.reads, 'Native condition/filter byte witness')
        require((at + (0xA8 if mode == 1 else 0xB0), 4) in n.reads, 'Native condition number dword witness')
        if actual == 20:
            require((n.unit + 0x16, 2) in n.reads, 'Native troop word witness')
        results.append(dict(name=name, state=state, condition=actual, value=number, mode=mode,
                            personality=personality, matched=bool(got), targets=targets, **evidence(n)))
    states = [('base', {}), ('transport', dict(kind=2)), ('transport-retreat', dict(kind=2, order=11)),
              ('siege', dict(siege=True)), ('supplied', dict(supply=True))]
    states += [(f'terrain-{v}', dict(terrain=v)) for v in (2, 3, 4, 5, 6)]
    states += [(f'ailment-{i}', dict(ailments=tuple(1 if i == j else 0 for j in range(3)))) for i in range(3)]
    states += [(f'order-{v}', dict(order=v)) for v in (10, 11, 12, 13)]
    states += [(f'village-{v}', dict(terrainId=v)) for v in (15, 16, 18, 19)]
    states += [('foreign-territory', dict(territory=2)), ('unowned-territory', dict(territory=0))]
    for name, state in states:
        trait_state(n, state)
        for condition in range(42):
            check(name, state, condition)
        for filter_ in (0, 1, 11, 37, 100, 255):
            check(name, state, 11, mode=2, filter_=filter_)
    for troops in (0, 1, 3999, 4000, 4001, 60000):
        state = dict(troops=troops); trait_state(n, state)
        for value in (0, 1, 3999, 4000, 4001, 60000):
            check('troops-boundary', state, 20, value)
    spatial = [[], [(2, 10001, 1, 0)], [(2, 10001, 2, 0)],
               [(2, 10001, 1, 0), (3, 9999, 1, 0)], [(2, 10001, 2, 0), (3, 9999, 2, 0)],
               [(2, 10001, 1, 0), (3, 9999, 1, 0), (4, 10220, 1, 1)],
               [(2, 10001, 2, 0), (3, 9999, 2, 0), (4, 10220, 2, 1)],
               [(2, 10002, 46, 0)], [(2, 10010, 1, 0)]]
    for i, neighbors in enumerate(spatial):
        for gender in (0, 1):
            state = dict(neighbors=neighbors, gender=gender); trait_state(n, state)
            for condition in (2, 3, 9, 10, 21, 22, 23, 24, 26, 31, 32, 38):
                check(f'spatial-{i}-{gender}', state, condition, value=1)
    state = dict(neighbors=[(2, 10001, 1, 0), (3, 10002, 2, 0), (4, 10010, 1, 0)])
    trait_state(n, state)
    for personality in (1, 255, 256, 355):
        for radius in (0, 1, 2, 3, 9, 10):
            for condition in (9, 10):
                check('range-boundary', state, condition, radius, personality=personality)
    trait_state(n, {})
    for condition in (42, 99, 100, 101, 254, 255):
        check('no-handler', {}, condition)
    return results


def range_cases(n):
    results = []
    for personality in (1, 255, 256, 355):
        for condition in range(256):
            for value in (-2147483648, -1, 0, 1, 2, 10, 2147483647):
                data = bytearray(0xE8)
                data[0xA4], data[0xB6] = condition, 1
                struct.pack_into('<i', data, 0xA8, value)
                at = n.traits + personality * 0xE8
                n.put(at, n.record(data, 'CPersonalityData'))
                got = n.invoke(0x22C3B0, at) & 0xFFFFFFFF
                expected = 1 if condition in (2, 3, 23, 24) else (
                    value if personality >= 256 and value > 0 else 3) if condition in (9, 10, 21, 22, 31, 32, 39, 40) else 0
                require(got == expected, f'Range {personality}/{condition}/{value}: {got}/{expected}')
                require((at + 0xA4, 1) in n.reads, 'Range condition byte witness')
                if personality >= 256 and condition in (9, 10, 21, 22, 31, 32, 39, 40):
                    require((at + 0xA8, 4) in n.reads, 'Range value dword witness')
                results.append(dict(personality=personality, condition=condition, value=value, result=got,
                                    nativeTraceSha256=sha(struct.pack('<' + 'I' * len(n.trace), *n.trace))))
    return results


def recorded_cases(n, rows):
    results = []
    for case in rows[0]['trials']:
        key = case['key']
        if key in ('officer-14D', 'officer-182'):
            for stage, data in recorded_images(rows, case):
                spec = dict(level=3 if key == 'officer-14D' else data[0x182],
                            policy=data[0x14D] if key == 'officer-14D' else 1)
                people = {42: dict(level=0, status=1), 43: spec}
                slots = [(0, 0, 43, 0)]
                policy_fixture(n, people, slots)
                expected_level, expected_effect = policy_expected(people, slots, 0, 0, 1)
                level = n.policy_effect(1, True)
                effect = n.policy_effect(1)
                offset = 0x14D if key == 'officer-14D' else 0x182
                require((n.officers + 43 * 0x1F0 + offset, 1) in n.reads, 'Recorded policy field not consumed')
                require((level, effect) == (expected_level, expected_effect), 'Recorded policy effect differs')
                results.append(dict(key=key, stage=stage, sourceRecordSha256=sha(data),
                                    context='Recorded edited field; other policy/level and assignment are controlled inputs',
                                    level=level, effect=effect, **evidence(n)))
        elif key.startswith('force-policy-') or key == 'force-strategist':
            for stage, data in recorded_images(rows, case):
                raw = data.ljust(0x268, b'\0')
                ids = policy_members(raw, True)
                people = {42: dict(level=0, status=1)}
                for person in ids:
                    people[person] = dict(level=1, status=1 if person == struct.unpack_from('<H', raw, 0x10)[0] else 3)
                policy_fixture(n, people)
                n.put(n.force, n.record(raw, 'CForceData'))
                level = n.policy_effect(1, True)
                effect = n.policy_effect(1)
                expected = min(10, len(ids))
                require((level, effect) == (expected, 100 + expected if expected else 0), 'Recorded force policy sum differs')
                for edit in case['steps'][0]:
                    for change in edit['changes']:
                        require((n.force + change['offset'], change['size']) in n.reads, 'Recorded force field not consumed')
                results.append(dict(key=key, stage=stage, sourceRecordSha256=sha(data),
                                    context='Recorded ForceRead prefix; unrecorded tail zero and valid level-one lookup officers',
                                    selected=ids, level=level, effect=effect, **evidence(n)))
        elif key in ('personality-A4', 'personality-A8', 'personality-AC'):
            images = recorded_images(rows, case)
            for state in ({}, dict(terrain=4)):
                state = dict(state)
                if key == 'personality-A8' and images[0][1][0xA4] == 20:
                    state['troops'] = max(0, min(60000, struct.unpack_from('<i', images[0][1], 0xA8)[0]))
                trait_state(n, state)
                for stage, data in images:
                    condition, filter_ = data[0xA4], data[0xAC]
                    value = struct.unpack_from('<i', data, 0xA8)[0]
                    for mode in (1, 2):
                        got, selected = n.condition(condition, value, filter_, mode, record=data)
                        number = value if mode == 1 else struct.unpack_from('<i', data, 0xB0)[0]
                        expected, targets = trait_expected(condition if mode == 1 else filter_, number, state)
                        require((bool(got), sorted(selected)) == (expected, sorted(targets) if mode == 1 else []), 'Recorded condition/filter differs')
                        results.append(dict(key=key, stage=stage, sourceRecordSha256=sha(data), state=state,
                                            context='Recorded trait fields; controlled unit and personality index 256',
                                            mode=mode, matched=bool(got), targets=selected, **evidence(n)))
    return results


def run(args):
    old = MemoryImage.load(args.legacy_image, int(args.legacy_base, 0))
    new = MemoryImage.load(args.november_image, int(args.november_base, 0))
    require(sha(old.data) == LEGACY_IMAGE and sha(new.data) == NOVEMBER_IMAGE, 'Pinned original images')
    functions, globals_, verified, changed = mappings(old, new)
    rows = None
    if args.log:
        require(args.layout_evidence, 'Recorded log requires Native layout evidence')
        rows = [json.loads(line) for line in Path(args.log).read_text(encoding='utf-8-sig').splitlines() if line.strip()]
        proof = verify_rows(rows, read_json(args.layout_evidence))
        require(proof['verifiedTrials'] == 93 and not proof['failures'] and not proof['snapshotDifferences'], 'Recorded live round-trip evidence')
    results = []
    for image, november in ((old, False), (new, True)):
        n = EffectsNative(image, november, functions, globals_)
        policies = policy_cases(n)
        print('PASS policy final effects', november, len(policies), flush=True)
        traits = trait_cases(n)
        print('PASS personality conditions/filters', november, len(traits), flush=True)
        ranges = range_cases(n)
        print('PASS original range helper', november, len(ranges), flush=True)
        recorded = recorded_cases(n, rows) if rows else []
        print('PASS recorded edit images', november, len(recorded), flush=True)
        results.append(dict(build='november' if november else 'legacy', policies=policies, traits=traits,
                            ranges=ranges, recorded=recorded,
                            nativeExecutedRvas=sorted(n.executed), hostCalls=n.host_calls))
    return dict(passed=True, gameAccess=False, screenAccess=False, liveWrites=0,
                imageSha256=[LEGACY_IMAGE, NOVEMBER_IMAGE], verifiedFunctionPairs=verified,
                changedRangeHelper=changed, builds=results,
                boundaries=['CRT mutex lock/unlock in the single-thread emulator', 'Exact memset semantics'],
                semanticStubs=[], scope='Controlled input fixtures; no live game or historical world reconstruction')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('legacy-image', 'legacy-base', 'november-image', 'november-base', 'output'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--log')
    parser.add_argument('--layout-evidence')
    args = parser.parse_args()
    output = Path(args.output)
    require(not output.exists(), 'Output must be new')
    result = run(args)
    result['sources'] = {p.name: sha(p.read_bytes()) for p in
                         (Path(__file__), Path(__file__).with_name('editor_effects_audit.py'))}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print('PASS original policy/condition/filter audit; no gameplay function was replaced', flush=True)


if __name__ == '__main__':
    main()
