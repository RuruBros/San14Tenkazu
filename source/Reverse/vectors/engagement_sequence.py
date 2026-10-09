# Execute the entire native caller for consecutive ordinary unit engagements.
# Tactics are blocked by real cooldown inputs; all eight phases, experience and step end execute natively.
# Only the two presentation callbacks are stubbed. Destruction, siege and mission-active cases are separate.
import random, struct
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8
from context import *

COUNTIES = 0x6CAF0
PHASES = [0x174BC0, 0x16A030, 0x16AB50, 0x16A990, 0x16A780, 0x16FDD0, 0x1749A0, 0x174E30]
COVER = [0x169C50, *PHASES, 0x178E30, 0x2B8EC0, 0x2571F0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x169C50)
    for rva in [0x1A3A00, 0x1A3C50]: o.stub(rva, 0)
    manager, sources, records = o.alloc(0x100), o.alloc(3 * 64), o.alloc(3 * 64)
    leaders = {f: [i for i in range(1, len(rec[OFFICERS])) if rec[OFFICERS][i][0xE6] in [2, 3, 4, 6, 7]
                  and ctx.officer_force(i) == f and ctx.u16(OFFICERS, i, 0x10) < 6001] for f in [3, 6]}
    if not all(leaders.values()): raise RuntimeError('Both standing forces need valid commanders')
    tables = [UNITS, OFFICERS, COUNTIES, FORCES]
    starts = {t: ctx.addr(t, 0) for t in tables}
    for t in tables:
        size, count = world.TABLES[t][1:]
        if ctx.addr(t, count - 1) != starts[t] + size * (count - 1): raise RuntimeError('Noncontiguous observed table')
    traces, frame_ptrs, hooks = {}, {}, []
    def capture(mu, address, size, data):
        stage = address - ctx.g.exe
        node = mu.reg_read(UC_X86_REG_RCX if stage in PHASES[1:5] else UC_X86_REG_RDX)
        i = (node - records) // 64
        if not 0 <= i < 3 or node != records + i * 64: raise RuntimeError('Unexpected engagement record')
        entry = traces.setdefault(i, {'stages': []})
        entry['stages'].append(stage)
        if stage == 0x16FDD0:
            frame = mu.reg_read(UC_X86_REG_R8)
            values = struct.unpack('<8I', o.read(frame, 32))
            entry['calculation'] = [values[k] for k in [0, 1, 2, 5, 6, 7]] + [struct.unpack('<H', o.read(frame + 32, 2))[0]]
        if stage == 0x1749A0: frame_ptrs[i] = mu.reg_read(UC_X86_REG_R8)
        if stage == 0x174E30:
            entry['frame'] = list(struct.unpack('<HHI', o.read(frame_ptrs[i] + 32, 8)))
    for stage in PHASES:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, capture, begin=ctx.g.exe + stage, end=ctx.g.exe + stage))
    def poke(t, i, off, data): return [hex(t), i, off, data.hex()]
    cases = []
    try:
        for n in range(600):
            commanders = [rng.choice(leaders[f]) for f in [3, 6]]
            positions = [20000, 20001]
            troops = [rng.choice([9000, 15000, 20000]) for _ in range(2)]
            sides = [[400 + k, 27, positions[k], [3, 6][k], rng.choice([0, 1, 2, 3])] for k in range(2)]
            sequence = [[sides[0], sides[1]], [sides[1], sides[0]], [sides[0], sides[1]]]
            p = [['worldstate', 0, 0x3A, b'\0'.hex()], ['worldstate', 0, 0x165C, b'\0'.hex()],
                 ['exe', 'challenge_active', 0, struct.pack('<i', 0).hex()],
                 ['listdata', '0x70', [], ''], ['listdata', '0x140', [], ''],
                 ['settings', 0, 0xE58, struct.pack('<i', 1).hex()],
                 ['settings', 0, 0xCC, struct.pack('<i', n % 3 == 0).hex()]]
            # Both hexes share county 59. Repeated losses cross development thresholds and change its owner panel.
            p += [poke(COUNTIES, 59, 0x3A, struct.pack('<H', positions[0])),
                  poke(COUNTIES, 59, 0x3C, struct.pack('<3H', *rng.choices([0, 1, 99, 100, 101, 300, 600, 1000], k=3))),
                  poke(COUNTIES, 59, 0x52, struct.pack('<HB', rng.choice([5000, 10000, 60000]), rng.choice([0, 1, 50, 100]))),
                  poke(TILES, positions[0], 0x14, b'\3'),
                  poke(FORCES, 3, 0x1BE, struct.pack('<h', rng.choice([-1000, 0, 1000])))]
            for k, f in enumerate([3, 6]):
                unit, cmd, pos = 400 + k, commanders[k], positions[k]
                p += [poke(UNITS, unit, 0x10, b'\1'), poke(UNITS, unit, 0x12, struct.pack('<H', ctx.u16(OFFICERS, cmd, 0x10))),
                      poke(UNITS, unit, 0x16, struct.pack('<HHBB', troops[k], rng.choice([0, 100, 1000]), 100, rng.randrange(1, 21))),
                      poke(UNITS, unit, 0x20, b'\1'), poke(UNITS, unit, 0x23, b'\0'), poke(UNITS, unit, 0x25, b'\0'),
                      poke(UNITS, unit, 0x2A, struct.pack('<H', pos)), poke(TILES, pos, 0x1C, struct.pack('<H', unit)),
                      poke(FORCES, f, 0xEA, bytes(52)), poke(FORCES, f, 0x194, b'\0'), poke(FORCES, f, 0x15F, b'\0'),
                      poke(FORCES, f, 0x13 + [6, 3][k], bytes([rng.choice([0, 15, 16, 100, 200])]))]
            if n % 10 == 0: p.append(poke(UNITS, 401, 0x10, b'\0'))
            def run():
                traces.clear(); frame_ptrs.clear()
                o.poke(manager, bytes(0x100)); o.poke(manager + 0xA0, struct.pack('<Q', records))
                for i, (a, b) in enumerate(sequence):
                    src, node = sources + i * 64, records + i * 64
                    o.poke(src, bytes(64)); o.poke(src, struct.pack('<H2xI4xIIB3x', *a))
                    o.poke(src + 10, struct.pack('<H', troops[a[0] - 400]))
                    o.poke(node, bytes(64)); o.poke(node, struct.pack('<Q', src))
                    o.poke(node + 8, struct.pack('<H2xI4xIIB3x', *b))
                    o.poke(node + 18, struct.pack('<H', troops[b[0] - 400]))
                    o.poke(node + 48, struct.pack('<Q', records + (i + 1) * 64 if i < 2 else 0))
                before = {t: o.read(starts[t], world.TABLES[t][1] * world.TABLES[t][2]) for t in tables}
                changes = []
                def inspect():
                    for t in tables:
                        size, count = world.TABLES[t][1:]
                        after = o.read(starts[t], size * count)
                        for i in range(count):
                            part = after[size * i:size * (i + 1)]
                            if part != before[t][size * i:size * (i + 1)]: changes.append([hex(t), i, part.hex()])
                    for i in [400, 401]:
                        if o.read(ctx.addr(UNITS, i) + 0x10, 1) != b'\0' and struct.unpack('<H', o.read(ctx.addr(UNITS, i) + 0x16, 2))[0] == 0:
                            raise RuntimeError('This fixture must stay on the nonlethal ordinary engagement path')
                    return []
                result = o.call32(0x169C50, manager, read_after=inspect)
                return [result, [traces.get(i, {'stages': []}) for i in range(3)], changes]
            result = ctx.run(p, run)
            if result is None: raise RuntimeError(f'Native sequence failed in case {n}: {ctx.failures[-1]}')
            cases.append([sequence, *result, p])
            if (n + 1) % 100 == 0: print(f'  actual engagement sequences {n + 1}/600', flush=True)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    ctx.save('engagement_sequence.json.gz', {'cases': cases, 'tables': [hex(t) for t in tables],
        'phases': PHASES, 'presentation': ['0x1a3a00', '0x1a3c50'], 'world': 'world.json.gz'})
    return f'full caller {len(cases)} sequences / {3 * len(cases)} records, consecutive table changes'
