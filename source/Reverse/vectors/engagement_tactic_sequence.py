# Consecutive native engagements with live tactic selection, linked dispatch and deferred troop damage.
# Only presentation callbacks are replaced. Nonlethal unit encounters isolate destruction/siege consumers.
import collections, random, struct, sys
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, encoded, TargetOracle
from tactic_damage import DamageMaps
from engagement_sequence import COUNTIES, PHASES
from battlepower_fx import ring

COVER = [0x169C50, *PHASES, 0x172E20, 0x175350, 0x178900, 0x178E30, 0x16AD70]
PRESENTATION = [0x1A3A00, 0x1A3C50, 0x404080, 0x18ACF0, 0x18C270, 0x84DEF0, 0x1B4AB0, 0x168BB0]


class TacticManagerOracle:
    def __init__(self, ctx):
        self.ctx, self.o = ctx, ctx.o
        self.native = TargetOracle(ctx)
        self.manager = ctx.g.exe + 0x1BB38A0
        self.nodes, self.events = self.o.alloc(16 * 64), self.o.alloc(0x20000)
        self.maps = DamageMaps(ctx); self.maps.maps = self.manager + 0xB8
        self.refs = {ctx.addr(t, i): [hex(t), i] for t in [UNITS, CITIES, GATES] for i in range(len(ctx.rec[t]))}
        self.tactics = {ctx.addr(TACTICS, i): i for i in range(len(ctx.rec[TACTICS]))}
        self.tiles = {ctx.addr(TILES, i): i for i in range(len(ctx.rec[TILES]))}

    def reset(self):
        o, manager = self.o, self.manager
        # Preserve the real singleton's initialization guard at +0x3F0.
        o.poke(manager, bytes(0x3F0))
        o.poke(manager + 0x48, o.read(self.ctx.g.exe + 0x27D1128 + 0x40, 0x50))
        for i in range(16):
            node = self.nodes + 64 * i
            o.poke(node, bytes(64)); o.poke(node + 48, struct.pack('<Q', node + 64 if i < 15 else 0))
        o.poke(manager + 0x28, struct.pack('<Q', self.nodes)); self.maps.prepare(None)
        for i in range(11):
            row = manager + 0xD8 + 64 * i
            o.poke(row + 8, struct.pack('<I', 255))
            for slot in range(2):
                buf = self.events + (2 * i + slot) * 0x1000
                o.poke(row + 16 + 24 * slot, struct.pack('<QQQ', buf, buf, buf + 0x1000))

    def items(self, wrapper):
        o = self.o
        cell = o.q(wrapper + 8)
        index = struct.unpack('<I', o.read(cell, 4))[0] if cell else 0x14000
        node, result, seen = o.q(self.native.pool + index * 8), [], set()
        while node:
            if node in seen or len(seen) >= 1000: raise RuntimeError('Invalid native target/candidate list')
            seen.add(node); ptr, node = struct.unpack('<QQ', o.read(node, 16)); result.append(ptr)
        return result

    def inspect(self, candidates):
        o, manager = self.o, self.manager
        queue, rows, maps = [], [], []
        node, seen = o.q(manager + 0x18), set()
        while node:
            if node in seen or len(seen) >= 11: raise RuntimeError('Invalid native linked command queue')
            seen.add(node)
            kind, unit, tactic = struct.unpack('<IH2xQ', o.read(node, 16))
            targets = [[self.tiles[p] for p in self.items(node + 16 + 16 * slot)] for slot in range(2)]
            queue.append([kind, unit, self.tactics[tactic], targets]); node = o.q(node + 48)
        for i in range(11):
            row = manager + 0xD8 + 64 * i
            tactic, kind, number = struct.unpack('<QII', o.read(row, 16))
            if kind == 255: continue
            outputs = []
            for slot in range(2):
                begin, end, cap = struct.unpack('<QQQ', o.read(row + 16 + 24 * slot, 24))
                if end < begin or end > cap or (end - begin) % 16: raise RuntimeError('Invalid native tactic output vector')
                output = []
                for at in range(begin, end, 16):
                    k, index, amount, unused, h = struct.unpack('<IIHHI', o.read(at, 16)); output.append([k, index, h, amount])
                outputs.append(output)
            rows.append([i, self.tactics[tactic], kind, number, outputs])
        for slot in range(2):
            head = o.q(manager + 0xB8 + 16 * slot)
            values, visited = [], set()
            def walk(node):
                if node == head: return
                if not node or node in visited or len(visited) >= 1000: raise RuntimeError('Invalid native tactic damage map')
                visited.add(node); data = o.read(node, 48)
                walk(struct.unpack_from('<Q', data)[0])
                ptr, amount = struct.unpack_from('<Qi', data, 32); values.append([*self.refs[ptr], amount])
                walk(struct.unpack_from('<Q', data, 16)[0])
            walk(o.q(head + 8))
            if len(values) != o.q(manager + 0xC0 + 16 * slot): raise RuntimeError('Native damage map count differs')
            maps.append(values)
        return {'candidates': candidates, 'queue': queue, 'rows': rows, 'maps': maps}


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    limit = int(next((a.split('=', 1)[1] for a in sys.argv if a.startswith('--limit=')), 600))
    rng = random.Random(0x16FDD017)
    for rva in PRESENTATION: o.stub(rva, 0)
    tactic = TacticManagerOracle(ctx)
    manager, sources, records = o.alloc(0x100), o.alloc(3 * 64), o.alloc(3 * 64)
    leaders = {f: [i for i in range(1, len(rec[OFFICERS])) if rec[OFFICERS][i][0xE6] in [2, 3, 4, 6, 7]
                  and ctx.officer_force(i) == f and ctx.u16(OFFICERS, i, 0x10) < 6001] for f in [3, 6]}
    tables = [UNITS, OFFICERS, COUNTIES, FORCES, CITIES, GATES, BUILDINGS, TILES]
    starts = {t: ctx.addr(t, 0) for t in tables}
    for t in tables:
        size, count = world.TABLES[t][1:]
        if ctx.addr(t, count - 1) != starts[t] + size * (count - 1): raise RuntimeError('Noncontiguous observed table')
    traces, frames, hooks = {}, {}, []
    current = [None]
    def capture(mu, address, size, data):
        stage = address - ctx.g.exe
        if stage in [0x172E20, 0x16B880]:
            entry = traces[current[0]]
            if stage == 0x172E20: entry['flow_called'] = True
            else: entry['candidates'] = [tactic.tactics[p] for p in tactic.items(mu.reg_read(UC_X86_REG_RDX))]
            return
        node = mu.reg_read(UC_X86_REG_RCX if stage in PHASES[1:5] else UC_X86_REG_RDX)
        i = (node - records) // 64
        if not 0 <= i < 3 or node != records + i * 64: raise RuntimeError('Unexpected engagement record')
        current[0] = i; entry = traces.setdefault(i, {'stages': [], 'flow_called': False, 'candidates': []})
        entry['stages'].append(stage)
        if stage == 0x16FDD0:
            frame = mu.reg_read(UC_X86_REG_R8); values = struct.unpack('<8I', o.read(frame, 32))
            entry['calculation'] = [values[k] for k in [0, 1, 2, 5, 6, 7]] + [struct.unpack('<H', o.read(frame + 32, 2))[0]]
        if stage == 0x1749A0:
            frames[i] = mu.reg_read(UC_X86_REG_R8)
            entry['tactic_frame'] = list(struct.unpack('<HHI', o.read(frames[i] + 32, 8)))
            if entry['flow_called']: entry['flow'] = tactic.inspect(entry['candidates'])
        if stage == 0x174E30: entry['frame'] = list(struct.unpack('<HHI', o.read(frames[i] + 32, 8)))
    for stage in [*PHASES, 0x172E20, 0x16B880]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, capture, begin=ctx.g.exe + stage, end=ctx.g.exe + stage))
    def poke(t, i, off, data): return [hex(t), i, off, data.hex()]
    cases, stats = [], collections.Counter()
    effects = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 21, 22, 23]
    try:
        for n in range(limit):
            f3, f6 = rng.sample(leaders[3], 2), rng.sample(leaders[6], 2)
            commanders, forces, positions = [f3[0], f6[0], f3[1], f6[1]], [3, 6, 3, 6], [20000, 20001, 20220, 20221]
            troops = [20000] * 4
            sides = [[400 + k, 27, positions[k], forces[k], rng.choice([0, 1, 2, 3])] for k in range(2)]
            sequence = [[sides[0], sides[1]], [sides[1], sides[0]], [sides[0], sides[1]]]
            p = [['worldstate', 0, 0x3A, '00'], ['worldstate', 0, 0x165C, '00'], ['worldstate', 0, 0x165D, '00'],
                 ['exe', 'challenge_active', 0, '01000000'], ['exe', 'challenge_flags', 0, '02000000'],
                 ['exe', 'challenge_kind', 0, '00000000'], ['exe', 'challenge_rules', 0, '00000000'],
                 ['listdata', '0x70', [], ''], ['listdata', '0x140', [], ''],
                 ['settings', 0, 0xE58, struct.pack('<i', 1).hex()], ['settings', 0, 0x28, struct.pack('<i', n % 6).hex()],
                 ['settings', 0, 0xCC, struct.pack('<i', n % 3 == 0).hex()],
                 poke(COUNTIES, 59, 0x3A, struct.pack('<H', positions[0])),
                 poke(COUNTIES, 59, 0x3C, struct.pack('<3H', *rng.choices([99, 100, 101, 300, 600, 1000], k=3))),
                 poke(COUNTIES, 59, 0x52, struct.pack('<HB', 30000, 100))]
            for h in ring(20000, 4) + [20000]:
                p += [poke(TILES, h, 0x1A, bytes(4)), poke(TILES, h, 0x10, b'\1'), poke(TILES, h, 0x14, b'\3')]
            for k, f in enumerate(forces):
                unit, cmd, pos = 400 + k, commanders[k], positions[k]
                p += [poke(UNITS, unit, 0x10, b'\1'), poke(UNITS, unit, 0x12, struct.pack('<H', ctx.u16(OFFICERS, cmd, 0x10))),
                      poke(UNITS, unit, 0x16, struct.pack('<HHBBB', troops[k], 7000, 100, rng.choice([1, 2, 3, 4, 14, 15]), 15)),
                      poke(UNITS, unit, 0x1D, bytes([191, 192, 193])), poke(UNITS, unit, 0x20, bytes([n % 10 == 0, 150, 200])),
                      poke(UNITS, unit, 0x23, bytes(3)), poke(UNITS, unit, 0x2A, struct.pack('<H', pos)),
                      poke(UNITS, unit, 0x152, struct.pack('<H', pos)), poke(TILES, pos, 0x1C, struct.pack('<H', unit)),
                      poke(TILES, pos, 0x14, bytes([f])),
                      poke(OFFICERS, cmd, 0x130, bytes([191, 192, 193]) + bytes(7)),
                      poke(OFFICERS, cmd, 0xE2, bytes(2)), poke(OFFICERS, cmd, 0xE4, struct.pack('<H', unit + 0x3D)),
                      poke(OFFICERS, cmd, 0xBC, struct.pack('<H', 33 if f == 3 else 34)),
                      poke(OFFICERS, cmd, 0x15E, struct.pack('<H', ctx.u16(OFFICERS, cmd, 0x15E) & ~0x4000))]
            for f in [3, 6]:
                p += [poke(FORCES, f, 0xEA, bytes(52)), poke(FORCES, f, 0x194, b'\0'), poke(FORCES, f, 0x15F, b'\0'),
                      poke(FORCES, f, 0x13 + (6 if f == 3 else 3), bytes([rng.choice([0, 15, 100, 200])]))]
            for t, kinds in zip([191, 192, 193], [[effects[n % len(effects)], 1], [5, 15], [19, 20]]):
                p += [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, kinds, [n % 2, 1], [2, 2], flags=3).hex()],
                      poke(TACTICS, t, 0x40, bytes([rng.choice([1, 2, 5, 6])])),
                      poke(TACTICS, t, 0x42, struct.pack('<H', rng.choice([0, 1, 8, 30]))),
                      poke(TACTICS, t, 0x6C, struct.pack('<I', 0xFFFFFFFF))]
            def run():
                traces.clear(); frames.clear(); current[0] = None; tactic.reset()
                o.poke(manager, bytes(0x100)); o.poke(manager + 0xA0, struct.pack('<Q', records))
                for i, (a, b) in enumerate(sequence):
                    src, node = sources + 64 * i, records + 64 * i
                    o.poke(src, bytes(64)); o.poke(src, struct.pack('<H2xI4xIIB3x', *a)); o.poke(src + 10, struct.pack('<H', 20000))
                    o.poke(node, bytes(64)); o.poke(node, struct.pack('<Q', src)); o.poke(node + 8, struct.pack('<H2xI4xIIB3x', *b))
                    o.poke(node + 18, struct.pack('<H', 20000)); o.poke(node + 48, struct.pack('<Q', node + 64 if i < 2 else 0))
                before = {t: o.read(starts[t], world.TABLES[t][1] * world.TABLES[t][2]) for t in tables}
                changed = []
                def inspect():
                    for t in tables:
                        size, count = world.TABLES[t][1:]; after = o.read(starts[t], size * count)
                        for i in range(count):
                            part = after[size * i:size * (i + 1)]
                            if part != before[t][size * i:size * (i + 1)]: changed.append([hex(t), i, part.hex()])
                    spec = world.LISTS[0x140]; index = struct.unpack('<I', o.read(o.q(ctx.raw + 0x140), 4))[0]
                    if o.q(o.q(ctx.g.exe + spec['pool']) + 8 * index): raise RuntimeError('Nonlethal tactic fixture queued destruction')
                    return []
                result = o.call32(0x169C50, manager, read_after=inspect)
                return [result, [traces[i] for i in range(3)], changed]
            result = ctx.run(p, run)
            if result is None: raise RuntimeError(f'Native active tactic sequence failed in case {n}: {ctx.failures[-1]}')
            for entry in result[1]:
                stats['records'] += 1; stats['flow_called'] += entry['flow_called']
                stats['fired'] += entry['frame'][2] != 0; stats['transferred'] += entry['frame'][1] != 0
                if 'flow' in entry: stats['linked'] += len(entry['flow']['queue']) > 1
            cases.append([sequence, *result, p])
            if (n + 1) % 50 == 0: print(f'  active tactic sequences {n + 1}/{limit}: {dict(stats)}', flush=True)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    ctx.save('engagement_tactic_sequence.json.gz', {'cases': cases, 'tables': [hex(t) for t in tables], 'phases': PHASES,
        'stats': dict(stats), 'presentation': [hex(r) for r in PRESENTATION], 'world': 'world.json.gz'})
    return f'actual active tactic caller sequences {len(cases)}, {dict(stats)}'
