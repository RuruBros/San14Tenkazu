# Remaining unnamed scalar info fields. Real node -> blackboard -> read, in copied pages only.
# python ai_raw_info.py --vectors ../Tests/AiRawInfoVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
import bases
from ai_friend import HERE, vectors_text
from game import Game

FIELDS = {
    'Person': [(31, 0x17A, 'h', 0), (37, 0x1E8, 'i', 0)],
    'Foothold': [(13, 0xD0, 'i', 0), (14, 0xD4, 'i', 0), (15, 0xD8, 'i', 0),
                 (16, 0x50, 'i', 0), (17, 0x54, 'i', 0), (18, 0xC8, 'B', 0),
                 (19, 0xC9, 'B', 1), (25, 0x67, 'B', 1)],
    'District': [(1, 0x16, 'b', 0), (5, 0x14, 'B', 0)],
    'Area': [(6, 0x44, 'H', 0), (7, 0x46, 'H', 0), (8, 0x48, 'H', 0)],
    'Hex': [(3, 0x16, 'H', 0)],
    'Scenery': [(2, 0x64, 'B', 0), (7, 0x66, 'H', 0)],
}


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    raw = game.base - 0x290
    manager = game.q(game.q(game.exe + ai.AI_MANAGER) + 8)
    context = game.q(manager + 0x38)
    board = game.q(context + 0x18)
    force = next(p for i in range(1, 52) if (p := game.q(raw + 0xDF30 + 8*i))
                 and oracle.call32(0x32F500, p) == 1)
    district = oracle.call(0x22E7E0, force)
    def valid_table(offset, count):
        return next(p for i in range(1, count) if (p := game.q(raw + offset + 8*i))
                    and oracle.call32(0x32F500, p) == 1)
    objects = {'Person': game.officer(736), 'District': district,
               'Foothold': oracle.call(0x22D3A0, district), 'Area': valid_table(0x6CAF0, 501),
               'Hex': valid_table(0xE270, 48400), 'Scenery': valid_table(0x85DB8, 33)}
    classes = {value['name']: value for value in ai.classes().values()}
    source, target = 0x61A17031, 0x61A17032
    node, output = oracle.alloc(0x80), oracle.alloc(4)
    answers = []
    for family, fields in FIELDS.items():
        obj = objects[family]
        oracle.call32(0xB26BA0, board, source, obj, keep=True)
        for kind, offset, fmt, mask in fields:
            bits = struct.calcsize(fmt)*8
            values = [0, 1, 2, 3, 127, 128, 255] if bits == 8 else [0, 1, 127, 255, 32767, 32768, 65535]
            if bits == 32:
                values += [0x7FFFFFFF, 0x80000000, 0xFFFFFFFF]
            for value in sorted(set(v & ((1 << bits)-1) for v in values)):
                oracle.poke(obj+offset, value.to_bytes(bits//8, 'little'))
                expected = int.from_bytes(value.to_bytes(bits//8, 'little'), 'little', signed=fmt.islower())
                if mask:
                    expected &= mask
                for myself in [False, True] if family == 'District' else [False]:
                    name = f'CGetMyself{family}InfoNode' if myself else f'CVariableGet{family}InfoNode'
                    params = [kind, target] if myself else [source, kind, target]
                    oracle.poke(node+0x38, struct.pack('<'+'I'*len(params), *params))
                    if myself:
                        oracle.call32(0xB26990, board, ai.name_hash('AgentType'), 3, keep=True)
                    status = oracle.call32(classes[name]['exec'], node, context, district if myself else 0, keep=True)
                    ok = oracle.call32(0xB26300, board, target, output, 0, read_after=[(output,4)])
                    actual = struct.unpack('<i', oracle.after[0])[0]
                    if status != 1 or ok != 1 or actual != expected:
                        raise AssertionError((name, kind, value, status, ok, actual, expected))
                    answers.append({'class': name, 'kind': kind, 'offset': offset, 'format': fmt,
                                    'mask': mask, 'stored': value, 'native': actual})
            # Preserve real fields before another helper/validity check.
            oracle.poke(obj+offset, game.read(obj+offset, bits//8))
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
            'method': 'native info node and blackboard getter; copied pages only, no stubs',
            'oracle_sha256': hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(), 'cases': answers}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    args = parser.parse_args()
    result = verify(Game())
    text = vectors_text(result)
    folder = HERE/'.cache'/'ai_raw_info'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json')
    path.write_text(text, encoding='utf-8')
    if args.vectors:
        args.vectors.write_text(text, encoding='utf-8')
    print(f"{len(result['cases'])} unnamed field reads matched; {path}")


if __name__ == '__main__':
    main()
