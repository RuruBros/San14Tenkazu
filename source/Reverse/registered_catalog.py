"""Audit registered choices with the saved original native eligibility code.

python registered_catalog.py
No live process, code patches, helper stubs or game file writes. Rebuilds the
registered choice subset and produces independent vectors for the C# predicates.
"""
import gzip
import hashlib
import json
from pathlib import Path
import re
import struct

from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RSP,
                              UC_X86_REG_RAX, UC_X86_REG_R14)

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SNAPSHOT = HERE / '.cache/fresh_snapshot_end.json.gz'
SCRATCH, STOP = 0x100000000, 0xD0000000


def audit():
    im = Image()
    snapshot = json.loads(gzip.decompress(SNAPSHOT.read_bytes()))
    mu = Uc(UC_ARCH_X86, UC_MODE_64)
    mu.mem_map(im.base, (len(im.img) + 4095) & ~4095)
    mu.mem_write(im.base, im.img)
    mu.mem_map(SCRATCH, 0x100000)
    mu.mem_map(STOP, 0x1000)
    world = SCRATCH + 0x60000
    mu.mem_write(im.base + 0x277DF18, struct.pack('<Q', world))
    decisions = []
    def stop_predicate(uc, address, size, _):
        if address in (im.base + 0x20AD7A, im.base + 0x20ADA1):
            decisions.append(address == im.base + 0x20AD7A)
            uc.emu_stop()
    mu.hook_add(UC_HOOK_CODE, stop_predicate)
    vectors, boundaries = [], []
    for kind, key, vtable, vec, entry in (
        ('personality', '0x7dd18', 0x13A71F0, 0xC8, 0x16F7F0),
        ('tactic', '0x774d0', 0x13A6DB8, 0x70, 0x20AD1A),
    ):
        table = snapshot['tables'][key]
        # Confirm these original class virtuals really use the decoded byte/word operations.
        assert im.u64(vtable + (0x58 if kind == 'personality' else 0x48)) == im.base + (0xDB30 if kind == 'personality' else 0xDB00)
        array = SCRATCH + 0x10000
        mu.mem_write(world + int(key, 16), struct.pack('<Q', array))
        for index, raw in enumerate(table['records']):
            record = bytearray.fromhex(raw)
            vector = bytes.fromhex(table['extra'][str(index)]['vecC8' if kind == 'personality' else 'vec70'])
            pointer = SCRATCH + 0x30000 + index * 64
            patched = bytearray(record)
            struct.pack_into('<Q', patched, 0, im.base + vtable)
            struct.pack_into('<QQ', patched, vec, pointer, pointer + len(vector))
            address = array + index * table['size']
            mu.mem_write(address, bytes(patched))
            mu.mem_write(pointer, vector)
        for index, raw in enumerate(table['records']):
            record = bytes.fromhex(raw)
            vector = bytes.fromhex(table['extra'][str(index)]['vecC8' if kind == 'personality' else 'vec70'])
            stack = SCRATCH + 0xF0008
            mu.mem_write(stack, struct.pack('<Q', STOP))
            mu.reg_write(UC_X86_REG_RSP, stack)
            address = array + index * table['size']
            if kind == 'personality':
                mu.reg_write(UC_X86_REG_RCX, address)
                mu.emu_start(im.base + 0x32F500, STOP, count=10000)
                valid = bool(mu.reg_read(UC_X86_REG_RAX))
                mu.reg_write(UC_X86_REG_RSP, stack)
                mu.reg_write(UC_X86_REG_RCX, address)
                mu.reg_write(UC_X86_REG_RDX, 4)
                mu.emu_start(im.base + entry, STOP, count=10000)
                eligible = bool(valid and record[0xBE] and mu.reg_read(UC_X86_REG_RAX))
            else:
                mu.reg_write(UC_X86_REG_R14, address)
                decisions.clear()
                mu.emu_start(im.base + entry, STOP, count=10000)
                assert len(decisions) == 1
                eligible = decisions[0]
            vectors.append(dict(kind=kind, id=index, record=raw, vector=vector.hex(), eligible=eligible))
        # Validity is a separate gate: an availability flag cannot make entry 0,
        # or a custom personality without its first effect, valid.
        for index, effect in ([(0, 1), (1, 0), (255, 0), (256, 0), (256, 1), (355, 0), (355, 1)] if kind == 'personality' else [(0, 0)]):
            record = bytearray.fromhex(table['records'][index])
            record[0xBE if kind == 'personality' else 0x40] = 1
            record[0xBF if kind == 'personality' else 0x44] = 16 if kind == 'personality' else 2
            if kind == 'personality': record[0xB6] = effect
            struct.pack_into('<QQ', record, vec, 0, 0)
            patched = bytearray(record)
            struct.pack_into('<Q', patched, 0, im.base + vtable)
            address = array + index * table['size']
            mu.mem_write(address, bytes(patched))
            mu.mem_write(stack, struct.pack('<Q', STOP))
            mu.reg_write(UC_X86_REG_RSP, stack)
            if kind == 'personality':
                mu.reg_write(UC_X86_REG_RCX, address)
                mu.emu_start(im.base + 0x32F500, STOP, count=10000)
                valid = bool(mu.reg_read(UC_X86_REG_RAX))
                mu.reg_write(UC_X86_REG_RSP, stack)
                mu.reg_write(UC_X86_REG_RCX, address)
                mu.reg_write(UC_X86_REG_RDX, 4)
                mu.emu_start(im.base + entry, STOP, count=10000)
                eligible = bool(valid and mu.reg_read(UC_X86_REG_RAX))
            else:
                mu.reg_write(UC_X86_REG_R14, address)
                decisions.clear()
                mu.emu_start(im.base + entry, STOP, count=10000)
                assert len(decisions) == 1
                eligible = decisions[0]
            boundaries.append(dict(kind=kind, id=index, record=record.hex(), vector='', eligible=eligible))
    names_path = ROOT / 'source/Core/RegisteredNames.txt'
    lines = [l for l in names_path.read_text(encoding='utf-8').splitlines() if not l.startswith(('registered-personality\t', 'registered-tactic\t'))]
    available = {(v['kind'], v['id']) for v in vectors if v['eligible'] and v['id'] > 0}
    additions = []
    for line in lines:
        parts = line.split('\t')
        if len(parts) < 3 or parts[0] not in ('personality', 'tactic'):
            continue
        if (parts[0], int(parts[1])) in available and not re.fullmatch(r'(개성|전법|전)\d+', parts[2]):
            additions.append('registered-' + line)
    names_path.write_text('\n'.join(lines + additions) + '\n', encoding='utf-8')
    result = dict(snapshotSha256=hashlib.sha256(SNAPSHOT.read_bytes()).hexdigest(),
                  nativeFunctions=['0x32F500', '0x234270', '0x234500', '0x16F7F0', '0x20AD1A..0x20AD7A/0x20ADA1', '0xDB00', '0xDB30'],
                  scope='Original eligibility instructions and decoders, captured valid table records; no live game or conversion execution.',
                  choices={k: sum(l.startswith('registered-' + k + '\t') for l in additions) for k in ('personality', 'tactic')},
                  vectors=vectors, boundaryVectors=boundaries)
    (ROOT / 'source/Tests/RegisteredCatalogVectors.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    output = ROOT / '.cache/registered-officers/catalog-audit.json'
    output.write_text(json.dumps({k: v for k, v in result.items() if k not in ('vectors', 'boundaryVectors')}, ensure_ascii=False, indent=2), encoding='utf-8')
    print(output.read_text(encoding='utf-8'))


if __name__ == '__main__':
    audit()
