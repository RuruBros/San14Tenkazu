"""Run product-generated trait list hooks against captured PE memory, without a game process."""
import argparse
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import (UC_X86_REG_EFLAGS, UC_X86_REG_R11, UC_X86_REG_RBP, UC_X86_REG_RCX,
    UC_X86_REG_RDX, UC_X86_REG_RIP, UC_X86_REG_RSI, UC_X86_REG_RSP)

from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, ROOT, require
from version_audit import MemoryImage, digest


def replay(image, payload, metadata, layout):
    base, scratch, allocation = image.base, 0x200000000, metadata['allocation']
    require(metadata['module'] == base and len(payload) == metadata['size'], 'Exported payload identity')
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    cpu.mem_map(base, image.size)
    cpu.mem_write(base, image.data)
    cpu.mem_map(allocation, len(payload))
    cpu.mem_write(allocation, payload)
    cpu.mem_map(scratch, 0x4000)
    stack, frame, receiver = scratch + 0x3000, scratch + 0x1000, scratch + 0x2000
    site = base + metadata['site']
    original = bytes(cpu.mem_read(site, 22))
    require(original.hex() == '488b4577488986500100004c8b6d7f4c89ae58010000', 'Trait hook/resume ABI')
    cpu.mem_write(site, b'\xff\x25\0\0\0\0' + struct.pack('<Q', allocation) + b'\x90')
    columns = base + layout['Columns']['Rva']
    state = [(UC_X86_REG_RSP, stack), (UC_X86_REG_RBP, frame), (UC_X86_REG_RSI, receiver),
             (UC_X86_REG_RCX, 0x11112222), (UC_X86_REG_RDX, 0x55556666), (UC_X86_REG_R11, 0x33334444),
             (UC_X86_REG_EFLAGS, 0x246)]
    cases = 0
    for table in [*layout['Tables'], dict(Rva=0x123450, TraitIndex=-1)]:
        for matching_columns in (True, False):
            table_address = base + table['Rva']
            column_address = columns if matching_columns else columns + 96
            cpu.mem_write(frame + 0x77, struct.pack('<QQ', table_address, column_address))
            cpu.mem_write(receiver + 0x150, bytes(16))
            for register, value in state:
                cpu.reg_write(register, value)
            cpu.emu_start(site, site + len(original), count=2000)
            require(cpu.reg_read(UC_X86_REG_RIP) == site + len(original), 'Hook did not return')
            require(all(cpu.reg_read(register) == value for register, value in state), 'Hook changed a preserved register')
            actual_table, actual_columns = struct.unpack('<QQ', cpu.mem_read(receiver + 0x150, 16))
            trait = table['TraitIndex']
            if trait < 0 or not matching_columns:
                require((actual_table, actual_columns) == (table_address, column_address), 'Hook changed an unrelated list')
            else:
                require(actual_columns == allocation + 4096, 'Wrong copied columns pointer')
                size = len(table['Bytes']) // 2
                source = bytearray(cpu.mem_read(table_address, size))
                replacement = cpu.mem_read(actual_table, size)
                vector, = struct.unpack_from('<Q', replacement, trait * 24)
                struct.pack_into('<Q', source, trait * 24, vector)
                require(source == replacement, 'Hook changed another tab')
                first = table.get('TraitBase', 61)
                added = 217 if first == 170 else 213
                require(list(struct.unpack('<11i', cpu.mem_read(vector, 44))) ==
                        [0, *range(first, first + 5), *range(added, added + 4), -1], 'Incorrect expanded trait columns')
            cases += 1
    cpu.mem_write(site, original)
    require(bytes(cpu.mem_read(site, len(original))) == original, 'Hook restore')
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', type=Path, required=True)
    parser.add_argument('--base', type=lambda s: int(s, 0), required=True)
    parser.add_argument('--payload', type=Path, required=True)
    parser.add_argument('--november', action='store_true')
    args = parser.parse_args()
    image = MemoryImage.load(args.image, args.base)
    require(digest(image.data) == (NOVEMBER_IMAGE if args.november else LEGACY_IMAGE), 'Unregistered PE capture')
    definition = json.loads((ROOT / ('November2026.json' if args.november else 'trait-list.json')).read_text())
    layout = definition['Traits']['Layout'] if args.november else definition
    metadata = json.loads((args.payload / 'payload.json').read_text())
    cases = replay(image, (args.payload / 'payload.bin').read_bytes(), metadata, layout)
    print(f'{cases} native trait list hook cases verified, including unrelated lists and original bytes')


if __name__ == '__main__':
    main()
