# Observe the registered, currently unused facility-attack executor before its area lookup.
# python ai_relay_fault_audit.py [--vectors ../Tests/AiRelayFaultVectors.json]
# Copied native code only; expected faults remain failures, never forced success.
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import time

from ai_friend import HERE, vectors_text
from ai_executor_audit import Harness
from ai_facility_special import read_clock, native_list
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import linked_list
from game import Game
from oracle import OracleError
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_R14, UC_X86_REG_RCX, UC_X86_REG_RIP


def verify(game):
    before = read_clock(game)
    h = Harness(game)
    forces = h.valid_table(0xDF30, 52)
    groups = h.valid_table(0x7F8D8, 600)
    group = next(g for g in groups if any(h.o.call32(0x22CEF0, g) == h.o.call32(0x22DC80, f) for f in forces))
    owner = h.o.call32(0x22CEF0, group)
    own_force = next(f for f in forces if h.o.call32(0x22DC80, f) == owner)
    enemy_force = next(f for f in forces if h.o.call32(0x2CA280, owner, h.o.call32(0x22DC80, f)))
    cities = [h.o.call(0x22D3A0, h.o.call(0x22E7E0, f)) for f in (own_force, enemy_force)]
    target = struct.unpack('<H', h.o.read(cities[1] + 0x4E, 2))[0]
    centers = [struct.unpack('<H', h.o.read(h.o.call(0x229D40, c) + 0x3A, 2))[0] for c in cities]
    h.setup('CExecuteAttackAreaBuildingNode', [0, 0], 2)
    relay_pairs = []
    for relation, center in zip(('own', 'hostile'), centers):
        hex_ = h.o.q(h.raw + 0xE270 + 8 * center)
        neighbors = native_list(h, 7, 7)
        h.o.call(0x2A7AA0, hex_, 1, neighbors, keep=True)
        second = next(n for n in linked_list(h.o, neighbors) if n != hex_)
        relay_pairs.append((relation, center, h.o.call32(0x22DDA0, second)))
    answers, failures = [], []
    for relation, first, second in relay_pairs:
        for length in (1, 2):
            for purpose in (3, 9, 13):
                if read_clock(game) != before:
                    break
                h.setup('CExecuteAttackAreaBuildingNode', [0, 0], 2)
                state = h.state()
                relays = [first] if length == 1 else [first, second]
                h.o.poke(group + 0x14, struct.pack('<HH', purpose, target))
                h.o.poke(group + 0x18, struct.pack('<10H', *relays, *([48400] * (10 - length))))
                frames, handles = [], []

                def observe(uc, address, size, user):
                    pointer = uc.reg_read(UC_X86_REG_R14)
                    data = h.o.read(pointer, 32)
                    frames.append({'rva': address - h.o.exe, 'statePointer': pointer,
                                   'state': data.hex(), 'firstTarget': struct.unpack_from('<Q', data, 8)[0],
                                   'secondTarget': struct.unpack_from('<Q', data, 16)[0],
                                   'rcx': uc.reg_read(UC_X86_REG_RCX)})

                for rva in (0x66E9B, 0x671BB, 0x671C7):
                    handles.append(h.o.mu.hook_add(UC_HOOK_CODE, observe, None, h.o.exe + rva, h.o.exe + rva))
                row = {'class': 'CExecuteAttackAreaBuildingNode', 'group': group, 'ownerForce': owner,
                       'relation': relation, 'purpose': purpose, 'inputTargetBase': target,
                       'inputRelays': relays, 'relayPointers': [h.o.q(h.raw + 0xE270 + 8 * r) for r in relays],
                       'stateBefore': h.o.read(state, 32).hex()}
                try:
                    row['status'] = h.execute(group)
                    row['stateAfter'] = h.o.read(state, 32).hex()
                    answers.append(row)
                except OracleError as error:
                    row.update({'error': str(error), 'faultRva': h.o.mu.reg_read(UC_X86_REG_RIP) - h.o.exe,
                                'faultRcx': h.o.mu.reg_read(UC_X86_REG_RCX),
                                'stateAfter': h.o.read(state, 32).hex()})
                    failures.append(row)
                finally:
                    row['frames'] = frames
                    for handle in handles:
                        h.o.mu.hook_del(handle)
                print(relation, relays, purpose, row.get('status', row.get('error')), flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
            'clockBefore': before, 'clockAfter': after, 'clockStable': before == after,
            'listPoolSnapshot': game.pool_snapshot,
            'method': 'registered executor full native calls with one or two actual neighboring relay hexes; initialization and area-lookup observers; faults retained separately; no game writes or helper substitution',
            'oracle_sha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
            'cases': answers, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    args = parser.parse_args()
    game = Game()
    before = read_clock(game)
    time.sleep(3)
    if read_clock(game) != before:
        raise SystemExit('Game clock changed before native execution')
    snapshot = ListPoolSnapshot()
    if read_clock(snapshot) != before:
        raise SystemExit('Game clock changed while copying list pool')
    result = verify(snapshot)
    folder = HERE / '.cache' / 'ai_relay_fault'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable']:
        # This fixture explicitly retains fault evidence in a separate array.
        args.vectors.write_text(text, encoding='utf-8')
    print(f"{len(result['cases'])} completed; {len(result['failures'])} recorded faults; {path}")
    if not result['clockStable']:
        raise SystemExit('Game clock changed; diagnostics only')


if __name__ == '__main__':
    main()
