"""Offline Native audit of supplied traits9/stats200/growth200 patch bytes.

Only saved pages and an existing image are read. The `game` import is replaced
before importing Oracle, so this runner has no live-process discovery/access.
Supplied patch modules are parsed as AST, never imported or executed.

Usage: python mod_patch_isolation.py --tenkazu
   or: python mod_patch_isolation.py --patch-source <codepatch.py>
"""
import argparse
import ast
import hashlib
import json
import pickle
from pathlib import Path
import struct
import sys
import types

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / '.cache/registered-officers/pydeps'))
parser = argparse.ArgumentParser(description=__doc__, add_help=__name__ == '__main__', allow_abbrev=False)
source_options = parser.add_mutually_exclusive_group(required=True)
source_options.add_argument('--tenkazu', action='store_true', help='Audit the product patch manifest')
source_options.add_argument('--patch-source', type=Path, help='Path to the supplied patch module (parsed, never executed)')
# Importing audit runners retain their own positional and named arguments.
options = parser.parse_args() if __name__ == '__main__' else parser.parse_known_args()[0]
TENKAZU = options.tenkazu
SOURCE = (ROOT / 'source/Core/RuntimeRules/patches.json') if TENKAZU else options.patch_source
if not SOURCE.is_file():
    parser.error('Patch source must be an existing file')
OUT = ROOT / ('.cache/tenkazu-rules-isolation' if TENKAZU else '.cache/mod-patch-isolation')
OUT.mkdir(parents=True, exist_ok=True)

def sha(data):
    return hashlib.sha256(data).hexdigest()

def denied(*args, **kwargs):
    raise RuntimeError('Live game access is forbidden in this offline audit')

class FrozenMemory:
    def __init__(self, path):
        raw = Path(path).read_bytes()
        self.digest = sha(raw)
        d = pickle.loads(raw)
        self.exe, self.exe_size, self.base, self._teb = (d[k] for k in ('exe', 'exe_size', 'base', 'teb'))
        self.pages, self._imports = d['pages'], d['imports']
        self.fallback_reads = set()
        self.pid = None
        image_path = HERE / '.cache/image.bin'
        self.image = bytearray(image_path.read_bytes())
        self.image_sha256 = sha(self.image)
        old_base = int((HERE / '.cache/image.meta').read_text().strip(), 16)
        pe = struct.unpack_from('<I', self.image, 0x3C)[0]
        opt = pe + 24
        count = struct.unpack_from('<H', self.image, pe + 6)[0]
        sec = opt + struct.unpack_from('<H', self.image, pe + 20)[0]
        self.static_sections = []
        self.text_section = None
        for i in range(count):
            at = sec + 40 * i
            name = self.image[at:at + 8].rstrip(b'\0').decode()
            size, rva = struct.unpack_from('<II', self.image, at + 8)
            if name in ('.text', '.rdata', '.pdata', '.reloc'):
                self.static_sections.append((rva, (rva + size + 4095) & ~4095))
            if name == '.text':
                self.text_section = (rva, rva + size)
        reloc, size = struct.unpack_from('<II', self.image, opt + 112 + 5 * 8)
        pos = reloc
        while pos < reloc + size:
            page, length = struct.unpack_from('<II', self.image, pos)
            if length < 8:
                break
            for at in range(pos + 8, pos + length, 2):
                item = struct.unpack_from('<H', self.image, at)[0]
                if item >> 12 == 10:
                    target = page + (item & 4095)
                    value = struct.unpack_from('<Q', self.image, target)[0]
                    struct.pack_into('<Q', self.image, target, (value + self.exe - old_base) & 0xFFFFFFFFFFFFFFFF)
            pos += length
        # PE relocation alone cannot relocate pointers assigned by the native CRT.
        # Reconstruct this known list initializer using its real code, not the product
        # manifest's fixup list. Keep captured pages authoritative and read-only.
        if self.image_sha256 != 'aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59':
            raise RuntimeError('Native list initializer requires the verified saved image')
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_PROT_ALL
        from unicorn.x86_const import UC_X86_REG_RSP
        init = Uc(UC_ARCH_X86, UC_MODE_64)
        for page in [0x2000, 0x1a47000, *range(0x1a2b000, 0x1a31000, 4096)]:
            init.mem_map(self.exe + page, 4096, UC_PROT_ALL)
            init.mem_write(self.exe + page, bytes(self.image[page:page+4096]))
        stack = 0x30000000000
        init.mem_map(stack, 4096, UC_PROT_ALL)
        init.reg_write(UC_X86_REG_RSP, stack + 0x808)
        init.mem_write(stack + 0x808 - 8 - 0x3c, bytes(self.image[0x1a2b850+22*96+0x24:0x1a2b850+22*96+0x28]))
        init.emu_start(self.exe + 0x2260, self.exe + 0x24f0, count=500)
        rebuilt = bytes(init.mem_read(self.exe + 0x1a2b850, 213*96))
        self.image[0x1a2b850:0x1a2b850+213*96] = rebuilt
        self.code_page_comparison = []
        for page, data in self.pages.items():
            rva = page - self.exe
            if self.text_section[0] <= rva < self.text_section[1]:
                expected = bytes(self.image[rva:rva + 4096])
                differences = [hex(rva + i) for i, (a, b) in enumerate(zip(data, expected)) if a != b]
                self.code_page_comparison.append(dict(rva=hex(rva), equal=not differences, differences=differences))

    def read(self, at, n):
        result = bytearray()
        while n:
            page, offset = at & ~4095, at & 4095
            data = self.pages.get(page)
            if data is None:
                rva = page - self.exe
                if not any(a <= rva < b for a, b in self.static_sections):
                    return None
                data = bytes(self.image[rva:rva + 4096]).ljust(4096, b'\0')
                self.fallback_reads.add(rva)
            count = min(n, 4096 - offset)
            result.extend(data[offset:offset + count])
            at += count
            n -= count
        return bytes(result)

    def region(self, at):
        page = at & ~4095
        if page in self.pages:
            return page, 4096, 0x1000, 4
        end = min((p for p in self.pages if p > page), default=page + 0x100000000)
        return page, end - page, 0x10000, 0

    def main_teb(self):
        return self._teb

    def imports(self):
        return self._imports

fake_game = types.ModuleType('game')
fake_game.Game = denied
fake_game.FrozenGame = FrozenMemory
sys.modules['game'] = fake_game
from oracle import Oracle
from contextlib import contextmanager

def literal(node):
    if isinstance(node, ast.Constant):
        return node.value
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Mult):
        return literal(node.left) * literal(node.right)
    raise ValueError(ast.dump(node))

source_bytes = SOURCE.read_bytes()
tree = ast.parse('PATCHES = {}' if TENKAZU else source_bytes.decode('utf-8-sig'))
patch_node = next(n.value for n in tree.body if isinstance(n, ast.Assign)
                  and any(isinstance(t, ast.Name) and t.id == 'PATCHES' for t in n.targets))
PATCHES = {}
for key, value in zip(patch_node.keys, patch_node.values):
    name = literal(key)
    if name not in ('stats200', 'growth200', 'traits9'):
        continue
    rows = []
    for call in value.elts:
        assert isinstance(call, ast.Call) and call.func.id == '_p'
        rva, before, after, guard_rva, guard = map(literal, call.args)
        rows.append((rva, bytes.fromhex(before), bytes.fromhex(after), guard_rva, bytes.fromhex(guard)))
    PATCHES[name] = rows

if TENKAZU:
    manifest = json.loads(source_bytes)
    for name, field in (('traits9', 'Traits'), ('stats200', 'Cap'), ('growth200', 'Growth')):
        PATCHES[name] = [(r['Rva'], bytes.fromhex(r['Before']), bytes.fromhex(r[field]),
                          r['GuardRva'], bytes.fromhex(r['Guard'])) for r in manifest if r[field] != r['Before']]

class Audit:
    def __init__(self, o, g):
        self.o, self.g = o, g
        self.cases = []
        self.original_code = {rva: before for rows in PATCHES.values() for rva, before, *_ in rows}
        self.notifications = []
        # Only the post-level-up message bookkeeping endpoint is replaced.
        o.stub(0x2589C0, fn=lambda: self.notifications.append(1) or 0)
        self.person = self.ptr(0x73A50, 1)
        self.unit = self.ptr(0x7E838, 1)
        self.sums = o.alloc(0x200)
        self.mode = 'original'

    def ptr(self, table, index):
        return self.o.q(self.g.base - 0x290 + table + index * 8)

    def mode_set(self, mode):
        for rva, before in self.original_code.items():
            self.o.poke(self.g.exe + rva, before)
        groups = {'original': (), 'traits9': ('traits9',), 'stats200': ('stats200',),
                  'growth200': ('growth200',), 'combined': ('traits9', 'growth200'),
                  'combined_cap': ('traits9', 'stats200')}[mode]
        for group in groups:
            for rva, before, after, *_ in PATCHES[group]:
                self.o.poke(self.g.exe + rva, after)
        for rva, before in self.original_code.items():
            self.o.mu.ctl_remove_cache(self.g.exe + rva, self.g.exe + rva + len(before))
        self.mode = mode

    @contextmanager
    def fixture(self):
        undo = []
        def put(at, data):
            undo.append((at, self.o.read(at, len(data))))
            self.o.poke(at, data)
        try:
            yield put
        finally:
            for at, data in reversed(undo):
                self.o.poke(at, data)

    def check(self, name, actual, expected, **inputs):
        case = dict(mode=self.mode, name=name, inputs=inputs, actual=actual,
                    expected=expected, passed=actual == expected)
        self.cases.append(case)
        if not case['passed']:
            print('FAIL', json.dumps(case, ensure_ascii=False), flush=True)

    def setup(self, put):
        p = self.person
        # Synthetic unit is neither pending removal nor in an engagement.
        put(self.g.base - 0x290 + 0x70, bytes(8))
        put(self.g.base - 0x290 + 0x140, bytes(8))
        put(p + 0x118, bytes(18))
        put(p + 0x184, bytes(20))
        put(p + 0xE0, b'\0')
        put(p + 0xE6, b'\x03')
        put(p + 0x14C, b'\0')
        put(p + 0x1BC, b'\0')
        put(p + 0x161, b'\0')
        put(self.ptr(0xE0D0, 0) + 0x10, b'\0')
        put(self.unit + 0x12, self.o.read(p + 0x10, 2))
        put(self.unit + 0x64, bytes(36 * 6))
        for slot in range(9):
            trait = self.ptr(0x7DD18, 256 + slot)
            put(trait + 0xA4, bytes(20))
            put(trait + 0xB4, struct.pack('<hBBhBBhBB', -1, 5, 0, (slot + 1) * 10, 0, 0, 0, 0, 16))

    def trait_cases(self):
        enabled = self.mode in ('traits9', 'combined', 'combined_cap')
        for slot in range(9):
            for duration, lit in ((-1, False), (3, False), (3, True)):
                with self.fixture() as put:
                    self.setup(put)
                    trait_id = 256 + slot
                    put(self.person + 0x118 + slot * 2, struct.pack('<H', trait_id))
                    put(self.ptr(0x7DD18, trait_id) + 0xB4, struct.pack('<h', duration))
                    put(self.person + 0x184, struct.pack('<H', (1 << slot) if lit else 0))
                    active = enabled or slot < 5
                    value = (slot + 1) * 10 if active else 0
                    self.check('slot_getter', self.o.call32(0x2305D0, self.person, slot), trait_id if active else 0,
                               slot=slot + 1, duration=duration, lit=lit)
                    self.check('has_personality', self.o.call32(0x232D00, self.person, trait_id), int(active), slot=slot + 1)
                    self.check('effect_sum', self.o.call32(0x2B2DA0, self.person, 5), value, slot=slot + 1)
                    on = active and (duration == -1 or lit)
                    self.check('lit_effect', self.o.call32(0x2C37F0, self.person, 5), int(on), slot=slot + 1,
                               duration=duration, lit=lit)
                    self.o.call(0x26A2C0, self.unit, 0, self.sums, 0, read_after=[(self.sums, 0x1FC)])
                    self.check('battle_effect_sum', struct.unpack_from('<i', self.o.after[0], 5 * 4)[0],
                               value if on else 0, slot=slot + 1, duration=duration, lit=lit)

    def growth_probe(self):
        with self.fixture() as put:
            self.setup(put)
            level_table = self.o.q(self.g.exe + 0x277DF00)
            level100 = self.o.q(level_table + 0x60 + 100 * 8)
            need100 = struct.unpack('<i', self.o.read(level100 + 0x10, 4))[0]
            need101 = need100 * 101 * 101 // 10000
            put(self.person + 0xEC, b'\x64')
            put(self.person + 0x104, struct.pack('<i', need101 - 1))
            self.o.call32(0x2B9D50, self.person, 0, 1, 0, read_after=[(self.person, 0x1F0)])
            data = self.o.after[0]
            print('growth_probe', self.mode, need100, need101, data[0xEC], struct.unpack_from('<i', data, 0x104)[0], flush=True)

    def cap_cases(self):
        patched = self.mode in ('stats200', 'growth200', 'combined', 'combined_cap')
        with self.fixture() as put:
            self.setup(put)
            for function, offset, original in ((0x229410, 0x1EC, 120), (0x2293A0, 0x1ED, 100)):
                self.o.call32(function, self.person, read_after=[(self.person + offset, 1)])
                encoded = self.o.after[0][0]
                decoded = (((encoded << 3) | (encoded >> 5)) & 255) ^ 1
                self.check('native_cap_initialization', decoded, 200 if patched and not TENKAZU else original, field=hex(offset))
                put(self.person + offset, bytes([encoded]))
            for ability in range(5):
                for value in (0, 1, 99, 100, 101, 119, 120, 121, 199, 200, 201, 255):
                    put(self.person + 0xEC + ability, bytes([value]))
                    cap = 200 if patched else (100 if ability == 4 else 120)
                    self.check('effective_ability_cap', self.o.call32(0x22FCE0, self.person, ability, 0),
                               max(1, min(value, cap)), ability=ability, value=value, cap=cap)

    def condition_probe(self):
        for condition in (1, 13):
            with self.fixture() as put:
                self.setup(put)
                put(self.person + 0x128, struct.pack('<H', 264))
                put(self.unit + 0x10, b'\x01')
                put(self.ptr(0x7DD18, 264) + 0xA4, bytes([condition]))
                put(self.ptr(0x7DD18, 264) + 0xB4, struct.pack('<h', 3))
                self.o.call(0x26A2C0, self.unit, 0, self.sums, 0, stack_args=(1,), read_after=[(self.sums, 0x1FC)])
                got = struct.unpack_from('<i', self.o.after[0], 20)[0]
                self.check('native_condition_battle_sum', got, 90 if condition == 1 and self.mode in ('traits9', 'combined', 'combined_cap') else 0,
                           slot=9, condition=condition)

    def threshold(self, level, ability):
        table = self.o.q(self.g.exe + 0x277DF00)
        entry = self.o.q(table + 0x60 + min(level, 100) * 8)
        value = struct.unpack('<i', self.o.read(entry + 0x10 + ability * 4, 4))[0]
        return value if level <= 100 else value * level * level // 10000

    def growth_cases(self):
        growth = self.mode in ('growth200', 'combined')
        capped = self.mode in ('stats200', 'combined_cap')
        for ability in range(5):
            for level in (1, 69, 70, 98, 99, 100, 101, 119, 120, 149, 150, 198, 199, 200):
                for delta in (-1, 0, 1):
                    target = min(level + 1, 201)
                    need = self.threshold(target, ability)
                    initial = need - 1
                    amount = delta + 1
                    with self.fixture() as put:
                        self.setup(put)
                        put(self.person + 0xEC + ability, bytes([level]))
                        put(self.person + 0x104 + ability * 4, struct.pack('<i', initial))
                        self.o.call32(0x2B9D50, self.person, ability, amount, 0,
                                      read_after=[(self.person, 0x1F0)])
                        data = self.o.after[0]
                        unchanged = level == 100 and not growth or level >= 100 and capped or TENKAZU and growth and level >= 200
                        exp = initial if unchanged else initial + amount
                        expected = level
                        if not unchanged:
                            max_level = 200 if growth else 100
                            while expected < max_level and exp >= self.threshold(expected + 1, ability):
                                expected += 1
                        self.check('native_growth_boundary', [data[0xEC + ability], struct.unpack_from('<i', data, 0x104 + ability * 4)[0]],
                                   [expected, exp], ability=ability, level=level, threshold=need, initial=initial, amount=amount)

    def extended_traits(self):
        enabled = self.mode in ('traits9', 'combined', 'combined_cap')
        with self.fixture() as put:
            self.setup(put)
            put(self.person + 0x118, struct.pack('<9H', *range(256, 265)))
            expected = 450 if enabled else 150
            self.check('all_nine_effects', self.o.call32(0x2B2DA0, self.person, 5), expected)
            self.o.call(0x26A2C0, self.unit, 0, self.sums, 0, read_after=[(self.sums, 0x1FC)])
            self.check('all_nine_battle_effects', struct.unpack_from('<i', self.o.after[0], 20)[0], expected)
        for slot in range(9):
            for condition, unit_kind in ((1, 1), (1, 2), (13, 1), (13, 2)):
                with self.fixture() as put:
                    self.setup(put)
                    trait_id = 256 + slot
                    put(self.person + 0x118 + slot * 2, struct.pack('<H', trait_id))
                    put(self.unit + 0x10, bytes([unit_kind]))
                    put(self.ptr(0x7DD18, trait_id) + 0xA4, bytes([condition]))
                    put(self.ptr(0x7DD18, trait_id) + 0xB4, struct.pack('<h', 3))
                    self.o.call(0x26A2C0, self.unit, 0, self.sums, 0, stack_args=(1,), read_after=[(self.sums, 0x1FC)])
                    got = struct.unpack_from('<i', self.o.after[0], 20)[0]
                    on = (enabled or slot < 5) and (condition == 1 and unit_kind == 1 or condition == 13 and unit_kind == 2)
                    self.check('battle_condition_evaluation', got, (slot + 1) * 10 if on else 0,
                               slot=slot + 1, condition=condition, unit_kind=unit_kind)
            for allowed in (False, True):
                with self.fixture() as put:
                    self.setup(put)
                    put(self.person + 0x118 + slot * 2, struct.pack('<H', 256))
                    put(self.ptr(0x7DD18, 256) + 0xBF, bytes([16 if allowed else 0]))
                    self.check('trait_validity_flag', self.o.call32(0x2305D0, self.person, slot),
                               256 if allowed and (enabled or slot < 5) else 0, slot=slot + 1, allowed=allowed)
            with self.fixture() as put:
                self.setup(put)
                put(self.person + 0x118 + slot * 2, struct.pack('<H', 256))
                put(self.ptr(0x7DD18, 256) + 0xB6, b'\x4B')
                put(self.ptr(0x7DD18, 256) + 0xB8, struct.pack('<h', 100))
                put(self.person + 0xEC, b'\x32')
                initial = self.threshold(50, 0)
                put(self.person + 0x104, struct.pack('<i', initial))
                self.o.call32(0x2B9D50, self.person, 0, 100, 0, read_after=[(self.person + 0x104, 4)])
                self.check('trait_affects_actual_exp_award', struct.unpack('<i', self.o.after[0])[0] - initial,
                           200 if enabled or slot < 5 else 100, slot=slot + 1)

    def growth_sequence(self):
        if self.mode not in ('growth200', 'combined'):
            return
        for ability in range(5):
            with self.fixture() as put:
                self.setup(put)
                level = 99
                exp = self.threshold(level, ability)
                while level < 200:
                    need = self.threshold(level + 1, ability)
                    put(self.person + 0xEC + ability, bytes([level]))
                    put(self.person + 0x104 + ability * 4, struct.pack('<i', exp))
                    self.o.call32(0x2B9D50, self.person, ability, need - exp, 0,
                                  read_after=[(self.person, 0x1F0)])
                    data = self.o.after[0]
                    actual = [data[0xEC + ability], struct.unpack_from('<i', data, 0x104 + 4 * ability)[0]]
                    self.check('continuous_growth_step', actual, [level + 1, need], ability=ability, from_level=level)
                    if actual != [level + 1, need]:
                        break
                    level, exp = actual
                put(self.person + 0xEC + ability, bytes([level]))
                put(self.person + 0x104 + ability * 4, struct.pack('<i', exp))
                self.o.call32(0x2B9D50, self.person, ability, 100000, 0, read_after=[(self.person, 0x1F0)])
                self.check('continuous_growth_stops_at_200', self.o.after[0][0xEC + ability], 200, ability=ability)

    def existing_cap_case(self):
        if self.mode not in ('stats200', 'growth200', 'combined', 'combined_cap'):
            return
        with self.fixture() as put:
            self.setup(put)
            for ability, cap, offset in ((0, 120, 0x1EC), (4, 100, 0x1ED)):
                x = cap ^ 1
                encoded = ((x >> 3) | (x << 5)) & 255
                put(self.person + offset, bytes([encoded]))
                put(self.person + 0xEC + ability, bytes([180]))
                self.check('existing_record_cap_effective_value',
                           self.o.call32(0x22FCE0, self.person, ability, 0), 180 if TENKAZU else cap,
                           ability=ability, input=180, existing_cap=cap)

    def terminal_experience_cases(self):
        if not TENKAZU or self.mode not in ('stats200', 'growth200', 'combined', 'combined_cap'):
            return
        for ability in range(5):
            for level in (200, 201, 255):
                for experience in (self.threshold(200, ability), 2147483646, 2147483647):
                    for amount in (0, 1, 100000):
                        with self.fixture() as put:
                            self.setup(put)
                            put(self.person + 0xEC + ability, bytes([level]))
                            put(self.person + 0x104 + 4 * ability, struct.pack('<i', experience))
                            self.o.call32(0x2B9D50, self.person, ability, amount, 0, read_after=[(self.person, 0x1F0)])
                            data = self.o.after[0]
                            self.check('terminal_level_no_overflow_or_loop',
                                       [data[0xEC + ability], struct.unpack_from('<i', data, 0x104 + 4 * ability)[0]],
                                       [level, experience], ability=ability, level=level, experience=experience, amount=amount)

def main():
    g = FrozenMemory(HERE / '.cache/frozen.pkl')
    o = Oracle(g)
    preflight = []
    for name, rows in PATCHES.items():
        for rva, before, after, guard_rva, guard in rows:
            try:
                got = o.read(g.exe + guard_rva, len(guard))
                preflight.append(dict(group=name, rva=hex(rva), match=got == guard,
                                      expected=guard.hex(), actual=got.hex()))
            except Exception as e:
                preflight.append(dict(group=name, rva=hex(rva), match=False, error=str(e)))
    result = dict(live_game_access=False, supplied_module_executed=False,
                  frozen_sha256=g.digest, source_sha256=sha(source_bytes), preflight=preflight,
                  native_stubs=['0x2589C0: post-level-up notification bookkeeping'],
                  infrastructure_stubs='Existing Oracle memory/lock imports and CRT math emulation; no gameplay decision stubs',
                  code_page_comparison=g.code_page_comparison)
    (OUT / 'preflight.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(dict(matches=sum(r['match'] for r in preflight), total=len(preflight),
                          failures=[r for r in preflight if not r['match']]), indent=2))
    if not all(row['match'] for row in preflight):
        raise RuntimeError('Patch preflight failed')
    if not all(row['equal'] for row in g.code_page_comparison):
        raise RuntimeError('Captured Native code differs from the original cached image')
    audit = Audit(o, g)
    complete = False
    try:
        modes = ('original', 'traits9', 'stats200', 'growth200', 'combined') + (('combined_cap',) if TENKAZU else ())
        for mode in modes:
            audit.mode_set(mode)
            audit.trait_cases()
            audit.growth_probe()
            audit.cap_cases()
            audit.growth_cases()
            audit.condition_probe()
            audit.extended_traits()
            audit.growth_sequence()
            audit.existing_cap_case()
            audit.terminal_experience_cases()
            print(mode, len(audit.cases), 'cases', flush=True)
        complete = True
    finally:
        audit.mode_set('original')
        restored = all(o.read(g.exe + rva, len(data)) == data for rva, data in audit.original_code.items())
        input_unchanged = dict(source=sha(SOURCE.read_bytes()) == sha(source_bytes),
                               frozen=sha((HERE / '.cache/frozen.pkl').read_bytes()) == g.digest,
                               image=sha((HERE / '.cache/image.bin').read_bytes()) == g.image_sha256)
        result.update(cases=audit.cases, image_sha256=g.image_sha256,
                      static_fallback_pages=[hex(rva) for rva in sorted(g.fallback_reads)],
                      complete=complete, emulator_code_restored=restored, input_files_unchanged=input_unchanged,
                      runner_sha256=sha(Path(__file__).read_bytes()))
        (OUT / 'results.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
        from collections import Counter
        summary = dict(complete=complete, passed=sum(c['passed'] for c in audit.cases),
                       failed=sum(not c['passed'] for c in audit.cases), total=len(audit.cases),
                       modes=dict(Counter(c['mode'] for c in audit.cases)),
                       checks=dict(Counter(c['name'] for c in audit.cases)),
                       original_code_pages_verified=len(g.code_page_comparison),
                       live_game_access=False, emulator_code_restored=restored, input_files_unchanged=input_unchanged)
        (OUT / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(json.dumps(summary, indent=2), flush=True)
    if not complete or not restored or not all(input_unchanged.values()) or any(not c['passed'] for c in audit.cases):
        raise RuntimeError('Offline audit failed; see results.json')

if __name__ == '__main__':
    main()
