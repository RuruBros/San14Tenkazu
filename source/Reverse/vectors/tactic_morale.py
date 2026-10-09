# Native morale damage, including real route queue insertion; no gameplay functions are stubbed.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, PERSONALITIES, encoded
from effect_oracle import EffectOracle

COVER = [0x177900, 0x270550, 0x241690, 0x31EAB0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x177900)
    native = EffectOracle(ctx)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 46)] + [[None, [hex(GATES), i]] for i in range(1, 11)]
    places = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    cases = []
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    for n in range(3000):
        t, source, slot, links = rng.randrange(1, 201), rng.choice(sources), rng.randrange(2), rng.randrange(8)
        targets = rng.choices(places, k=rng.randrange(1, 5))
        p = [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [16, 16], [1, 1], [rng.randrange(256), rng.randrange(256)], ability=rng.randrange(3)).hex()],
             ['listdata', '0x140', [], ''], ['listdata', '0x70', [], '']]
        if source[0] is not None: p.append(put(UNITS, source[0], 0x21, bytes([rng.randrange(2), rng.randrange(2)])))
        for h in set(targets):
            u = ctx.u16(TILES, h, 0x1C)
            b = ctx.u16(TILES, h, 0x1A)
            if 0 < u <= 500:
                p += [put(UNITS, u, 0x1A, bytes([rng.choice([0, 1, 10, 100, 200, 255])]))]
                person = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))
                if person and n % 3 == 0:
                    p += [put(OFFICERS, person, 0x118, struct.pack('<5H', 256, 0, 0, 0, 0)),
                          put(PERSONALITIES, 256, 0xB4, struct.pack('<hBBhBBh', -1, 13, 0, rng.choice([-100, -1, 1, 50, 100, 300]), 0, 0, 0))]
            if 1 <= b <= 61:
                p += [put(CITIES if b < 52 else GATES, b if b < 52 else b - 51, 0x44, bytes([rng.choice([0, 1, 50, 100, 200, 255])]))]
        state = []
        def prepare(effect):
            manager = o.call(0x16D960)
            o.poke(manager + 0x98, bytes(4))
            state.append(manager)
        def inspect(effect):
            routes = world.list_items(o, ctx.raw, ctx.g.exe, 0x140, world.LISTS[0x140])
            flag = struct.unpack('<I', o.read(state[0] + 0x98, 4))[0]
            state.append([routes, flag])
        result = native.apply(0x177900, t, source, slot, links, targets, p, prepare=prepare, inspect=inspect)
        if result is None: raise RuntimeError(f'tactic morale oracle failed: {ctx.failures[-1]}')
        cases.append([t, source, slot, links, targets, *result, state[1], p])
        if (n + 1) % 1000 == 0: print(f'  tactic morale: {n + 1}/3000', flush=True)
    ctx.save('tactic_morale.json.gz', {'apply': cases, 'world': 'world.json.gz'})
    return f'morale applications {len(cases)}'
