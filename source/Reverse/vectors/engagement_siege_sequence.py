# Run consecutive native siege, base retaliation and tactic phases without resetting their state.
# Presentation alone is replaced; nonlethal inputs keep capture/destruction consumers outside this fixture.
import collections, random, struct, sys
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, encoded
from engagement_sequence import COUNTIES, PHASES
from engagement_tactic_sequence import TacticManagerOracle, PRESENTATION
from battlepower_fx import ring

COVER = [0x169C50, *PHASES, 0x2AA2E0, 0x29E840, 0x2E3050, 0x2B8EC0,
         0x172E20, 0x177580, 0x178E30, 0x496700, 0x515330]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    limit = int(next((a.split('=', 1)[1] for a in sys.argv if a.startswith('--limit=')), 600))
    rng = random.Random(0x174BC029)
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation: o.stub(rva, 0)
    tactic = TacticManagerOracle(ctx)
    manager, sources, records = o.alloc(0x100), o.alloc(3 * 64), o.alloc(3 * 64)
    leaders = {f: [i for i in range(1, len(rec[OFFICERS])) if rec[OFFICERS][i][0xE6] in [2, 3, 4, 6, 7]
                  and ctx.officer_force(i) == f and ctx.u16(OFFICERS, i, 0x10) < 6001] for f in [3, 6]}
    corps6 = next(i for i in range(1, len(rec[CORPS])) if rec[CORPS][i][0x10] == 6 and rec[CORPS][i][0x11])
    tables = [UNITS, OFFICERS, COUNTIES, FORCES, CITIES, GATES, BUILDINGS, TILES]
    starts = {t: ctx.addr(t, 0) for t in tables}
    for t in tables:
        size, count = world.TABLES[t][1:]
        if ctx.addr(t, count - 1) != starts[t] + size * (count - 1): raise RuntimeError('Noncontiguous observed table')
    traces, frames, hooks, current = {}, {}, [], [None]
    target = [None]

    def capture(mu, address, size, data):
        stage = address - ctx.g.exe
        if stage not in PHASES:
            entry = traces[current[0]]
            if stage == 0x172E20: entry['flow_called'] = True
            elif stage == 0x16B880: entry['candidates'] = [tactic.tactics[p] for p in tactic.items(mu.reg_read(UC_X86_REG_RDX))]
            elif entry['stages'][-1] == PHASES[0]:
                building = mu.reg_read(UC_X86_REG_RCX)
                entry['siege_call'] = [(building - starts[BUILDINGS]) // world.TABLES[BUILDINGS][1],
                                       mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF]
            return
        node = mu.reg_read(UC_X86_REG_RCX if stage in PHASES[1:5] else UC_X86_REG_RDX)
        i = (node - records) // 64
        if not 0 <= i < 3 or node != records + i * 64: raise RuntimeError('Unexpected engagement record')
        current[0] = i; entry = traces.setdefault(i, {'stages': [], 'flow_called': False, 'candidates': []})
        entry['stages'].append(stage)
        if stage in [PHASES[1], PHASES[-1]] and 'start_state' not in entry:
            kind, index, bid = target[0]
            entry['start_step'] = 2 if stage == PHASES[1] else 8
            entry['start_state'] = [struct.unpack('<H', o.read(ctx.addr(BUILDINGS, bid) + 0x14, 2))[0],
                                    o.read(ctx.addr(CITIES if kind == 5 else GATES, index) + 0x66, 1)[0] if kind in [5, 6] else None]
        if stage == 0x16FDD0:
            frame = mu.reg_read(UC_X86_REG_R8); values = struct.unpack('<8I', o.read(frame, 32))
            entry['calculation'] = [values[k] for k in [0, 1, 2, 5, 6, 7]] + [struct.unpack('<H', o.read(frame + 32, 2))[0]]
        if stage == 0x1749A0:
            frames[i] = mu.reg_read(UC_X86_REG_R8)
            entry['tactic_frame'] = list(struct.unpack('<HHI', o.read(frames[i] + 32, 8)))
            if entry['flow_called']: entry['flow'] = tactic.inspect(entry['candidates'])
        if stage == 0x174E30 and i in frames: entry['frame'] = list(struct.unpack('<HHI', o.read(frames[i] + 32, 8)))

    for stage in [*PHASES, 0x172E20, 0x16B880, 0x29E840]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, capture, begin=ctx.g.exe + stage, end=ctx.g.exe + stage))
    def poke(t, i, at, value): return [hex(t), i, at, value.hex()]
    cases, stats = [], collections.Counter()
    try:
        for n in range(limit):
            kind = [5, 6, 11][n % 3]
            index = rng.choice([5, 7]) if kind == 5 else rng.choice([1, 2, 10]) if kind == 6 else 1000
            bid = index + 51 if kind == 6 else index
            target[0] = [kind, index, bid]
            challenge, far = n % 2 != 0, n % 4 == 0
            protected = kind == 5 and index == 5 and challenge and n % 5 == 0
            h = 20000; distance = (2 if kind == 5 else 1) + int(far)
            positions = [h + distance, h + distance + 220]
            commanders, governor = rng.sample(leaders[3], 2), rng.choice(leaders[6])
            formation = 3 if far else rng.choice([1, 2, 5, 9, 10, 11, 13])
            a = [400, 27, positions[0], 3, rng.choice([0, 1, 2, 3])]
            b = [index, kind, h, 6, rng.choice([0, 1, 2, 3])]
            sequence = [[a, b], [b, a], [a, b]] if kind in [5, 6] else [[a, b]] * 3
            durability = 1 if protected else rng.choice([1499, 1500, 3000, 6000, 9999])
            p = [['worldstate', 0, 0x3A, '00'], ['worldstate', 0, 0x165C, '00'], ['worldstate', 0, 0x165D, '00'],
                 ['exe', 'challenge_active', 0, struct.pack('<i', challenge).hex()],
                 ['exe', 'challenge_flags', 0, '02000000'], ['exe', 'challenge_kind', 0, '03000000'],
                 ['exe', 'challenge_rules', 0, struct.pack('<i', 64 if protected else 0).hex()],
                 ['listdata', '0x70', [], ''], ['listdata', '0x140', [], ''],
                 ['listdata', '0xf0', [[[hex(BUILDINGS), bid]]] if n % 11 == 0 else [], ''],
                 ['settings', 0, 0xE58, '01000000'], ['settings', 0, 0x28, struct.pack('<i', n % 6).hex()],
                 ['settings', 0, 0xB8, '01000000'], ['settings', 0, 0xCC, struct.pack('<i', n % 3 == 0).hex()],
                 poke(COUNTIES, 59, 0x3A, struct.pack('<H', h)),
                 poke(COUNTIES, 59, 0x3C, struct.pack('<3H', *rng.choices([99, 100, 101, 300, 600, 1000], k=3))),
                 poke(COUNTIES, 59, 0x52, struct.pack('<HB', 30000, 100)),
                 poke(BUILDINGS, bid, 0x10, struct.pack('<HBBH', h, rec[BUILDINGS][bid][0x12] if kind != 11 else 9, 6, durability))]
            for cell in ring(h, 5) + [h]:
                p += [poke(TILES, cell, 0x12, struct.pack('<H', 59)), poke(TILES, cell, 0x1A, bytes(4)),
                      poke(TILES, cell, 0x10, b'\1'), poke(TILES, cell, 0x14, b'\6')]
            p.append(poke(TILES, h, 0x1A, struct.pack('<H', bid)))
            for unit, cmd, pos in zip([400, 401], commanders, positions):
                p += [poke(UNITS, unit, 0x10, b'\1'), poke(UNITS, unit, 0x12, struct.pack('<H', ctx.u16(OFFICERS, cmd, 0x10))),
                      poke(UNITS, unit, 0x16, struct.pack('<HHBBB', 20000, 7000, 100, formation, 15)),
                      poke(UNITS, unit, 0x1D, bytes([191, 0, 0])), poke(UNITS, unit, 0x20, bytes([n % 10 == 0, 150, 200])),
                      poke(UNITS, unit, 0x23, bytes(3)), poke(UNITS, unit, 0x2A, struct.pack('<H', pos)),
                      poke(UNITS, unit, 0x152, struct.pack('<H', pos)), poke(UNITS, unit, 0x37, b'\0'),
                      poke(TILES, pos, 0x1C, struct.pack('<H', unit)), poke(TILES, pos, 0x14, b'\3'),
                      poke(OFFICERS, cmd, 0x130, bytes([191]) + bytes(9)), poke(OFFICERS, cmd, 0xE2, bytes(2)),
                      poke(OFFICERS, cmd, 0xE4, struct.pack('<H', unit + 0x3D)), poke(OFFICERS, cmd, 0xBC, struct.pack('<H', 33)),
                      poke(OFFICERS, cmd, 0x15E, struct.pack('<H', ctx.u16(OFFICERS, cmd, 0x15E) & ~0x4000))]
            if kind in [5, 6]:
                table = CITIES if kind == 5 else GATES
                p += [poke(table, index, 0x2E, struct.pack('<H', ctx.u16(OFFICERS, governor, 0x10))),
                      poke(table, index, 0x3C, struct.pack('<iiB', 30000, 7000, 100)),
                      poke(table, index, 0x48, bytes([191 if n % 5 else 0, 0, 0, 0, 0, 0])),
                      poke(table, index, 0x66, bytes([rng.randrange(4)])), poke(table, index, 0x69, b'\0'),
                      poke(OFFICERS, governor, 0x130, bytes([191]) + bytes(9)),
                      poke(OFFICERS, governor, 0xE2, struct.pack('<HH', bid, bid)), poke(OFFICERS, governor, 0x161, b'\0')]
                if kind == 5: p.append(poke(table, index, 0x30, bytes([corps6])))
            for f in [3, 6]:
                p += [poke(FORCES, f, 0xEA, bytes(52)), poke(FORCES, f, 0x194, b'\0'), poke(FORCES, f, 0x15F, b'\0'),
                      poke(FORCES, f, 0x13 + (6 if f == 3 else 3), bytes([rng.choice([0, 15, 100, 200])]))]
            p += [['vecdata', [hex(TACTICS), 191], 0x70, encoded(191, [18, 1], [1, 1], [5, 2], flags=3).hex()],
                  poke(TACTICS, 191, 0x40, b'\1'), poke(TACTICS, 191, 0x42, struct.pack('<H', rng.choice([0, 1, 8, 30]))),
                  poke(TACTICS, 191, 0x6C, struct.pack('<I', 0xFFFFFFFF))]

            def run():
                traces.clear(); frames.clear(); current[0] = None; tactic.reset()
                o.poke(manager, bytes(0x100)); o.poke(manager + 0xA0, struct.pack('<Q', records))
                for i, (source, dest) in enumerate(sequence):
                    src, node = sources + 64 * i, records + 64 * i
                    o.poke(src, bytes(64)); o.poke(src, struct.pack('<H2xI4xIIB3x', *source))
                    o.poke(src + 10, struct.pack('<H', 20000 if source[1] == 27 else 30000))
                    o.poke(node, bytes(64)); o.poke(node, struct.pack('<Q', src))
                    o.poke(node + 8, struct.pack('<H2xI4xIIB3x', *dest))
                    o.poke(node + 18, struct.pack('<H', 20000 if dest[1] == 27 else 30000))
                    o.poke(node + 48, struct.pack('<Q', node + 64 if i < 2 else 0))
                before = {t: o.read(starts[t], world.TABLES[t][1] * world.TABLES[t][2]) for t in tables}
                changed = []
                def inspect():
                    for t in tables:
                        size, count = world.TABLES[t][1:]; after = o.read(starts[t], size * count)
                        for i in range(count):
                            part = after[size * i:size * (i + 1)]
                            if part != before[t][size * i:size * (i + 1)]: changed.append([hex(t), i, part.hex()])
                    if world.list_items(o, ctx.raw, ctx.g.exe, 0x140, world.LISTS[0x140]): raise RuntimeError('Nonlethal siege fixture queued destruction')
                    if struct.unpack('<H', o.read(ctx.addr(BUILDINGS, bid) + 0x14, 2))[0] == 0: raise RuntimeError('Nonlethal siege fixture lost all durability')
                    return []
                result = o.call32(0x169C50, manager, read_after=inspect)
                return [result, [traces.get(i, {'stages': []}) for i in range(3)], changed]
            result = ctx.run(p, run)
            if result is None: raise RuntimeError(f'Native siege sequence failed in case {n}: {ctx.failures[-1]}')
            stats[f'kind_{kind}'] += 1
            for entry in result[1]:
                stats['records'] += 1; stats['admitted'] += bool(entry['stages'])
                stats['siege_calls'] += 'siege_call' in entry; stats['flow_called'] += entry.get('flow_called', False)
                stats['fired'] += entry.get('frame', [0, 0, 0])[2] != 0
                stats['transferred'] += entry.get('frame', [0, 0, 0])[1] != 0
                stats['linked'] += len(entry.get('flow', {}).get('queue', [])) > 1
            cases.append([sequence, *result, p])
            if (n + 1) % 50 == 0: print(f'  native siege sequences {n + 1}/{limit}: {dict(stats)}', flush=True)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    ctx.save('engagement_siege_sequence.json.gz', {'cases': cases, 'tables': [hex(t) for t in tables], 'phases': PHASES,
        'stats': dict(stats), 'presentation': [hex(r) for r in presentation], 'world': 'world.json.gz'})
    return f'actual siege/retaliation caller sequences {len(cases)}, {dict(stats)}'
