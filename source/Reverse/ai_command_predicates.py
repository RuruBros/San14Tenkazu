# Predicate routing/gates/membership, observing real native helpers in copied pages only.
# python ai_command_predicates.py --vectors ../Tests/AiCommandPredicateVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct

import ai
import bases
from ai_friend import HERE, vectors_text
from ai_native_trace import Trace
from game import Game
from ai_native_snapshot import ListPoolSnapshot

# predicate: source, gate RVA, candidate RVA, list argument, second-object kind
PLANS = {
    44: ('District', 0x709CF0, 0, 1, None), 46: ('District', 0, 0x6DD5E0, 1, 'person'),
    52: ('District', 0, 0x6DA8A0, 1, 'person'),
    57: ('MainDistrict', 0x70BEE0, 0, 1, None), 58: ('MainDistrict', 0x70BEE0, 0x6DE910, 1, 'force'),
    66: ('MainDistrict', 0x70B810, 0, 1, None), 67: ('MainDistrict', 0x70B810, 0x6DE160, 1, 'force'),
    68: ('Foothold', 0x70DC70, 0, 1, None), 69: ('Foothold', 0x70DC70, 0x6D7EA0, 1, 'person'),
    70: ('AgentDistrictOrMain', 0x70ACF0, 0, 1, None), 71: ('AgentDistrictOrMain', 0x70ACF0, 0x6DAA10, 1, 'person'),
    72: ('District', 0x70DF40, 0, 1, 'person'), 73: ('District', 0x70DF40, 0x6D8350, 1, 'person'),
    74: ('MainDistrict', 0x709E30, 0, 1, None), 75: ('MainDistrict', 0x709E30, 0x6D7AA0, 1, 'person'),
    76: ('Force', 0x70A0D0, 0, 1, None), 77: ('Force', 0x70A0D0, 0x6DBF00, 1, 'person'),
    79: ('AgentDistrictOrMain', 0x70BBF0, 0, 1, None), 80: ('AgentDistrictOrMain', 0x70BBF0, 0x6DAF20, 1, 'person'),
    81: ('AgentDistrictOrMain', 0x70BBF0, 0x6DE4C0, 2, 'force'),
    85: ('MainDistrict', 0x70C530, 0, 1, None), 86: ('MainDistrict', 0x70C530, 0x6DF9B0, 1, 'force'),
    88: ('AgentDistrictOrMain', 0x70CC00, 0, 1, None),
    90: ('AgentDistrictOrMain', 0, 0x6DAF20, 1, 'person'),
    92: ('AgentDistrictOrMain', 0x70C8D0, 0, 1, None),
    93: ('AgentDistrictOrMain', 0, 0x6DAF20, 1, 'person'), 94: ('AgentDistrictOrMain', 0, 0x6E0210, 1, 'person'),
    96: ('AgentDistrictOrMain', 0x70C0B0, 0, 1, None), 97: ('AgentDistrictOrMain', 0, 0x6DAF20, 1, 'person'),
    98: ('AgentDistrictOrMain', 0, 0x6DEB30, 1, 'area'), 100: ('AgentDistrictOrMain', 0x70C1E0, 0, 1, None),
    101: ('AgentDistrictOrMain', 0, 0x6DAF20, 1, 'person'), 102: ('AgentDistrictOrMain', 0, 0x6DECA0, 1, 'area'),
    104: ('MainDistrict', 0x70C2F0, 0, 1, None), 105: ('MainDistrict', 0, 0x6DF3F0, 1, 'force'),
    106: ('MainDistrict', 0x70B9E0, 0, 1, None), 107: ('MainDistrict', 0, 0x6DE310, 1, 'force'),
    136: ('MainDistrict', 0x70CF00, 0, 1, None), 142: ('MainDistrict', 0x70CD30, 0, 1, None),
    143: ('MainDistrict', 0, 0x6E0720, 1, 'force'), 144: ('AgentDistrictOrMain', 0x70BDD0, 0, 1, None),
    151: ('District', 0, 0x6D8670, 1, 'person'), 152: ('District', 0, 0x6D6440, 1, 'force'),
    153: ('Force', 0x70AE70, 0, 1, None), 154: ('MainDistrict', 0, 0x6DD920, 1, 'force'),
    155: ('MainDistrict', 0, 0x6DABE0, 1, 'person'), 156: ('MainDistrict', 0, 0x6D7650, 1, 'person'),
    158: ('DistrictForce', 0x70D930, 0, 1, None), 165: ('District', 0x70C7D0, 0, 1, None),
    166: ('District', 0x70B390, 0, 1, None),
}


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    raw = game.base - 0x290
    manager = game.q(game.q(game.exe + ai.AI_MANAGER) + 8)
    context = game.q(manager + 0x38)
    board = game.q(context + 0x18)
    force_objects = [p for i in range(1, 52) if (p := game.q(raw + 0xDF30 + i * 8))
                     and oracle.call32(0x32F500, p) == 1]
    if len(force_objects) < 2:
        raise AssertionError('need two forces to distinguish current agent from the argument')
    force, other_force = force_objects[:2]
    district = oracle.call(0x22E7E0, force)
    other_district = oracle.call(0x22E7E0, other_force)
    if district == other_district:
        raise AssertionError('need two distinct districts')
    city = oracle.call(0x22D3A0, district)
    person = game.officer(736)
    area = next(p for i in range(1, 501) if (p := game.q(game.base + bases.COUNTIES + i * 8))
                and oracle.call32(0x32F500, p) == 1)
    objects = {'force': other_force, 'person': person, 'area': area}
    keys = [0x61A17011, 0x61A17012]
    missing = 0x61A17013
    args = oracle.alloc(8)
    functions = {0xB4EB0: 0, 0x224C00: None}
    for _, gate, candidate, index, _ in PLANS.values():
        if gate:
            functions.setdefault(gate, None)
        if candidate:
            functions[candidate] = index
    trace = Trace(oracle, functions)
    predicate_functions = ai.predicate_functions()
    answers = []

    def run(predicate, source, target, agent, agent_type, mode, missing_first=False):
        kind, gate, candidate, list_index, second = PLANS[predicate]
        oracle.call32(0xB26990, board, ai.name_hash('AgentType'), agent_type, keep=True)
        for key, obj in zip(keys, [source, target]):
            if oracle.call32(0xB26BA0, board, key, obj, keep=True) != 1:
                raise AssertionError('blackboard setup failed')
        values = [missing if missing_first else keys[0]] + ([keys[1]] if second else [])
        oracle.poke(args, struct.pack('<' + 'I' * len(values), *values))
        trace.clear()
        actual = oracle.call32(0xC2610, predicate, 0, context, agent,
                              stack_args=(args, len(values))) & 255
        start = predicate_functions[predicate]
        events = [e for e in trace.events if start <= e['return_address'] < start + 0x1000]
        valid_first = not missing_first and source != 0 and oracle.call32(0x32F500, source) == 1
        valid_second = not second or target != 0 and oracle.call32(0x32F500, target) == 1
        permission = next((e['result'] != 0 for e in events if e['rva'] == 0x224C00), False)
        gate_event = next((e for e in events if e['rva'] == gate), None) if gate else None
        list_event = next((e for e in events if e['rva'] == candidate), None) if candidate else None
        if predicate == 155 and list_event:
            filtered = next((e for e in events if e['rva'] == 0xB4EB0), None)
            if not filtered:
                raise AssertionError('spy agent filter not observed')
            present = target in filtered['list']
        else:
            present = bool(list_event) and target in list_event['list']
        gate_passed = bool(gate_event) and gate_event['result'] != 0
        expected = valid_first and valid_second and (predicate != 166 or permission) \
            and (not gate or gate_passed) and (not candidate or present)
        if actual != int(expected):
            raise AssertionError((predicate, mode, actual, expected, events))
        main = oracle.call(0x22E7E0, force)
        resolved = {'District': source, 'Foothold': source, 'Force': source, 'DistrictForce': force,
                    'MainDistrict': main, 'AgentDistrictOrMain': other_district if agent else main}[kind]
        second_district = oracle.call(0x40160, target) if second == 'person' and target else 0
        for event in [gate_event, list_event]:
            expected_source = second_district if predicate == 73 and event is list_event else resolved
            if event and event['args'][0] != expected_source:
                raise AssertionError(('routing', predicate, mode, event['args'][0], expected_source))
        if predicate in (76, 77) and gate_event:
            expected_district = second_district if predicate == 77 else main
            if gate_event['args'][1] != expected_district:
                raise AssertionError(('learner-district', predicate, gate_event, expected_district))
        answers.append({'predicate': predicate, 'mode': mode, 'sourceKind': kind,
                        'gate': gate, 'candidates': candidate, 'listArgument': list_index,
                        'first': source, 'mainDistrict': main, 'districtForce': force,
                        'agentDistrict': other_district if agent else 0, 'validAgent': bool(agent),
                        'resolved': resolved, 'validFirst': valid_first, 'validSecond': valid_second,
                        'permission': permission, 'gatePassed': gate_passed, 'candidatePresent': present,
                        'native': actual, 'events': events})
        return list_event

    for predicate, (kind, gate, candidate, index, second) in PLANS.items():
        trace.close()
        oracle = Oracle(game=game)
        args = oracle.alloc(8)
        trace = Trace(oracle, functions)
        source = city if kind == 'Foothold' else district if kind in ('District', 'DistrictForce') else force
        target = objects.get(second, 0)
        for action in [0, 255]:
            for obj in [district, other_district]:
                oracle.poke(obj + 0x14, bytes([action]))
            for agent_type, agent, mode in [(4, 0, 'force-fallback'), (3, other_district, 'different-agent')]:
                event = run(predicate, source, target, agent, agent_type, f'{mode}-action-{action}')
                if event and event['list']:
                    # An actual member, not a fabricated list entry; the entire native gate/list runs again.
                    run(predicate, source, event['list'][0], agent, agent_type, f'{mode}-member-{action}')
        run(predicate, source, target, 0, 4, 'missing-first', True)
        run(predicate, 0, target, 0, 4, 'null-first')
        if second:
            run(predicate, source, 0, 0, 4, 'null-second')
        print(f'P{predicate}: recorded', flush=True)
    trace.close()
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid, 'listPoolSnapshot': game.pool_snapshot,
            'method': 'native dispatcher and unmodified helpers; copied-page entry/return observers only',
            'oracle_sha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
            'cases': answers}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    args = parser.parse_args()
    result = verify(ListPoolSnapshot())
    text = vectors_text(result)
    folder = HERE / '.cache' / 'ai_command_predicates'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    path.write_text(text, encoding='utf-8')
    if args.vectors:
        args.vectors.write_text(text, encoding='utf-8')
    print(f"{len(PLANS)} predicates, {len(result['cases'])} native calls matched; {path}")


if __name__ == '__main__':
    main()
