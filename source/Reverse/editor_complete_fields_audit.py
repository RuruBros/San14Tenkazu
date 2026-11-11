"""Offline native evidence for every ordinary editor field and November-only adapters.

Record/array serializers execute original instructions at archive IO boundaries.
The play unlock setter's profile-eligibility query is an explicit boundary: these
checks certify bit effects, not live-game eligibility, saving, or gameplay.
"""
import argparse
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX,
                               UC_X86_REG_R8, UC_X86_REG_RIP, UC_X86_REG_RSP)
from editor_linked_fields_audit import RECORDS, corresponding_call, replay
from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, require
from version_audit import MemoryImage, digest


EXTRA_RECORDS = {'Unit': (0x235B80, 0x237520), 'Item': (0x238680, 0x23A030),
                 'Personality': (0x23AB50, 0x23C500), 'Corps': (0x237390, 0x238D40),
                 'City': (0x236D70, 0x238710), 'County': (0x2356F0, 0x237090),
                 'Building': (0x238F30, 0x23A8E0), 'Gate': (0x238310, 0x239CC0)}
SPECIALS = {'play-constructor': (0x3EAF90, 0x3ED4B0), 'play-getter': (0x3E9FF0, 0x3EC510),
            'play-unlock': (0x3EF450, 0x3F1970), 'ancient-bit': (0x3EB430, 0x3ED950),
            'owned-item-add': (0x323ED0, 0x325F80)}


def machine(image):
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    cpu.mem_map(image.base, image.size)
    cpu.mem_write(image.base, image.data)
    scratch = 0x200000000
    cpu.mem_map(scratch, 0x10000)
    return cpu, scratch


def call(cpu, image, entry, scratch, *args):
    stack, stop = scratch + 0xEEF8, scratch + 0xF000
    cpu.mem_write(stack, struct.pack('<Q', stop))
    cpu.reg_write(UC_X86_REG_RSP, stack)
    for register, value in zip((UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8),
                               [*args, *([0] * (3 - len(args)))], strict=True):
        cpu.reg_write(register, value & 0xFFFFFFFFFFFFFFFF)
    cpu.emu_start(image.base + entry, stop, count=10000)
    require(cpu.reg_read(UC_X86_REG_RIP) == stop and cpu.reg_read(UC_X86_REG_RSP) == stack + 8,
            'Original native return/stack convention')
    return cpu.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF


def caps_replay(old, new):
    result = []
    for image, entry, caps_rva in ((old, 0x2311F0, 0x1A4836C), (new, 0x232C50, 0x1A4B3B8)):
        # Authenticate all twelve jump-table destinations, including each native return.
        table = [struct.unpack_from('<I', image.data, entry + 60 + index * 4)[0] - entry for index in range(12)]
        require(table == [32, 32, 32, 39, 39, 46, 46, 53, 53, 32, 32, 32], 'Rank jump table')
        rows = list(image.md.disasm(image.data[entry:entry + 60], entry))
        require(rows[0].op_str == 'eax, byte ptr [rcx + 0x47]', 'Rank byte consumer')
        for instruction, delta in zip([i for i in rows if i.mnemonic == 'mov' and 'eax, dword ptr [rip' in i.op_str],
                                      (32, 20, 8, 0), strict=True):
            require(instruction.address + instruction.size + instruction.operands[1].mem.disp == caps_rva + delta,
                    'Exact native cap data references')
        cpu, scratch = machine(image)
        count = 0
        for values in ((50000, 100000, 150000, 200000), (11, 23, 37, 59), (0, 0, 0, 0)):
            for offset, value in zip((0, 8, 20, 32), values, strict=True):
                cpu.mem_write(image.base + caps_rva + offset, struct.pack('<i', value))
            for rank in range(256):
                cpu.mem_write(scratch + 0x47, bytes([rank]))
                actual = call(cpu, image, entry, scratch, scratch)
                expected = values[3 if rank in (1, 2, 3, 10, 11, 12) else 2 if rank in (4, 5) else 1 if rank in (6, 7) else 0]
                require(actual == expected, 'Native cap dispatch')
                count += 1
        result.append({'entry': hex(entry), 'capsRva': hex(caps_rva), 'cases': count})
    return result


def play_replay(old, new):
    results = []
    for index, image in enumerate((old, new)):
        cpu, scratch = machine(image)
        setter = SPECIALS['play-unlock'][index]
        bit_function = SPECIALS['ancient-bit'][index]
        gate = next(edge['rva'] for edge in image.describe(setter)['direct_edges'])
        allowed = True

        def gate_boundary(uc, _address, _size, _user):
            rsp = uc.reg_read(UC_X86_REG_RSP)
            uc.reg_write(UC_X86_REG_RAX, int(allowed))
            uc.reg_write(UC_X86_REG_RSP, rsp + 8)
            uc.reg_write(UC_X86_REG_RIP, struct.unpack('<Q', uc.mem_read(rsp, 8))[0])

        cpu.hook_add(UC_HOOK_CODE, gate_boundary, begin=image.base + gate, end=image.base + gate)
        bits = {str(officer): call(cpu, image, bit_function, scratch, officer) for officer in range(1900, 2201)}
        original = bytes((offset * 37 + 11) & 255 for offset in range(0x8E0))
        cases = 0
        for allowed in (False, True):
            for bit in range(65):
                for enabled in (0, 1):
                    cpu.mem_write(scratch, original)
                    call(cpu, image, setter, scratch, scratch, bit, enabled)
                    expected = bytearray(original)
                    if allowed and bit <= 60:
                        expected[0x274] = 1
                        at, mask = 0x19C + (bit // 32) * 4, 1 << (bit % 32)
                        before = struct.unpack_from('<I', expected, at)[0]
                        struct.pack_into('<I', expected, at, (before & ~mask) | (mask if enabled else 0))
                    require(cpu.mem_read(scratch, len(original)) == expected, 'Unlock byte effects and refusal')
                    cases += 1
        record_rva = (0x2784AB0, 0x2787B00)[index]
        require(image.data[record_rva:record_rva + 8] == struct.pack('<Q', 0x000000075331344F), 'Play record identity')
        getter_refs = image.describe(SPECIALS['play-getter'][index])['references']
        require(sum(reference['rva'] == record_rva for reference in getter_refs) == 2, 'Exact singleton record references')
        constructor = image.describe(SPECIALS['play-constructor'][index])
        require(constructor['instruction_count'] > 20, 'Full original initializer')
        results.append({'recordRva': hex(record_rva), 'unlockCases': cases, 'officerBits': bits})
    require(results[0]['officerBits'] == results[1]['officerBits'], 'Ancient officer mapping changed')
    return results


def audit(old, new):
    RECORDS.update(EXTRA_RECORDS)
    writer = corresponding_call(old, new, 'officer', 0x3FCC80)
    arrays = {'force': [corresponding_call(old, new, 'force', rva) for rva in (0x2206D0, 0x2211E0)],
              'Unit': [corresponding_call(old, new, 'Unit', rva) for rva in (0x220450, 0x220550, 0x220850, 0x2209D0)],
              'officer': [corresponding_call(old, new, 'officer', rva) for rva in
                          (0x1D49A0, 0x204270, 0x204370, 0x204470, 0x204570, 0x204670)],
              'City': [RECORDS['base']], 'Gate': [RECORDS['base']]}
    functions = {**RECORDS, **SPECIALS}
    for kind, pairs in arrays.items():
        functions.update({f'{kind}-array-{index}': pair for index, pair in enumerate(pairs)})
    for name, (a, b) in functions.items():
        require(old.describe(a)['shape_sha256'] == new.describe(b)['shape_sha256'], f'Original code shape/widths: {name}')
    records = {}
    for name, entries in RECORDS.items():
        versions = []
        for index, image in enumerate((old, new)):
            fields, boundaries = replay(image, entries[index], writer[index],
                                        [pair[index] for pair in arrays.get(name, [])], index == 1)
            versions.append({'fields': {hex(k): v for k, v in sorted(fields.items())},
                             'boundaries': [hex(rva) for rva in boundaries]})
        require(versions[0]['fields'] == versions[1]['fields'], f'{name} serialized offsets/sizes')
        records[name] = versions
    guards = []
    for name, pair in functions.items():
        entry = pair[1]
        length = new.describe(entry)['code_end'] - entry
        guards.append({'name': name, 'rva': hex(entry), 'length': length,
                       'sha256': digest(new.data[entry:entry + length]).upper()})
    guards.append({'name': 'military-cap-with-jump-table', 'rva': '0x232c50', 'length': 108,
                   'sha256': digest(new.data[0x232C50:0x232CBC]).upper()})
    unique = {}
    for guard in guards:
        prior = unique.setdefault(guard['rva'], guard)
        require((prior['length'], prior['sha256']) == (guard['length'], guard['sha256']), 'Conflicting code witness')
    return {'gameAccess': False, 'archiveIoBoundary': True, 'playEligibilityBoundary': True,
            'records': records, 'military': caps_replay(old, new), 'play': play_replay(old, new), 'guards': list(unique.values())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for version in ('legacy', 'november'):
        parser.add_argument(f'--{version}-image', type=Path, required=True)
        parser.add_argument(f'--{version}-base', type=lambda value: int(value, 0), required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--write-fixture', action='store_true')
    args = parser.parse_args()
    old, new = (MemoryImage.load(getattr(args, f'{name}_image'), getattr(args, f'{name}_base')) for name in ('legacy', 'november'))
    require(digest(old.data) == LEGACY_IMAGE and digest(new.data) == NOVEMBER_IMAGE, 'Unregistered capture')
    result = audit(old, new)
    fixture = Path(__file__).resolve().parents[1] / 'Tests' / 'Vectors' / 'GameEditorCoverage20261111.json'
    if args.write_fixture:
        fixture.write_text(json.dumps(result, indent=2) + '\n')
    else:
        require(json.loads(fixture.read_text()) == result, 'Complete editor field regression vector differs')
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print('PASS complete editor native serializers, caps and unlock effects for both captures; gameAccess=false')


if __name__ == '__main__':
    main()
