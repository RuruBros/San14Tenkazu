# Observe attack node +0x80 reads/writes during the full native execute body.
# python ai_attack_cache_audit.py [--vectors ../Tests/AiAttackCacheVectors.json]
# No game writes, game hooks, native helper replacements or forced return paths.
import argparse
from collections import deque
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
from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ
from unicorn.x86_const import (UC_X86_REG_RIP, UC_X86_REG_RSP, UC_X86_REG_RCX,
                              UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)


WORKERS = {11: 0x4ABE0, 8: 0x48CE0, 9: 0x46B80, 10: 0x4BA50, 22: 0x44A80}


def verify(game, force_limit, selected_forces=None, purposes=None, instruction_limit=300_000_000):
    before = read_clock(game)
    h = Harness(game)
    cities = []
    for city_id in range(1, 47):
        city = h.o.call(0x32C130, h.raw, city_id)
        force = h.o.call(0x22C990, city)
        if not h.o.call32(0x32F500, force):
            continue
        tile = h.o.call32(0x22DDA0, h.o.call(0x22D500, city))
        cities.append({'id': city_id, 'city': city, 'force': force,
                       'forceId': h.o.call32(0x22DC80, force), 'tile': tile})
    pairs = []
    for force_id in dict.fromkeys(c['forceId'] for c in cities):
        if selected_forces and force_id not in selected_forces:
            continue
        owned = [c for c in cities if c['forceId'] == force_id]
        enemies = [c for c in cities if h.o.call32(0x2CA280, force_id, c['forceId'])]
        if not enemies:
            continue
        distance, source, target = min((h.o.call32(0x2AA850, a['tile'], b['tile']), a['id'], b['id'])
                                       for a in owned for b in enemies)
        enemy = next(c for c in enemies if c['id'] == target)
        pairs.append({'forceId': force_id, 'force': owned[0]['force'], 'owned': owned,
                      'enemy': enemy, 'distance': distance, 'nearestSource': source})
        if len(pairs) == force_limit:
            break
    answers, failures = [], []
    for pair in pairs:
        district = h.o.call(0x22E7E0, pair['force'])
        for purpose, worker in WORKERS.items():
            if purposes and purpose not in purposes:
                continue
            for option in (0, 1):
                if read_clock(game) != before:
                    break
                h.setup('CCommandFootholdAttackDepatureNode',
                        [h.keys[0], h.keys[1], h.keys[2], h.keys[3], h.keys[4], purpose, h.keys[6], 300])
                h.o.insn_limit = instruction_limit
                changes = []
                change_input(h, changes, district + 0x14, 1, 1, 'corps command budget in copied input')
                h.obj(h.keys[0], pair['enemy']['city'])
                bases = native_list(h, 5, 1)
                value = h.o.alloc(8)
                for city in pair['owned']:
                    h.o.poke(value, struct.pack('<Q', city['city']))
                    h.o.call(0x58820, bases, value, keep=True)
                for key, number in ((h.keys[2], option), (h.keys[3], 0), (h.keys[4], 0), (h.keys[6], 3000)):
                    h.number(key, number)
                accesses = []

                def memory(uc, access, address, size, value, user):
                    cache = h.node + 0x80
                    if address < cache + 4 and cache < address + size:
                        accesses.append({'rva': uc.reg_read(UC_X86_REG_RIP) - h.o.exe,
                                         'access': 'read' if access == UC_MEM_READ else 'write',
                                         'offset': address - h.node, 'size': size,
                                         'value': value if access != UC_MEM_READ else None})

                handle = h.o.mu.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, memory,
                                         None, h.node, h.node + 0x1FF)
                release_calls, release_count = deque(maxlen=16), [0]

                def release_enter(uc, address, size, user):
                    release_count[0] += 1
                    rsp = uc.reg_read(UC_X86_REG_RSP)
                    release_calls.append({'rva': address - h.o.exe,
                                          'return_address': h.o.q(rsp) - h.o.exe,
                                          'rsp': rsp, 'rcx': uc.reg_read(UC_X86_REG_RCX)})

                release_handle = h.o.mu.hook_add(UC_HOOK_CODE, release_enter, None,
                                                h.o.exe + 0xFF83B4, h.o.exe + 0xFF83B4)
                trace = Trace(h.o, {**{rva: None for rva in WORKERS.values()}, 0x42E70: None})
                row = {'force': pair['forceId'], 'enemyCity': pair['enemy']['id'],
                       'nearestOwnedCity': pair['nearestSource'], 'nativeDistance': pair['distance'],
                       'ownedCities': [c['id'] for c in pair['owned']], 'purpose': purpose,
                       'expectedWorker': worker, 'input': option, 'copiedInputChanges': changes}
                try:
                    status = h.execute(district)
                    row.update({'status': status,
                                'cache': struct.unpack('<i', h.o.read(h.node + 0x80, 4))[0],
                                'groups': linked_list(h.o, h.node + 0x98)})
                    answers.append(row)
                except OracleError as error:
                    row['error'] = str(error)
                    row['faultRegisters'] = {name: h.o.mu.reg_read(reg) for name, reg in
                        (('rip', UC_X86_REG_RIP), ('rsp', UC_X86_REG_RSP),
                         ('rcx', UC_X86_REG_RCX), ('rdx', UC_X86_REG_RDX),
                         ('r8', UC_X86_REG_R8), ('r9', UC_X86_REG_R9))}
                    row['faultStack'] = h.o.read(row['faultRegisters']['rsp'], 512).hex()
                    failures.append(row)
                finally:
                    row['cacheAccesses'], row['events'] = accesses, trace.events.copy()
                    row['releaseCalls'] = {'count': release_count[0], 'last': list(release_calls)}
                    trace.close()
                    h.o.mu.hook_del(handle)
                    h.o.mu.hook_del(release_handle)
                print(pair['forceId'], purpose, option, row.get('status', row.get('error')),
                      'reads', sum(a['access'] == 'read' for a in accesses), flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
            'clockBefore': before, 'clockAfter': after, 'clockStable': before == after,
            'listPoolSnapshot': game.pool_snapshot,
            'method': 'full native attack execute; actual hostile cities chosen by native hex distance; actual owned base list, copied corps budget 1, troop target 3000 and option 0/1; emulator memory observers only',
            'oracle_sha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
            'instructionLimit': instruction_limit,
            'cases': answers, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    parser.add_argument('--forces', type=int, default=2)
    parser.add_argument('--force-ids', type=int, nargs='+')
    parser.add_argument('--purposes', type=int, choices=WORKERS, nargs='+')
    parser.add_argument('--instruction-limit', type=int, default=300_000_000)
    parser.add_argument('--merge', type=Path, nargs='+', help='merge completed rows from stable recorded runs; retain earlier failures separately')
    args = parser.parse_args()
    if args.forces < 1 or args.instruction_limit < 1:
        parser.error('--forces and --instruction-limit must be positive')
    if args.merge:
        runs = [json.loads(path.read_text(encoding='utf-8')) for path in args.merge]
        if any(not run.get('clockStable') or not run.get('cases') for run in runs):
            parser.error('--merge requires stable runs with completed native rows')
        rows = {(row['force'], row['purpose'], row['input']): row for run in runs for row in run['cases']}
        prior_failures = [row for run in runs for row in run['failures']]
        if any((row['force'], row['purpose'], row['input']) not in rows for row in prior_failures):
            parser.error('each earlier failure must have a completed native rerun before exporting a fixture')
        if any(run['clockBefore'] != runs[0]['clockBefore'] for run in runs):
            parser.error('--merge requires the same paused world for these paired observations')
        result = {'utc': datetime.now(timezone.utc).isoformat(), 'clockStable': True,
                  'clockBefore': runs[0]['clockBefore'], 'clockAfter': runs[-1]['clockAfter'],
                  'method': 'completed full native rows from stable recorded runs; earlier instruction-limit failures retained separately and matched to completed reruns',
                  'sourceLogs': [str(path) for path in args.merge],
                  'cases': list(rows.values()), 'failures': [], 'priorFailures': prior_failures}
    else:
        game = Game()
        before = read_clock(game)
        time.sleep(3)
        if read_clock(game) != before:
            raise SystemExit('Game clock changed before native execution')
        game = ListPoolSnapshot()
        if read_clock(game) != before:
            raise SystemExit('Game clock changed while copying the list pool')
        result = verify(game, args.forces, args.force_ids, args.purposes, args.instruction_limit)
    folder = HERE / '.cache' / 'ai_attack_cache'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable'] and not result['failures']:
        args.vectors.write_text(text, encoding='utf-8')
    print(f"{len(result['cases'])} completed native cases; {len(result['failures'])} failures; {path}")
    if not result['clockStable']:
        raise SystemExit('Game clock changed; diagnostics only')


if __name__ == '__main__':
    main()
