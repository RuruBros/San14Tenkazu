# Check format-4 raw resolution records and report cached/table troop differences from captured poll inputs.
# Repeated before/after records are counted as evidence entries, not distinct game invocations.
import argparse
import collections
import hashlib
import json
import struct
from pathlib import Path

parser = argparse.ArgumentParser(description='Audit format-4 observation bytes, pointer chains, pairs and cached troop words offline.')
parser.add_argument('directory', type=Path, help='Observation directory containing initial.json and events.jsonl')
args = parser.parse_args()
directory = args.directory.resolve()
counts = collections.Counter()
errors = collections.Counter()
cache_differences = collections.Counter()
examples = []

def require(condition, message):
    if not condition:
        raise ValueError(message)

def side(data):
    return [struct.unpack_from('<H', data)[0], struct.unpack_from('<I', data, 4)[0],
            struct.unpack_from('<I', data, 12)[0], struct.unpack_from('<I', data, 16)[0], data[20]]

def validate(combat, event=None, phase=None):
    counts['combat_states'] += 1
    counts['complete_combat_states' if combat['complete'] else 'incomplete_combat_states'] += 1
    if not combat['complete']:
        errors[combat.get('error', 'unspecified')] += 1
    rows = combat['records']
    addresses = [int(row['node'], 16) for row in rows]
    require(len(addresses) == len(set(addresses)), 'Duplicate combat node addresses')
    if combat['complete']:
        require(int(combat['head'], 16) == (addresses[0] if rows else 0), 'Complete combat head does not match the first node')
    pairs = []
    for index, row in enumerate(rows):
        raw = bytes.fromhex(row['record_raw'])
        require(len(raw) == 56, f'Invalid combat record length at {row["node"]}')
        target = raw[8:32]
        require(row['target'] == side(target), f'Target fields differ from raw bytes at {row["node"]}')
        require(row['target_troops'] == struct.unpack_from('<H', target, 10)[0], f'Target cached troop word differs at {row["node"]}')
        source_pointer = struct.unpack_from('<Q', raw)[0]
        if source_pointer:
            source = bytes.fromhex(row['source_raw'])
            require(len(source) == 24, f'Invalid source record length at {row["node"]}')
            require(row['source'] == side(source), f'Source fields differ from raw bytes at {row["node"]}')
            require(row['source_troops'] == struct.unpack_from('<H', source, 10)[0], f'Source cached troop word differs at {row["node"]}')
            pairs.append([row['source'], row['target']])
        else:
            require(row['source_raw'] is None and row['source'] is None and row['source_troops'] is None, f'Non-null data for a null source at {row["node"]}')
            counts['null_sources'] += 1
        if index + 1 < len(rows):
            require(struct.unpack_from('<Q', raw, 48)[0] == addresses[index + 1], f'Combat link does not match the next node at {row["node"]}')
        elif combat['complete']:
            require(struct.unpack_from('<Q', raw, 48)[0] == 0, f'Non-null final link in complete combat state at {row["node"]}')
        counts['raw_records'] += 1
        if event is None:
            continue
        for role in ('source', 'target'):
            s = row[role]
            if s is None or s[1] not in (27, 5, 6):
                continue
            number, kind = s[:2]
            group = 'unit' if kind == 27 else 'base'
            key = str(number if kind != 6 else number + 51)
            current = event[group].get(key)
            if phase == 'after':
                current = event['after_' + group].get(key, current)
            if current is None:
                counts['troop_comparison_missing'] += 1
                continue
            data = bytes.fromhex(current)
            troops = struct.unpack_from('<H' if kind == 27 else '<i', data, 22 if kind == 27 else 60)[0]
            cached = row[role + '_troops']
            counts['troop_comparisons'] += 1
            if cached != troops:
                cache_differences[f'{phase}:{role}:kind{kind}'] += 1
                if len(examples) < 12:
                    examples.append({'t': event['t'], 'phase': phase, 'role': role, 'kind': kind,
                                     'index': number, 'cached': cached, 'table_troops': troops})
    return pairs

initial = json.loads((directory / 'initial.json').read_text(encoding='utf-8'))
require(initial['format'] == 4, 'This audit requires observation format 4')
validate(initial['combat'])
with (directory / 'events.jsonl').open(encoding='utf-8') as events:
    for line in events:
        event = json.loads(line)
        require(event['format'] == 4, f'Unsupported event format at t={event["t"]}')
        counts['events'] += 1
        for phase in ('before', 'after'):
            pairs = validate(event['combat_' + phase], event, phase)
            require(pairs == event['records_' + phase], f'Raw combat projection differs from {phase} pairs at t={event["t"]}')
            counts['incomplete_tactic_states'] += not event['tactics_' + phase]['complete']
        require(event['records'] == (event['records_before'] or event['records_after']), f'Replay pair selection differs at t={event["t"]}')
        require(event['records_changed'] == (event['records_before'] != event['records_after']), f'Pair transition flag differs at t={event["t"]}')
summary_path = directory / 'summary.json'
if summary_path.exists():
    summary = json.loads(summary_path.read_text(encoding='utf-8'))
    require(summary['events'] == counts['events'], 'Event count differs from summary.json')
else:
    summary = None
result = {'directory': str(directory), 'summary': summary, 'counts': dict(counts),
          'combat_errors': dict(errors), 'cached_table_differences': dict(cache_differences),
          'examples': examples, 'checks': 'raw bytes, cached words, pointer chain, five-field pairs and event count passed',
          'limitations': 'Cached/table comparisons are poll inputs, not atomic game invocations.'}
if summary is not None:
    result['sha256'] = {name: hashlib.sha256((directory / name).read_bytes()).hexdigest()
                        for name in ('world.json.gz', 'initial.json', 'events.jsonl', 'summary.json')}
print(json.dumps(result, ensure_ascii=False, indent=2))
