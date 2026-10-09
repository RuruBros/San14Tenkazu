# Audit the original facility spacing helper against real rings in copied memory.
# python ai_facility_spacing.py [--static | --smoke | --vectors ../Tests/AiFacilitySpacingVectors.json]
# Native list generation, allocation, lookup, byte decoding and cleanup remain intact.
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import time

from ai_executor_audit import Harness
from ai_facility_special import read_clock, native_list, change_input
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list
from game import Game
from image import Image, IMAGE
from oracle import OracleError
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RBP, UC_X86_REG_RDI


def restore(o, changes):
    for change in reversed(changes):
        o.poke(change['address'], change['before'].to_bytes(change['size'], 'little'))


def configurations():
    result = []
    for distance in range(1, 5):
        for spacing in (0, 1, 2, 3, 4, 5, 255):
            for category in (1, 2, 3, 4):
                for mode in (0, 1):
                    for length in (0, 76):
                        result.append({'distance': distance, 'spacing': spacing, 'category': category,
                            'mode': mode, 'parameterBytes': length, 'variant': 'boundary'})
    for category in (0, 5, 255):
        for mode in (0, 1):
            for length in (0, 76):
                result.append({'distance': 2, 'spacing': 2, 'category': category,
                    'mode': mode, 'parameterBytes': length, 'variant': 'otherCategory'})
    for length in (72, 75, 77, 78, 79, 80):
        for spacing in (1, 2):
            result.append({'distance': 2, 'spacing': spacing, 'category': 1,
                'mode': 1, 'parameterBytes': length, 'variant': 'formatBoundary'})
    for mode in (2, 0xFFFFFFFF):
        result.append({'distance': 2, 'spacing': 2, 'category': 2,
            'mode': mode, 'parameterBytes': 76, 'variant': 'nonzeroMode'})
    for mode in (0, 1):
        for variant in ('noRingTestFacility', 'centerOnly', 'emptyTarget', 'invalidKind31', 'invalidKind255', 'missingDefinition'):
            result.append({'distance': 0 if variant in ('noRingTestFacility', 'centerOnly') else 1,
                'spacing': 255, 'category': 1, 'mode': mode, 'parameterBytes': 76, 'variant': variant})
        result.append({'distance': 5, 'spacing': 255, 'category': 1,
            'mode': mode, 'parameterBytes': 76, 'variant': 'beyondDistance4'})
    for owner in (0, 1, 52, 255):
        result.append({'distance': 2, 'spacing': 2, 'category': 1, 'mode': 1,
            'parameterBytes': 76, 'variant': 'ownerByte', 'ownerInput': owner})
    return result


def verify(game, center_id, smoke):
    clock = read_clock(game)
    h = Harness(game)
    h.setup('CGetCandidateFacilityListNode', h.keys[:5])
    o = h.o
    center = o.q(h.raw + 0xE270 + 8 * center_id)
    source = o.call(0x22FB10, center)
    if not o.call32(0x32F500, source):
        raise AssertionError('center must contain an actual valid facility')
    source_id = o.call32(0x22DEA0, source)
    definitions = {kind: o.q(h.raw + 0x73958 + 8 * kind) for kind in range(31)}
    definition = definitions[5]
    if not o.call32(0x32F500, definition) or o.read(definitions[9] + 0x2C, 1) != b'\x03':
        raise AssertionError('valid kind 5 and ignored category-3 kind 9 are required')
    rings, targets, isolation, observed = [], {}, [], set()
    pointer = native_list(h, 7, 6)
    for distance in range(1, 6):
        o.call32(0x2B4750, center_id, distance, pointer, keep=True)
        items = []
        for hex_ in linked_list(o, pointer):
            index = o.call32(0x22DDA0, hex_)
            measured = o.call32(0x2AA850, center_id, index)
            if measured != distance:
                raise AssertionError('native ring is not at the requested exact distance')
            building = o.call(0x22FB10, hex_)
            valid = bool(o.call32(0x32F500, building))
            items.append({'hex': index, 'pointer': hex_, 'distance': measured,
                'originalBuilding': building, 'validBuilding': valid})
            if not valid and distance not in targets:
                targets[distance] = (hex_, index)
            if distance <= 4 and valid and building not in observed:
                observed.add(building)
                change_input(h, isolation, building + 0x12, 9, 1,
                    'other ring facility uses ignored category 3 in copied input')
        rings.append({'distance': distance, 'items': items})
    if len(targets) != 5:
        raise AssertionError('every exact ring needs an actual empty valid target hex')
    storage = o.alloc(0x100)
    plan = configurations()
    if smoke:
        plan = [row for row in plan if row['distance'] == 2 and row['spacing'] == 2][:12]
    answers, failures, interrupted = [], [], None
    for number, config in enumerate(plan):
        current = read_clock(game)
        if current != clock:
            interrupted = {'caseIndex': number, 'reason': 'game clock/world changed', 'clock': current}
            break
        changes, frames = [], []
        distance, spacing, category, length = [config[key] for key in ('distance', 'spacing', 'category', 'parameterBytes')]
        variant = config['variant']
        target, target_id = (center, center_id) if distance == 0 else targets[distance]
        encoded_spacing = ((spacing ^ 5) >> 3 | (spacing ^ 5) << 5) & 255
        # Deliberately disagreeing legacy/encoded values prove which format is selected.
        legacy_spacing = spacing if length >> 2 != 19 else (spacing + 17) & 255
        encoded_value = encoded_spacing if length >> 2 == 19 else ((spacing + 17) & 255)
        o.poke(storage, bytes(0x100))
        o.poke(storage + 0xC, bytes((encoded_value, 0xA7, 0xDB, 0xF1)))
        for address, value, size, role in (
                (definition + 0x2C, category, 1, 'facility definition category'),
                (definition + 0x35, legacy_spacing, 1, 'legacy spacing byte'),
                (definition + 0x50, storage, 8, 'parameter array start'),
                (definition + 0x58, storage + length, 8, 'parameter array end')):
            change_input(h, changes, address, value, size, role + ' in copied input')
        if variant not in ('noRingTestFacility', 'emptyTarget'):
            kind = 31 if variant == 'invalidKind31' else 255 if variant == 'invalidKind255' else 5
            change_input(h, changes, source + 0x12, kind, 1, 'test facility kind in copied input')
            if distance:
                change_input(h, changes, center + 0x1A, 0, 2, 'remove source hex link in copied input')
                change_input(h, changes, target + 0x1A, source_id, 2, 'target hex links source facility in copied input')
                change_input(h, changes, source + 0x10, target_id, 2, 'facility links target hex in copied input')
            if variant == 'missingDefinition':
                change_input(h, changes, h.raw + 0x73958 + 5 * 8, 0, 8, 'missing definition pointer in copied input')
            if variant == 'ownerByte':
                change_input(h, changes, source + 0x13, config['ownerInput'], 1, 'facility owner byte in copied input')
        resolved = o.call(0x22FB10, target)
        valid_building = bool(o.call32(0x32F500, resolved))
        input_kind = o.read(resolved + 0x12, 1)[0] if valid_building else 0
        resolved_definition = o.q(h.raw + 0x73958 + 8 * (input_kind if input_kind <= 30 else 0))
        valid_definition = bool(o.call32(0x32F500, resolved_definition)) if valid_building else False
        input_category = o.read(resolved_definition + 0x2C, 1)[0] if valid_definition else 0
        if variant in ('invalidKind31', 'invalidKind255') and valid_definition:
            restore(o, changes)
            raise AssertionError('invalid-kind controls require an invalid actual definition-0 fallback')
        row = {**config, 'targetHex': target_id, 'legacySpacing': legacy_spacing, 'encodedByte': encoded_value,
            'targetValidBuilding': valid_building, 'targetValidDefinition': valid_definition,
            'targetKind': input_kind, 'targetCategory': input_category, 'copiedInputChanges': changes}
        trace = Trace(o, {0x2B4750: 2, 0x22DC50: None, 0xDB00: None})
        def observe(uc, address, size, user):
            phase = address - o.exe
            definition_pointer = uc.reg_read(UC_X86_REG_RDI)
            if definition_pointer != definition:
                return
            frame = {'rva': phase, 'distance': uc.reg_read(UC_X86_REG_RBP) & 0xFFFFFFFF,
                'category': o.read(definition_pointer + 0x2C, 1)[0]}
            if phase == 0x27BED8:
                frame['spacing'] = uc.reg_read(UC_X86_REG_RAX) & 255
            frames.append(frame)
        handles = [o.mu.hook_add(UC_HOOK_CODE, observe, None, o.exe + phase, o.exe + phase)
            for phase in (0x27BE88, 0x27BED8)]
        try:
            actual = o.call32(0x27BD50, center, config['mode'])
            eligible = (distance in range(1, 5) and valid_building and valid_definition
                and input_category not in (3, 4) and (config['mode'] != 0 or input_category != 2))
            expected = int(not (eligible and distance <= spacing))
            if actual != expected:
                raise AssertionError((row, actual, expected, frames))
            row.update({'native': actual, 'frames': frames, 'events': trace.events.copy()})
            answers.append(row)
        except (OracleError, AssertionError) as error:
            failures.append({**row, 'error': str(error), 'frames': frames, 'events': trace.events.copy()})
        finally:
            trace.close()
            for handle in handles:
                o.mu.hook_del(handle)
            restore(o, changes)
        if (number + 1) % 50 == 0:
            print(f'{number + 1}/{len(plan)} spacing calls; {len(failures)} failures', flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
        'clockBefore': clock, 'clockAfter': after, 'clockStable': clock == after,
        'completeMatrix': not smoke and len(answers) + len(failures) == len(plan),
        'plannedCases': len(plan), 'interrupted': interrupted,
        'method': 'original 27BD50 with native exact-ring lists, allocation, lookup, byte decoder and cleanup; copied inputs only',
        'oracleSha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
        'centerHex': center_id, 'sourceBuilding': source, 'sourceBuildingId': source_id,
        'listPoolSnapshot': game.pool_snapshot, 'rings': rings, 'baselineChanges': isolation,
        'cases': answers, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--center', type=int, default=13001)
    parser.add_argument('--vectors', type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--static', action='store_true')
    mode.add_argument('--smoke', action='store_true')
    args = parser.parse_args()
    if args.vectors and (args.static or args.smoke):
        parser.error('vectors require the complete native matrix')
    folder = HERE / '.cache' / 'ai_facility_spacing'
    folder.mkdir(parents=True, exist_ok=True)
    if args.static:
        image = Image()
        result = {'imageSha256': hashlib.sha256(Path(IMAGE).read_bytes()).hexdigest(),
            'code': {f'{rva:X}': image.flowtext(rva) for rva in (0x27BD50, 0x2B4750, 0x2B48A0, 0x22FB10)}}
        path = folder / 'static.json'
    else:
        game = Game()
        before = read_clock(game)
        time.sleep(3)
        if read_clock(game) != before:
            raise SystemExit('game clock changed; no native calls made')
        game = ListPoolSnapshot()
        if read_clock(game) != before:
            raise SystemExit('game clock changed during pool capture; no native calls made')
        result = verify(game, args.center, args.smoke)
        path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result) if not args.static else json.dumps(result, ensure_ascii=False, indent=2)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable'] and not result['failures']:
        args.vectors.write_text(text.replace('\n', '\r\n'), encoding='utf-8', newline='')
    print(f"{len(result.get('cases', []))} spacing cases; {len(result.get('failures', []))} failures; {path}")
    if not args.static and (not result['clockStable'] or result['failures']):
        raise SystemExit('diagnostic log saved; no successful fixture written')


if __name__ == '__main__':
    main()
