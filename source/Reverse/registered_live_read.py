"""Read the 11 registered officers' current numeric values; never write/suspend the game.

Comparison is with the isolated conversion report, not proof of all gameplay effects.
Differences are recorded without inferring how the current game was created/edited.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
from game import Game

ROOT = Path(__file__).resolve().parent.parent.parent


def run(original):
    before = Path(original).read_bytes()
    g = Game()
    converted = json.loads((ROOT / '.cache/registered-officers/runtime-audit.json').read_text(encoding='utf-8'))
    assert converted['originalSha256'] == hashlib.sha256(before).hexdigest()
    reference = {int(r['name'].split('-')[1]): r['converted'] for r in converted['cases'] if r['name'].startswith('original-')}
    rows = []
    for oid in reference:
        pointer = g.q(g.base - 0x290 + 0x158 + oid*8)
        data = g.read(pointer, 0x400) if pointer else None
        stable = data is not None and data == g.read(pointer, 0x400)
        if data is None:
            rows.append(dict(id=oid, readable=False)); continue
        word = lambda at: struct.unpack_from('<H', data, at)[0]
        values = dict(face=word(0xA6), gender=data[0xA9], appear=word(0xAA), birth=word(0xAC), death=word(0xAE),
            clan=word(0xB2), father=word(0xB4), mother=word(0xB6), spouse=word(0xBA), sworn=word(0xBC), compatibility=data[0xBE],
            likes=[word(0xC0+2*k) for k in range(8)], dislikes=[word(0xD0+2*k) for k in range(8)],
            abilities=list(data[0xEC:0xF1]), personalities=[word(0x118+2*k) for k in range(9)],
            formations=struct.unpack_from('<I', data, 0x12C)[0], tactics=list(data[0x130:0x13A]),
            ideology=data[0x14C], policy=data[0x14D], policyLevel=data[0x182], voiceStored=data[0x150], voiceUsed=data[0x14E])
        rows.append(dict(id=oid, storedId=word(0x10), readable=True, stable=stable,
            name=g.utf16(pointer+0x12, 7) + g.utf16(pointer+0x20, 7), **values,
            differences={k: dict(converted=reference[oid][k], current=v) for k, v in values.items() if v != reference[oid][k]}))
    after = Path(original).read_bytes()
    assert after == before, 'Original changed during the read-only audit; do not claim a stable comparison.'
    report = dict(pid=g.pid, processRights='PROCESS_QUERY_INFORMATION | PROCESS_VM_READ', writes=False,
        originalSha256=hashlib.sha256(before).hexdigest(), originalUnchanged=True, officers=rows)
    output = ROOT / '.cache/registered-officers/live-readonly-audit.json'
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(dict(output=str(output), pid=g.pid, readable=sum(r['readable'] for r in rows),
        stable=sum(r.get('stable', False) for r in rows), totals={r['id']: sum(r['abilities']) for r in rows if r['readable']},
        differences={r['id']: r['differences'] for r in rows if r.get('differences')}), ensure_ascii=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--original', required=True)
    run(parser.parse_args().original)
