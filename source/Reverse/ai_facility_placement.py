# Full native placement gates and area-capacity counting, with copied inputs only.
# python ai_facility_placement.py [--static | --smoke | --vectors ../Tests/AiFacilityPlacementVectors.json]
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import time

from ai_executor_audit import Harness
from ai_facility_special import read_clock, change_input
from ai_facility_spacing import restore
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace
from game import Game
from image import Image, IMAGE
from oracle import OracleError
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EFLAGS, UC_X86_REG_RDI


BRANCHES = {
    0x2791B8: ('force', False), 0x2791CB: ('hex', False),
    0x2791DF: ('sceneryPresent', False), 0x2791ED: ('sceneryClear', True),
    0x2791F9: ('definition', False), 0x279209: ('terrain', False),
    0x279225: ('buildingPresent', False), 0x279233: ('buildingCategory4', True),
    0x279251: ('buildingHostile', False), 0x279262: ('hexOwnerSame', True),
    0x279283: ('hexOwnerHostile', False), 0x279297: ('capacity', True),
    0x27929D: ('requestCategory4', True), 0x2792B4: ('areaValid', False),
    0x2792C1: ('areaOwnerSame', True), 0x2792C6: ('checkPrerequisite', False),
    0x2792D5: ('prerequisiteAllowed', False), 0x2792F0: ('spacing', False)}
GATES = ('force', 'hex', 'scenery', 'definition', 'terrain', 'existing', 'owner', 'capacity', 'areaOwner', 'prerequisite', 'spacing')


def gate_facts(branches):
    values = {frame['name']: frame['value'] for frame in branches}
    facts = {name: value for name, value in (
        ('force', values.get('force', True)), ('hex', values.get('hex', True)),
        ('scenery', not values.get('sceneryPresent', False) or values.get('sceneryClear', True)),
        ('definition', values.get('definition', True)), ('terrain', values.get('terrain', True)),
        ('existing', not values.get('buildingPresent', False) or
            values.get('buildingCategory4', True) and values.get('buildingHostile', True)),
        ('owner', values.get('hexOwnerSame', True) or values.get('hexOwnerHostile', False)),
        ('capacity', values.get('capacity', True)),
        ('areaOwner', not values.get('requestCategory4', False) or not values.get('areaValid', True)
            or values.get('areaOwnerSame', True)),
        ('prerequisite', not values.get('checkPrerequisite', False) or values.get('prerequisiteAllowed', True)),
        ('spacing', values.get('spacing', True)))}
    markers = {'scenery': 'sceneryPresent', 'existing': 'buildingPresent', 'owner': 'hexOwnerSame',
        'areaOwner': 'requestCategory4', 'prerequisite': 'checkPrerequisite'}
    # Omit the unexecuted suffix; never label its default neutral values as observations.
    return {name: value for name, value in facts.items() if markers.get(name, name) in values}


def placement_plan(own, enemy, friendly, policy_effect_value):
    result = [{'variant': 'actual'}]
    for variant in ('nullForce', 'dummyForce', 'nullHex', 'nullDefinition', 'dummyDefinition', 'terrainDenied', 'invalidArea'):
        result.append({'variant': variant})
    for terrain in (0, 30, 31, 255):
        result.append({'variant': 'terrainIndex', 'terrain': terrain})
    for flags in (0, 1, 2, 3, 128, 255):
        result.append({'variant': 'sceneryFlags', 'flags': flags})
    for scenery_id in (0, 101, 255):
        result.append({'variant': 'sceneryFallback', 'sceneryId': scenery_id})
    for category in (0, 1, 2, 3, 4, 5, 255):
        for owner in dict.fromkeys((own, enemy, friendly, 0)):
            result.append({'variant': 'existingFacility', 'existingCategory': category, 'existingOwner': owner})
    for owner in dict.fromkeys((own, enemy, friendly, 0, 255)):
        result.append({'variant': 'hexOwner', 'owner': owner})
    for limit in (0, 2, 3, 4, 255):
        result.append({'variant': 'capacity', 'limit': limit})
    for category in (0, 1, 2, 3, 4, 5, 255):
        result.append({'variant': 'requestCategory', 'category': category})
    for category in (2, 4):
        for owner in dict.fromkeys((own, enemy, friendly, 0)):
            result.append({'variant': 'areaCenterOwner', 'category': category, 'owner': owner})
    for option in (0, 1, 2, 0xFFFFFFFF):
        for required in sorted({0, policy_effect_value, policy_effect_value + 1, 255}):
            result.append({'variant': 'prerequisite', 'option': option, 'required': required})
    for category in (1, 2):
        result.append({'variant': 'spacingCategory2', 'category': category})
    return result


def verify(game, hex_id, smoke):
    clock = read_clock(game)
    h = Harness(game)
    o, raw = h.o, h.raw
    target = o.q(raw + 0xE270 + hex_id * 8)
    if not o.call32(0x32F500, target) or o.call32(0x32F500, o.call(0x22FB10, target)):
        raise AssertionError('an actual valid empty target hex is required')
    force = o.call(0x22C9D0, target)
    own = o.call32(0x22DC80, force)
    area = o.call(0x229EA0, target)
    area_center_id = int.from_bytes(o.read(area + 0x3A, 2), 'little')
    area_center = o.q(raw + 0xE270 + 8 * area_center_id)
    definitions = {kind: o.q(raw + 0x73958 + 8 * kind) for kind in range(31)}
    definition = definitions[5]
    survey = Trace(o, {0x22FB10: None})
    actual_capacity = o.call32(0x2C9E30, area)
    tiles = survey.events.copy()
    survey.close()
    facilities = sorted(set(e['result64'] for e in tiles if o.call32(0x32F500, e['result64'])))
    if len(facilities) != 3:
        raise AssertionError('capacity matrix requires three actual facilities in this area')
    source = facilities[1]
    source_id = o.call32(0x22DEA0, source)
    source_hex_id = int.from_bytes(o.read(source + 0x10, 2), 'little')
    source_hex = o.q(raw + 0xE270 + source_hex_id * 8)
    if o.call32(0x2AA850, hex_id, source_hex_id) != 1:
        raise AssertionError('the actual source facility must be at distance 1')
    valid_forces = [index for index in range(1, 52) if o.call32(0x32F500, o.q(raw + 0xDF30 + index * 8))]
    enemy = next(index for index in valid_forces if o.call32(0x2CA280, own, index))
    friendly = next((index for index in valid_forces if index != own and not o.call32(0x2CA280, own, index)), None)
    alliance_control = friendly is None
    if alliance_control:
        friendly = next(index for index in valid_forces if index not in (own, enemy))
    scenery_id = next(index for index in range(1, 101) if o.call32(0x32F500, o.q(raw + 0x85A90 + index * 8)))
    scenery = o.q(raw + 0x85A90 + scenery_id * 8)
    scenery_hex_id = int.from_bytes(o.read(scenery + 0x5C, 2), 'little')
    scenery_hex = o.q(raw + 0xE270 + scenery_hex_id * 8)
    policy_effect = next(index for index in range(1, 101) if o.call32(0x32F500, o.q(raw + 0x80880 + index * 8)))
    policy_definition = o.q(raw + 0x80880 + policy_effect * 8)
    policy_type = Image().rtti_vtables().get(o.q(policy_definition) - o.exe)
    if policy_type != '.?AVCPolicyEffectData@@':
        raise AssertionError('unexpected prerequisite definition type')
    level = o.call32(0x41C440, force, policy_effect)
    if not 0 <= level < 255:
        raise AssertionError('policy effect value must provide a byte boundary above the current value')
    capacity_cases, cases, failures, interrupted = [], [], [], None
    capacity_plan = [(category, count, limit) for category in (0, 1, 2, 3, 4, 5, 255)
        for count in (0, 1, 3) for limit in (0, 1, 2, 3, 4, 255)]
    if smoke:
        capacity_plan = [(2, 3, 3), (2, 3, 4), (1, 3, 0), (1, 3, 1)]
    for category, count, limit in capacity_plan:
        current = read_clock(game)
        if current != clock:
            interrupted = {'phase': 'capacity', 'reason': 'clock/world changed', 'clock': current}
            break
        changes, frames = [], []
        for index, facility in enumerate(facilities):
            change_input(h, changes, facility + 0x12, 5 if index < count else 31, 1, 'capacity facility kind in copied input')
        change_input(h, changes, definition + 0x2C, category, 1, 'capacity facility category in copied input')
        change_input(h, changes, area + 0x6E, limit, 1, 'area capacity byte in copied input')
        def observe_count(uc, address, size, user):
            frames.append({'count': uc.reg_read(UC_X86_REG_RDI) & 0xFFFFFFFF,
                'capacity': o.read(area + 0x6E, 1)[0]})
        handle = o.mu.hook_add(UC_HOOK_CODE, observe_count, None, o.exe + 0x2C9F32, o.exe + 0x2C9F32)
        row = {'category': category, 'includedFacilities': count, 'limit': limit, 'copiedInputChanges': changes}
        try:
            actual = o.call32(0x2C9E30, area)
            row['native'] = actual
            relevant = count if category in (2, 3, 4) else 0
            if actual != int(relevant >= limit) or frames != [{'count': relevant, 'capacity': limit}]:
                raise AssertionError((actual, frames, relevant, limit))
            capacity_cases.append({**row, 'native': actual, 'frames': frames})
        except (OracleError, AssertionError) as error:
            failures.append({**row, 'phase': 'capacity', 'error': str(error), 'frames': frames})
        finally:
            o.mu.hook_del(handle)
            restore(o, changes)
    for input_area in (() if interrupted else (0, o.q(raw + 0x6CAF0))):
        actual = o.call32(0x2C9E30, input_area)
        valid = bool(o.call32(0x32F500, input_area))
        capacity_cases.append({'variant': 'invalidArea', 'area': input_area, 'valid': valid, 'native': actual})
        if valid or actual != 0:
            raise AssertionError('invalid-area capacity control failed')
    baseline = []
    if alliance_control:
        address = force + 0xEA + friendly
        before = o.read(address, 1)[0]
        change_input(h, baseline, address, before | 1, 1,
            'actual force alliance duration low bit for nonhostile control in copied input')
    if not interrupted and o.call32(0x2CA280, own, friendly):
        raise AssertionError('original hostility helper did not accept the nonhostile input control')
    for address, value, size, role in (
            (definition + 0x50, 0, 8, 'legacy candidate parameter start'),
            (definition + 0x58, 0, 8, 'legacy candidate parameter end'),
            (definition + 0x3C, 0xFFFFFFFF, 4, 'candidate terrain mask'),
            (definition + 0x34, 0, 1, 'candidate prerequisite index')):
        change_input(h, baseline, address, value, size, role + ' in copied input')
    plan = placement_plan(own, enemy, friendly, level)
    if smoke:
        plan = [c for c in plan if c['variant'] in ('actual', 'nullForce', 'sceneryFlags', 'capacity', 'spacingCategory2')]
    for number, config in enumerate([] if interrupted else plan):
        current = read_clock(game)
        if current != clock:
            interrupted = {'phase': 'placement', 'caseIndex': number, 'reason': 'clock/world changed', 'clock': current}
            break
        changes, branches = [], []
        input_force, input_hex, input_definition = force, target, definition
        variant = config['variant']
        option = config.get('option', 0)
        def change(address, value, size, role):
            change_input(h, changes, address, value, size, role + ' in copied input')
        if variant == 'nullForce': input_force = 0
        if variant == 'dummyForce': input_force = o.q(raw + 0xDF30)
        if variant == 'nullHex': input_hex = 0
        if variant == 'nullDefinition': input_definition = 0
        if variant == 'dummyDefinition': input_definition = definitions[0]
        if variant == 'terrainDenied': change(definition + 0x3C, 0, 4, 'denied terrain mask')
        if variant == 'terrainIndex': change(target + 0x10, config['terrain'], 1, 'target terrain byte')
        if variant == 'invalidArea':
            change(target + 0x12, 0, 2, 'target area index 0')
            change(definition + 0x2C, 4, 1, 'requested category 4 with invalid area')
        if variant == 'sceneryFlags':
            change(scenery_hex + 0x1E, 0, 1, 'remove original scenery hex link')
            change(target + 0x1E, scenery_id, 1, 'target scenery link')
            change(scenery + 0x5C, hex_id, 2, 'scenery target hex link')
            change(scenery + 0x5F, config['flags'], 1, 'scenery flag byte')
        if variant == 'sceneryFallback':
            change(target + 0x1E, config['sceneryId'], 1, 'target scenery index')
            change(o.q(raw + 0x85A90) + 0x5F, 1, 1, 'invalid dummy scenery flag')
        if variant == 'existingFacility':
            change(source_hex + 0x1A, 0, 2, 'remove original facility hex link')
            change(target + 0x1A, source_id, 2, 'target facility link')
            change(source + 0x10, hex_id, 2, 'facility target hex link')
            change(source + 0x12, 11, 1, 'existing facility kind')
            change(source + 0x13, config['existingOwner'], 1, 'existing facility owner')
            change(definitions[11] + 0x2C, config['existingCategory'], 1, 'existing facility category')
        if variant == 'hexOwner': change(target + 0x14, config['owner'], 1, 'target owner byte')
        if variant == 'capacity': change(area + 0x6E, config['limit'], 1, 'area capacity byte')
        if variant in ('requestCategory', 'areaCenterOwner', 'spacingCategory2'):
            change(definition + 0x2C, config['category'], 1, 'requested facility category')
        if variant == 'areaCenterOwner': change(area_center + 0x14, config['owner'], 1, 'area center owner byte')
        if variant == 'prerequisite':
            change(definition + 0x34, policy_effect, 1, 'requested policy effect index')
            change(definition + 0x41, config['required'], 1, 'required policy effect value')
        if variant == 'spacingCategory2': change(source + 0x12, 6, 1, 'distance-1 facility category 2')
        trace = Trace(o, {0x230FD0: None, 0x42B40: None, 0xEE90: None, 0x22C9D0: None,
            0x2CA280: None, 0x2C9E30: None, 0x22C810: None, 0x2C4990: None, 0x27BD50: None})
        def observe_branch(uc, address, size, user):
            rva = address - o.exe
            name, zero = BRANCHES[rva]
            zf = bool(uc.reg_read(UC_X86_REG_EFLAGS) & 0x40)
            branches.append({'rva': rva, 'name': name, 'value': zf == zero})
        handles = [o.mu.hook_add(UC_HOOK_CODE, observe_branch, None, o.exe + rva, o.exe + rva) for rva in BRANCHES]
        row = {**config, 'option': option, 'inputForce': input_force, 'inputHex': input_hex,
            'inputDefinition': input_definition, 'requestCategory': o.read(definition + 0x2C, 1)[0],
            'copiedInputChanges': changes}
        try:
            actual = o.call32(0x279190, input_force, input_hex, input_definition, option)
            row['native'] = actual
            facts = gate_facts(branches)
            if actual != int(all(facts.values())):
                raise AssertionError((actual, facts, branches))
            cases.append({**row, 'native': actual, 'evaluatedGates': facts,
                'branches': branches, 'events': trace.events.copy()})
        except (OracleError, AssertionError) as error:
            failures.append({**row, 'phase': 'placement', 'error': str(error),
                'branches': branches, 'events': trace.events.copy()})
        finally:
            trace.close()
            for handle in handles: o.mu.hook_del(handle)
            restore(o, changes)
        if (number + 1) % 20 == 0:
            print(f'{number + 1}/{len(plan)} placement cases; {len(failures)} failures', flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid, 'clockBefore': clock, 'clockAfter': after,
        'clockStable': clock == after, 'completeMatrix': not smoke and not interrupted
            and len(cases) == len(plan) and len(capacity_cases) == len(capacity_plan) + 2,
        'plannedPlacementCases': len(plan), 'interrupted': interrupted,
        'method': 'original full 279190 and 2C9E30; observed conditional branches and actual helper results; copied data inputs only',
        'oracleSha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
        'targetHex': hex_id, 'forceId': own, 'enemyId': enemy, 'nonhostileOwnerId': friendly,
        'nonhostileOwnerIsValidForce': friendly in valid_forces,
        'nonhostileOwnerUsesCopiedAlliance': alliance_control,
        'area': area, 'areaCenterHex': area_center_id, 'actualCapacityResult': actual_capacity,
        'actualFacilities': facilities, 'sceneryId': scenery_id, 'policyEffect': policy_effect, 'policyEffectValue': level,
        'policyEffectDefinitionType': policy_type,
        'listPoolSnapshot': game.pool_snapshot, 'baselineChanges': baseline,
        'capacityCases': capacity_cases, 'cases': cases, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--hex', type=int, default=12780)
    parser.add_argument('--vectors', type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--static', action='store_true')
    mode.add_argument('--smoke', action='store_true')
    args = parser.parse_args()
    if args.vectors and (args.static or args.smoke): parser.error('vectors require the complete native matrix')
    folder = HERE / '.cache' / 'ai_facility_placement'
    folder.mkdir(parents=True, exist_ok=True)
    if args.static:
        image = Image()
        result = {'imageSha256': hashlib.sha256(Path(IMAGE).read_bytes()).hexdigest(),
            'code': {f'{rva:X}': image.flowtext(rva) for rva in (0x279190, 0x230FD0, 0x2C9E30, 0x2C4990, 0x22C810)}}
        path = folder / 'static.json'
    else:
        game = Game()
        before = read_clock(game)
        time.sleep(3)
        if read_clock(game) != before: raise SystemExit('clock changed; no native calls made')
        game = ListPoolSnapshot()
        if read_clock(game) != before: raise SystemExit('clock changed during pool capture; no native calls made')
        result = verify(game, args.hex, args.smoke)
        path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result) if not args.static else json.dumps(result, ensure_ascii=False, indent=2)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable'] and result['completeMatrix'] and not result['failures']:
        args.vectors.write_text(text.replace('\n', '\r\n'), encoding='utf-8', newline='')
    print(f"{len(result.get('cases', []))} placement / {len(result.get('capacityCases', []))} capacity cases; "
          f"{len(result.get('failures', []))} failures; {path}")
    if not args.static and (not result['clockStable'] or result['failures']):
        raise SystemExit('diagnostic log saved; no successful fixture written')


if __name__ == '__main__': main()
