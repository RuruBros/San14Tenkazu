# Verify the second facility replacement path without changing the live game.
# python ai_facility_replacement.py --city 23 [--vectors ../Tests/AiFacilityReplacementVectors.json]
import argparse
from datetime import datetime, timezone
import hashlib
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
from unicorn.x86_const import (UC_X86_REG_RSP, UC_X86_REG_RBP, UC_X86_REG_RBX, UC_X86_REG_RSI,
                              UC_X86_REG_RDI, UC_X86_REG_R15, UC_X86_REG_R9)


def copy_pending(h, changes, force_id, city_id, target_hex, command, facility_kind):
    head = h.o.q(h.raw + 0x60)
    index = int.from_bytes(h.o.read(head, 4), 'little')
    node = h.o.q(h.o.q(h.o.exe + 0x27D10F0) + 8 * index)
    seen = set()
    while node:
        if node in seen:
            raise AssertionError('active army list cycle')
        seen.add(node)
        army = h.o.q(node)
        officer_id = int.from_bytes(h.o.read(army + 0x12, 2), 'little')
        officer = h.o.q(h.raw + 0x158 + 8 * officer_id)
        force = h.o.call(0x22CA00, officer)
        if (h.o.read(army + 0x10, 2) == b'\x01\x00'
                and h.o.call32(0x22DC80, force) == force_id
                and not h.o.call32(0x233440, army)):
            for address, value, size, role in (
                (officer + 0xE2, city_id, 2, 'commander home city'),
                (army + 0x37, command, 1, 'build/repair command'),
                (army + 0x38, 9, 1, 'target type hex'),
                (army + 0x3A, target_hex, 2, 'target hex'),
                (army + 0x46, facility_kind, 1, 'requested facility kind')):
                change_input(h, changes, address, value, size, role + ' in copied input')
            return army
        node = h.o.q(node + 8)
    raise AssertionError('a real non-engaged army of this force is required')


def verify(game, city_id, source_hex=None):
    clock = read_clock(game)
    h = Harness(game)
    h.setup('CGetCandidateFacilityListNode', h.keys[:5])
    city = h.o.call(0x32C130, h.raw, city_id)
    force = h.o.call(0x22C990, city)
    force_id = h.o.call32(0x22DC80, force)
    district = h.o.call(0x22E7E0, force)
    home, gate = [h.o.call(rva, city) for rva in (0x229D40, 0x229E40)]
    neighbors = native_list(h, 6, 6)
    h.o.call(0x22F060, gate, neighbors, keep=True)
    areas = dict.fromkeys([home, gate, *linked_list(h.o, neighbors)])
    kinds = {h.o.q(h.raw + 0x73958 + index * 8): index for index in range(31)}
    categories = {kind: h.o.read(definition + 0x2C, 1)[0] for definition, kind in kinds.items()}
    structures = []
    for building in h.valid_table(0x6DA98, 3001):
        hex_id, kind = struct.unpack('<HB', h.o.read(building + 0x10, 3))
        if kind not in (9, 10):
            continue
        hex_ = h.o.q(h.raw + 0xE270 + 8 * hex_id)
        area = h.o.call(0x229EA0, hex_)
        if area in areas and area != home:
            structures.append({'building': building, 'hex': hex_, 'hexId': hex_id, 'kind': kind, 'area': area})
    answers, failures = [], []
    relevant = [b for b in structures if source_hex is None or b['hexId'] == source_hex]
    if not relevant:
        raise AssertionError('no actual source facility matches the requested hex')
    for area in dict.fromkeys(b['area'] for b in relevant):
        source = next(b for b in relevant if b['area'] == area)
        variants = ['actual', 'mode1', 'kind10', 'lowFunds', 'noCategory3Neighbor',
                    'withCategory2Neighbor', 'pending8Category2', 'pending9Category2',
                    'pending8Category3', 'pending9Category3']
        for variant in variants:
            if read_clock(game) != clock:
                break
            h.setup('CGetCandidateFacilityListNode', h.keys[:5])
            h.obj(h.keys[2], city)
            h.obj(h.keys[3], area)
            h.number(h.keys[1], 1 if variant == 'mode1' else 0)
            changes, frames, handles = [], [], []
            row = {'city': city_id, 'force': force_id, 'area': area, 'home': home, 'gate': gate,
                   'variant': variant, 'sourceStructure': source, 'copiedInputChanges': changes}
            trace = None
            try:
                if variant == 'kind10':
                    change_input(h, changes, source['building'] + 0x12, 10, 1, 'source kind in copied input')
                if variant == 'lowFunds':
                    cost5, cost6 = [h.o.call32(0x3FCB0, h.o.q(h.raw + 0x73958 + 8 * k)) for k in (5, 6)]
                    if not cost5 < cost6:
                        raise AssertionError('facility costs do not provide a kind 5-only boundary')
                    row['cost5'], row['cost6'] = cost5, cost6
                    change_input(h, changes, city + 0x34, cost5, 4, 'money at kind 5 cost in copied input')
                if variant in ('noCategory3Neighbor', 'withCategory2Neighbor'):
                    adjacent = native_list(h, 7, 7)
                    h.o.call(0x2A7AA0, source['hex'], 1, adjacent, keep=True)
                    for neighbor in linked_list(h.o, adjacent):
                        building = h.o.call(0x22FB10, neighbor)
                        if building == source['building'] or not h.o.call32(0x32F500, building):
                            continue
                        definition = h.o.q(h.raw + 0x73958 + 8 * h.o.read(building + 0x12, 1)[0])
                        if h.o.read(definition + 0x2C, 1) == b'\x03':
                            kind = 11 if variant == 'noCategory3Neighbor' else 5
                            if categories[kind] != (4 if variant == 'noCategory3Neighbor' else 2):
                                raise AssertionError('the neighbor control requires the observed definition category')
                            change_input(h, changes, building + 0x12, kind, 1, 'neighbor category in copied input')
                            if variant == 'withCategory2Neighbor':
                                break
                if variant.startswith('pending'):
                    row['inputArmy'] = copy_pending(h, changes, force_id, city_id, source['hexId'],
                        9 if variant.startswith('pending9') else 8, 5 if variant.endswith('Category2') else 9)

                def observe(uc, address, size, user):
                    rsp, rbp = uc.reg_read(UC_X86_REG_RSP), uc.reg_read(UC_X86_REG_RBP)
                    rva = address - h.o.exe
                    frame = {'rva': rva, 'candidates': [kinds[p] for p in linked_list(h.o, h.o.q(rsp + 0x20))]}
                    if rva == 0x3903A:
                        frame['pendingCategory2'] = int.from_bytes(h.o.read(rbp - 0x78, 4), 'little')
                    if rva == 0x391C0:
                        frame['sourceHex'] = h.o.q(uc.reg_read(UC_X86_REG_RDI))
                        frame['category3Neighbors'] = uc.reg_read(UC_X86_REG_RSI) & 0xFFFFFFFF
                        frame['category2Neighbors'] = uc.reg_read(UC_X86_REG_R15) & 0xFFFFFFFF
                    if rva == 0x39304:
                        frame['neighborHex'] = h.o.q(uc.reg_read(UC_X86_REG_RBX))
                        frame['placementResult'] = uc.reg_read(UC_X86_REG_R9) & 0xFFFFFFFF
                    frames.append(frame)

                for rva in (0x38926, 0x3903A, 0x391C0, 0x39304, 0x39318, 0x3941F):
                    handles.append(h.o.mu.hook_add(UC_HOOK_CODE, observe, None, h.o.exe + rva, h.o.exe + rva))
                trace = Trace(h.o, {0x3F5D0: None, 0x22F060: 1, 0x2A7AA0: 2, 0x279190: None})
                status = h.execute(district)
                output = [kinds[p] for p in linked_list(h.o, h.output(h.keys[0]))]
                results = []
                for key in (h.keys[1], h.keys[4]):
                    h.o.call32(0xB26300, h.board, key, h.out, 0, read_after=[(h.out, 4)])
                    results.append(struct.unpack('<i', h.o.after[0])[0])
                row.update({'status': status, 'outputMode': results[0], 'special': results[1], 'nativeKinds': output})
                answers.append(row)
            except (OracleError, AssertionError) as error:
                row['error'] = str(error)
                failures.append(row)
            finally:
                row['frames'] = frames
                row['events'] = trace.events.copy() if trace else []
                if trace:
                    trace.close()
                for handle in handles:
                    h.o.mu.hook_del(handle)
            print(city_id, hex(area), variant, row.get('nativeKinds', row.get('error')), flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
            'clockBefore': clock, 'clockAfter': after, 'clockStable': clock == after,
            'listPoolSnapshot': game.pool_snapshot, 'structures': structures, 'definitionCategories': categories,
            'method': 'full native second facility replacement path; actual source facilities and logged copied input controls; emulator observers only, no game writes or helper substitution',
            'oracle_sha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
            'cases': answers, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--city', type=int, default=23)
    parser.add_argument('--source-hex', type=int, help='select an actual kind 9/10 source facility by hex ID')
    parser.add_argument('--vectors', type=Path)
    args = parser.parse_args()
    if not 1 <= args.city <= 46:
        parser.error('--city accepts IDs 1..46')
    game = Game()
    before = read_clock(game)
    time.sleep(3)
    if read_clock(game) != before:
        raise SystemExit('Game clock changed before native execution')
    snapshot = ListPoolSnapshot()
    if read_clock(snapshot) != before:
        raise SystemExit('Game clock changed while copying the list pool')
    result = verify(snapshot, args.city, args.source_hex)
    folder = HERE / '.cache' / 'ai_facility_replacement'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable'] and result['cases'] and not result['failures']:
        args.vectors.write_text(text, encoding='utf-8')
    print(f"{len(result['cases'])} completed; {len(result['failures'])} failures; {path}")
    if not result['clockStable']:
        raise SystemExit('Game clock changed; diagnostics only')


if __name__ == '__main__':
    main()
