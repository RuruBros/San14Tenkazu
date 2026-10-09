"""Run original numeric conversion and slot readers on isolated copied records.

No live process, original-file writes, instruction patches or helper stubs.
This executes 0x20AA26..0x20AE38, not the whole new-game creation path:
names, biography, initialization, UI/resources and gameplay are outside its scope.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import struct

from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RBX, UC_X86_REG_RCX,
    UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R14, UC_X86_REG_R15,
    UC_X86_REG_RBP, UC_X86_REG_RSP, UC_X86_REG_RSI, UC_X86_REG_RDI, UC_X86_REG_RIP)

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SCRATCH, STOP = 0x100000000, 0xD0000000
STRIDE = 1117
# Independently transcribed packed serializer -> CEditPerson memory offsets.
LAYOUT = [(0x16, 0x48, 2), (0x18, 0x4A, 1), (0x19, 0x4C, 2),
    (0x1B, 0x4E, 2), (0x1D, 0x50, 2), (0x1F, 0x52, 2),
    (0x21, 0x54, 2), (0x23, 0x56, 2), (0x25, 0x58, 2),
    (0x29, 0x5C, 1), (0x2A, 0x5E, 16), (0x3A, 0x6E, 16),
    (0x4A, 0x7E, 5), (0x4F, 0x84, 18), (0x61, 0x98, 4),
    (0x65, 0x9C, 10), (0x6F, 0xA6, 1), (0x70, 0xA7, 1),
    (0x71, 0xA8, 1), (0x72, 0xA9, 1)]


def run(original=None):
    im = Image()
    snapshot_path = HERE / '.cache/fresh_snapshot_end.json.gz'
    snapshot = json.loads(gzip.decompress(snapshot_path.read_bytes()))
    mu = Uc(UC_ARCH_X86, UC_MODE_64)
    mu.mem_map(im.base, (len(im.img) + 4095) & ~4095)
    mu.mem_write(im.base, im.img)
    mu.mem_map(SCRATCH, 0x100000)
    mu.mem_map(STOP, 0x1000)
    world, edit, person, stack = (SCRATCH + n for n in (0x60000, 0x50000, 0x51000, 0xF0008))
    mu.mem_write(im.base + 0x277DF18, struct.pack('<Q', world))
    tables = {}
    for kind, key, vtable, vec, at, vat in (
        ('personality', '0x7dd18', 0x13A71F0, 0xC8, 0x10000, 0x30000),
        ('tactic', '0x774d0', 0x13A6DB8, 0x70, 0x26000, 0x38000),
    ):
        table = snapshot['tables'][key]
        tables[kind] = []
        for index, raw in enumerate(table['records']):
            data = bytearray.fromhex(raw)
            vector = bytes.fromhex(table['extra'][str(index)]['vecC8' if kind == 'personality' else 'vec70'])
            address, pointer = SCRATCH + at + index * table['size'], SCRATCH + vat + index * 64
            # Rebase captured object/vector pointers; the instructions remain unmodified.
            struct.pack_into('<Q', data, 0, im.base + vtable)
            struct.pack_into('<QQ', data, vec, pointer, pointer + len(vector))
            mu.mem_write(address, bytes(data))
            if vector: mu.mem_write(pointer, vector)
            mu.mem_write(world + int(key, 16) + index * 8, struct.pack('<Q', address))
            tables[kind].append((address, bytes(data)))

    def call(rva, rcx, rdx=0, r8=0):
        mu.mem_write(stack, struct.pack('<Q', STOP))
        for reg, value in ((UC_X86_REG_RSP, stack), (UC_X86_REG_RCX, rcx),
            (UC_X86_REG_RDX, rdx), (UC_X86_REG_R8, r8)):
            mu.reg_write(reg, value)
        mu.emu_start(im.base + rva, STOP, count=100000)
        assert mu.reg_read(UC_X86_REG_RIP) == STOP, hex(rva)
        return mu.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF

    eligible = {}
    for kind, entries in tables.items():
        ids = []
        for index, (address, data) in enumerate(entries):
            valid = call(0x32F500, address) != 0
            if kind == 'personality':
                allowed = valid and data[0xBE] != 0 and call(0x16F7F0, address, 4) != 0
            else:
                # Vector availability is already independently audited in registered_catalog.py.
                encoded = bytes.fromhex(snapshot['tables']['0x774d0']['extra'][str(index)]['vec70'])
                flags = (((encoded[0] << 1 | encoded[0] >> 7) ^ index) & 255) if len(encoded) == 40 else data[0x44]
                allowed = valid and data[0x40] != 0 and (flags & 2) != 0
            if allowed: ids.append(index)
        eligible[kind] = ids

    def u16(data, offset): return struct.unpack_from('<H', data, offset)[0]
    def values(data):
        return dict(face=u16(data, 0xA6), gender=data[0xA9], appear=u16(data, 0xAA),
            birth=u16(data, 0xAC), death=u16(data, 0xAE), clan=u16(data, 0xB2),
            father=u16(data, 0xB4), mother=u16(data, 0xB6), spouse=u16(data, 0xBA),
            sworn=u16(data, 0xBC), compatibility=data[0xBE],
            likes=[u16(data, 0xC0 + 2*k) for k in range(8)],
            dislikes=[u16(data, 0xD0 + 2*k) for k in range(8)], abilities=list(data[0xEC:0xF1]),
            personalities=[u16(data, 0x118 + 2*k) for k in range(9)],
            formations=struct.unpack_from('<I', data, 0x12C)[0], tactics=list(data[0x130:0x13A]),
            ideology=data[0x14C], policy=data[0x14D], policyLevel=data[0x182],
            voiceStored=data[0x150], voiceUsed=data[0x14E])

    cases = []
    original_bytes = Path(original).read_bytes() if original else None
    if original_bytes is not None:
        assert len(original_bytes) == STRIDE * 150
        for k in range(150):
            data = original_bytes[k*STRIDE:(k+1)*STRIDE]
            if u16(data, 0): cases.append((f'original-{u16(data, 0)}', data))
    for number in (0, 1, 5, 6, 13, 16, 100, 149, 255, 350, 6000, 65535):
        data = bytearray(STRIDE)
        for packed, _, size in LAYOUT:
            if size == 2: struct.pack_into('<H', data, packed, number)
            elif size == 1: data[packed] = min(number, 255)
        data[0x4A:0x4F] = bytes([min(number, 255)]) * 5
        struct.pack_into('<I', data, 0x61, 0xFFFFFFFF)
        for start in (0x2A, 0x3A):
            struct.pack_into('<8H', data, start, *([number]*8))
        struct.pack_into('<9H', data, 0x4F, *eligible['personality'][:9])
        data[0x65:0x6F] = bytes(eligible['tactic'][:10])
        cases.append((f'boundary-{number}', bytes(data)))
    sparse = bytearray(cases[-1][1])
    struct.pack_into('<9H', sparse, 0x4F, 0, 79, 0, *eligible['personality'][:6])
    cases.append(('sparse-personalities-compacted', bytes(sparse)))
    report = []
    for name, packed in cases:
        source = bytearray(0x500)
        for a, b, size in LAYOUT: source[b:b+size] = packed[a:a+size]
        destination = bytearray(0x400)
        struct.pack_into('<H', destination, 0x10, 3001)
        mu.mem_write(edit, bytes(source)); mu.mem_write(person, bytes(destination))
        for reg, value in ((UC_X86_REG_RBX, person), (UC_X86_REG_R14, edit),
            (UC_X86_REG_R15, 0), (UC_X86_REG_RSI, 6000),
            (UC_X86_REG_RBP, SCRATCH + 0xE8000), (UC_X86_REG_RSP, stack - 0x200)):
            mu.reg_write(reg, value)
        mu.mem_write(SCRATCH + 0xE8000 - 0x39, struct.pack('<Q', edit))
        mu.emu_start(im.base + 0x20AA26, im.base + 0x20AE38, count=100000)
        assert mu.reg_read(UC_X86_REG_RIP) == im.base + 0x20AE38
        got = values(bytes(mu.mem_read(person, 0x400)))
        valid_traits = [u16(packed, 0x4F+2*k) for k in range(9) if u16(packed, 0x4F+2*k) in eligible['personality']]
        valid_tactics = [x for x in packed[0x65:0x6F] if x in eligible['tactic']]
        expected = dict(face=u16(packed, 0x16), gender=min(packed[0x18], 1),
            appear=min(u16(packed, 0x19)+14, 350), birth=min(u16(packed, 0x19), 350),
            death=min(u16(packed, 0x1B), 350), clan=u16(packed, 0x1D),
            father=min(u16(packed, 0x1F), 6000), mother=min(u16(packed, 0x21), 6000),
            spouse=min(u16(packed, 0x23), 6000), sworn=min(u16(packed, 0x25), 6000),
            compatibility=min(packed[0x29], 149),
            likes=[min(u16(packed, 0x2A+2*k), 6000) for k in range(8)],
            dislikes=[min(u16(packed, 0x3A+2*k), 6000) for k in range(8)],
            abilities=[min(x, 100) for x in packed[0x4A:0x4F]],
            personalities=valid_traits + [0]*(9-len(valid_traits)),
            formations=struct.unpack_from('<I', packed, 0x61)[0] & (((1 << 21)-1) & ~((1<<14)|(1<<19)|(1<<20))),
            tactics=valid_tactics + [0]*(10-len(valid_tactics)), ideology=min(packed[0x6F], 10),
            policy=min(packed[0x70], 120), policyLevel=min(packed[0x71], 6),
            voiceStored=min(packed[0x72], 16), voiceUsed=packed[0x72] if 1 <= packed[0x72] <= 13 else 0)
        assert got == expected, (name, got, expected)
        used_traits = [call(0x2305D0, person, k) for k in range(9)]
        used_tactics = [call(0x22A320, person, k) for k in range(10)]
        assert used_traits == got['personalities'][:5] + [0]*4
        assert used_tactics == got['tactics']
        report.append(dict(name=name, converted=got, personalityReads=used_traits, tacticReads=used_tactics))

    slot_cases = []
    for officer_id in (3001, 4101, 5101):
        mu.mem_write(person, bytes(0x400)); mu.mem_write(person+0x10, struct.pack('<H', officer_id))
        for k, trait in enumerate(eligible['personality'][:9]): call(0x241960, person, k, trait)
        stored = list(struct.unpack('<9H', mu.mem_read(person+0x118, 18)))
        read = [call(0x2305D0, person, k) for k in range(9)]
        assert stored == eligible['personality'][:9] and read == stored[:5] + [0]*4
        slot_cases.append(dict(officerId=officer_id, stored=stored, read=read))
    clan_cases = []
    for number in (0, 1, 3001, 6000, 6001, 65535):
        mu.mem_write(person+0xB2, struct.pack('<H', number))
        mu.mem_write(person+0x10, struct.pack('<H', 3001))
        for reg, value in ((UC_X86_REG_RDI, person), (UC_X86_REG_R15, 0), (UC_X86_REG_RSP, stack - 0x200)):
            mu.reg_write(reg, value)
        mu.emu_start(im.base + 0x2181D4, im.base + 0x21821B, count=10000)
        assert mu.reg_read(UC_X86_REG_RIP) == im.base + 0x21821B
        actual = struct.unpack('<H', mu.mem_read(person+0xB2, 2))[0]
        assert actual == (number if 1 <= number <= 6000 else 3001)
        clan_cases.append(dict(input=number, output=actual))
    if original_bytes is not None: assert Path(original).read_bytes() == original_bytes
    result = dict(scope=__doc__.strip(), imageSha256=hashlib.sha256(im.img).hexdigest(),
        snapshotSha256=hashlib.sha256(snapshot_path.read_bytes()).hexdigest(),
        originalSha256=hashlib.sha256(original_bytes).hexdigest() if original_bytes else None,
        originalUnchanged=original_bytes is not None, patches=[], helperStubs=[],
        numericSpan=['0x20AA26', '0x20AE38'], clanSpan=['0x2181D4', '0x21821B'],
        cases=report, slotCases=slot_cases, clanCases=clan_cases)
    output = ROOT / '.cache/registered-officers/runtime-audit.json'
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    vectors = {k: result[k] for k in ('scope', 'imageSha256', 'snapshotSha256', 'patches', 'helperStubs', 'slotCases', 'clanCases')}
    (ROOT / 'source/Tests/RegisteredRuntimeVectors.json').write_text(json.dumps(vectors, indent=2), encoding='utf-8')
    print(json.dumps(dict(output=str(output), numericCases=len(report), slotCases=slot_cases,
        originalSha256=result['originalSha256'], originalUnchanged=result['originalUnchanged']), ensure_ascii=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--original')
    run(parser.parse_args().original)
