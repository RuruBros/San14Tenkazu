# Original facility prerequisite and parameter getters; copied data inputs only.
# python ai_facility_prerequisite.py [--static | --smoke | --vectors ../Tests/AiFacilityPrerequisiteVectors.json]
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
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


GLOBAL_POINTER = 0x27D29A0
FEATURE_POINTER = 0x27D2A10
FUNCTIONS = {rva: None for rva in (0x32F500, 0x56EE40, 0x56EE30, 0x22DC50,
    0x42410, 0x424A0, 0x42500, 0x41C440, 0xDB00)}


def encode_byte(value, index, rotation):
    raw = (value ^ index) & 255
    return ((raw >> rotation) | (raw << (8 - rotation))) & 255


def plan(policy_id, level):
    def row(index=15, length=76, state=1, flags=8, required=255, policy=policy_id, **extra):
        return {'facilityIndex': index, 'parameterBytes': length, 'globalValue': state,
            'featureFlags': flags, 'required': required, 'policyEffectId': policy,
            'globalPointerPresent': True, 'featurePointerPresent': True,
            'forceVariant': 'actual', **extra}
    result = [row(index, length, state, flags, required, variant='matrix')
        for index in (5, 14, 15, 16, 17, 18) for length in (0, 76)
        for state in (0, 1) for flags in (0, 8)
        for required in sorted({0, level, level + 1, 255})]
    result += [row(14, length, 1, 8, required, variant='formatBoundary')
        for length in (72, 75, 77, 78, 79, 80) for required in (level, level + 1)]
    result += [row(index, length, state, 8, 255, policy, variant='policyIndexBoundary')
        for policy in (0, 100, 101, 255) for index in (14, 15)
        for length in (0, 76) for state in (0, 1)]
    result += [row(length=length, flags=flags, variant='featureBit')
        for flags in (1, 7, 9, 128, 255) for length in (0, 76)]
    result += [row(index, length, 0xFFFFFFFF, variant='globalNonzero')
        for index in (14, 15) for length in (0, 76)]
    result += [row(length=length, globalPointerPresent=False, variant='nullGlobalPointer') for length in (0, 76)]
    result += [row(length=length, featurePointerPresent=False, variant='nullFeaturePointer') for length in (0, 76)]
    result += [row(index, length, variant='facilityIndexBoundary') for index in (1, 30) for length in (0, 76)]
    result += [row(length=length, forceVariant=force, variant='invalidForce')
        for force in ('null', 'dummy') for length in (0, 76)]
    return result


def signed32(value):
    return value - 0x100000000 if value & 0x80000000 else value


def verify(game, smoke):
    clock = read_clock(game)
    h = Harness(game)
    o, raw = h.o, h.raw
    target = o.q(raw + 0xE270 + 12780 * 8)
    force = o.call(0x22C9D0, target)
    if not o.call32(0x32F500, force): raise AssertionError('an actual valid force is required')
    force_id = o.call32(0x22DC80, force)
    definitions = {kind: o.q(raw + 0x73958 + 8 * kind) for kind in (1, 5, 14, 15, 16, 17, 18, 30)}
    parameter_arrays = {}
    for index, definition in definitions.items():
        start = o.q(definition + 0x50)
        if (o.call32(0x22DC50, definition) != index or not start
                or o.q(definition + 0x58) - start != 76
                or o.q(o.q(definition) + 0x48) != o.exe + 0xDB00):
            raise AssertionError('actual 76-byte facility parameter array and original decoder required')
        parameter_arrays[index] = start
    policy_id = next(index for index in range(1, 101) if o.call32(0x32F500, o.q(raw + 0x80880 + 8 * index)))
    policy_type = Image().rtti_vtables().get(o.q(o.q(raw + 0x80880 + 8 * policy_id)) - o.exe)
    if policy_type != '.?AVCPolicyEffectData@@': raise AssertionError('policy definition RTTI mismatch')
    level = o.call32(0x41C440, force, policy_id)
    if not 0 < level < 254: raise AssertionError('current policy value must provide distinct byte boundary controls')
    original_global, original_feature = o.q(o.exe + GLOBAL_POINTER), o.q(o.exe + FEATURE_POINTER)
    if not original_global: raise AssertionError('an actual global header is required')
    original_global_value = int.from_bytes(o.read(original_global, 4), 'little')
    # The original initializer at 0x4F9A16 allocates 13 bytes via 0x3F6BC0,
    # then zeroes qword +0, dword +8 and byte +12. Reproduce only this data
    # construction in Unicorn; do not invoke the surrounding game UI routine.
    feature = o.call(0x3F6BC0, 13, keep=True)
    if not feature: raise AssertionError('original native allocation failed')
    initialization = []
    for offset, size in ((0, 8), (8, 4), (12, 1)):
        change_input(h, initialization, feature + offset, 0, size, 'new copied feature context initialization')
    configs = plan(policy_id, level)
    if smoke:
        configs = [c for c in configs if c['variant'] == 'matrix' and c['facilityIndex'] in (14, 15)
            and c['required'] == level + 1 and c['globalValue'] == 1]
        configs += [c for c in plan(policy_id, level) if c['variant'] in ('nullGlobalPointer', 'nullFeaturePointer', 'invalidForce')]
    cases, failures, interrupted = [], [], None
    for number, config in enumerate(configs):
        current = read_clock(game)
        if current != clock:
            interrupted = {'caseIndex': number, 'reason': 'clock/world changed', 'clock': current}
            break
        index, length = config['facilityIndex'], config['parameterBytes']
        definition, array = definitions[index], parameter_arrays[index]
        policy, required = config['policyEffectId'], config['required']
        encoded_format = length & ~3 == 76
        # The unused format deliberately disagrees, including a different
        # validity result for policy ID 1. Native getters select the format.
        legacy_policy = 101 if encoded_format and policy == 1 else (policy ^ 255 if encoded_format else policy)
        legacy_required = required ^ 255 if encoded_format else required
        encoded_policy = policy if encoded_format else (101 if policy == 1 else policy ^ 255)
        encoded_required = required if encoded_format else required ^ 255
        changes = []
        for address, value, size, role in (
            (o.exe + GLOBAL_POINTER, original_global if config['globalPointerPresent'] else 0, 8, 'global header pointer'),
            (original_global, config['globalValue'], 4, 'global header nonzero value'),
            (o.exe + FEATURE_POINTER, feature if config['featurePointerPresent'] else 0, 8, 'feature context pointer'),
            (feature + 6, config['featureFlags'], 1, 'feature flags'),
            (definition + 0x58, array + length, 8, 'parameter end pointer'),
            (definition + 0x34, legacy_policy, 1, 'legacy policy index'),
            (definition + 0x41, legacy_required, 1, 'legacy required value'),
            (array + 8, encode_byte(encoded_policy, index, 1), 1, 'encoded policy index'),
            (array + 0x48, encode_byte(encoded_required, index, 3), 1, 'encoded required value')):
            change_input(h, changes, address, value, size, role + ' in copied input')
        input_force = {'actual': force, 'null': 0, 'dummy': o.q(raw + 0xDF30)}[config['forceVariant']]
        row = {**config, 'inputForce': input_force, 'inputDefinition': definition,
            'legacyPolicyIndex': legacy_policy, 'legacyRequired': legacy_required,
            'encodedPolicyByte': o.read(array + 8, 1)[0], 'encodedRequiredByte': o.read(array + 0x48, 1)[0],
            'copiedInputChanges': changes}
        # Direct getter returns are logged from call() below. Trace only their
        # nested decoder/index calls, whose actual return sites are in native code.
        trace = Trace(o, {0x22DC50: None, 0xDB00: None})
        try:
            pointer = o.call(0x42410, definition)
            raw_id = o.call32(0x424A0, definition)
            required_read = o.call32(0x42500, definition)
            policy_valid = bool(o.call32(0x32F500, pointer))
            getters = trace.events.copy()
            trace.close()
            trace = Trace(o, FUNCTIONS)
            expected_pointer = o.q(raw + 0x80880 + 8 * (policy if policy <= 100 else 0))
            if (pointer, raw_id, required_read) != (expected_pointer, policy, required):
                raise AssertionError(('parameter getters', pointer, raw_id, required_read, expected_pointer))
            actual = o.call32(0x2C4990, input_force, definition)
            row['native'] = actual
            events = trace.events.copy()
            if any('result' not in e for e in getters + events):
                raise AssertionError('an observed nested helper did not return')
            force_valid = config['forceVariant'] == 'actual'
            exception = force_valid and config['globalPointerPresent'] and config['globalValue'] != 0 \
                and config['featurePointerPresent'] and bool(config['featureFlags'] & 8) and 15 <= index <= 17
            effect_calls = [e for e in events if e['rva'] == 0x41C440]
            value = signed32(effect_calls[0]['result']) if effect_calls else None
            lookup_calls = [e for e in events if e['rva'] == 0x42410]
            if len(lookup_calls) != int(force_valid and not exception):
                raise AssertionError('policy lookup short-circuit did not match original prerequisite path')
            if len(effect_calls) != int(force_valid and not exception and policy_valid):
                raise AssertionError('policy value short-circuit did not match original prerequisite path')
            expected = force_valid and (exception or not policy_valid or value >= required)
            if actual != int(expected): raise AssertionError((actual, expected, value))
            cases.append({**row, 'forceValid': force_valid, 'exception': exception,
                'lookupPolicyPointer': pointer, 'policyDefinitionValid': policy_valid,
                'rawPolicyIndex': raw_id, 'requiredRead': required_read, 'evaluatedEffectValue': value,
                'getterEvents': getters, 'events': events})
        except (OracleError, AssertionError) as error:
            failures.append({**row, 'error': str(error), 'events': trace.events.copy()})
        finally:
            trace.close()
            restore(o, changes)
        if (number + 1) % 40 == 0:
            print(f'{number + 1}/{len(configs)} cases; {len(failures)} failures', flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid, 'clockBefore': clock, 'clockAfter': after,
        'clockStable': clock == after, 'completeMatrix': not smoke and not interrupted and len(cases) == len(configs),
        'plannedCases': len(configs), 'interrupted': interrupted,
        'method': 'original full 2C4990 and parameter getters; actual helper results; copied data inputs only',
        'oracleSha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
        'forceId': force_id, 'policyEffectId': policy_id, 'policyEffectValue': level,
        'policyEffectDefinitionType': policy_type, 'listPoolSnapshot': game.pool_snapshot,
        'originalGlobalPointer': original_global, 'originalGlobalValue': original_global_value,
        'originalFeaturePointer': original_feature,
        'copiedFeatureConstruction': {'allocator': 0x3F6BC0, 'size': 13, 'result': feature,
            'originalInitialization': 0x4F9A16, 'changes': initialization},
        'cases': cases, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--static', action='store_true')
    mode.add_argument('--smoke', action='store_true')
    args = parser.parse_args()
    if args.vectors and (args.static or args.smoke): parser.error('vectors require the complete native matrix')
    folder = HERE / '.cache' / 'ai_facility_prerequisite'
    folder.mkdir(parents=True, exist_ok=True)
    if args.static:
        image = Image()
        result = {'imageSha256': hashlib.sha256(Path(IMAGE).read_bytes()).hexdigest(),
            'code': {f'{rva:X}': image.flowtext(rva) for rva in
                (0x2C4990, 0x42410, 0x424A0, 0x42500, 0xDB00, 0x56EE40, 0x56EE30, 0x4F9A0A, 0x4F98E5)}}
        path = folder / 'static.json'
    else:
        game = Game()
        before = read_clock(game)
        time.sleep(3)
        if read_clock(game) != before: raise SystemExit('clock changed; no native calls made')
        game = ListPoolSnapshot()
        if read_clock(game) != before: raise SystemExit('clock changed during pool capture; no native calls made')
        result = verify(game, args.smoke)
        path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result) if not args.static else json.dumps(result, ensure_ascii=False, indent=2)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable'] and result['completeMatrix'] and not result['failures']:
        args.vectors.write_text(text.replace('\n', '\r\n'), encoding='utf-8', newline='')
    print(f"{len(result.get('cases', []))}/{result.get('plannedCases', 0)} cases; "
          f"{len(result.get('failures', []))} failures; {path}")
    if not args.static and (not result['clockStable'] or result['failures'] or result['interrupted']):
        raise SystemExit('diagnostic log saved; no successful fixture written')


if __name__ == '__main__': main()
