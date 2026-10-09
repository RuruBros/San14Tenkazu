# The complete native kind-7 verdict, plus the geographic and troop-count prerequisites.
import random, struct
from context import *

CITIES, GATES, BUILDINGS = 0xDD38, 0xDED8, 0x6DA98
COVER = [0x324300, 0x22F2D0, 0x2C74E0, 0x2AD2B0, 0x2B2D20, 0x32CBE0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x324307)
    forces = [i for i in range(1, 46) if ctx.u16(FORCES, i, 0x10) and ctx.officer_by_id(ctx.u16(FORCES, i, 0x10))]
    envoys = [i for i in range(1, len(rec[OFFICERS])) if rec[OFFICERS][i][0xE6] and ctx.officer_force(i)]
    cases, helpers = [], []
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    def verdict(a, envoy, b, kind, p):
        def run(): return o.call(0x324300, ctx.addr(FORCES, a), ctx.addr(OFFICERS, envoy), ctx.addr(FORCES, b), kind, stack_args=(0,) * 12) & 255
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'diplomacy oracle failed: {ctx.failures[-1]}')
        cases.append([a, envoy, b, kind, result, p])

    for n in range(200):
        a, b = rng.sample(forces, 2)
        verdict(a, rng.choice(envoys), b, rng.choice([6, 7, 0, 18, 20]), [])

    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    for n in range(4000):
        a, b, third = rng.sample(forces, 3)
        envoy = rng.choice(envoys)
        ruler_a, ruler_b = ctx.officer_by_id(ctx.u16(FORCES, a, 0x10)), ctx.officer_by_id(ctx.u16(FORCES, b, 0x10))
        ca, cb = rng.choice([1, 3, 4, 5, 8, 16, 30]), rng.choice([1, 1, 2, 3, 4, 5, 6])
        selected = rng.sample(range(1, 46), ca + cb)
        cities_a, cities_b = selected[:ca], selected[ca:]
        p = [put(BUILDINGS, i, 0x13, bytes([a if i in cities_a else b if i in cities_b else third])) for i in range(1, 46)]
        p += [put(CITIES, i, 0x3C, struct.pack('<i', rng.choice([1, 500, 5000, 30000, 200000]) if i in cities_b else rng.choice([10000, 100000, 200000]))) for i in selected]
        p += [put(CITIES, cities_a[0], 0x58, bytes([cities_b[0] if n % 7 else 0]) + bytes(9)),
              put(OFFICERS, ruler_b, 0xE2, struct.pack('<H', cities_b[0])),
              ['worldstate', 0, 0x450, struct.pack('<i', rng.choice([0, 360, 361, 500, 10000])).hex()],
              put(FORCES, a, 0x13 + b, bytes([rng.choice([0, 40, 120, 200, 255])])),
              put(OFFICERS, envoy, 0xEF, bytes([rng.choice([1, 50, 100, 200, 255])]))]
        if n % 4 == 0:
            p += [put(OFFICERS, ruler_b, 0xD0, struct.pack('<8H', ctx.u16(OFFICERS, ruler_a, 0x10) if n % 8 else ctx.u16(OFFICERS, envoy, 0x10), *([0] * 7)))]
        elif n % 4 == 1:
            p += [put(OFFICERS, ruler_b, 0xD0, bytes(16))]
            if n % 8 == 1: p += [put(OFFICERS, envoy, 0xBA, struct.pack('<H', ctx.u16(OFFICERS, ruler_b, 0x10)))]
            else: p += [put(OFFICERS, envoy, 0xBC, struct.pack('<H', 33)), put(OFFICERS, ruler_b, 0xBC, struct.pack('<H', 33))]
        else:
            p += [put(OFFICERS, ruler_b, 0xD0, bytes(16)), put(OFFICERS, ruler_b, 0xC0, struct.pack('<8H', ctx.u16(OFFICERS, envoy, 0x10), *([0] * 7)))]
        if n % 9 == 0: p += [['listdata', '0xc0', [[[hex(FORCES), a]], [[hex(FORCES), b]]], '']]
        if n % 6 == 0:
            p += [['worldstate', 0, 0x1654, struct.pack('<H', ctx.u16(OFFICERS, envoy, 0x10)).hex()],
                  put(OFFICERS, envoy, 0xE4, struct.pack('<H', cities_b[0] if n % 12 else cities_a[0]))]
        if n % 10 in (1, 2):
            p += [put(CITIES, cities_a[0], 0x58, bytes([52]) + bytes(9)),
                  put(GATES, 1, 0x58, bytes([53 if n % 10 == 2 else cities_b[0]]) + bytes(9)),
                  put(GATES, 2, 0x58, bytes([cities_b[0]]) + bytes(9))]
        if n % 3 == 0:
            from tactic_targets import PERSONALITIES
            p += [put(OFFICERS, envoy, 0x118, struct.pack('<5H', 256, 0, 0, 0, 0)),
                  put(PERSONALITIES, 256, 0xB4, struct.pack('<hBBhBBh', -1, 0x40, 0, rng.choice([-100, -1, 1, 50, 100, 300]), 0, 0, 0))]
        if n % 5 == 0:
            unit = rng.choice(live)
            cmd = ctx.officer_by_id(ctx.u16(UNITS, unit, 0x12))
            corps = rec[OFFICERS][cmd][0xE0]
            tile = ctx.u16(BUILDINGS, cities_b[0], 0x10)
            p += [put(CORPS, corps, 0x10, bytes([a])), put(UNITS, unit, 0x2A, struct.pack('<H', tile)),
                  put(UNITS, unit, 0x16, struct.pack('<H', rng.choice([1, 1000, 10000, 65535]))),
                  ['listdata', '0x60', [[[hex(UNITS), unit]]], ''], ['listdata', '0x140', [], ''], ['listdata', '0x70', [], '']]
        verdict(a, envoy, b, 7, p)
        if n < 800:
            def run():
                adjacent = o.call(0x2C74E0, ctx.addr(FORCES, a), ctx.addr(FORCES, b)) & 255
                troops = o.call32(0x2AD2B0, ctx.addr(FORCES, a), 1, 0)
                return [adjacent, troops]
            result = ctx.run(p, run)
            if result is None: raise RuntimeError(f'diplomacy prerequisite failed: {ctx.failures[-1]}')
            helpers.append([a, b, *result, p])
        if (n + 1) % 1000 == 0: print(f'  diplomacy kind 7: {n + 1}/4000', flush=True)
    ctx.save('diplomacy.json.gz', {'cases': cases, 'prerequisites': helpers, 'world': 'world.json.gz'})
    return f'verdicts {len(cases)}, prerequisites {len(helpers)}'
