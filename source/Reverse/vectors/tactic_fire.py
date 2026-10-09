# Native fire immunity and terrain gates, timer writes, source policy, and tactic result entries.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, PERSONALITIES, encoded
from effect_oracle import EffectOracle

COVER = [0x2E39E0, 0x2EDD40, 0x177BD0, 0x16E570]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x2E39E0)
    native = EffectOracle(ctx)
    # These two callbacks create/remove fire particles after the gameplay timer has been written.
    o.stub(0x18ACF0, 0)
    o.stub(0x18C270, 0)
    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 52)] + [[None, [hex(GATES), i]] for i in range(1, 11)] + [[None, None], [0, None]]
    places = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    checks, factors, cases = [], [], []
    def poke(t, i, off, b): return [hex(t), i, off, b.hex()]
    def target_pokes(h, traits=False):
        terrain = rec[TILES][h][0x10]
        p = [poke(TILES, h, 0x16, struct.pack('<H', rng.choice([0, 0, 0, 1, 720, 65535]))),
             poke(TERRAINS, terrain, 0x2A, struct.pack('<h', rng.choice([0, 1, 100]))),
             poke(TERRAINS, terrain, 0x41, bytes([rng.randrange(2)])), poke(TERRAINS, terrain, 0x2C, bytes([rng.choice([0, 2])]))]
        if traits:
            u, b = ctx.u16(TILES, h, 0x1C), ctx.u16(TILES, h, 0x1A)
            person = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12)) if 0 < u <= 500 else ctx.officer_by_id(ctx.u16(CITIES, b, 0x2E)) if 0 < b <= 51 else ctx.officer_by_id(ctx.u16(GATES, b - 51, 0x2E)) if 52 <= b <= 61 else 0
            if person:
                p += [poke(OFFICERS, person, 0x118, struct.pack('<5H', 256, 0, 0, 0, 0)),
                      poke(PERSONALITIES, 256, 0xB4, struct.pack('<hBBhBBh', rng.choice([-1, 0]), 25, 0, 1, 0, 0, 0))]
        return p

    for n in range(3000):
        h = rng.choice(places) if n % 2 else rng.randrange(48400)
        duration = rng.choice([-1, 0, 1, 120, 720, 721, 10000])
        p = target_pokes(h, n % 3 == 0)
        def run():
            o.call(0x2E39E0, ctx.addr(TILES, h), duration, 1, read_after=[(ctx.addr(TILES, h), 32)])
            return o.after[0].hex()
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'fire timer oracle failed: {ctx.failures[-1]}')
        checks.append([h, duration, result, p])
    print(f'  fire timer: {len(checks)}', flush=True)

    for n in range(1000):
        t, source, kind = rng.randrange(1, 201), rng.choice(sources), rng.randrange(1, 101)
        def run():
            native.setup(t, source, 0)
            return struct.pack('<f', o.call(0x16E570, native.info, kind, ret_float=True)).hex()
        result = ctx.run([], run)
        if result is None: raise RuntimeError(f'fire policy oracle failed: {ctx.failures[-1]}')
        factors.append([source, kind, result])

    for n in range(3000):
        t, source, slot, links = rng.randrange(1, 201), rng.choice(sources), rng.randrange(2), rng.randrange(8)
        targets = rng.choices(places + [rng.randrange(48400)], k=rng.randrange(1, 5))
        center = rng.choice(targets + [48400])
        p = [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [17, 17], [1, 1], [rng.randrange(256), rng.randrange(256)], ability=rng.randrange(3)).hex()]]
        if source[0] is not None: p.append(poke(UNITS, source[0], 0x21, bytes([rng.randrange(2), rng.randrange(2)])))
        for h in set(targets): p += target_pokes(h, n % 4 == 0)
        result = native.apply(0x177BD0, t, source, slot, links, targets, p, center=center, include_tiles=True)
        if result is None: raise RuntimeError(f'tactic fire oracle failed: {ctx.failures[-1]}')
        cases.append([t, source, slot, links, center, targets, *result, p])
        if (n + 1) % 1000 == 0: print(f'  tactic fire: {n + 1}/3000', flush=True)
    ctx.save('tactic_fire.json.gz', {'ignite': checks, 'policy': factors, 'apply': cases, 'world': 'world.json.gz'})
    return f'fire timer {len(checks)}, source policy {len(factors)}, applications {len(cases)}'
