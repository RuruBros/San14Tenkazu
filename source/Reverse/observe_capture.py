# Read-only officer-status and force-ruler polling alongside observe.py combat windows.
# These frames are neither native function boundaries nor coherent native Oracle inputs.
import argparse
import gzip
import json
import math
import struct
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import world
from game import BASE_POINTER_RVA, Game

HERE = Path(__file__).resolve().parent
KEYS = (0x73A50, 0xDF30, 0xE0D0, 0x7E838)
LIMITATIONS = 'Equal successive reads; polling window, not an atomic native invocation boundary.'


def read_frame(game, raw, tables):
    if game.q(game.exe + BASE_POINTER_RVA) != raw:
        raise RuntimeError('World pointer changed or became unreadable; stop this observation')
    result = {}
    for key, (_, size, count, start) in tables.items():
        pointers = game.read(raw + key, count * 8)
        expected = struct.pack('<' + 'Q' * count, *(start + i * size for i in range(count)))
        if pointers != expected:
            raise RuntimeError('Record table pointers changed or became unreadable')
        result[key] = game.read(start, size * count)
        if result[key] is None or len(result[key]) != size * count:
            raise RuntimeError('Record table became unreadable')
    if game.q(game.exe + BASE_POINTER_RVA) != raw:
        raise RuntimeError('World pointer changed during reading')
    return result


def changes(before, after, tables):
    officers, forces = [], []
    size, count = tables[0x73A50][1:3]
    for i in range(count):
        a = before[0x73A50][i * size:(i + 1) * size]
        b = after[0x73A50][i * size:(i + 1) * size]
        if a[0xE6] != b[0xE6]:
            officers.append(dict(index=i, id_before=struct.unpack_from('<H', a, 0x10)[0],
                id_after=struct.unpack_from('<H', b, 0x10)[0], status_before=a[0xE6],
                status_after=b[0xE6], before=a.hex(), after=b.hex()))
    size, count = tables[0xDF30][1:3]
    for i in range(1, count):
        a = before[0xDF30][i * size:(i + 1) * size]
        b = after[0xDF30][i * size:(i + 1) * size]
        x, y = struct.unpack_from('<H', a, 0x10)[0], struct.unpack_from('<H', b, 0x10)[0]
        if x != y:
            # Objects.ForceValid (0x233F10): numbered force with a nonzero ruler word.
            forces.append(dict(index=i, ruler_before=x, ruler_after=y, before=a.hex(), after=b.hex()))
    return officers, forces


def record(game, root, seconds, monotonic=time.monotonic, sleep=time.sleep):
    raw = game.q(game.exe + BASE_POINTER_RVA)
    if not raw:
        raise RuntimeError('Unreadable world pointer')
    tables = {}
    for key in KEYS:
        name, size, count = world.TABLES[key]
        pointers = game.read(raw + key, count * 8)
        if pointers is None or len(pointers) != count * 8:
            raise RuntimeError('Unreadable table pointer array')
        values = struct.unpack('<' + 'Q' * count, pointers)
        start = values[0]
        if not start or any(p != start + i * size for i, p in enumerate(values)):
            raise RuntimeError(f'Noncontiguous {name} table')
        tables[key] = name, size, count, start

    def read():
        return read_frame(game, raw, tables)

    # The baseline follows the same acceptance rule as every subsequent frame.
    for _ in range(20):
        initial = read()
        if initial == read():
            break
        sleep(.05)
    else:
        raise RuntimeError('No equal successive reads for the observation baseline')

    root.mkdir(parents=True, exist_ok=False)

    def save(name, frame):
        with gzip.open(root / name, 'wt', encoding='utf-8') as output:
            json.dump({hex(k): dict(size=tables[k][1], count=tables[k][2],
                address=hex(tables[k][3]), data=v.hex()) for k, v in frame.items()}, output)

    save('initial_tables.json.gz', initial)
    print('watching status and force transitions into', root.resolve(), flush=True)
    start = monotonic()
    previous = initial
    counts = dict(format=2, polls=0, unstable_polls=0, events=0, prisoner_entries=0,
        prisoner_exits=0, force_removals=0, end_reason='duration', completed=True)
    with (root / 'transitions.jsonl').open('w', encoding='utf-8') as log:
        try:
            while monotonic() - start < seconds:
                sleep(.05)
                current, check = read(), read()
                counts['polls'] += 1
                if current != check:
                    counts['unstable_polls'] += 1
                    continue
                officers, forces = changes(previous, current, tables)
                if officers or forces:
                    counts['events'] += 1
                    n = counts['events']
                    before, after = f'{n:04}-before.json.gz', f'{n:04}-after.json.gz'
                    save(before, previous)
                    save(after, current)
                    entry = dict(format=2, t=round(monotonic() - start, 3),
                        utc=datetime.now(timezone.utc).isoformat(), officers=officers, forces=forces,
                        before_tables=before, after_tables=after, limitations=LIMITATIONS)
                    log.write(json.dumps(entry) + '\n')
                    log.flush()
                    counts['prisoner_entries'] += sum(c['status_after'] == 6 for c in officers)
                    counts['prisoner_exits'] += sum(c['status_before'] == 6 for c in officers)
                    counts['force_removals'] += sum(c['ruler_before'] != 0 and c['ruler_after'] == 0 for c in forces)
                    print('event', n, 'at', entry['t'], 's:',
                        [(c['id_after'], c['status_before'], c['status_after']) for c in officers],
                        'forces:', [(c['index'], c['ruler_before'], c['ruler_after']) for c in forces], flush=True)
                previous = current
        except (RuntimeError, KeyboardInterrupt) as error:
            # Process exit, load or interruption must preserve the last accepted frame and journal.
            counts.update(completed=False, end_reason='interrupted' if isinstance(error, KeyboardInterrupt)
                else 'read_stopped', error=str(error))
        finally:
            save('last_tables.json.gz', previous)
            counts.update(seconds=round(monotonic() - start, 3),
                last_frame='last_tables.json.gz', limitations=LIMITATIONS)
            (root / 'summary.json').write_text(json.dumps(counts, indent=2), encoding='utf-8')
    print(json.dumps(counts), flush=True)
    return counts


def main():
    parser = argparse.ArgumentParser(description='Retain read-only officer and force polling transitions.')
    parser.add_argument('seconds', type=float, nargs='?', default=600)
    args = parser.parse_args()
    if not math.isfinite(args.seconds) or args.seconds < 0:
        parser.error('seconds must be finite and nonnegative')
    game = Game()
    root = HERE / '.cache/observe' / datetime.now().strftime('%Y%m%d-%H%M%S-capture')
    result = record(game, root, args.seconds)
    return 0 if result['completed'] else 1


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8', line_buffering=True)
    raise SystemExit(main())
