# Search additional existing literal sources; do not invent or assign original hash names.
# python ai_name_sources.py [--vectors ../Tests/AiNameHashVectors.json]
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import time

import ai
from ai_friend import HERE, vectors_text
from ai_name_audit import hash_bytes
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace
from game import Game
from image import Image


def sources():
    image = Image()
    trees = ai.read_file()
    known = ai.var_names()
    used = {value & 0xFFFFFFFF for tree in trees for node in tree for value in node.params if abs(value) >= 65536}
    blocks = {node.params[0] & 0xFFFFFFFF for tree in trees for node in tree if node.cls == ai.STATIC_LINK}
    unknown = used - known.keys() - blocks
    candidates, records = defaultdict(set), []
    for raw in re.finditer(rb'(?<![\x20-\x7E])[\x20-\x7E]{3,200}\x00', image.img):
        text = raw.group()[:-1].decode('ascii')
        if re.fullmatch(r'[A-Za-z_][A-Za-z0-9_:\-]{2,100}', text):
            candidates[text].add(f'exe ASCII literal RVA {raw.start():X}')
    for section in ('.rdata', '.data', '_RDATA', '.rsrc'):
        start, size = image.sections[section]
        data = image.img[start:start + size]
        # ASCII and non-ASCII NUL-delimited UTF-16 source text. Odd/even
        # alignment is explicit; matches remain candidates, never evidence alone.
        for alignment in (0, 1):
            raw = data[alignment:len(data) - ((len(data) - alignment) & 1)]
            text = raw.decode('utf-16le', errors='replace')
            for match in re.finditer(r'(?:^|(?<=\x00))([^\x00]{3,100})(?=\x00)', text):
                value = match.group(1)
                if re.fullmatch(r'[\w:.-]{3,100}', value) and any(c.isalpha() for c in value):
                    candidates[value].add(f'exe UTF-16LE {section} RVA {start + alignment + match.start() * 2:X}')
    directory = Path(ai.game_dir()) / '0000'
    for filename in ('bt.bin', 'bt_ex.bin', 'bt_exce.bin'):
        path = directory / filename
        variant = ai.read_file(path)
        records.append({'file': filename, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'treeCounts': [len(tree) for tree in variant], 'annotations': sum(bool(n.text) for t in variant for n in t)})
        for tree in variant:
            for node in tree:
                if node.text:
                    candidates[node.text].add(f'{filename} tree {node.tree} node {node.i} annotation')
                    candidates[node.text.strip()].add(f'{filename} stripped annotation')
    for name in known.values(): candidates[name].add('existing verified name')
    for name, provenance in list(candidates.items()):
        if name.isascii():
            for candidate in (name.lower(), name.upper(), name[:1].lower() + name[1:]):
                candidates[candidate].add('case variant of existing source text')
    matches, checked, nul_terminated = [], 0, 0
    for name, provenance in sorted(candidates.items()):
        for encoding in ('ascii',) if name.isascii() else ('cp932', 'utf-8', 'utf-16le'):
            try: raw = name.encode(encoding)
            except UnicodeEncodeError: continue
            checked += 1
            nul_terminated += int(0 in raw)
            value = hash_bytes(raw)
            if value in blocks or value in unknown:
                matches.append({'candidate': name, 'encoding': encoding, 'hash': value,
                    'nativeInputHex': raw.hex(), 'consumedHex': raw.split(b'\0', 1)[0].hex(),
                    'kind': 'block' if value in blocks else 'unresolved parameter', 'provenance': sorted(provenance)})
    return {'method': 'existing image ASCII/UTF-16 literals and all three tree annotations; NUL-terminated signed-byte hash; collision candidates are not original names',
        'imageSha256': hashlib.sha256(image.img).hexdigest(), 'treeSources': records,
        'candidateTexts': len(candidates), 'encodedCandidates': checked, 'candidatesWithNul': nul_terminated,
        'blockHashes': len(blocks), 'unresolvedParameterHashes': len(unknown),
        'matches': matches, 'assignedNames': 0}


def native_hashes(game, matched):
    before = read_clock(game)
    h = Harness(game)
    o = h.o
    output = o.alloc(8)
    cases = [(bytes([value]) + b'A\0Z', f'first byte {value}') for value in range(256)]
    cases += [(value, role) for value, role in (
        (b'', 'empty'), (b'Dis', 'collision Dis'), (b'chs', 'collision chs'),
        (b'A\0BC', 'embedded NUL'), (b'Warlike', 'ASCII'),
        ('親善'.encode('cp932'), 'CP932'), ('친선'.encode('utf-8'), 'UTF-8'),
        ('Fame'.encode('utf-16le'), 'UTF-16 source with early NUL'))]
    cases += [(bytes.fromhex(m['nativeInputHex']), 'matched existing literal') for m in matched]
    result = []
    for raw, role in cases:
        current = read_clock(game)
        if current != before: raise RuntimeError('clock/world changed; no fixture written')
        pointer = o.alloc(len(raw) + 1)
        o.poke(pointer, raw + b'\0')
        trace = Trace(o, {0xB26300: None})
        try:
            status = o.call32(0xB263C0, h.board, pointer, output, 0)
            if len(trace.events) != 1 or 'result' not in trace.events[0]: raise AssertionError('original hash lookup did not return')
            value = trace.events[0]['args'][1] & 0xFFFFFFFF
            if value != hash_bytes(raw): raise AssertionError('native signed-byte/NUL hash mismatch')
            result.append({'inputHex': raw.hex(), 'role': role, 'hash': value, 'lookupStatus': status, 'event': trace.events[0]})
        finally: trace.close()
    after = read_clock(game)
    return {'clockBefore': before, 'clockAfter': after, 'clockStable': before == after,
        'listPoolSnapshot': game.pool_snapshot, 'cases': result}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    args = parser.parse_args()
    result = {'utc': datetime.now(timezone.utc).isoformat(), **sources()}
    if args.vectors:
        game = Game()
        before = read_clock(game)
        time.sleep(3)
        if read_clock(game) != before: raise SystemExit('clock changed; no native calls made')
        game = ListPoolSnapshot()
        if read_clock(game) != before: raise SystemExit('clock changed during capture; no native calls made')
        result.update(native_hashes(game, result['matches']))
    folder = HERE / '.cache' / 'ai_name_sources'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result)
    path.write_text(text, encoding='utf-8')
    if args.vectors and result['clockStable']:
        args.vectors.write_text(text.replace('\n', '\r\n'), encoding='utf-8', newline='')
    print(f"{result['encodedCandidates']} encoded source candidates; {len(result['matches'])} matches; "
          f"{len(result.get('cases', []))} native hash cases; {path}")
    print(json.dumps([{k:m[k] for k in ('candidate','encoding','hash','kind')} for m in result['matches']], ensure_ascii=False))


if __name__ == '__main__': main()
