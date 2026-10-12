"""Read-only native evidence for environment gameplay; never writes game memory."""
import argparse
import ctypes
import hashlib
import json
import pathlib
import struct
import capstone
from game import Game, psapi
from season_watch import read_snapshot


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    g = Game()
    path = ctypes.create_unicode_buffer(32768)
    if not psapi.GetModuleFileNameExW(ctypes.c_void_p(g.h), ctypes.c_void_p(g.exe), path, len(path)):
        raise ctypes.WinError(ctypes.get_last_error())
    data = pathlib.Path(path.value).read_bytes()
    sha = hashlib.sha256(data).hexdigest()
    if sha != '87ed4e46590e0939c9efcaef14fa0f892917604dfda86e0a4be319fccffa2d6a':
        raise ValueError('Unverified executable')
    pe_at = struct.unpack_from('<I', data, 0x3C)[0]
    section_count = struct.unpack_from('<H', data, pe_at + 6)[0]
    optional_size = struct.unpack_from('<H', data, pe_at + 20)[0]
    sections = pe_at + 24 + optional_size
    def at(rva):
        for index in range(section_count):
            offset = sections + index * 40
            size, va, raw_size, raw = struct.unpack_from('<IIII', data, offset + 8)
            if va <= rva < va + raw_size:
                return raw + rva - va
        raise ValueError(f'RVA outside PE data: {rva:x}')
    exception_rva, exception_size = struct.unpack_from('<II', data, pe_at + 24 + 112 + 3 * 8)
    entries = [struct.unpack_from('<III', data, at(exception_rva) + i) for i in range(0, exception_size, 12)]
    targets = {}
    cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    for name, rva in [('app_update', 0x14B600), ('move_speed', 0x269150),
                      ('unit_loss', 0x2E00C0), ('add_morale', 0x270550),
                      ('set_fire', 0x2EDD40)]:
        entry = next(x for x in entries if x[0] == rva)
        # Steam's verified disk image has encrypted .text. Its exception directory
        # supplies boundaries, not plaintext code. Record two stable live reads.
        original = g.read(g.exe + rva, entry[1] - rva)
        live = g.read(g.exe + rva, len(original))
        targets[name] = dict(rva=hex(rva), size=len(original), stable=live == original,
                             sha256=hashlib.sha256(original).hexdigest(), first64=original[:64].hex())
        (args.out / (name + '.bin')).write_bytes(original)
        (args.out / (name + '.asm')).write_text('\n'.join(
            f'{i.address:x}  {i.bytes.hex():24} {i.mnemonic} {i.op_str}'
            for i in cs.disasm(original, rva)), encoding='utf-8')
    snapshot = read_snapshot(g)
    tiles = [g.q(g.base + 0xDFE0 + i * 8) for i in range(48400)]
    units = [g.q(g.base + 0x7E5A8 + i * 8) for i in range(501)]
    counties = [g.q(g.base + 0x6C860 + i * 8) for i in range(501)]
    cities = [g.q(g.base + 0xDAA8 + i * 8) for i in range(1, 47)]
    report = dict(pid=g.pid, exe=path.value, exeHash=sha, snapshot=snapshot,
                  gameWrites=0, targets=targets, classes=dict(
                  tile=hex(g.q(tiles[0])), unit=hex(g.q(units[0])),
                  county=hex(g.q(counties[1])), city=hex(g.q(cities[0]))),
                  contiguousTiles=all(p == tiles[0] + i * 0x20 for i, p in enumerate(tiles)),
                  contiguousUnits=all(p == units[0] + i * 0x200 for i, p in enumerate(units)),
                  appSlot=hex(g.q(g.exe + 0x1387728)),
                  cities=[dict(id=i+1, address=hex(p), record=g.read(p, 0x168).hex()) for i,p in enumerate(cities)],
                  terrainCounts={})
    raw = g.read(tiles[0], 48400 * 0x20)
    for i in range(48400):
        terrain = raw[i * 0x20 + 0x10]
        report['terrainCounts'][terrain] = report['terrainCounts'].get(terrain, 0) + 1
    (args.out / 'audit.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('cities', 'terrainCounts')}, indent=2))
    if not all(v['stable'] for v in targets.values()):
        raise ValueError('Native code changed between read-only snapshots')


if __name__ == '__main__':
    main()
