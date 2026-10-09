# Native siege damage, experience, damage levels and real base-fall queue insertion.
# Capture/destruction handlers and force battle events remain caller-owned engine behavior.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, PERSONALITIES, encoded
from effect_oracle import EffectOracle

COVER = [0x177580, 0x29E840, 0x2AA570, 0x267DB0, 0x2E3050, 0x31EAB0, 0x2B8EC0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x177580)
    native = EffectOracle(ctx)
    # This callback queues a damage-level presentation; 0x2E3050 still performs its gameplay byte write.
    o.stub(0x84DEF0, 0)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 46)] + [[0, None], [None, None]]
    places = [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    capacities, cases = [], []
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    for n in range(2000):
        table, index = (CITIES, rng.randrange(52)) if n % 2 else (GATES, rng.randrange(11))
        building = index if table == CITIES else index + 51
        p = [put(table, index, 0xB8 if table == CITIES else 0x46,
                 bytes([rng.randrange(6)]) if table == CITIES else struct.pack('<H', rng.choice([0, 1, 2000, 9999, 65535]))),
             put(BUILDINGS, building, 0x14, struct.pack('<H', rng.choice([0, 1, 499, 500, 999, 1000, 1499, 1500, 2000, 6000, 9999, 65535]))),
             ['settings', 0, 0xE58, struct.pack('<i', rng.randrange(2)).hex()],
             ['settings', 0, 0x68C, struct.pack('<i', rng.choice([0, 1, 50, 100, 200, -1])).hex()]]
        def run():
            bp = ctx.addr(table, index)
            return [o.call32(0x22C730, bp), o.call32(0x2AA570, bp)]
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'siege capacity oracle failed: {ctx.failures[-1]}')
        capacities.append([hex(table), index, *result, p])
    print(f'  durability capacity/levels: {len(capacities)}', flush=True)

    for n in range(4000):
        t, source, slot, links = rng.randrange(1, 201), rng.choice(sources), rng.randrange(2), rng.randrange(8)
        targets = rng.choices(places, k=rng.randrange(1, 5))
        p = [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [18, 18], [1, 1], [rng.randrange(256), rng.randrange(256)], ability=rng.randrange(3)).hex()],
             ['listdata', '0x140', [], ''], ['listdata', '0xf0', [], ''],
             ['settings', 0, 0xE58, struct.pack('<i', rng.randrange(2)).hex()],
             ['settings', 0, 0xB8, struct.pack('<i', rng.randrange(3)).hex()],
             ['settings', 0, 0x20C, struct.pack('<i', rng.choice([0, 1, 50, 100, 200])).hex()]]
        extra = []
        if source[0] is not None:
            unit = source[0]
            person = ctx.officer_by_id(ctx.u16(UNITS, unit, 0x12))
            extra = [(UNITS, unit), (OFFICERS, person)]
            p.append(put(UNITS, unit, 0x21, bytes([rng.randrange(2), rng.randrange(2)])))
            if targets and unit and n % 2:
                h = targets[0]
                p.append(put(UNITS, unit, 0x2A, struct.pack('<H', max(0, h - rng.choice([0, 1, 2, 3])))))
            if person and n % 3 == 0:
                p += [put(OFFICERS, person, 0x118, struct.pack('<5H', 256, 0, 0, 0, 0)),
                      put(PERSONALITIES, 256, 0xB4, struct.pack('<hBBhBBh', -1, 103, 0, rng.choice([-100, -1, 1, 50, 100, 300]), 0, 0, 0))]
        for h in set(targets):
            b = ctx.u16(TILES, h, 0x1A)
            p += [put(BUILDINGS, b, 0x14, struct.pack('<H', rng.choice([0, 1, 2, 50, 1000, 5000, 9999]))),
                  put(CITIES if b < 52 else GATES, b if b < 52 else b - 51, 0x66, bytes([rng.randrange(4)]))]
            if n % 13 == 0: p.append(['listdata', '0xf0', [[[hex(BUILDINGS), b]]], ''])
        state = []
        def inspect(effect):
            routes = world.list_items(o, ctx.raw, ctx.g.exe, 0x140, world.LISTS[0x140])
            state.append(routes)
        result = native.apply(0x177580, t, source, slot, links, targets, p, inspect=inspect,
                              include_buildings=True, extra_records=extra)
        if result is None: raise RuntimeError(f'tactic siege oracle failed: {ctx.failures[-1]}')
        cases.append([t, source, slot, links, targets, *result, state[0], p])
        if (n + 1) % 1000 == 0: print(f'  tactic siege: {n + 1}/4000', flush=True)
    ctx.save('tactic_siege.json.gz', {'capacity': capacities, 'apply': cases, 'world': 'world.json.gz'})
    return f'durability capacities/levels {len(capacities)}, siege applications {len(cases)}'
