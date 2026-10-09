"""Read-only tactic rule audit; unmodified Native code runs only in Unicorn.

Use the bundled Python with --synthetic or --live and --output <evidence.json>.
Live access requests QUERY_INFORMATION | VM_READ; neither mode writes a game or
profile. The synthetic audit covers every byte at representative tactic IDs;
the live audit covers both slots and every flag of all 201 current records.
"""
import argparse
import ctypes
import hashlib
import json
from pathlib import Path
import re
import struct
import sys
import time
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / '.venv/Lib/site-packages'))
from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8,
    UC_X86_REG_R9, UC_X86_REG_RSP, UC_X86_REG_RAX, UC_X86_REG_RIP)

ROOT_RVA, TABLE, STATE, SIZE, COUNT = 0x277DF18, 0x774D0, 0x871E8, 0x90, 201
VTABLE = 0x13A6DB8
# name, Native getter RVA, plain byte, encoded low byte, rotation, slot count
FIELDS = [('flags', 0x16FC30, 0x44, 0, 1, 1),
          ('kind', 0x16D510, 0x62, 4, 7, 2),
          ('range', 0x16D5A0, 0x64, 12, 1, 2),
          ('amount', 0x16D630, 0x66, 20, 3, 2),
          ('level', 0x16D6C0, 0x68, 28, 5, 2),
          ('ability', 0x16CDA0, 0x6A, 36, 7, 1)]
FUNCTIONS = {name: rva for name, rva, *_ in FIELDS} | {
    'index': 0x22E0A0, 'valid': 0x234500, 'valid_dispatch': 0x32F500,
    'encode_byte': 0xDB60, 'decode_byte': 0xDB00,
    'save_serializer': 0x23ADC0, 'game_data_serializer': 0x23FB90,
    'targets': 0x16BE90, 'dispatch': 0x178900, 'flow': 0x172E20}
KNOWN_EXE = '87ed4e46590e0939c9efcaef14fa0f892917604dfda86e0a4be319fccffa2d6a'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def encode(value, rotate, index):
    x = value ^ (index & 255)
    return ((x >> rotate) | (x << (8 - rotate))) & 255


def decode(value, rotate, index):
    return (((value << rotate) | (value >> (8 - rotate))) ^ index) & 255


class Native:
    def __init__(self, im):
        self.im, self.u = im, Uc(UC_ARCH_X86, UC_MODE_64)
        u = self.u
        u.mem_map(im.base, (len(im.img) + 4095) & ~4095)
        u.mem_write(im.base, im.img)  # Original instruction bytes and virtuals.
        self.root, self.array, self.vectors, self.stack, self.stop = (
            0x300000000, 0x301000000, 0x302000000, 0x303000000, 0x304000000)
        for at, length in ((self.root, 0x90000), (self.array, 0x8000),
                           (self.vectors, 0x4000), (self.stack, 0x10000), (self.stop, 0x1000)):
            u.mem_map(at, length)
        u.mem_write(im.base + ROOT_RVA, struct.pack('<Q', self.root))
        u.mem_write(self.root + TABLE, b''.join(struct.pack('<Q', self.array + i * SIZE) for i in range(COUNT)))
        assert im.u64(VTABLE + 0x40) == im.base + 0xDB60
        assert im.u64(VTABLE + 0x48) == im.base + 0xDB00

    def put(self, index, record, vector):
        # Only copied data addresses are relocated. No Native instructions are patched.
        data = bytearray(record)
        at = self.vectors + index * 64
        struct.pack_into('<Q', data, 0, self.im.base + VTABLE)
        struct.pack_into('<QQQ', data, 0x70, at, at + len(vector), at + 40)
        self.u.mem_write(self.array + index * SIZE, bytes(data))
        if vector:
            self.u.mem_write(at, bytes(vector))

    def call(self, rva, index, arg=0, rotate=0, key=0):
        u, sp = self.u, self.stack + 0xF008
        u.mem_write(sp, struct.pack('<Q', self.stop))
        for reg, value in ((UC_X86_REG_RSP, sp), (UC_X86_REG_RCX, self.array + index * SIZE),
                           (UC_X86_REG_RDX, arg), (UC_X86_REG_R8, rotate), (UC_X86_REG_R9, key)):
            u.reg_write(reg, value)
        u.emu_start(self.im.base + rva, self.stop, count=10000)
        if u.reg_read(UC_X86_REG_RIP) != self.stop:
            raise RuntimeError(f'Native {rva:#x} did not return')
        return u.reg_read(UC_X86_REG_RAX) & 255

    def value(self, field, index, slot):
        if field[0] == 'flags':
            return sum(self.call(field[1], index, bit) << bit for bit in range(8))
        return self.call(field[1], index, slot)


def synthetic(im):
    native = Native(im)
    ids = [0, 1, 26, 31, 130, 131, 139, 140, 170, 200]
    calls, mismatches, results = 0, [], hashlib.sha256()
    for index in ids:
        for mode in ('plain', 'encoded'):
            for value in range(256):
                record = bytearray(SIZE)
                vector = bytearray(bytes.fromhex('00a5c37e') * 10)
                for field in FIELDS:
                    name, rva, plain, offset, rotate, slots = field
                    for slot in range(slots):
                        # Different values in the two slots expose incorrect indexing.
                        expected = (value + slot * 73) & 255
                        record[plain + slot] = expected
                        vector[offset + slot * 4] = encode(expected, rotate, index)
                native.put(index, record, vector if mode == 'encoded' else b'')
                for field in FIELDS:
                    name, rva, plain, offset, rotate, slots = field
                    for slot in range(slots):
                        expected = record[plain + slot]
                        got = native.value(field, index, slot)
                        calls += 8 if name == 'flags' else 1
                        results.update(struct.pack('<5H', index, mode == 'encoded', plain + slot, expected, got))
                        if got != expected:
                            mismatches.append([index, mode, name, slot, expected, got])
                        if mode == 'encoded':
                            got_encode = native.call(0xDB60, index, expected, rotate, index)
                            calls += 1
                            if got_encode != vector[offset + slot * 4]:
                                mismatches.append([index, mode, name, slot, 'encoder', got_encode])
                # Unknown flag bits are not read past the byte by the Native getter.
                for bit in (-1, 8, 32):
                    if native.call(0x16FC30, index, bit) != 0:
                        mismatches.append([index, mode, 'flag-bit', bit])
                    calls += 1
    return {'ids': ids, 'valuesPerField': 256, 'paths': ['plain', 'encoded'],
            'nativeCalls': calls, 'upperDwordBytes': 'a5c37e',
            'resultSha256': results.hexdigest(), 'mismatches': mismatches,
            'liveGameAccess': False, 'nativeCodePatched': False}


def required(game, address, length):
    data = game.read(address, length)
    if data is None:
        raise RuntimeError(f'Unreadable range {address:#x}+{length:#x}')
    return data


def live(im):
    from game import Game, k32
    game = Game()  # OpenProcess(0x0410): query + read only.
    profile_path = Path.home() / 'AppData/Local/San14Tenkazu/rule_profiles.json'
    profile_before = profile_path.read_bytes() if profile_path.exists() else None
    try:
        path = ctypes.create_unicode_buffer(32768)
        size = ctypes.c_ulong(len(path))
        if not k32.QueryFullProcessImageNameW(ctypes.c_void_p(game.h), 0, path, ctypes.byref(size)):
            raise RuntimeError('Cannot read game executable path')
        exe_hash = digest(Path(path.value).read_bytes())
        if exe_hash != KNOWN_EXE:
            raise RuntimeError(f'Unverified game build: {exe_hash}')
        # Confirm all audited getter/helper instruction bytes match the saved image.
        code_checks = {}
        for name in [*dict((f[0], f[1]) for f in FIELDS), 'index', 'valid', 'valid_dispatch', 'encode_byte', 'decode_byte', 'save_serializer']:
            instructions = im.flow(FUNCTIONS[name])
            matched = all(required(game, game.exe + ins.address, ins.size) == bytes(ins.bytes) for ins in instructions)
            code_checks[name] = {'instructions': len(instructions), 'matched': matched}
        if not all(c['matched'] for c in code_checks.values()):
            raise RuntimeError('Live Native instructions differ from the audited image')
        world_tables = [rva for rva, name in im.rtti_vtables().items() if name == '.?AVCWorldData@@']
        if len(world_tables) != 1:
            raise RuntimeError('CWorldData RTTI is ambiguous')

        def snapshot():
            root = game.q(game.exe + ROOT_RVA)
            if not root:
                raise RuntimeError('World is not ready')
            state = game.q(root + STATE)
            if not state or game.q(state) != game.exe + world_tables[0]:
                raise RuntimeError('World state class does not match')
            clock = required(game, state + 0x34, 5)
            year = struct.unpack_from('<H', clock)[0]
            if not 100 < year < 400 or not 1 <= clock[2] <= 12 or not 1 <= clock[3] <= 31:
                raise RuntimeError('World date is not ready')
            pointers = required(game, root + TABLE, COUNT * 8)
            array = struct.unpack_from('<Q', pointers)[0]
            rows = []
            for index in range(COUNT):
                address = struct.unpack_from('<Q', pointers, index * 8)[0]
                if not array or address != array + SIZE * index:
                    raise RuntimeError(f'Tactic {index} is not contiguous')
                record = required(game, address, SIZE)
                if struct.unpack_from('<Q', record)[0] != game.exe + VTABLE:
                    raise RuntimeError(f'Tactic {index} class does not match')
                begin, end, cap = struct.unpack_from('<QQQ', record, 0x70)
                empty = begin == end and (begin == cap == 0 or begin >= 0x10000 and cap >= end)
                if not empty and not (begin >= 0x10000 and end == begin + 40 and end <= cap <= begin + 4096):
                    raise RuntimeError(f'Tactic {index} vector is not ready')
                vector = b'' if empty else required(game, begin, 40)
                rows.append((address, record, vector))
            if root != game.q(game.exe + ROOT_RVA) or state != game.q(root + STATE) or pointers != required(game, root + TABLE, COUNT * 8):
                raise RuntimeError('World/table changed during read')
            return root, state, clock, rows

        first = snapshot()
        clock_stable = True
        for _ in range(16):
            time.sleep(0.05)
            clock_stable &= required(game, first[1] + 0x34, 5) == first[2]
        second = snapshot()
        if first != second or not clock_stable:
            raise RuntimeError('Game clock or tactic records changed during read')
        native, rows, mismatches = Native(im), [], []
        for index, (address, record, vector) in enumerate(second[3]):
            native.put(index, record, vector)
            values = {}
            for field in FIELDS:
                name, rva, plain, offset, rotate, slots = field
                for slot in range(slots):
                    key = name + (str(slot + 1) if slots == 2 else '')
                    decoded = decode(vector[offset + slot * 4], rotate, index) if vector else record[plain + slot]
                    got = native.value(field, index, slot)
                    values[key] = {'plain': record[plain + slot], 'decoded': decoded, 'native': got}
                    if got != decoded or vector and decoded != record[plain + slot]:
                        mismatches.append([index, key, values[key]])
            name = record[0x10:0x1A].decode('utf-16-le').split('\0')[0]
            named = bool(name) and not re.fullmatch(r'(개성|전법|전)\d+', name)
            rows.append({'id': index, 'name': name, 'address': hex(address),
                         'vector': hex(struct.unpack_from('<Q', record, 0x70)[0]),
                         'vectorBytes': len(vector), 'category': record[0x40],
                         'cooldown': struct.unpack_from('<H', record, 0x42)[0],
                         'formationMask': hex(struct.unpack_from('<I', record, 0x6C)[0]),
                         'named': named, 'eligibleForRegisteredOfficer': index != 0 and record[0x40] != 0 and bool(values['flags']['native'] & 2),
                         'nativeValid': bool(native.call(0x32F500, index)), 'fields': values})
        profile_after = profile_path.read_bytes() if profile_path.exists() else None
        if profile_after != profile_before:
            raise RuntimeError('User profile changed while auditing; repeat the read')
        # Auditing copied Native data takes time. Re-read live storage before recording stable evidence.
        if second != snapshot():
            raise RuntimeError('Tactics changed while Native copied data was being audited')
        keys = list(rows[0]['fields'])
        return {'processId': game.pid, 'exePath': path.value, 'exeSha256': exe_hash,
                'root': hex(second[0]), 'state': hex(second[1]), 'clockHex': second[2].hex(),
                'pace': 'Paused', 'paceObservationMs': 800, 'stable': True,
                'profileSha256Before': None if profile_before is None else digest(profile_before),
                'profileSha256After': None if profile_after is None else digest(profile_after),
                'memoryWritten': False, 'profileSaved': False, 'nativeCodePatched': False,
                'nativeCallsOnCopiedData': COUNT * 18, 'codeChecks': code_checks,
                'counts': {'records': COUNT, 'namedNonzero': sum(r['id'] != 0 and r['named'] for r in rows),
                           'registeredNamed': sum(r['named'] and r['eligibleForRegisteredOfficer'] for r in rows),
                           'encoded': sum(r['vectorBytes'] == 40 for r in rows),
                           'plain': sum(r['vectorBytes'] == 0 for r in rows)},
                'observedValues': {k: sorted({r['fields'][k]['native'] for r in rows if r['id'] != 0 and r['named']}) for k in keys},
                'observedCooldowns': sorted({r['cooldown'] for r in rows if r['id'] != 0 and r['named']}),
                'mismatches': mismatches, 'records': rows}
    finally:
        k32.CloseHandle(ctypes.c_void_p(game.h))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--synthetic', action='store_true')
    mode.add_argument('--live', action='store_true')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    im = Image()
    result = {'utc': datetime.now(timezone.utc).isoformat(), 'imageSha256': digest(im.img),
              'fields': [dict(name=f[0], getter=hex(f[1]), plain=hex(f[2]), vector=hex(f[3]), rotate=f[4], slots=f[5]) for f in FIELDS],
              'nativeDisassembly': {name: im.flowtext(rva) for name, rva in FUNCTIONS.items()}}
    result['synthetic' if args.synthetic else 'live'] = data = synthetic(im) if args.synthetic else live(im)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'output': str(args.output.resolve()), 'mismatches': len(data['mismatches']),
                      'nativeCalls': data.get('nativeCalls', data.get('nativeCallsOnCopiedData')),
                      'counts': data.get('counts')}, ensure_ascii=False))
    if data['mismatches']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
