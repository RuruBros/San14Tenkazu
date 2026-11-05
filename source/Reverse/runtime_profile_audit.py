"""Reproduce the November runtime rule profile from two existing virtual PE captures."""
import argparse
import json
from pathlib import Path
import struct

from version_audit import MemoryImage, digest


ROOT = Path(__file__).resolve().parents[1] / 'Core' / 'RuntimeRules'
LEGACY_IMAGE = 'aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
NOVEMBER_IMAGE = 'ccebbe29092437deaecbfe6ae2fc3a22b8ccb3c597604051f35070a3f6b3039a'
NOVEMBER_EXE = '782CF229A56CDDE2B95399E0E7536740B3F5E96BEA702679365E4DAD061ABDCE'

# Reviewed old-to-new instruction sites. The only data edit is the four native cap constants.
SITES = {
    0x20a425: 0x20b7c5, 0x20abb2: 0x20bfda, 0x5dd4b8: 0x5dfd18, 0x142cbe0: 0x142fc70,
    0x5dd323: 0x5dfb83, 0x31fece: 0x321f3e, 0x2b9dce: 0x2bc14e, 0x4a6cf3: 0x4a90b3,
    0x4a7313: 0x4a96d3, 0x22558f: 0x226fbf, 0x23064a: 0x2320aa, 0x232d97: 0x234737,
    0x2b9f98: 0x2bc318, 0x2ba019: 0x2bc399, 0x2b9fe6: 0x2bc366, 0x2b9fd8: 0x2bc358,
    0x2ba03a: 0x2bc3ba, 0x133fa80: 0x1342180, 0x230126: 0x231b86, 0x23016a: 0x231bcd,
    0x2b9dca: 0x2bc14a, 0x22591c: 0x22734c,
}
GROWTH = {0x3359e0: 0x337c40, 0x242890: 0x2442f0}
TRAIT_GUARDS = {
    0x2bf02e: 0x2c13ae, 0x2bf120: 0x2c14a0, 0x2260: 0x2260,
    0x35eec0: 0x3613e0, 0x353810: 0x355bf0, 0x35f020: 0x361540,
}
TABLE_SHIFT = 0x2fe0
MUTABLE_ROWS = (107, 126, 127, 128, 129)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def rel32(start, target, opcode):
    return bytes([opcode]) + struct.pack('<i', target - start - 5)


def relative_target(image, site, opcode):
    require(image[site] == opcode, f'Opcode changed at {site:x}')
    return site + 5 + struct.unpack_from('<i', image, site + 1)[0]


def bounds(image, site, length):
    # x64 unwind fragments can start or end in an edited instruction span. Cover the
    # contiguous function fragments as well as the actual changed instructions.
    ranges = [(b, e) for b, e in image.functions if b < site + length and e > site]
    require(bool(ranges), f'No unwind entry for {site:x}')
    start, end = ranges[0][0], ranges[-1][1]
    if 0x2bc0d0 <= site < 0x2bc410:
        start, end = 0x2bc0d0, 0x2bc410
    return start, end


def patches(old, new, legacy):
    text_end = next(rva + size for name, rva, size, _ in new.sections if name == '.text')
    limit = (text_end + 4095) & ~4095
    result = []
    for source in legacy:
        before = bytes.fromhex(source['Before'])
        old_site = source['Rva']
        site = SITES[old_site]
        guard = site + source['GuardRva'] - old_site
        length = len(before)
        actual = new.data[site:site + length]
        require(old.data[old_site:old_site + length] == before, f'Legacy instruction at {old_site:x}')
        if old_site in (0x2b9fd8, 0x2ba03a):
            require(relative_target(old.data, old_site, 0xe8) == 0x242890 and
                    relative_target(new.data, site, 0xe8) == GROWTH[0x242890], f'Growth call at {site:x}')
        else:
            require(actual == before, f'Original patch bytes at {site:x}')
        if old_site == 0x133fa80:
            require(text_end <= site and site + length <= limit and not any(actual) and
                    not any(site <= r < site + length for r in new.relocations) and
                    not any(b < site + length and e > site for b, e in new.functions), 'Growth helper storage')
            start, end = site, site + length
        elif new.section(site) == '.rdata':
            require(old_site == 0x142cbe0 and new.section(site + length - 1) == '.rdata', 'Cap constants')
            start, end = site, site + length
        else:
            require(new.is_code(site) and new.is_code(site + length - 1), f'Code section at {site:x}')
            start, end = bounds(new, site, length)
        entry = dict(Rva=site, Before=actual.hex(), GuardRva=guard,
                     Guard=new.data[guard:guard + len(source['Guard']) // 2].hex())
        for mode in ('Traits', 'Cap', 'Growth'):
            variant = bytes.fromhex(source[mode])
            if variant == before:
                variant = actual
            elif mode == 'Growth' and old_site in (0x2b9fd8, 0x2ba03a):
                require(relative_target(variant, 0, 0xe8) + old_site == 0x133fa80, 'Legacy helper call')
                variant = rel32(site, SITES[0x133fa80], 0xe8)
            elif mode == 'Growth' and old_site == 0x133fa80:
                helper = bytearray(variant)
                for offset, opcode in [(0x1b, 0xe8), (0x29, 0xe8), (0x51, 0xe9)]:
                    target = GROWTH[relative_target(variant, offset, opcode) + old_site]
                    require(new.is_code(target), f'Growth helper callee {target:x}')
                    helper[offset:offset + 5] = rel32(site + offset, target, opcode)
                variant = bytes(helper)
            require(len(variant) == length, f'Patch size at {site:x}')
            entry[mode] = variant.hex()
        result.append(dict(entry, CodeStart=start, CodeEnd=end))
    return result, limit


def traits(old, new, legacy):
    columns = 0x1a2e830
    crt = {columns + c * 96 + offset for c in (22, 69, 99, 100) for offset in (0x18, 0x28)}
    crt.add(columns + 212 * 96 + 8)
    require(not (crt & new.relocations), 'CRT and PE relocations overlap')
    relocations = new.relocations | crt

    def block(rva, size):
        raw = new.data[rva:rva + size]
        fixups = sorted(p - rva for p in relocations if rva <= p < rva + size)
        actual = [i for i in range(len(raw) - 7) if new.base <= struct.unpack_from('<Q', raw, i)[0] < new.base + new.size]
        require(actual == fixups, f'Absolute pointer coverage at {rva:x}')
        return dict(Rva=rva, Bytes=raw.hex().upper(), Fixups=fixups)

    previous = legacy['Columns']
    table = block(columns, len(previous['Bytes']) // 2)
    require(table['Fixups'] == previous['Fixups'], 'Column relocation layout changed')
    a, b = bytearray.fromhex(previous['Bytes']), bytearray.fromhex(table['Bytes'])
    for at in table['Fixups']:
        a[at:at + 8] = b[at:at + 8] = bytes(8)
    for row in MUTABLE_ROWS:
        at = row * 96 + 0x5c
        a[at:at + 4] = b[at:at + 4] = bytes(4)
    require(a == b, 'Immutable column definitions changed')
    require(struct.unpack_from('<Q', new.data, columns + 65 * 96 + 0x18)[0] - new.base == 0x3613e0,
            'Normal trait getter identity')

    def ids(image, address):
        rva = address - image.base
        require(0 <= rva <= image.size - 4, 'Column vector address')
        values = []
        for n in range(60):
            value, = struct.unpack_from('<i', image.data, rva + n * 4)
            values.append(value)
            if value == -1:
                return values
            require(0 <= value < 213, 'Column vector value')
        raise ValueError('Unterminated column vector')

    tables, vectors = [], {}
    for entry in legacy['Tables']:
        old_rva, rva = entry['Rva'], entry['Rva'] + TABLE_SHIFT
        size = len(entry['Bytes']) // 2
        current = block(rva, size)
        require(current['Fixups'] == entry['Fixups'], f'Tab layout at {rva:x}')
        for offset in current['Fixups']:
            a, = struct.unpack_from('<Q', old.data, old_rva + offset)
            b, = struct.unpack_from('<Q', new.data, rva + offset)
            require(ids(old, a) == ids(new, b), f'Tab columns at {rva + offset:x}')
            if offset == entry['TraitIndex'] * 24:
                require(ids(new, b) == [0, *range(entry['TraitBase'], entry['TraitBase'] + 5), -1],
                        f'Trait tab at {rva:x}')
                vectors.setdefault(a - old.base, set()).add(b - new.base)
        tables.append(dict(current, TraitIndex=entry['TraitIndex'], TraitBase=entry['TraitBase']))
    # The constructor is one of several functions with the same instruction shape.
    # Its callback type is specific to this instantiation.
    rtti = b'.?AV?$_Func_impl@V<lambda_427f10b2cdc607723a256779d46f4702>'
    require(old.data.count(rtti) == 1 and new.data.count(rtti) == 1, 'Constructor callback RTTI')
    for image, constructor, vtable in [(old, 0x2befe0, 0x13ae010), (new, 0x2c1360, 0x13b1080)]:
        locator = struct.unpack_from('<Q', image.data, vtable - 8)[0] - image.base
        signature, _, _, type_rva, _, self_rva = struct.unpack_from('<6I', image.data, locator)
        require(signature == 1 and locator == self_rva and image.data.startswith(rtti, type_rva + 16),
                'Constructor callback type descriptor')
        require([r['instruction'] for r in image.describe(constructor)['references'] if r['rva'] == vtable] == [205, 215],
                'Constructor does not reference the verified callback vtable')
    require(new.data[0x2c13b3:0x2c13c2].hex() == '488b4577488986500100004c8b6d7f',
            'Trait list overwrites different instructions')
    guards = []
    for entry in legacy['Guards']:
        rva = entry['Rva']
        targets = [TRAIT_GUARDS[rva]] if rva in TRAIT_GUARDS else sorted(vectors[rva])
        for target in targets:
            guards.append(block(target, len(entry['Bytes']) // 2))
    # The trait getter's slot-count instruction has its own mode-specific patch guard.
    for start, length in [(0x232030, 0x7a), (0x2320ad, 0x1a), (0x331780, 0x24)]:
        guards.append(block(start, length))
    return dict(Site=0x2c13b3, CodeStart=0x2c1360, CodeEnd=0x2c1bf6, World=0x2780f60,
                AlternateValue=0x361540, Value=0x232030, Name=0x331780, EmptyName=0x1a4ab88, Sort=0x355bf0,
                Layout=dict(OriginalBase=new.base, ImageSize=new.size, Columns=table, Tables=tables, Guards=guards))


def replay_growth(image, sites, helper, manager_getter):
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
    from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDI, UC_X86_REG_RDX, UC_X86_REG_RSI, UC_X86_REG_RSP

    # Execute the helper and its actual native callees against a reproducible level table.
    base, scratch = 0x140000000, 0x200000000
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    cpu.mem_map(base, image.size)
    cpu.mem_write(base, image.data)
    cpu.mem_map(scratch, 0x5000)
    for patch in sites:
        cpu.mem_write(base + patch['Rva'], bytes.fromhex(patch['Growth']))
    getter = next(image.md.disasm(image.data[manager_getter:manager_getter + 8], manager_getter))
    require(getter.mnemonic == 'mov' and getter.op_str.startswith('rax, qword ptr [rip '), 'Ability manager ABI')
    manager_slot = getter.address + getter.size + getter.disp
    manager, level100, original, officer, stop = [scratch + i for i in (0x100, 0x500, 0x600, 0x800, 0x4000)]
    cpu.mem_write(base + manager_slot, struct.pack('<Q', manager))
    cpu.mem_write(manager + 0x380, struct.pack('<Q', level100))
    thresholds = [1_650_000] * 4 + [660_000]
    cpu.mem_write(level100 + 0x10, struct.pack('<5I', *thresholds))
    cpu.mem_write(original + 0x10, struct.pack('<5I', 1, 2, 3, 4, 5))
    tested = 0
    for level in (0, 98, 99, 100, 101, 149, 199, 200, 201, 255):
        for ability in range(5):
            stack = scratch + 0x3ef8
            cpu.mem_write(stack, struct.pack('<Q', stop))
            cpu.mem_write(officer + 0xec + ability, bytes([level]))
            for register, value in [(UC_X86_REG_RSP, stack), (UC_X86_REG_RSI, officer), (UC_X86_REG_RDI, ability),
                                    (UC_X86_REG_RCX, original), (UC_X86_REG_RDX, ability)]:
                cpu.reg_write(register, value)
            cpu.emu_start(base + helper, stop, count=100)
            actual = cpu.reg_read(UC_X86_REG_RAX) & 0xffffffff
            expected = ability + 1 if level < 100 else (
                thresholds[ability] * (level + 1) ** 2 // 10000 if level < 200 else 0x7fffffff)
            require(actual == expected and cpu.reg_read(UC_X86_REG_RSP) == stack + 8,
                    f'Growth replay at level {level}, ability {ability}: {actual} != {expected}')
            tested += 1
    return tested


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for version in ('legacy', 'november'):
        parser.add_argument(f'--{version}-image', type=Path, required=True)
        parser.add_argument(f'--{version}-base', type=lambda s: int(s, 0), required=True)
    parser.add_argument('--write', action='store_true', help='Write the reviewed profile for the two pinned captures')
    args = parser.parse_args()
    old = MemoryImage.load(args.legacy_image, args.legacy_base)
    new = MemoryImage.load(args.november_image, args.november_base)
    require(digest(old.data) == LEGACY_IMAGE and digest(new.data) == NOVEMBER_IMAGE, 'Unregistered PE capture')
    legacy = json.loads((ROOT / 'patches.json').read_text())
    sites, limit = patches(old, new, legacy)
    traits_profile = traits(old, new, json.loads((ROOT / 'trait-list.json').read_text()))
    profile = dict(ExeHash=NOVEMBER_EXE, ExecutableLimit=limit, Patches=sites, Traits=traits_profile)
    tested = replay_growth(old, legacy, 0x133fa80, 0x3359e0) + replay_growth(new, sites, 0x1342180, 0x337c40)
    file = ROOT / 'November2026.json'
    if args.write:
        file.write_bytes((json.dumps(profile, indent=2) + '\n').encode())
    else:
        require(profile == json.loads(file.read_text()), 'Runtime rule profile differs from captured evidence')
    print(f'{len(sites)} patch sites, {len(traits_profile["Layout"]["Tables"])} trait tables, '
          f'{len(traits_profile["Layout"]["Guards"])} trait guards verified')
    print(f'{tested} native growth-helper executions verified across both builds')


if __name__ == '__main__':
    main()
