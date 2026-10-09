# Vectors for Wounded: the game's wounded share (rva 0x17D1D0), morale loss (0x17D4F0) and seeded random (0x3FB600)
# on live officers and fuzzed inputs (morale, flags, the force's +0x42, presets, 개성 kind 0x6F, the world's
# counters). Run by run.py; writes wounded.json.gz with three case lists:
#   ratio:  [person, morale, flag, terrain, attacker, hasAttacker, float bits, pokes]
#   morale: [wounded, troops, lead, damage, result, pokes]
#   seeded: [n, div, a, b, c, d, result, pokes]
import random, struct
from context import *

RATIO, MORALE, SEEDED = 0x17D1D0, 0x17D4F0, 0x3FB600
PERSONALITIES = 0x7DD18
COVER = [RATIO, MORALE, SEEDED, 0x232920, 0x32C9E0]


def fbits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    officers, pers = rec[OFFICERS], rec[PERSONALITIES]
    active = [i for i in range(1, len(officers)) if officers[i][0xE6]]
    k6f = [x for x in range(1, 356) if pers[x][0xB6] == 0x6F or pers[x][0xBA] == 0x6F]
    rng = random.Random(71)

    def ratio(person, morale, flag, force, attacker, has, pokes):
        def run():
            pp = ctx.addr(OFFICERS, person) if person is not None else 0
            fp = ctx.addr(TERRAINS, force) if force is not None else 0
            ap = ctx.addr(OFFICERS, attacker) if attacker is not None else 0
            return fbits(o.call(RATIO, pp, morale, flag, fp, stack_args=(ap, has), ret_float=True))
        return ctx.run(pokes, run)

    cases_r = []
    for n in range(6000):
        p = []
        # 0x22CA00 and 0x232920 read officer fields unconditionally. Native battle callers pass the
        # bounded lookup's officer-0 record for an absent ID, never a null officer pointer.
        person = rng.choice(active) if rng.random() < 0.95 else 0
        attacker = rng.choice(active) if rng.random() < 0.7 else None
        has = rng.choice([0, 1, 1])
        force = rng.choice([None, 0, rng.randrange(1, 31), rng.randrange(1, 31)])   # the terrain of the hex (0x178E30 passes it)
        morale = rng.choice([0, 1, 50, 100, 120, 150, 255, -5])
        flag = rng.choice([0, 1])
        r = rng.random
        if force and r() < 0.5: p.append([f"{TERRAINS:#x}", force, 0x42, bytes([rng.choice([0, 1, 4, 7, 10, 11, 40])]).hex()])
        if person is not None and r() < 0.3: p.append([f"{OFFICERS:#x}", person, 0x118 + 2 * rng.randrange(5), struct.pack("<H", rng.choice(k6f)).hex()])
        if attacker is not None and r() < 0.3: p.append([f"{OFFICERS:#x}", attacker, 0x118 + 2 * rng.randrange(5), struct.pack("<H", rng.choice(k6f)).hex()])
        if r() < 0.2: p.append([f"{PERSONALITIES:#x}", rng.choice(k6f), 0xB8, struct.pack("<h", rng.choice([-200, -30, 0, 7, 45, 3000])).hex()])
        if person is not None and r() < 0.2: p.append([f"{OFFICERS:#x}", person, 0xE2, struct.pack("<H", rng.choice([0, 1, 30, 61, 62, 100])).hex()])
        if person is not None and r() < 0.2: p.append([f"{OFFICERS:#x}", person, 0xE0, bytes([rng.choice([0, 1, 0x2E, 0x30, 0x32, 0x33, rng.randrange(52)])]).hex()])
        if r() < 0.3:
            p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
            p.append(["settings", 0, 0x28, struct.pack("<i", rng.choice([0, 2, 3, 4, 5])).hex()])
            p.append(["settings", 0, 0x134, struct.pack("<i", rng.choice([0, 2, 3, 4])).hex()])
        if r() < 0.3: p.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, ctx.officer_force(person) if person else 1, rng.randrange(52)])]).hex()])
        if r() < 0.1: p.append(["exe", "wounded_force1", 0, struct.pack("<i", rng.choice([0, 7, -9])).hex()])
        v = ratio(person, morale, flag, force, attacker, has, p)
        if v is None: raise RuntimeError(f'wounded ratio oracle failed: {ctx.failures[-1]}')
        cases_r.append([person, morale, flag, force, attacker, has, v, p])

    def seeded(n, div, a, b, c, d, pokes):
        return ctx.run(pokes, lambda: o.call32(SEEDED, n, div, a, b, stack_args=(c, d)))

    cases_s = []
    for n in range(8000):
        p = []
        if rng.random() < 0.3: p.append(["worldstate", 0, 0x450, struct.pack("<i", rng.choice([0, 1, 77, 1000, -5, 2**31 - 1])).hex()])
        if rng.random() < 0.3: p.append(["worldstate", 0, 0x45C, struct.pack("<i", rng.choice([0, 1, 12345, -1, 2**31 - 1])).hex()])
        args = (rng.choice([-1, 0, 1, 2, 3, 100, 10000, 2**31 - 1]), rng.choice([-3, 0, 1, 2, 7]),
                rng.choice([0, 1, 500, 65535, -7, 2**31 - 1]), rng.choice([0, 1, 9999, 2**31 - 1]), rng.choice([0, 3]), rng.choice([0, 5]))
        v = seeded(*args, p)
        if v is not None: cases_s.append([*args, v, p])

    def morale(wounded, troops, lead, damage, pokes):
        return ctx.run(pokes, lambda: o.call32(MORALE, wounded, troops, lead, damage))

    cases_m = []
    for n in range(8000):
        p = []
        if rng.random() < 0.3: p.append(["worldstate", 0, 0x450, struct.pack("<i", rng.choice([0, 1, 77, 1000])).hex()])
        if rng.random() < 0.3: p.append(["worldstate", 0, 0x45C, struct.pack("<i", rng.choice([0, 1, 12345])).hex()])
        args = (rng.choice([0, 1, 10, 500, 3000, 20000]), rng.choice([0, 1, 10, 500, 3000, 65535]),
                rng.choice([0, 1, 30, 60, 99, 100, 101, 120, 255]), rng.choice([0, 1, 50, 777, 9999]))
        v = morale(*args, p)
        if v is not None: cases_m.append([*args, v, p])

    ctx.save("wounded.json.gz", {"ratio": cases_r, "morale": cases_m, "seeded": cases_s, "world": "world.json.gz"})
    return f"ratio {len(cases_r)}, morale {len(cases_m)}, seeded {len(cases_s)}"
