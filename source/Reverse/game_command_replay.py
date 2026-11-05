"""Replay native command leaf effects and refusal paths from the two registered captures."""
import argparse
from collections import Counter
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX,
                               UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RIP, UC_X86_REG_RSP)

from game_command_profile import FUNCTIONS
from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, require
from version_audit import MemoryImage, digest


def replay(image, november):
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    base, scratch = image.base, 0x200000000
    cpu.mem_map(base, image.size)
    cpu.mem_write(base, image.data)
    cpu.mem_map(scratch, 0x5000)
    stack, stop = scratch + 0x3ef8, scratch + 0x4000
    cases = Counter()

    def target(legacy):
        return FUNCTIONS[legacy] if november else legacy

    def call(legacy, *args, expect_return=True):
        cpu.mem_write(stack, struct.pack('<Q', stop))
        cpu.reg_write(UC_X86_REG_RSP, stack)
        for reg, value in zip((UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9),
                              [*args, *([0] * (4 - len(args)))], strict=True):
            cpu.reg_write(reg, value & 0xffffffffffffffff)
        cpu.emu_start(base + target(legacy), stop, count=10000)
        if expect_return:
            require(cpu.reg_read(UC_X86_REG_RIP) == stop and cpu.reg_read(UC_X86_REG_RSP) == stack + 8,
                    f'Native return convention {legacy:x}')
        return cpu.reg_read(UC_X86_REG_RAX) & 0xffffffff

    for year in range(101, 352):
        for month in range(1, 13):
            for day in (1, 30):
                cpu.mem_write(scratch, struct.pack('<HBBB', year, month, day, 23))
                require(call(0x333480, scratch) == 1, 'Allowed calendar date')
                cases['calendar-date'] += 1
    # Native accepts month 13, day 31 and hour 24. The editor's input is narrower.
    for year, month, day, hour in ((352, 1, 1, 0), (200, 0, 1, 0), (200, 14, 1, 0),
                                  (200, 1, 0, 0), (200, 1, 32, 0), (200, 1, 1, 25)):
        cpu.mem_write(scratch, struct.pack('<HBBB', year, month, day, hour))
        require(call(0x333480, scratch) == 0, 'Invalid calendar date')
        cases['calendar-date'] += 1
    for year, month, day, hour, steps in ((188, 12, 30, 23, 1), (189, 1, 1, 0, -1),
                                         (200, 2, 30, 12, 12), (200, 2, 1, 0, -24),
                                         (200, 2, 1, 0, 24 * 360)):
        cpu.mem_write(scratch, struct.pack('<HBBB', year, month, day, hour) + b'\xa5')
        call(0x31CBA0, scratch, steps)
        days, want_hour = divmod((year * 360 + (month - 1) * 30 + day - 1) * 24 + hour + steps, 24)
        want_year, day_of_year = divmod(days, 360)
        want_month, want_day = divmod(day_of_year, 30)
        require(cpu.mem_read(scratch, 6) == struct.pack('<HBBB', want_year, want_month + 1, want_day + 1, want_hour) + b'\xa5',
                'Calendar advance or preserved byte')
        cases['calendar-advance'] += 1

    record = bytes([0xa5]) * 0x200
    for corps in (-10, -1, 0, 1, 32, 51, 52, 100):
        cpu.mem_write(scratch, record)
        call(0x240C90, scratch, corps)
        expected = record[:0x30] + bytes([max(0, min(51, corps))]) + record[0x31:]
        require(cpu.mem_read(scratch, len(record)) == expected, 'Transfer corps setter')
        cases['transfer-corps'] += 1
    for bit in range(7):
        for enabled in (0, 1):
            cpu.mem_write(scratch, record)
            call(0x2408E0, scratch, bit, enabled)
            mask = (0xa5 & ~(1 << bit)) | (enabled << bit) if bit < 5 else 0xa5
            require(cpu.mem_read(scratch, len(record)) == record[:0x150] + bytes([mask]) + record[0x151:],
                    'Transfer unit flag setter')
            cases['transfer-unit-flag'] += 1

    cpu.mem_write(scratch, record)
    for command in (0x2E0CF0, 0x29EDE0):
        require(call(command, 0, 0, 0, 0) == 0, f'Refuse missing object {command:x}')
        require(cpu.mem_read(scratch, len(record)) == record, 'Refused command changed fixture')
        cases['null-object-refusal'] += 1

    for event in (0x3C1A00, 0x3A9300):
        payload = scratch + 0x100
        cpu.mem_write(scratch, struct.pack('<Q', payload))
        cpu.mem_write(payload + 0xC, struct.pack('<II', 192, 234))
        calls = []

        def unresolved(uc, address, _size, _):
            calls.append(uc.reg_read(UC_X86_REG_RCX))
            if len(calls) == 2:
                uc.emu_stop()
                return
            rsp = uc.reg_read(UC_X86_REG_RSP)
            uc.reg_write(UC_X86_REG_RAX, 0)
            uc.reg_write(UC_X86_REG_RIP, struct.unpack('<Q', uc.mem_read(rsp, 8))[0])
            uc.reg_write(UC_X86_REG_RSP, rsp + 8)

        resolvers = {edge['rva'] for edge in image.describe(target(event))['direct_edges'][:2]}
        hooks = [cpu.hook_add(UC_HOOK_CODE, unresolved, begin=base + rva, end=base + rva) for rva in resolvers]
        try:
            call(event, 0, scratch, expect_return=False)
            require(calls == [192, 234], 'Scenario event parameter offsets')
            cases['event-argument-boundary'] += 1
        finally:
            for hook in hooks:
                cpu.hook_del(hook)
    return dict(cases)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for version in ('legacy', 'november'):
        parser.add_argument(f'--{version}-image', type=Path, required=True)
        parser.add_argument(f'--{version}-base', type=lambda v: int(v, 0), required=True)
    args = parser.parse_args()
    result = {}
    for version, expected in [('legacy', LEGACY_IMAGE), ('november', NOVEMBER_IMAGE)]:
        image = MemoryImage.load(getattr(args, f'{version}_image'), getattr(args, f'{version}_base'))
        require(digest(image.data) == expected, 'Unregistered PE capture')
        result[version] = replay(image, november=version == 'november')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
