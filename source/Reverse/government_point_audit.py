"""Run the original military intelligence point cap/setter on copied records only."""
import argparse
import hashlib
import json
import struct
from datetime import datetime, timezone
from pathlib import Path

from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RSP, UC_X86_REG_RIP


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--copied-records', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    copied = json.loads(args.copied_records.read_text(encoding='utf-8-sig'))
    im = Image()
    u = Uc(UC_ARCH_X86, UC_MODE_64)
    u.mem_map(im.base, (len(im.img) + 0xFFF) & ~0xFFF)
    u.mem_write(im.base, im.img)
    root, forces, corps, stack, stop = [0x100000000 + i * 0x100000 for i in range(5)]
    for at in (root, forces, corps, stack, stop):
        u.mem_map(at, 0x100000)
    u.mem_write(im.base + 0x277DF18, struct.pack('<Q', root))
    for table, offset, start, stride in [('force', 0xDF30, forces, 0x268), ('corps', 0xE0D0, corps, 0x30)]:
        for record in copied[table]:
            index = record['index']
            u.mem_write(root + offset + index * 8, struct.pack('<Q', start + index * stride))
            u.mem_write(start + index * stride, bytes.fromhex(record['data']))

    def call(rva, ptr, value=0):
        sp = stack + 0x80008
        u.mem_write(sp, struct.pack('<Q', stop))
        for reg, val in [(UC_X86_REG_RSP, sp), (UC_X86_REG_RCX, ptr), (UC_X86_REG_RDX, value & 0xFFFFFFFF)]:
            u.reg_write(reg, val)
        u.emu_start(im.base + rva, stop, count=1000)
        if u.reg_read(UC_X86_REG_RIP) != stop:
            raise RuntimeError('Original Native function did not return')
        return u.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF

    failures, rows, calls = [], [], 0
    # Rank switches and invalid rank defaults are executed by the original jump table.
    ptr = corps + 0x30
    u.mem_write(ptr + 0x10, b'\x01')
    for rank in range(14):
        u.mem_write(forces + 0x268 + 0x47, bytes([rank]))
        cap = call(0x2311B0, ptr)
        calls += 1
        for before in (0, 111, cap):
            for value in (-2147483648, -1, 0, 1, cap - 1, cap, cap + 1, 2147483647):
                u.mem_write(ptr + 0x24, struct.pack('<i', before))
                call(0x6273E0, ptr, value)
                calls += 1
                got = struct.unpack('<i', bytes(u.mem_read(ptr + 0x24, 4)))[0]
                expected = min(max(0, value), cap)
                if got != expected:
                    failures.append([rank, before, value, expected, got])
        rows.append({'rank': rank, 'cap': cap})
    result = {'utc': datetime.now(timezone.utc).isoformat(), 'copiedReadUtc': copied['utc'],
              'copiedProcessId': copied['pid'], 'imageSha256': hashlib.sha256(im.img).hexdigest(),
              'nativeCalls': calls, 'mismatches': failures, 'caps': rows,
              'nativeCodePatched': False, 'liveMemoryWritten': False, 'profileSaved': False}
    args.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({'nativeCalls': calls, 'mismatches': len(failures), 'caps': rows}))
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
