# Observe registered area-occupy construction stages using full native calls.
# python ai_occupy_stage_audit.py [--vectors ../Tests/AiOccupyStageVectors.json]
# Explicit state/record inputs in copies; no game writes, hooks or helper replacement.
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import time

from ai_friend import HERE, vectors_text
from ai_executor_audit import Harness
from ai_facility_special import read_clock, native_list, change_input
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace, linked_list
from game import Game
from oracle import OracleError
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_R14


def verify(game):
    clock = read_clock(game)
    h = Harness(game)
    cities = []
    for city_id in range(1, 47):
        city = h.o.call(0x32C130, h.raw, city_id)
        force = h.o.call(0x22C990, city)
        if not h.o.call32(0x32F500, force):
            continue
        area = h.o.call(0x229D40, city)
        if not area or not h.o.call32(0x32F500, area):
            continue
        center = int.from_bytes(h.o.read(area + 0x3A, 2), 'little')
        cities.append({'id': city_id, 'city': city, 'owner': h.o.call32(0x22DC80, force), 'area': area, 'center': center})
    choices = []
    for group in h.valid_table(0x7F8D8, 600):
        unit = h.o.call(0x22E1B0, group)
        if not h.o.call32(0x32F500, unit):
            continue
        owner = h.o.call32(0x22CEF0, group)
        hex_id = int.from_bytes(h.o.read(unit + 0x2A, 2), 'little')
        for city in cities:
            if city['owner'] != owner:
                continue
            center = h.o.q(h.raw + 0xE270 + 8 * city['center'])
            if h.o.read(center + 0x14, 1)[0] != owner:
                continue
            distance = h.o.call32(0x2AA850, hex_id, city['center'])
            if distance < 11:
                choices.append((distance, group, unit, city))
    if not choices:
        raise AssertionError('an actual group within ten hexes of an owned area center is required')
    distance, group, unit, city = min(choices, key=lambda c: (c[0], c[1], c[3]['id']))
    h.setup('CExecuteAreaOccupyNode', [0, 100, h.keys[2]], 2)
    buildings = [b for b in h.valid_table(0x6DA98, 3001) if h.o.read(b + 0x12, 1)[0] in (3, 4, 9, 10)]
    building = next(b for b in buildings if 0 < h.o.call32(0x2AC120, b) <= 65535)
    build_hex = int.from_bytes(h.o.read(building + 0x10, 2), 'little')
    maximum = h.o.call32(0x2AC120, building)
    neighbors = native_list(h, 7, 7)
    h.o.call(0x2A7AA0, h.o.q(h.raw + 0xE270 + 8 * build_hex), 1, neighbors, keep=True)
    empty = next(n for n in linked_list(h.o, neighbors) if not h.o.call32(0x32F500, h.o.call(0x22FB10, n)))
    empty_hex = h.o.call32(0x22DDA0, empty)
    variants = [('stage0', 0, False, build_hex, None), ('stage0BuildEnabled', 0, True, build_hex, None),
                ('stage1', 1, True, build_hex, None), ('stage4', 4, True, build_hex, None),
                ('stage2Complete', 2, True, build_hex, maximum),
                ('stage2Incomplete', 2, True, build_hex, maximum - 1),
                ('stage2EmptyHex', 2, True, empty_hex, None),
                ('stage2OutOfRangeHex', 2, True, 48400, None),
                ('stage2MissingTile', 2, True, build_hex, None)]
    answers, failures = [], []
    for variant, stage, enabled, target_hex, durability in variants:
        if read_clock(game) != clock:
            break
        h.setup('CExecuteAreaOccupyNode', [0, 100, h.keys[2]], 2)
        h.number(h.keys[2], int(enabled))
        state = h.state()
        changes = []
        # Initialized persistent state inputs, not an assertion of natural game progression.
        for address, value, size, role in (
            (group + 0x14, 3, 2, 'group purpose'), (group + 0x16, city['id'], 2, 'group target base'),
            (group + 0x18, city['center'], 2, 'group first relay'),
            (state + 8, city['area'], 8, 'state current area'), (state + 0x10, 0, 8, 'state next area'),
            (state + 0x18, 1, 4, 'state initialized'), (state + 0x1C, stage, 1, 'state construction stage'),
            (state + 0x1D, int(enabled), 1, 'state construction enabled'),
            (state + 0x20, target_hex, 4, 'state construction hex')):
            change_input(h, changes, address, value, size, role + ' in copied input')
        for offset in range(1, 10):
            change_input(h, changes, group + 0x18 + 2 * offset, 48400, 2, 'group remaining relay in copied input')
        if durability is not None:
            change_input(h, changes, building + 0x14, durability, 2, 'construction durability in copied input')
        if variant == 'stage2MissingTile':
            change_input(h, changes, h.raw + 0xE270 + 8 * target_hex, 0, 8,
                         'missing construction tile pointer in copied world table input')
        hex_ = h.o.q(h.raw + 0xE270 + 8 * target_hex) if target_hex < 48400 else h.o.q(h.raw + 0xE270)
        valid_hex = bool(h.o.call32(0x32F500, hex_))
        at_hex = h.o.call(0x22FB10, hex_) if valid_hex else 0
        valid_building = bool(h.o.call32(0x32F500, at_hex))
        poll = {'validHex': valid_hex, 'validBuilding': valid_building,
                'durability': int.from_bytes(h.o.read(at_hex + 0x14, 2), 'little') if valid_building else 0,
                'maximum': h.o.call32(0x2AC120, at_hex) if valid_building else 0}
        frames, handles = [], []

        def observe(uc, address, size, user):
            pointer = uc.reg_read(UC_X86_REG_R14)
            data = h.o.read(pointer, 40)
            frames.append({'rva': address - h.o.exe, 'state': data.hex(),
                           'currentArea': struct.unpack_from('<Q', data, 8)[0],
                           'nextArea': struct.unpack_from('<Q', data, 16)[0],
                           'stage': data[0x1C], 'enabled': data[0x1D],
                           'constructionHex': struct.unpack_from('<I', data, 32)[0]})

        for rva in (0x66667, 0x666D3, 0x668AE, 0x668BA):
            handles.append(h.o.mu.hook_add(UC_HOOK_CODE, observe, None, h.o.exe + rva, h.o.exe + rva))
        trace = Trace(h.o, {0x723E0: None, 0x70830: None, 0xCB0D0: None, 0xB9070: 3,
                            0x2EAAE0: None, 0x2EAB40: None, 0x2EE010: None, 0x2EE1E0: None})
        row = {'variant': variant, 'inputStage': stage, 'inputEnabled': enabled,
               'pollInputs': poll, 'copiedInputChanges': changes, 'stateBefore': h.o.read(state, 40).hex()}
        try:
            row['status'] = h.execute(group)
            row['stateAfter'] = h.o.read(state, 40).hex()
            answers.append(row)
        except OracleError as error:
            row['error'] = str(error)
            failures.append(row)
        finally:
            row['frames'], row['events'] = frames, trace.events.copy()
            trace.close()
            for handle in handles:
                h.o.mu.hook_del(handle)
        print(variant, row.get('status', row.get('error')), 'stages', [f['stage'] for f in frames], flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
            'clockBefore': clock, 'clockAfter': after, 'clockStable': clock == after,
            'listPoolSnapshot': game.pool_snapshot, 'sourceGroup': group, 'sourceUnit': unit,
            'ownedCity': city, 'nativeDistance': distance, 'constructionBuilding': building,
            'method': 'registered executor full native calls; explicit initialized state stages and logged copied record inputs; no game writes, hooks, forced return or helper replacement',
            'oracle_sha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
            'cases': answers, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    parser.add_argument('--from-log', type=Path, help='export a stable recorded run including its separate fault evidence')
    args = parser.parse_args()
    if args.from_log:
        result = json.loads(args.from_log.read_text(encoding='utf-8'))
        if not result.get('clockStable') or not result.get('cases') or 'failures' not in result:
            parser.error('--from-log requires a stable run with explicit completed and failed arrays')
        result['sourceLog'] = str(args.from_log)
    else:
        game = Game()
        before = read_clock(game)
        time.sleep(3)
        if read_clock(game) != before:
            raise SystemExit('Game clock changed before native execution')
        snapshot = ListPoolSnapshot()
        if read_clock(snapshot) != before:
            raise SystemExit('Game clock changed while copying list pool')
        result = verify(snapshot)
    folder = HERE / '.cache' / 'ai_occupy_stage'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable'] and result['cases']:
        # Failed calls remain separate diagnostics and carry no native status.
        args.vectors.write_text(text, encoding='utf-8')
    print(f"{len(result['cases'])} completed; {len(result['failures'])} failures; {path}")
    if not result['clockStable']:
        raise SystemExit('Game clock changed; diagnostics only')


if __name__ == '__main__':
    main()
