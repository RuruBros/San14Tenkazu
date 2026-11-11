"""Offline Native consumers, using recorded edit images and declared lookup fixtures.

Never attaches to a process. Policy collection observes the visitor boundary; it
certifies membership, not policy levels/effect aggregation. Tactic validity and
personality effect selection execute original instructions without semantic stubs.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_READ
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX,
                               UC_X86_REG_R8, UC_X86_REG_RSP, UC_X86_REG_RIP)
from editor_roundtrip_log import verify_rows
from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, require
from version_audit import MemoryImage

FUNCTIONS = {'policy-members': (0x26AA20, 0x26C340), 'policy-visitor': (0x258D90, 0x25A730),
             'officer-tactic': (0x22A320, 0x22BD60), 'personality-effect': (0x22C630, 0x22E070)}
ROOTS = (0x277DF18, 0x2780F60)


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def sha(data):
    return hashlib.sha256(data).hexdigest()


def vtable(image, name):
    label = ('.?AV' + name + '@@\0').encode()
    require(image.data.count(label) == 1, 'Unique RTTI name: ' + name)
    descriptor = image.data.index(label) - 16
    locators = []
    for section, start, size, _ in image.sections:
        if section != '.rdata':
            continue
        for at in range(start, start + size - 23, 4):
            if image.data[at:at + 8] != b'\x01\0\0\0\0\0\0\0':
                continue
            words = struct.unpack_from('<6I', image.data, at)
            if words[3] == descriptor and words[5] == at:
                locators.append(at)
    require(len(locators) == 1, 'Unique complete-object locator: ' + name)
    slots = [at + 8 for at in image.relocation_sites
             if struct.unpack_from('<Q', image.data, at)[0] == image.base + locators[0]]
    require(len(slots) == 1, 'Unique vtable: ' + name)
    return slots[0]


def u16(data, at):
    return struct.unpack_from('<H', data, at)[0]


def policy_members(data, enacted):
    if u16(data, 0x10) == 0:
        return []
    valid = lambda number: 0 < number <= 6000
    ids = [u16(data, 0x10)]
    if not enacted or u16(data, 0x16C) == 0:
        ids.append(u16(data, 0x52))
    for start in (0x54, 0x72, 0x90, 0xAE, 0xCC):
        for slot in range(7):
            if not enacted or u16(data, start + 14 + slot * 2) == 0:
                ids.append(u16(data, start + slot * 2))
    return [number for number in ids if valid(number)]


def effect_value(data, kind, none):
    for at in (0xB6, 0xBA):
        if data[at] == kind:
            return max(-100, min(2000, struct.unpack_from('<h', data, at + 2)[0]))
    return none


class Native:
    def __init__(self, image, november):
        self.image, self.index = image, int(november)
        self.cpu = Uc(UC_ARCH_X86, UC_MODE_64)
        self.cpu.mem_map(image.base, image.size)
        self.cpu.mem_write(image.base, image.data)
        self.root = 0x300000000
        self.cpu.mem_map(self.root, 0x1000000)
        self.officers, self.forces = self.root + 0x100000, self.root + 0x500000
        self.tactics, self.personality = self.root + 0x600000, self.root + 0x620000
        self.output = self.root + 0x630000
        self.stack, self.stop = self.root + 0xF00008, self.root + 0xFFF000
        self.vtables = {name: vtable(image, name) for name in
                       ('CPersonData', 'CForceData', 'CTacticsData', 'CPersonalityData')}
        self.put(image.base + ROOTS[self.index], struct.pack('<Q', self.root))
        # Controlled dependencies: valid ordinary officers by ID, no merit/trait
        # simulation, and no tactic reference entries. They are not a live world.
        people = bytearray(6001 * 0x1F0)
        for number in range(6001):
            at = number * 0x1F0
            struct.pack_into('<Q', people, at, image.base + self.vtables['CPersonData'])
            struct.pack_into('<H', people, at + 0x10, number)
            people[at + 0xE6] = 3
        self.put(self.officers, people)
        self.put(self.root + 0x158, b''.join(struct.pack('<Q', self.officers + n * 0x1F0) for n in range(6001)))
        self.put(self.root + 0x73A50, struct.pack('<Q', self.officers))
        self.put(self.root + 0xDF30, struct.pack('<Q', self.forces))
        for n in range(201):
            self.put(self.root + 0x774D0 + n * 8, struct.pack('<Q', self.tactics + n * 0x90))
            self.put(self.tactics + n * 0x90, struct.pack('<Q', image.base + self.vtables['CTacticsData']))
        self.reads, self.visited, self.executed = set(), [], set()
        self.cpu.hook_add(UC_HOOK_MEM_READ, self.on_read)
        self.cpu.hook_add(UC_HOOK_CODE, self.on_code)

    def put(self, address, data):
        self.cpu.mem_write(address, bytes(data))

    def on_read(self, cpu, access, address, size, value, user):
        self.reads.add((address, size))

    def on_code(self, cpu, address, size, user):
        if address == self.image.base + FUNCTIONS['policy-visitor'][self.index]:
            person = cpu.reg_read(UC_X86_REG_RCX)
            self.visited.append(u16(bytes(cpu.mem_read(person, 0x12)), 0x10))
            sp = cpu.reg_read(UC_X86_REG_RSP)
            cpu.reg_write(UC_X86_REG_RIP, struct.unpack('<Q', cpu.mem_read(sp, 8))[0])
            cpu.reg_write(UC_X86_REG_RSP, sp + 8)
            return
        self.executed.add(address - self.image.base)

    def call(self, name, *args):
        self.reads.clear()
        self.visited.clear()
        self.put(self.stack, struct.pack('<Q', self.stop))
        self.cpu.reg_write(UC_X86_REG_RSP, self.stack)
        for reg, value in zip((UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8), args):
            self.cpu.reg_write(reg, value & 0xFFFFFFFFFFFFFFFF)
        self.cpu.emu_start(self.image.base + FUNCTIONS[name][self.index], self.stop, count=100000)
        require(self.cpu.reg_read(UC_X86_REG_RIP) == self.stop, 'Native return/instruction limit')
        require(self.cpu.reg_read(UC_X86_REG_RSP) == self.stack + 8, 'Native stack convention')
        result = self.cpu.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF
        return result - (1 << 32) if result >= (1 << 31) else result

    def record(self, data, name):
        result = bytearray(data)
        struct.pack_into('<Q', result, 0, self.image.base + self.vtables[name])
        return bytes(result)

    def policy(self, data, enacted=True):
        # Product logs cover ForceRead (0x120), not the full 0x268 record.
        # The unrecorded tail, including strategist waiting days, is an explicit
        # zero-valued fixture. No claim about that live tail is made.
        data = data.ljust(0x268, b'\0')
        at = self.forces + 0x268
        self.put(at, self.record(data, 'CForceData'))
        self.put(self.output, bytes(16))
        self.call('policy-members', at, self.output, int(enacted))
        require(self.visited == policy_members(data, enacted),
                f'Policy membership/order differs: enacted={enacted}; actual={self.visited}; expected={policy_members(data, enacted)}')
        return at, list(self.visited)

    def tactic(self, data, slot, flags):
        at = self.officers + 0x1F0
        self.put(at, self.record(data, 'CPersonData'))
        for number in range(1, 201):
            self.put(self.tactics + number * 0x90 + 0x44, bytes([flags]))
        number, person_id = data[0x130 + slot], u16(data, 0x10)
        special = 5101 <= person_id <= 5200 or 4101 <= person_id <= 4200
        expected = number if special or not 1 <= number <= 200 or flags & 2 else 0
        got = self.call('officer-tactic', at, slot)
        require(got == expected, f'Officer tactic filter differs: {got}/{expected}')
        require((at + 0x130 + slot, 1) in self.reads, 'Native did not consume the byte slot')
        return got

    def effect(self, data, kind):
        self.put(self.personality, self.record(data, 'CPersonalityData'))
        got = self.call('personality-effect', self.personality, kind, 123456)
        require(got == effect_value(data, kind, 123456), 'Personality effect selection/clamp differs')
        require((self.personality + 0xB6, 1) in self.reads, 'Native did not consume the first kind byte')
        return got


def recorded_images(rows, case):
    key = case['key']
    address = case['steps'][0][0]['address']
    selected = [row for row in rows if row.get('key') == key and 'records' in row]
    result = []
    for stage in ('prepared', 'changed-readback', 'restored-byte-exact'):
        row = next(row for row in selected if row['stage'] == stage)
        rec = next(rec for rec in row['records'] if rec['address'] == address)
        result.append((stage, bytes.fromhex(rec['before'] if stage == 'prepared' else rec['actual'])))
    require(result[0][1] == result[-1][1], 'Recorded original/restored images differ')
    return result


def read_witness(native, address, data):
    return dict(recordedImageSha256=sha(data), recordedBytes=len(data),
                recordReads=[dict(offset=at - address, size=size) for at, size in sorted(native.reads)
                             if address <= at and at + size <= address + len(data)])


def audit(old, new, rows):
    # Exact input image pins; shape matching only selects already reviewed entries.
    require(sha(old.data) == LEGACY_IMAGE, 'Legacy image pin')
    require(sha(new.data) == NOVEMBER_IMAGE, 'November image pin')
    guards = {}
    for name, (left, right) in FUNCTIONS.items():
        a, b = old.describe(left), new.describe(right)
        require(a['shape_sha256'] == b['shape_sha256'], 'Native consumer shape: ' + name)
        guards[name] = [{'rva': rva, 'length': desc['code_end'] - rva,
                         'sha256': sha(image.data[rva:desc['code_end']])}
                        for image, rva, desc in ((old, left, a), (new, right, b))]
    cases = rows[0]['trials']
    result = []
    for image, november in ((old, False), (new, True)):
        native = Native(image, november)
        actual, controlled = [], []
        for case in cases:
            key = case['key']
            if key.startswith('force-policy-') or key == 'force-strategist':
                for stage, data in recorded_images(rows, case):
                    at, got = native.policy(data)
                    for edit in case['steps'][0]:
                        for change in edit['changes']:
                            require((at + change['offset'], change['size']) in native.reads,
                                    'Native did not consume the edited policy field')
                    actual.append(dict(key=key, stage=stage, members=got, **read_witness(native, at, data)))
            elif key in {f'officer-{at:X}' for at in range(0x130, 0x136)}:
                slot = int(key.split('-')[1], 16) - 0x130
                for stage, data in recorded_images(rows, case):
                    for flags in (0, 2):
                        got = native.tactic(data, slot, flags)
                        actual.append(dict(key=key, stage=stage, flags=flags, result=got,
                                           **read_witness(native, native.officers + 0x1F0, data)))
            elif key in ('personality-B6', 'personality-BA', 'personality-B8', 'personality-BC'):
                for stage, data in recorded_images(rows, case):
                    for kind in sorted({data[0xB6], data[0xBA], 255}):
                        got = native.effect(data, kind)
                        actual.append(dict(key=key, stage=stage, kind=kind, result=got,
                                           **read_witness(native, native.personality, data)))
        # A successful result alone cannot certify an edited field that was never read.
        for case in cases:
            evidence = [row for row in actual if row['key'] == case['key']]
            if not evidence:
                continue
            reads = {(r['offset'], r['size']) for row in evidence for r in row['recordReads']}
            for edit in case['steps'][0]:
                for change in edit['changes']:
                    require((change['offset'], change['size']) in reads, 'Edited field has no Native read witness')
        for field in range(5):
            for slot in range(7):
                for days in (0, 1, 32767):
                    for enacted in (False, True):
                        data = bytearray(0x268)
                        struct.pack_into('<H', data, 0x10, 42)
                        start = (0x54, 0x72, 0x90, 0xAE, 0xCC)[field]
                        struct.pack_into('<H', data, start + slot * 2, 100 + field * 7 + slot)
                        struct.pack_into('<H', data, start + 14 + slot * 2, days)
                        _, got = native.policy(data, enacted)
                        controlled.append(dict(policyField=field, slot=slot, days=days, enacted=enacted, members=got))
        for person_id in (42, 4101, 5101):
            for slot in range(6):
                for number in (0, 1, 200, 201, 255):
                    for flags in (0, 2):
                        data = bytearray(0x1F0)
                        struct.pack_into('<H', data, 0x10, person_id)
                        data[0x130 + slot] = number
                        controlled.append(dict(personId=person_id, slot=slot, tactic=number, flags=flags,
                                               result=native.tactic(data, slot, flags)))
        for effect_slot in (0xB6, 0xBA):
            for value in (-32768, -101, -100, 0, 2000, 2001, 32767):
                data = bytearray(0xE8)
                data[effect_slot] = 1
                struct.pack_into('<h', data, effect_slot + 2, value)
                controlled.append(dict(effectSlot=effect_slot, value=value, result=native.effect(data, 1)))
                require((native.personality + effect_slot + 2, 2) in native.reads, 'Native effect value is not a word')
        result.append(dict(build='november' if november else 'legacy', vtables=native.vtables,
                           recordedImageQueries=actual, controlledQueries=controlled,
                           executedRvas=sorted(native.executed)))
    return dict(passed=True, gameAccess=False, screenAccess=False, liveWrites=0, codeGuards=guards, builds=result,
                scope='Original Native consumers on recorded before/changed/restored record images with controlled lookup dependencies',
                boundaries=['Policy visitor observed only: policy sums, merit bonuses and final effects are not executed',
                            'Unlogged force tail is a zero-valued fixture, including strategist waiting days; it is not a captured live tail',
                            'Officer lookup records are valid ordinary fixture officers, not the old live roster',
                            'Tactics use plain flags and no reference entries; encoded flags and positive reference matches are outside this audit',
                            'Personality effect kinds/values only; condition/filter evaluation and holder activation are outside this audit'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('legacy-image', 'legacy-base', 'november-image', 'november-base', 'log', 'layout-evidence', 'output'):
        parser.add_argument('--' + name, required=True)
    args = parser.parse_args()
    output = Path(args.output)
    require(not output.exists(), 'Output must be new')
    rows = [json.loads(line) for line in Path(args.log).read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    proof = verify_rows(rows, read_json(args.layout_evidence))
    require(proof['verifiedTrials'] == 93 and not proof['failures'] and not proof['snapshotDifferences'],
            'Actual game round-trip evidence must pass independently')
    old = MemoryImage.load(args.legacy_image, int(args.legacy_base, 0))
    new = MemoryImage.load(args.november_image, int(args.november_base, 0))
    result = audit(old, new, rows)
    result['logSha256'] = sha(Path(args.log).read_bytes())
    result['auditSourceSha256'] = sha(Path(__file__).read_bytes())
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    counts = [(b['build'], len(b['recordedImageQueries']), len(b['controlledQueries'])) for b in result['builds']]
    print('PASS offline Native consumer queries:', counts, 'gameAccess=false; policy visitor boundary explicit')


if __name__ == '__main__':
    main()
