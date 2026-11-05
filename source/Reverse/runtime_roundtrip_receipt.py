"""Validate a completed developer runtime round-trip JSONL against the reviewed profile."""
import argparse
import hashlib
import json
from pathlib import Path
import re


PROFILE = Path(__file__).resolve().parents[1] / 'Core' / 'RuntimeRules' / 'November2026.json'
ABILITIES = {'Original': 'Before', 'Cap200': 'Cap', 'Growth200': 'Growth'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(hex_bytes):
    return hashlib.sha256(bytes.fromhex(hex_bytes)).hexdigest().upper()


def mode_key(mode):
    return mode['ability'], mode['Traits9'], mode['TraitList9']


def verify(rows, profile):
    expected_modes = {(mode, traits, listing) for mode in ABILITIES
                      for traits, listing in ((False, False), (True, False), (True, True))}
    expected_modes.remove(('Original', False, False))
    require(len(rows) == 2 + len(expected_modes) * 2, 'Incomplete or failed round trip')
    start, finish = rows[0], rows[-1]
    require(start['stage'] == 'started' and start['ExeHash'] == profile['ExeHash'] and start['ProcessId'] > 0,
            'Wrong session executable')
    require(finish == {'stage': 'all-restored', 'passed': len(expected_modes)}, 'Missing successful final stage')
    require({mode_key(mode) for mode in start['modes']} == expected_modes, 'Wrong mode inventory')
    verified = set()
    ranges = 0
    for planned, restored in zip(rows[1:-1:2], rows[2:-1:2], strict=True):
        mode = mode_key(planned['mode'])
        require(planned['stage'] == 'planned' and restored['stage'] == 'restored-byte-exact' and
                planned['mode'] == restored['mode'] and mode in expected_modes - verified, 'Unexpected trial order or duplicate')
        ability, traits, listing = mode
        expected = {}
        for patch in profile['Patches']:
            before = patch['Before']
            after = patch['Traits'] if traits and patch['Traits'] != before else patch[ABILITIES[ability]]
            if before != after:
                expected[patch['Rva']] = (len(before) // 2, sha(before), sha(after))
        if listing:
            expected[profile['Traits']['Site']] = (15, sha('488B4577488986500100004C8B6D7F'), None)
        writes = restored['ranges']
        require(len(writes) == len(expected) and {write['rva'] for write in writes} == set(expected), 'Unexpected write ranges')
        for write in writes:
            length, before, after = expected[write['rva']]
            require(write['length'] == length and write['original'] == write['restored'] == before,
                    f'Original bytes not restored at {write["rva"]:#x}')
            require(write['applied'] == after if after is not None else
                    write['applied'] != before and re.fullmatch('[0-9A-F]{64}', write['applied']),
                    f'Unexpected applied bytes at {write["rva"]:#x}')
        verified.add(mode)
        ranges += len(writes)
    return dict(pid=start['ProcessId'], exeSha256=start['ExeHash'], modes=len(verified), byteRangesRestored=ranges)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    args = parser.parse_args()
    rows = [json.loads(line) for line in args.log.read_text(encoding='utf-8-sig').splitlines()]
    print(json.dumps(verify(rows, json.loads(PROFILE.read_text())), indent=2))


if __name__ == '__main__':
    main()
