# Boost merge and the twelve tactic boost effects, compared to complete native gameplay writes.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, encoded
from effect_oracle import EffectOracle

COVER = [0x2EC3D0, 0x176580, 0x1768F0, 0x16B790, 0x16D7B0, 0x16E790]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x2EC3D0)
    native = EffectOracle(ctx)
    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and struct.unpack_from('<H', rec[UNITS][i], 0x12)[0]]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 52)] + [[None, [hex(GATES), i]] for i in range(1, 11)] + [[None, None], [0, None]]
    places = [struct.unpack_from('<H', rec[UNITS][i], 0x2A)[0] for i in live] + [struct.unpack_from('<H', rec[BUILDINGS][i], 0x10)[0] for i in range(1, 62)]
    states, factors, cases = [], [], []
    def poke(t, i, off, b): return [hex(t), i, off, b.hex()]
    def vector(t, b): return ['vecdata', [hex(TACTICS), t], 0x70, b.hex()]

    for n in range(5000):
        unit, stat = rng.choice(live + [0]), rng.randrange(6)
        level, time = rng.choice([-255, -20, -10, -1, 0, 1, 10, 20, 255]), rng.choice([0, 1, 240, 241, 1000, 65535, -1, -2147483648])
        p = [poke(UNITS, unit, 0x4A + stat, bytes([rng.choice([0, 1, 10, 127, 128, 246, 255])])),
             poke(UNITS, unit, 0x50 + stat, bytes([rng.choice([0, 1, 240, 255])]))]
        def run():
            o.call(0x2EC3D0, ctx.addr(UNITS, unit), stat, level, time, stack_args=[0, 255, 0], read_after=[(ctx.addr(UNITS, unit), 512)])
            return o.after[0].hex()
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'boost merge oracle failed: {ctx.failures[-1]}')
        states.append([unit, stat, level, time, result, p])
    print(f'  boost merge: {len(states)} cases', flush=True)

    for n in range(1500):
        t, source, target = rng.randrange(1, 201), rng.choice(sources), rng.choice(sources)
        ability = rng.choice([0, 1, 2, 255])
        p = [vector(t, encoded(t, [5, 10], [1, 1], ability=ability))]
        if n % 3 == 0: p = [vector(t, b''), poke(TACTICS, t, 0x6A, bytes([ability]))]
        if target[1] and n % 4 == 0: p.append(poke(int(target[1][0], 16), target[1][1], 0x2E, struct.pack('<H', rng.choice([0, 6000, 65535]))))
        def run():
            native.setup(t, source, 0)
            tu, tb = target
            unit = ctx.addr(UNITS, tu) if tu is not None else 0
            base = ctx.addr(int(tb[0], 16), tb[1]) if tb else 0
            return [struct.pack('<f', o.call(0x16B790, native.info, unit, base, ret_float=True)).hex(),
                    o.call32(0x16D7B0, native.info), o.call32(0x16E790, native.info, unit, base)]
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'attack factor oracle failed: {ctx.failures[-1]}')
        factors.append([t, source, target, result, p])

    kinds = list(range(5, 15)) + [21, 22]
    for n in range(4800):
        t, source, kind, slot, links = rng.randrange(1, 201), rng.choice(sources), kinds[n % 12], rng.randrange(2), rng.randrange(8)
        levels = [rng.choice([0, 1, 2, 10, 11, 255]), rng.choice([0, 1, 2, 10, 11, 255])]
        p = [vector(t, encoded(t, [kind, kind], [1, 1], [rng.randrange(256), rng.randrange(256)], levels, ability=rng.randrange(3))),
             poke(TACTICS, t, 0x40, bytes([rng.choice([0, 1, 3, 4])]))]
        if source[0] is not None: p.append(poke(UNITS, source[0], 0x21, bytes([rng.randrange(2), rng.randrange(2)])))
        targets = rng.choices(places + [0, 48399], k=rng.randrange(5))
        for h in set(targets):
            u = struct.unpack_from('<h', rec[TILES][h], 0x1C)[0]
            if not 0 < u <= 500: continue
            p += [poke(UNITS, u, 0x4A, bytes(rng.choice([0, 1, 2, 10, 246, 254, 255]) for _ in range(5))),
                  poke(UNITS, u, 0x50, bytes(rng.choice([0, 1, 120, 240, 255]) for _ in range(5)))]
        positive = kind in range(5, 10) or kind == 21
        stat = kind - 5 if kind in range(5, 10) else kind - 10 if kind in range(10, 15) else None
        fn = 0x176580 if stat is not None else 0x1768F0
        result = native.apply(fn, t, source, slot, links, targets, p, arg4=stat if stat is not None else int(positive), stack_args=[int(positive)] if stat is not None else [])
        if result is None: raise RuntimeError(f'tactic boost oracle failed: {ctx.failures[-1]}')
        cases.append([t, source, slot, links, kind, targets, *result, p])
        if (n + 1) % 1200 == 0: print(f'  tactic boost: {n + 1}/4800', flush=True)
    ctx.save('tactic_boost.json.gz', {'merge': states, 'factors': factors, 'apply': cases, 'world': 'world.json.gz'})
    return f'merge {len(states)}, attack factors {len(factors)}, boost applications {len(cases)}'
