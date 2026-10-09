# Challenge troop protection through the real step-7 minimum, guard and troop-application calls.
# Experience, force missions and destruction dispatch are explicit boundaries, not verified here.
import random, struct
from context import *
from tactic_targets import CITIES, GATES, BUILDINGS

COVER = [0x1749A0, 0x178E30, 0x515390]


def generate(ctx):
    o = ctx.o
    rng = random.Random(0x515390)
    boundaries = [0x1B4990, 0x2571F0, 0x174520, 0x1A3A00, 0x1A3C50]
    for rva in boundaries: o.stub(rva, 0)
    side, record, frame = o.alloc(64), o.alloc(64), o.alloc(64)
    live = [i for i in range(1, 501) if ctx.rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    cases = []
    protected = 0
    for n in range(1200):
        kind, index = [(5, 5), (5, 7), (5, 1), (6, 1)][n % 4]
        table, building = (CITIES, index) if kind == 5 else (GATES, index + 51)
        unit = live[n % len(live)]
        person = ctx.officer_by_id(ctx.u16(UNITS, unit, 0x12))
        source = [unit, 27, ctx.u16(UNITS, unit, 0x2A), ctx.officer_force(person), 1]
        troops = rng.choice([0, 1, 2, 50, 1000, 30000])
        basic, extra = rng.choice([(0, 0), (1, 0), (0, 1), (7, 9), (32768, 32767), (65535, 65535)])
        active, rules = (n // 4) % 2, [0, 64, 255][(n // 8) % 3]
        def poke(t, i, off, data): return [hex(t), i, off, data.hex()]
        p = [['exe', 'challenge_active', 0, struct.pack('<i', active).hex()],
             ['exe', 'challenge_rules', 0, struct.pack('<i', rules).hex()],
             poke(table, index, 0x3C, struct.pack('<i', troops)),
             poke(table, index, 0x40, struct.pack('<i', rng.choice([0, 123, 20000]))),
             poke(table, index, 0x44, bytes([rng.choice([0, 1, 50, 100])])),
             poke(table, index, 0x4E, struct.pack('<H', building)),
             poke(table, index, 0x69, b'\0'),
             poke(BUILDINGS, building, 0x12, b'\1'),
             poke(BUILDINGS, building, 0x13, b'\1'),
             ['settings', 0, 0xE58, struct.pack('<i', 1).hex()],
             ['settings', 0, 0xCC, struct.pack('<i', n % 3 == 0).hex()]]
        def run():
            o.poke(side, struct.pack('<H2xI4xIIB3x', *source))
            o.poke(record, bytes(64)); o.poke(record, struct.pack('<Q', side))
            o.poke(record + 8, struct.pack('<H2xI4xIIB3x', index, kind, 0, 1, 1))
            o.poke(frame, bytes(64)); o.poke(frame + 0x20, struct.pack('<HHI', basic, extra, 0))
            ptr = ctx.addr(table, index)
            step = o.call32(0x1749A0, 0, record, frame, read_after=[(ptr, 0x70), (frame, 40)])
            b = o.after[0]
            return [list(struct.unpack_from('<ii', b, 0x3C)) + [b[0x44]],
                    list(struct.unpack_from('<HHI', o.after[1], 0x20)), step]
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'Challenge troop application failed: {ctx.failures[-1]}')
        if kind == 5 and index == 5 and active and rules & 64 and troops > 0 and basic + extra >= troops:
            if result[0][0] != 1: raise RuntimeError('Native protected city did not retain its last troop')
            protected += 1
        cases.append([source, [hex(table), index], [basic, extra, 0], *result, p])
    o.unstub()
    ctx.save('engagement_challenge.json.gz', {'cases': cases, 'protected': protected,
        'boundaries': [hex(rva) for rva in boundaries], 'world': 'world.json.gz'})
    return f'challenge troop application {len(cases)}, protected lethal hits {protected}'
