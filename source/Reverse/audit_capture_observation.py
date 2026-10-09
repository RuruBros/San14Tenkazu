# Verify retained observation bytes offline; this is not native engine parity.
import argparse
import collections
import gzip
import hashlib
import json
import struct
from pathlib import Path

LAYOUTS = {'0x73a50': (0x1F0, 1851), '0xdf30': (0x268, 52),
    '0xe0d0': (0x30, 52), '0x7e838': (0x200, 501)}


def audit(directory):
    directory = directory.resolve()
    counts, groups = collections.Counter(), collections.Counter()
    cases, force_cases, files = [], [], {}

    def frame(filename):
        path = directory / filename
        if path.parent != directory:
            raise ValueError('Frame reference escapes the observation directory')
        compressed = path.read_bytes()
        value = json.loads(gzip.decompress(compressed))
        if set(value) != set(LAYOUTS):
            raise ValueError('Unexpected frame tables')
        for key, table in value.items():
            if (table['size'], table['count']) != LAYOUTS[key]:
                raise ValueError('Unexpected record layout')
            table['bytes'] = bytes.fromhex(table['data'])
            if len(table['bytes']) != table['size'] * table['count']:
                raise ValueError('Table byte count differs')
        files[filename] = hashlib.sha256(compressed).hexdigest()
        return value

    def force_ids(value):
        table = value['0xdf30']
        return [dict(index=i, ruler=struct.unpack_from('<H', table['bytes'], i * table['size'] + 0x10)[0])
            for i in range(1, table['count'])
            if struct.unpack_from('<H', table['bytes'], i * table['size'] + 0x10)[0]]

    def transition_state(value):
        officers, forces = value['0x73a50'], value['0xdf30']
        return (officers['bytes'][0xE6::officers['size']],
            tuple(struct.unpack_from('<H', forces['bytes'], i * forces['size'] + 0x10)[0]
                for i in range(1, forces['count'])))

    initial = frame('initial_tables.json.gz')
    previous = initial
    with (directory / 'transitions.jsonl').open(encoding='utf-8') as log:
        for line in log:
            event = json.loads(line)
            version = event.get('format', 1)
            if version not in (1, 2):
                raise ValueError('Unsupported observation format')
            before, after = frame(event['before_tables']), frame(event['after_tables'])
            if version == 2 and transition_state(previous) != transition_state(before):
                raise ValueError('An unlogged transition precedes the retained event')
            for key in LAYOUTS:
                if (before[key]['size'], before[key]['count'], before[key]['address']) != (
                        after[key]['size'], after[key]['count'], after[key]['address']):
                    raise ValueError('Record layout changed within a retained window')
                if before[key]['address'] != initial[key]['address']:
                    raise ValueError('Record table moved during observation')
            a, b = before['0x73a50'], after['0x73a50']
            counts['events'] += 1
            observed = set()
            for row in event['officers']:
                i, size = row['index'], a['size']
                if not 0 <= i < a['count'] or i in observed:
                    raise ValueError('Invalid or repeated officer index')
                observed.add(i)
                x, y = bytes.fromhex(row['before']), bytes.fromhex(row['after'])
                if x != a['bytes'][i * size:(i + 1) * size] or y != b['bytes'][i * size:(i + 1) * size]:
                    raise ValueError('Retained officer record bytes differ')
                if (x[0xE6], y[0xE6]) != (row['status_before'], row['status_after']) or x[0xE6] == y[0xE6]:
                    raise ValueError('Status transition differs')
                if (struct.unpack_from('<H', x, 0x10)[0], struct.unpack_from('<H', y, 0x10)[0]) != (
                        row['id_before'], row['id_after']):
                    raise ValueError('Officer ID differs')
                counts['officer_transitions'] += 1
                if x[0xE6] != 6 and y[0xE6] != 6:
                    continue
                direction = 'entry' if y[0xE6] == 6 else 'exit'
                counts['prisoner_entries' if direction == 'entry' else 'prisoner_exits'] += 1
                marked = bool(struct.unpack_from('<H', x, 0x15E)[0] & 0x4000)
                in_unit = 62 <= struct.unpack_from('<H', x, 0xE4)[0] <= 561
                groups[(direction, x[0xE6], y[0xE6], marked, in_unit)] += 1
                cases.append(dict(utc=event['utc'], id=row['id_after'], index=i, direction=direction,
                    before_status=x[0xE6], after_status=y[0xE6], marked_before=marked,
                    unit_location_before=in_unit, before_tables=event['before_tables'], after_tables=event['after_tables']))
            changed = {i for i in range(a['count'])
                if a['bytes'][i * a['size'] + 0xE6] != b['bytes'][i * b['size'] + 0xE6]}
            if changed != observed:
                raise ValueError('A retained status change is missing or extra')

            a, b = before['0xdf30'], after['0xdf30']
            changed = set()
            for i in range(1, a['count']):
                offset = i * a['size'] + 0x10
                x, y = struct.unpack_from('<H', a['bytes'], offset)[0], struct.unpack_from('<H', b['bytes'], offset)[0]
                if x == y:
                    continue
                changed.add(i)
                counts['force_ruler_transitions'] += 1
                counts['force_removals'] += x != 0 and y == 0
                force_cases.append(dict(index=i, ruler_before=x, ruler_after=y, utc=event['utc'],
                    before_tables=event['before_tables'], after_tables=event['after_tables']))
            if version == 2:
                observed = set()
                for row in event['forces']:
                    i, size = row['index'], a['size']
                    if not 1 <= i < a['count'] or i in observed:
                        raise ValueError('Invalid or repeated force index')
                    observed.add(i)
                    x, y = bytes.fromhex(row['before']), bytes.fromhex(row['after'])
                    if x != a['bytes'][i * size:(i + 1) * size] or y != b['bytes'][i * size:(i + 1) * size]:
                        raise ValueError('Retained force record bytes differ')
                    if (struct.unpack_from('<H', x, 0x10)[0], struct.unpack_from('<H', y, 0x10)[0]) != (
                            row['ruler_before'], row['ruler_after']):
                        raise ValueError('Force ruler transition differs')
                if changed != observed:
                    raise ValueError('A retained force change is missing or extra')
            previous = after

    summary = json.loads((directory / 'summary.json').read_text(encoding='utf-8'))
    for name in ('events', 'prisoner_entries', 'prisoner_exits'):
        if summary[name] != counts[name]:
            raise ValueError(f'{name} count differs from summary')
    last = frame(summary['last_frame']) if summary.get('last_frame') else None
    if summary.get('format') == 2:
        if last is None or summary['force_removals'] != counts['force_removals']:
            raise ValueError('Missing final accepted frame or force summary differs')
        if any(last[key]['address'] != initial[key]['address'] for key in LAYOUTS):
            raise ValueError('Final record table address differs')
        if transition_state(last) != transition_state(previous):
            raise ValueError('An unlogged transition appears in the final frame')
    return dict(directory=str(directory), summary=summary, counts=dict(counts),
        groups=[dict(direction=k[0], before_status=k[1], after_status=k[2], marked_before=k[3],
            unit_location_before=k[4], count=v) for k, v in groups.items()],
        cases=cases, force_cases=force_cases, initial_forces=force_ids(initial),
        last_forces=force_ids(last) if last is not None else None, sha256=files,
        checks='Retained status/ID/ruler bytes, record layouts, changed-index sets and summary counts passed.',
        limitations='Polling frames are not atomic native invocation inputs or proof of complete engine parity. Status exit alone does not identify release or recruitment.')


def main():
    parser = argparse.ArgumentParser(description='Check retained officer/force observation record bytes offline.')
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    print(json.dumps(audit(args.directory), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
