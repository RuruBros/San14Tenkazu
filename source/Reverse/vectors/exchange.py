# Vectors for Exchange.Judge: the game's verdict on an exchange proposal (rva 0x324300, kind 0x13, the debug
# lines "交換成否判定"): force A's envoy offers force B gold, food or an item for gold, food or an item of B's.
# Live force pairs and envoys, then fuzzed: the amounts, B's base gold and food and troops, the relation byte,
# the envoy's ability, the items' owners and grades, the player. Run by run.py; writes exchange.json.gz:
# [A, envoy, B, [gold, food, item] given, [gold, food, item] asked, verdict, pokes].
import random, struct
from context import *

FN = 0x324300
CITIES = 0xDD38
COVER = [FN, 0x3222B0, 0x22E8C0, 0x22E970]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    officers, forces, items, cities = rec[OFFICERS], rec[FORCES], rec[ITEMS], rec[CITIES]
    rng = random.Random(229)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    real = [f for f in range(1, 0x33) if u16(forces[f], 0x10) and ctx.officer_by_id(u16(forces[f], 0x10))]
    active = [i for i in range(1, len(officers)) if officers[i][0xE6] and ctx.officer_force(i)]
    by_force = {}
    for k in active: by_force.setdefault(ctx.officer_force(k), []).append(k)
    owned_by = {}
    for item in ctx.owned: owned_by.setdefault(u16(items[item], 0x4A), []).append(item)

    def judge(a, envoy, b, give, ask, pokes):
        def run():
            r = o.call(FN, ctx.addr(FORCES, a), ctx.addr(OFFICERS, envoy), ctx.addr(FORCES, b), 0x13,
                       stack_args=(give[0], give[1], give[2], 0, 0, 0, 0, 0, 0, ask[0], ask[1], ask[2]))
            return r & 0xFF
        return ctx.run(pokes, run)

    def offer(f):
        kind = rng.randrange(3)
        if kind == 0: return [rng.choice([100, 1000, 5000, 20000]), 0, 0]
        if kind == 1: return [0, rng.choice([1000, 10000, 50000]), 0]
        ruler = ctx.officer_by_id(u16(forces[f], 0x10))
        own = owned_by.get(u16(officers[ruler], 0x10), []) if ruler else []
        return [0, 0, rng.choice(own) if own and rng.random() < 0.7 else rng.randrange(1, 202)]

    cases = []
    for a in real:
        for b in rng.sample(real, min(5, len(real))):
            if a == b or a not in by_force: continue
            envoy = rng.choice(by_force[a])
            give, ask = offer(a), offer(b)
            r = judge(a, envoy, b, give, ask, [])
            if r is not None: cases.append([a, envoy, b, give, ask, r, []])
    natural = len(cases)

    for n in range(7000):
        a, b = rng.sample(real, 2)
        if a not in by_force: continue
        envoy = rng.choice(by_force[a])
        give, ask = offer(a), offer(b)
        ruler_b = ctx.officer_by_id(u16(forces[b], 0x10))
        base_b = u16(officers[ruler_b], 0xE2) if ruler_b else 0
        p = []
        for _ in range(rng.randint(1, 3)):
            k = rng.randrange(8)
            if k == 0: p.append([f"{FORCES:#x}", a, 0x13 + b, bytes([rng.choice([0, 20, 60, 120, 200])]).hex()])
            elif k == 1 and 1 <= base_b <= 0x33: p.append([f"{CITIES:#x}", base_b, rng.choice([0x34, 0x38]), struct.pack("<i", rng.choice([0, 1500, 2100, 30000, 300000])).hex()])
            elif k == 2 and 1 <= base_b <= 0x33: p.append([f"{CITIES:#x}", base_b, 0x3C, struct.pack("<i", rng.choice([0, 5000, 50000])).hex()])
            elif k == 3: p.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, a, b, rng.randrange(52)])]).hex()])
            elif k == 4 and ask[2]: p.append([f"{ITEMS:#x}", ask[2], 0x49, bytes([rng.choice([0, 5, 30, 50, 90])]).hex()])
            elif k == 5 and give[2]: p.append([f"{ITEMS:#x}", give[2], 0x49, bytes([rng.choice([0, 5, 30, 50, 90])]).hex()])
            elif k == 6 and give[2] and ruler_b: p.append([f"{ITEMS:#x}", give[2], 0x4A, struct.pack("<H", rng.choice([0, u16(officers[envoy], 0x10)])).hex()])
            elif k == 7 and ask[2]: p.append([f"{ITEMS:#x}", ask[2], 0x45, bytes([rng.randrange(6)]).hex()])
        r = judge(a, envoy, b, give, ask, p)
        if r is not None: cases.append([a, envoy, b, give, ask, r, p])

    ctx.save("exchange.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
