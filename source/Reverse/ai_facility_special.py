# Observe special facility replacement with real existing structures and native eligibility checks.
# python ai_facility_special.py [--static | --check-clock | --vectors ../Tests/AiFacilitySpecialVectors.json]
# All execution and writes are in copied Unicorn pages; no game hooks or helper replacements.
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import time

from ai_friend import HERE, vectors_text
from ai_executor_audit import Harness
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list
from oracle import OracleError
from game import Game
from image import Image, IMAGE
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RSP, UC_X86_REG_R12, UC_X86_REG_RSI


def read_clock(game):
    world = game.q(game.exe + 0x277DF18)
    state = game.q(world + 0x871E8) if world else None
    value = game.read(state + 0x34, 5) if state else None
    if value is None:
        raise RuntimeError('game clock is not readable')
    return {'pid': game.pid, 'exe': game.exe, 'world': world, 'state': state,
            'value': list(struct.unpack('<HBBB', value))}


def native_list(h, kind, key_index):
    # The helpers clear/fill an already constructed typed pool list; a zeroed
    # 16-byte buffer has no native index and is not a valid substitute.
    key = h.keys[key_index]
    h.o.call(0x1A2F0, key, kind, h.context, h.out, keep=True)
    pointer = h.output(key)
    if not h.o.q(pointer + 8):
        raise AssertionError('native list index was not constructed')
    return pointer


def change_input(h, changes, address, value, size, role):
    before = int.from_bytes(h.o.read(address, size), 'little')
    changes.append({'address': address, 'before': before, 'after': value, 'size': size, 'role': role})
    h.o.poke(address, value.to_bytes(size, 'little'))


def result_text(result):
    if 'runs' not in result:
        return vectors_text(result)
    metadata = {k: v for k, v in result.items() if k != 'runs'}
    prefix = json.dumps(metadata, ensure_ascii=False, indent=2)[:-1].rstrip()
    if metadata:
        prefix += ','
    runs = ',\n'.join('\n'.join('    ' + line for line in vectors_text(run).splitlines())
                      for run in result['runs'])
    return prefix + '\n  "runs": [\n' + runs + '\n  ]\n}\n'


def verify(game, synthetic_forts=False, city_ids=range(1, 47), detailed=False):
    h = Harness(game)
    before = read_clock(game)
    h.setup('CGetCandidateFacilityListNode', h.keys[:5])
    kinds = {game.q(h.raw + 0x73958 + index * 8): index for index in range(31)}
    fort_areas, kinds_count, category3 = {}, {}, []
    # 0x22FB10 accepts building indices 0..3000; the next pointers are another table.
    for building in h.valid_table(0x6DA98, 3001):
        hex_id, kind, owner = struct.unpack('<HBB', game.read(building + 0x10, 4))
        kinds_count[kind] = kinds_count.get(kind, 0) + 1
        if kind == 9:
            category3.append({'building': building, 'id': h.o.call32(0x22DEA0, building), 'hex': hex_id})
        if kind not in ((3, 4, 5, 6, 9, 10) if synthetic_forts else (5, 6)):
            continue
        hex_ = game.q(h.raw + 0xE270 + 8 * hex_id)
        area = h.o.call(0x229EA0, hex_)
        fort_areas.setdefault(area, []).append({'building': building, 'kind': kind, 'owner': owner, 'hex': hex_id})
    survey, answers, failures = [], [], []
    for city_id in city_ids:
        if read_clock(game) != before:
            break
        city = h.o.call(0x32C130, h.raw, city_id)
        force = h.o.call(0x22C990, city)
        if not h.o.call32(0x32F500, force):
            continue
        district = h.o.call(0x22E7E0, force)
        home, gate = [h.o.call(rva, city) for rva in (0x229D40, 0x229E40)]
        if not gate or not h.o.call32(0x32F500, gate):
            continue
        neighbors = native_list(h, 6, 6)
        h.o.call(0x22F060, gate, neighbors, keep=True)
        areas = dict.fromkeys([home, gate, *linked_list(h.o, neighbors)])
        relevant = [area for area in areas if area in fort_areas]
        force_id = h.o.call32(0x22DC80, force)
        survey.append({'city': city_id, 'force': force_id,
                       'home': home, 'gate': gate, 'relevantAreas': relevant})
        for area in relevant:
            variants = ((0, 'actual'), (0, 'withoutNearbyCategory3'), (1, 'actual'))
            if synthetic_forts:
                variants = ((0, 'actual'), (0, 'copiedFort'),
                            (0, 'copiedFortWithoutNearbyCategory3'), (1, 'copiedFort'))
                if detailed:
                    variants += ((0, 'copiedFortKind6'), (0, 'copiedFortLowFunds'), (0, 'copiedFortWithCategory3Neighbor'),
                                 (0, 'copiedFortWithPending8Category3'), (0, 'copiedFortWithPending9Category3'),
                                 (0, 'copiedFortWithPending8Category2'), (0, 'copiedFortWithPending9Category2'))
            for mode, variant in variants:
                if read_clock(game) != before:
                    break
                print('city/area/mode/variant', city_id, hex(area), mode, variant, flush=True)
                h.setup('CGetCandidateFacilityListNode', h.keys[:5])
                h.obj(h.keys[2], city)
                h.obj(h.keys[3], area)
                h.number(h.keys[1], mode)
                changes = []
                actual_forts = [fort for fort in fort_areas[area] if fort['kind'] in (5, 6)]
                input_forts = actual_forts.copy()
                if variant.startswith('copiedFort'):
                    fort = next((f for f in fort_areas[area] if f['kind'] not in (5, 6)), None)
                    if fort:
                        change_input(h, changes, fort['building'] + 0x12, 6 if variant == 'copiedFortKind6' else 5,
                                     1, 'fort kind in copied input')
                        input_forts.append(fort)
                if variant == 'copiedFortLowFunds':
                    cost9, cost10 = [h.o.call32(0x3FCB0, h.o.q(h.raw + 0x73958 + 8 * kind)) for kind in (9, 10)]
                    if not cost9 < cost10:
                        raise AssertionError('costs do not provide a kind 9-only budget boundary')
                    change_input(h, changes, city + 0x34, cost9, 4, 'city money at kind 9 cost in copied input')
                if variant == 'copiedFortWithCategory3Neighbor' and input_forts:
                    fort = input_forts[0]
                    neighbor_list = native_list(h, 7, 7)
                    hex_ = game.q(h.raw + 0xE270 + 8 * fort['hex'])
                    h.o.call(0x2A7AA0, hex_, 1, neighbor_list, keep=True)
                    neighbor = next((n for n in linked_list(h.o, neighbor_list)
                                     if not h.o.call32(0x32F500, h.o.call(0x22FB10, n))), None)
                    building = next((b for b in category3 if b['building'] != fort['building']), None)
                    if neighbor and building:
                        old_hex = game.q(h.raw + 0xE270 + 8 * building['hex'])
                        new_hex_id = h.o.call32(0x22DDA0, neighbor)
                        change_input(h, changes, old_hex + 0x1A, 0, 2, 'detach category 3 building from old hex in copy')
                        change_input(h, changes, neighbor + 0x1A, building['id'], 2, 'attach category 3 building to fort neighbor in copy')
                        change_input(h, changes, building['building'] + 0x10, new_hex_id, 2, 'category 3 building hex in copy')
                if variant.startswith('copiedFortWithPending') and input_forts:
                    active_head = h.o.q(h.raw + 0x60)
                    active_index = int.from_bytes(h.o.read(active_head, 4), 'little')
                    node = h.o.q(h.o.q(h.o.exe + 0x27D10F0) + 8 * active_index)
                    army = None
                    while node:
                        candidate = h.o.q(node)
                        commander_id = int.from_bytes(h.o.read(candidate + 0x12, 2), 'little')
                        commander = h.o.q(h.raw + 0x158 + 8 * commander_id)
                        # +0x10 is a UInt16 unit kind, not a force byte. Resolve the
                        # commander's corps/force through the real officer helper.
                        candidate_force = h.o.call(0x22CA00, commander)
                        if (h.o.read(candidate + 0x10, 2) == b'\x01\x00'
                                and h.o.call32(0x22DC80, candidate_force) == force_id
                                and not h.o.call32(0x233440, candidate)):
                            army = candidate
                            break
                        node = h.o.q(node + 8)
                    if not army:
                        raise AssertionError('a real non-engaged army of this force is required for a pending command input')
                    officer_id = int.from_bytes(h.o.read(army + 0x12, 2), 'little')
                    officer = h.o.q(h.raw + 0x158 + 8 * officer_id)
                    change_input(h, changes, officer + 0xE2, city_id, 2, 'real army commander base in copied input')
                    command = 9 if 'Pending9' in variant else 8
                    change_input(h, changes, army + 0x37, command, 1, 'real army build/repair command in copied input')
                    change_input(h, changes, army + 0x38, 9, 1, 'real army target type hex in copied input')
                    change_input(h, changes, army + 0x3A, input_forts[0]['hex'], 2, 'real army target hex in copied input')
                    change_input(h, changes, army + 0x46, 9 if 'Category3' in variant else 5, 1,
                                 'real army requested facility kind in copied input')
                if variant in ('withoutNearbyCategory3', 'copiedFortWithoutNearbyCategory3'):
                    for fort in input_forts:
                        neighbor_list = native_list(h, 7, 7)
                        hex_ = game.q(h.raw + 0xE270 + 8 * fort['hex'])
                        h.o.call(0x2A7AA0, hex_, 1, neighbor_list, keep=True)
                        for neighbor in linked_list(h.o, neighbor_list):
                            building = h.o.call(0x22FB10, neighbor)
                            if not h.o.call32(0x32F500, building):
                                continue
                            kind = h.o.read(building + 0x12, 1)[0]
                            definition = h.o.q(h.raw + 0x73958 + 8 * kind)
                            if h.o.read(definition + 0x2C, 1) == b'\x03':
                                change_input(h, changes, building + 0x12, 5, 1, 'neighbor category in copied input')
                frames, handles = [], []

                def observe(uc, address, size, user):
                    rsp = uc.reg_read(UC_X86_REG_RSP)
                    frame = {'rva': address - h.o.exe,
                             'candidates': [kinds[p] for p in linked_list(h.o, h.o.q(rsp + 0x20))]}
                    if frame['rva'] == 0x38CA4:
                        frame['pendingCategory3'] = uc.reg_read(UC_X86_REG_R12) & 0xFFFFFFFF
                        index_pointer = uc.reg_read(UC_X86_REG_RSI)
                        index = struct.unpack('<I', h.o.read(index_pointer, 4))[0] if index_pointer else 0x14000
                        capacity = struct.unpack('<I', h.o.read(h.o.exe + 0x27D1120, 4))[0]
                        heads = h.o.q(h.o.exe + 0x27D10F0)
                        node = h.o.q(heads + 8 * index) if heads and index < capacity else 0
                        forts, seen = [], set()
                        while node:
                            if node in seen:
                                raise AssertionError('native fort list cycle')
                            seen.add(node)
                            forts.append(h.o.q(node))
                            node = h.o.q(node + 8)
                        frame['fortHexes'] = forts
                    frames.append(frame)

                for rva in (0x387B3, 0x38926, 0x38CA4, 0x38FE9):
                    handles.append(h.o.mu.hook_add(UC_HOOK_CODE, observe, None, h.o.exe + rva, h.o.exe + rva))
                trace = Trace(h.o, {0x3F5D0: None, 0x22F060: 1, 0x2A7AA0: 2, 0x279190: None})
                try:
                    status = h.execute(district)
                except OracleError as error:
                    rsp = h.o.mu.reg_read(UC_X86_REG_RSP)
                    stack = h.o.read(rsp, 512).hex()
                    failures.append({'city': city_id, 'force': force_id,
                                     'area': area, 'inputMode': mode, 'variant': variant,
                                     'copiedInputChanges': changes, 'error': str(error), 'stack': stack,
                                     'frames': frames, 'events': trace.events.copy()})
                    trace.close()
                    for handle in handles:
                        h.o.mu.hook_del(handle)
                    continue
                output = [kinds[p] for p in linked_list(h.o, h.output(h.keys[0]))]
                events = trace.events.copy()
                trace.close()
                for handle in handles:
                    h.o.mu.hook_del(handle)
                special = after_mode = None
                if status == 1:
                    for key, label in ((h.keys[4], 'special'), (h.keys[1], 'mode')):
                        h.o.call32(0xB26300, h.board, key, h.out, 0, read_after=[(h.out, 4)])
                        value = struct.unpack('<i', h.o.after[0])[0]
                        if label == 'special': special = value
                        else: after_mode = value
                if special not in (None, 0, 1):
                    raise AssertionError('unexpected special flag')
                answers.append({'city': city_id, 'force': force_id, 'area': area,
                                'home': home, 'gate': gate, 'existingForts': actual_forts,
                                'inputForts': input_forts,
                                'variant': variant, 'copiedInputChanges': changes,
                                'inputMode': mode, 'outputMode': after_mode, 'status': status,
                                'special': special, 'nativeKinds': output, 'frames': frames, 'events': events})
                print(city_id, hex(area), mode, status, special, output, flush=True)
    after = read_clock(game)
    world_stable = before['world'] == after['world'] and before['state'] == after['state']
    stable = before == after
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
            'clockBefore': before['value'], 'clockAfter': after['value'],
            'worldStable': world_stable, 'clockStable': stable,
            'listPoolSnapshot': game.pool_snapshot, 'survey': survey, 'validBuildingKinds': kinds_count,
            'method': 'actual owned cities and gate neighbors; actual inputs and logged fort kinds, city money, moved neighboring facility and real army pending-command inputs in copies; full native body, no helper replacement',
            'oracle_sha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
            'cases': answers, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    parser.add_argument('--synthetic-forts', action='store_true', help='also test logged fort kind changes in copied existing facilities')
    parser.add_argument('--cities', type=int, nargs='+', help='limit the actual city IDs examined')
    parser.add_argument('--detailed', action='store_true', help='also test city money and an actual category-3 building moved to a neighbor in copies')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--static', action='store_true', help='record the static branch instructions without native execution')
    mode.add_argument('--check-clock', action='store_true', help='read the game clock twice, three seconds apart, without native execution')
    mode.add_argument('--merge', nargs='+', type=Path, help='combine successful stable native logs without reading the live game')
    args = parser.parse_args()
    if args.vectors and (args.static or args.check_clock):
        parser.error('--vectors requires native verification')
    if args.detailed and not args.synthetic_forts:
        parser.error('--detailed requires --synthetic-forts')
    if args.cities and any(not 1 <= city <= 46 for city in args.cities):
        parser.error('--cities accepts IDs 1..46')
    if args.merge:
        if args.synthetic_forts or args.cities or args.detailed:
            parser.error('--merge takes recorded logs, not native input options')
        runs = []
        for path in args.merge:
            run = json.loads(path.read_text(encoding='utf-8'))
            if not (run.get('clockStable') and run.get('worldStable') and run.get('cases')
                    and 'failures' in run and not run['failures']):
                parser.error(f'{path} is not a successful stable native run')
            runs.append({'sourceLog': path.name, **run})
        result = {'utc': datetime.now(timezone.utc).isoformat(), 'clockStable': True,
                  'method': 'combined successful native runs; each retains its clock, pool, copied input and observer records',
                  'runs': runs}
    elif args.static:
        image = Image()
        result = {'utc': datetime.now(timezone.utc).isoformat(),
                  'method': 'static branch instructions only; no claim about an observed positive native result',
                  'image_sha256': hashlib.sha256(Path(IMAGE).read_bytes()).hexdigest(),
                  'gateCode': image.text(0x387B3, 0x38926),
                  'specialCode': image.text(0x38C91, 0x3903A), 'cases': []}
    else:
        game = Game()
        before = read_clock(game)
        time.sleep(3)
        after = read_clock(game)
        result = {'utc': datetime.now(timezone.utc).isoformat(),
                  'method': 'two read-only process clock reads, three seconds apart; no native execution',
                  'clockBefore': before, 'clockAfter': after, 'clockStable': before == after,
                  'cases': []}
        if not args.check_clock and result['clockStable']:
            snapshot = ListPoolSnapshot()
            after_snapshot = read_clock(snapshot)
            if after_snapshot == after:
                result = verify(snapshot, args.synthetic_forts, args.cities or range(1, 47), args.detailed)
                result['clockPreflight'] = {'before': before, 'after': after,
                                            'afterPoolSnapshot': after_snapshot}
            else:
                result['clockAfterPoolSnapshot'] = after_snapshot
                result['clockStable'] = False
    folder = HERE / '.cache' / 'ai_facility_special'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = result_text(result)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable']:
        fixture = result if args.merge else {'runs': [result]}
        args.vectors.write_text(result_text(fixture), encoding='utf-8')
    if args.static:
        print(f'Static branch instructions saved; {path}')
    elif args.check_clock or ('failures' not in result and not args.merge):
        print(f"clock stable={result['clockStable']}; {path}")
    else:
        runs = result['runs'] if args.merge else [result]
        cases = [row for run in runs for row in run['cases']]
        print(f"{len(cases)} native cases; {sum(r['special'] == 1 for r in cases)} special results; {sum(len(run['failures']) for run in runs)} failures; {path}")
    if not args.static and not result['clockStable']:
        raise SystemExit('Game clock changed; saved diagnostics only, no test fixture written')


if __name__ == '__main__':
    main()
