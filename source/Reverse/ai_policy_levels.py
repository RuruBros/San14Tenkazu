# Full native 0x41C440 policy-effect levels, not the level-to-benefit value at 0x41C200.
# python ai_policy_levels.py [--static | --smoke | --vectors ../Tests/AiPolicyLevelVectors.json.gz]
import argparse
from datetime import datetime, timezone
import gzip
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
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX


SLOTS = [0x54 + field * 30 + slot * 2 for field in range(5) for slot in range(7)]
EFFECTS = {'one': [1] + [0] * 7, 'duplicate': [1, 1] + [0] * 6,
    'eight': [1] * 8, 'other': [2] * 8, 'invalid': [0, 101, 255, 0, 101, 255, 0, 0]}


def signed32(value):
    return value - 0x100000000 if value & 0x80000000 else value


def matrix(force_ids):
    result = [{'variant': 'actual', 'forceId': force, 'effect': effect}
        for force in force_ids for effect in (*range(101), 101, 255)]
    result += [{'variant': 'levelAndEffectSlots', 'baseLevel': level, 'pattern': pattern}
        for level in (0, 1, 9, 10, 11, 255) for pattern in EFFECTS]
    result += [{'variant': 'enactedSlot', 'slotOffset': offset, 'remaining': remaining}
        for offset in SLOTS for remaining in (0, 1, 65535)]
    result += [{'variant': 'strategist', 'remaining': remaining} for remaining in (0, 1, 65535)]
    result += [{'variant': 'repeatedOfficer', 'slotCopies': copies} for copies in (0, 1, 2, 3, 7, 35)]
    result += [{'variant': 'effectIndex', 'effect': effect, 'pattern': 'hundred'} for effect in (0, 1, 2, 100, 101, 255)]
    result += [{'variant': 'policyIndex', 'policyIndex': policy} for policy in (0, 120, 121, 255)]
    result += [{'variant': 'officerIndex', 'officerIndex': officer, 'role': role}
        for officer in (0, 6000, 6001, 65535) for role in ('ruler', 'strategist', 'lastSlot')]
    result += [{'variant': 'invalidForce', 'forceVariant': force} for force in ('null', 'dummy')]
    return [{ 'forceId': 29, 'effect': 1, **config } for config in result]


def members(o, raw, force):
    result = []
    for role, offset, days_offset in [('ruler', 0x10, None), ('strategist', 0x52, 0x16C),
            *[('slot', offset, offset + 14) for offset in SLOTS]]:
        index = int.from_bytes(o.read(force + offset, 2), 'little')
        pointer = o.q(raw + 0x158 + index * 8) if index <= 6000 else o.q(raw + 0x73A50)
        result.append({'role': role, 'offset': offset, 'officerIndex': index, 'pointer': pointer,
            'valid': bool(o.call32(0x32F500, pointer)),
            'remaining': int.from_bytes(o.read(force + days_offset, 2), 'little') if days_offset else 0})
    return result


def contributions(o, raw, events):
    visits = [(i, e) for i, e in enumerate(events) if e['rva'] == 0x258D90]
    result = []
    for number, (start, event) in enumerate(visits):
        end = visits[number + 1][0] if number + 1 < len(visits) else len(events)
        pointer = event['args'][0]
        policy_index = o.read(pointer + 0x14D, 1)[0]
        policy = o.q(raw + 0x78D50 + 8 * (policy_index if policy_index <= 120 else 0))
        valid = bool(o.call32(0x32F500, policy))
        bonuses = [e for e in events[start:end] if e['rva'] == 0x2A9D30]
        if len(bonuses) != int(valid): raise AssertionError('policy validity and native credit bonus call mismatch')
        if valid and (bonuses[0]['args'][0] != pointer or bonuses[0]['args'][1] != 3):
            raise AssertionError('native credit bonus inputs mismatch')
        result.append({'officer': pointer, 'policyIndex': policy_index, 'policyDefinition': policy,
            'validPolicy': valid, 'baseLevel': o.read(pointer + 0x182, 1)[0],
            'creditBonus': signed32(bonuses[0]['result']) if valid else None,
            'effects': list(o.read(policy + 0x98, 8)) if valid else []})
    return result


def expected_level(effect, visits):
    total = 0
    for visit in visits:
        if not visit['validPolicy']: continue
        own = min(signed32((visit['baseLevel'] + visit['creditBonus']) & 0xFFFFFFFF), 10)
        for kind in visit['effects']:
            if 1 <= kind <= 100 and kind == effect:
                total = max(0, signed32((min(total, 10) + own) & 0xFFFFFFFF))
    return total, min(total, 10)


def verify(game, smoke):
    before = read_clock(game)
    h = Harness(game)
    o, raw = h.o, h.raw
    forces = {o.call32(0x22DC80, pointer): pointer for pointer in h.valid_table(0xDF30, 52)}
    force = forces[29]
    ruler_index = int.from_bytes(o.read(force + 0x10, 2), 'little')
    ruler = o.q(raw + 0x158 + 8 * ruler_index)
    if not o.call32(0x32F500, ruler): raise AssertionError('actual ruler required')
    policy = o.q(raw + 0x78D50 + 8)
    if not o.call32(0x32F500, policy): raise AssertionError('actual policy 1 required')
    configs = matrix(forces)
    if smoke:
        configs = [c for c in configs if c['variant'] == 'actual' and c['effect'] in (1, 100)]
        configs += [c for c in matrix(forces) if c['variant'] in ('levelAndEffectSlots', 'strategist', 'repeatedOfficer', 'invalidForce')]
    cases, failures, interrupted = [], [], None
    for number, config in enumerate(configs):
        clock = read_clock(game)
        if clock != before:
            interrupted = {'caseIndex': number, 'reason': 'clock/world changed', 'clock': clock}
            break
        input_force = forces[config['forceId']]
        changes, frames = [], []
        variant = config['variant']
        def change(address, value, size, role):
            change_input(h, changes, address, value, size, role + ' in copied input')
        if variant not in ('actual', 'invalidForce'):
            change(force + 0x10, ruler_index, 2, 'retain actual ruler required by force validity')
            for offset in (0x52, 0x16C): change(force + offset, 0, 2, 'clear controlled strategist/days')
            for field in range(5): change(force + 0x54 + field * 30, 0, 28, 'clear seven policy officers and days')
            change(ruler + 0x14D, config.get('policyIndex', 1), 1, 'controlled officer policy')
            change(ruler + 0x182, config.get('baseLevel', 3), 1, 'controlled officer base policy level')
            kinds = [100] + [0] * 7 if config.get('pattern') == 'hundred' else EFFECTS[config.get('pattern', 'one')]
            change(policy + 0x98, int.from_bytes(bytes(kinds), 'little'), 8, 'controlled policy eight effect IDs')
            if variant == 'enactedSlot':
                change(force + config['slotOffset'], ruler_index, 2, 'single controlled policy slot')
                change(force + config['slotOffset'] + 14, config['remaining'], 2, 'single slot remaining days')
            elif variant == 'strategist':
                change(force + 0x52, ruler_index, 2, 'controlled strategist')
                change(force + 0x16C, config['remaining'], 2, 'strategist remaining days')
            elif variant == 'officerIndex':
                offset = {'ruler': 0x10, 'strategist': 0x52, 'lastSlot': SLOTS[-1]}[config['role']]
                change(force + offset, config['officerIndex'], 2, 'controlled officer lookup index')
            else:
                change(force + 0x10, ruler_index, 2, 'controlled ruler')
                if variant == 'repeatedOfficer':
                    change(force + 0x52, ruler_index, 2, 'same officer in strategist input')
                    for offset in SLOTS[:config['slotCopies']]: change(force + offset, ruler_index, 2, 'same officer in policy slot input')
        if variant == 'invalidForce':
            input_force = 0 if config['forceVariant'] == 'null' else o.q(raw + 0xDF30)
        valid_force = bool(o.call32(0x32F500, input_force))
        entries = members(o, raw, input_force) if valid_force else []
        trace = Trace(o, {rva: None for rva in (0x312EF0, 0x26AA20, 0x258D90, 0x2A9D30, 0x313020, 0x3130D0)})
        def observe_final(uc, address, size, user):
            request = uc.reg_read(UC_X86_REG_RCX)
            frames.append({'request': request, 'rawLevel': int.from_bytes(o.read(request + 0x10, 4), 'little', signed=True)})
        handle = o.mu.hook_add(UC_HOOK_CODE, observe_final, None, o.exe + 0x41C57F, o.exe + 0x41C57F)
        row = {**config, 'inputForce': input_force, 'validForce': valid_force,
            'members': entries, 'copiedInputChanges': changes}
        try:
            native = o.call32(0x41C440, input_force, config['effect'])
            row['native'] = native
            events = trace.events.copy()
            trace.close()
            trace = None
            if any('result' not in e for e in events): raise AssertionError('a nested native helper did not return')
            visits = contributions(o, raw, events)
            selected = [e['pointer'] for e in entries if e['valid'] and (e['role'] == 'ruler' or e['remaining'] == 0)]
            if [v['officer'] for v in visits] != selected: raise AssertionError('native officer selection mismatch')
            raw_level, expected = expected_level(config['effect'], visits)
            if native != expected or (valid_force and (len(frames) != 1 or frames[0]['rawLevel'] != raw_level)):
                raise AssertionError(('native policy level', native, expected, raw_level, frames))
            if not valid_force and (frames or events): raise AssertionError('invalid force did not stop before construction')
            cases.append({**row, 'contributions': visits, 'finalFrames': frames, 'events': events})
        except (OracleError, AssertionError) as error:
            failures.append({**row, 'error': str(error), 'frames': frames,
                'events': trace.events.copy() if trace else events})
        finally:
            if trace: trace.close()
            o.mu.hook_del(handle)
            restore(o, changes)
        if (number + 1) % 100 == 0:
            print(f'{number + 1}/{len(configs)} cases; {len(failures)} failures', flush=True)
    after = read_clock(game)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
        'clockBefore': before, 'clockAfter': after, 'clockStable': before == after,
        'completeMatrix': not smoke and not interrupted and len(cases) == len(configs),
        'plannedCases': len(configs), 'interrupted': interrupted,
        'method': 'original full 41C440; native policy member visitor, credit bonus and accumulator; copied inputs only',
        'oracleSha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
        'actualForceIds': list(forces), 'controlledOfficerIndex': ruler_index,
        'listPoolSnapshot': game.pool_snapshot, 'cases': cases, 'failures': failures}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--static', action='store_true')
    mode.add_argument('--smoke', action='store_true')
    args = parser.parse_args()
    if args.vectors and (args.static or args.smoke): parser.error('vectors require the complete native matrix')
    folder = HERE / '.cache' / 'ai_policy_levels'
    folder.mkdir(parents=True, exist_ok=True)
    if args.static:
        image = Image()
        result = {'imageSha256': hashlib.sha256(Path(IMAGE).read_bytes()).hexdigest(),
            'code': {f'{rva:X}': image.flowtext(rva) for rva in
                (0x41C440, 0x26AA20, 0x258D90, 0x312EF0, 0x313020, 0x3130D0, 0x41C200)}}
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
        args.vectors.write_bytes(gzip.compress(text.replace('\n', '\r\n').encode('utf-8'), mtime=0))
    print(f"{len(result.get('cases', []))}/{result.get('plannedCases', 0)} cases; "
          f"{len(result.get('failures', []))} failures; {path}")
    if not args.static and (not result['clockStable'] or result['failures'] or result['interrupted']):
        raise SystemExit('diagnostic log saved; no successful fixture written')


if __name__ == '__main__': main()
