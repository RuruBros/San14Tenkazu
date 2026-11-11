"""Verify linked editor field widths using the two pinned, offline PE captures.

Execute original record/array serializers in Unicorn. Archive IO and unrelated
string/list serializers are boundaries; no running game or gameplay API is used.
"""
import argparse
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX,
                               UC_X86_REG_R8, UC_X86_REG_RIP, UC_X86_REG_RSP)

from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, require
from version_audit import MemoryImage, digest


RECORDS = {'officer': (0x2390B0, 0x23AA60), 'force': (0x237BB0, 0x239560),
           'base': (0x237810, 0x2391C0)}
OFFICER_FIELDS = {0xE0: 1, 0xE2: 2, 0xE4: 2, 0xE6: 1, 0xE7: 1,
                  0x162: 2, 0x164: 1, 0x165: 1, 0x166: 2, 0x168: 1,
                  0x1BC: 1, 0x1BE: 2}
POLICY_FIELDS = {start + slot * 2: 2 for start in
                 (0x54, 0x62, 0x72, 0x80, 0x90, 0x9E, 0xAE, 0xBC, 0xCC, 0xDA)
                 for slot in range(7)}


def corresponding_call(old, new, record, target):
    a, b = RECORDS[record]
    old_edges = old.describe(a)['direct_edges']
    new_edges = new.describe(b)['direct_edges']
    matches = {left['rva'] for left, right in zip(old_edges, new_edges, strict=True)
               if right['rva'] == target}
    require(len(matches) == 1, f'Unique {record} serializer boundary {target:x}')
    return matches.pop(), target


def replay(image, entry, writer, arrays, november):
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    cpu.mem_map(image.base, image.size)
    cpu.mem_write(image.base, image.data)
    scratch = 0x200000000
    cpu.mem_map(scratch, 0x100000)
    record, archive, stack, stop = scratch, scratch + 0x1000, scratch + 0xEEF8, scratch + 0xF000
    cpu.mem_write(record, bytes((i * 37 + 11) & 255 for i in range(0x300)))
    if entry == RECORDS['officer'][int(november)]:
        # The serializer's final two trait flags use its object's vtable and
        # original table slot. Keep that unrelated virtual query at the boundary.
        raw = scratch + 0x10000
        cpu.mem_write(image.base + (0x2780F60 if november else 0x277DF18), struct.pack('<Q', raw))
        cpu.mem_write(raw + 0x73A50, struct.pack('<Q', record))
        cpu.mem_write(record, struct.pack('<Q', image.base + (0x13A9BC8 if november else 0x13A6B88)))
    cpu.mem_write(archive + 0x88, struct.pack('<I', 0x100))
    ranges = [(image.base + start, image.base + image.describe(start)['code_end'])
              for start in (entry, *arrays)]
    fields, boundaries = {}, set()

    def hook(uc, address, _size, _user):
        if any(lo <= address < hi for lo, hi in ranges):
            return
        rva = address - image.base
        if rva == writer:
            pointer, size = uc.reg_read(UC_X86_REG_RDX), uc.reg_read(UC_X86_REG_R8)
            require(scratch <= pointer < scratch + 0x10000 and 0 < size <= 0x100,
                    f'Archive field boundary: pointer={pointer:x}, size={size}')
            if record <= pointer < record + 0x300:
                offset = pointer - record
                require(offset not in fields, f'Field serialized twice: {offset:x}')
                fields[offset] = size
        else:
            boundaries.add(rva)
        rsp = uc.reg_read(UC_X86_REG_RSP)
        uc.reg_write(UC_X86_REG_RAX, uc.reg_read(UC_X86_REG_RCX))
        uc.reg_write(UC_X86_REG_RSP, rsp + 8)
        uc.reg_write(UC_X86_REG_RIP, struct.unpack('<Q', uc.mem_read(rsp, 8))[0])

    cpu.hook_add(UC_HOOK_CODE, hook)
    cpu.mem_write(stack, struct.pack('<Q', stop))
    cpu.reg_write(UC_X86_REG_RSP, stack)
    cpu.reg_write(UC_X86_REG_RCX, record)
    cpu.reg_write(UC_X86_REG_RDX, archive)
    before = bytes(cpu.mem_read(record, 0x300))
    try:
        cpu.emu_start(image.base + entry, stop, count=100000)
    except Exception as error:
        raise RuntimeError(f'Serializer {entry:x}, instruction {cpu.reg_read(UC_X86_REG_RIP) - image.base:x}') from error
    require(cpu.reg_read(UC_X86_REG_RIP) == stop and cpu.reg_read(UC_X86_REG_RSP) == stack + 8,
            'Serializer returned normally')
    after = bytes(cpu.mem_read(record, 0x300))
    ignored = {0x1EC, 0x1ED} if entry == RECORDS['officer'][int(november)] else set()
    require(all(a == b for offset, (a, b) in enumerate(zip(before, after, strict=True)) if offset not in ignored),
            'Archive preserves fields outside unrelated final trait flags')
    return fields, sorted(boundaries)


def audit(old, new):
    writer = corresponding_call(old, new, 'officer', 0x3FCC80)
    arrays = [corresponding_call(old, new, 'force', target) for target in (0x2206D0, 0x2211E0)]
    for a, b in [*RECORDS.values(), *arrays]:
        require(old.describe(a)['shape_sha256'] == new.describe(b)['shape_sha256'],
                f'Original instruction/operand widths differ at {b:x}')
    versions = {}
    for version, image, index in [('legacy', old, 0), ('november', new, 1)]:
        records = {}
        for kind, entries in RECORDS.items():
            fields, skipped = replay(image, entries[index], writer[index],
                                     [pair[index] for pair in arrays] if kind == 'force' else [], index == 1)
            expected = (OFFICER_FIELDS | {start + slot * 2: 2 for start in (0xC0, 0xD0) for slot in range(8)}
                        if kind == 'officer' else
                        POLICY_FIELDS | {0x52: 2} | {0x14 + slot: 1 for slot in range(51)}
                        if kind == 'force' else {0x2E: 2})
            require(all(fields.get(offset) == size for offset, size in expected.items()),
                    f'{version} {kind} actual field widths: {fields}')
            records[kind] = {'fields': {hex(k): v for k, v in sorted(expected.items())},
                             'unrelatedBoundaries': [hex(rva) for rva in skipped]}
        versions[version] = records
    guards = []
    for entry in [pair[1] for pair in RECORDS.values()] + [pair[1] for pair in arrays]:
        length = new.describe(entry)['code_end'] - entry
        guards.append({'rva': hex(entry), 'length': length,
                       'sha256': digest(new.data[entry:entry + length]).upper()})
    return {'gameAccess': False, 'archiveIoBoundary': True, 'versions': versions, 'guards': guards}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for version in ('legacy', 'november'):
        parser.add_argument(f'--{version}-image', type=Path, required=True)
        parser.add_argument(f'--{version}-base', type=lambda value: int(value, 0), required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    old = MemoryImage.load(args.legacy_image, args.legacy_base)
    new = MemoryImage.load(args.november_image, args.november_base)
    require(digest(old.data) == LEGACY_IMAGE and digest(new.data) == NOVEMBER_IMAGE, 'Unregistered capture')
    result = audit(old, new)
    fixture = Path(__file__).resolve().parents[1] / 'Tests' / 'Vectors' / 'GameLinkedEditSerializers20261111.json'
    require(json.loads(fixture.read_text()) == result, 'Linked field regression vector differs')
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print('Linked field serializers verified for both pinned captures; gameAccess=false')


if __name__ == '__main__':
    main()
