# Vectors for Capture.Chance: the game's capture roll (rva 0x27E420) for an officer a caught by an officer b, on
# the live officers (commanders in the field, officers in cities and gates) and then fuzzed: b's unit formation,
# a's standing and 개성, a friend's 개성 0x36 nearby, an item, the escape routes (units around, their relations),
# options, presets, sliders and the world's counter. Run by run.py; writes capture.json.gz:
# [a, b, result, pokes] (officer indexes).
import random, struct
from context import *

FN = 0x27E420
PERSONALITIES, CITIES, GATES, BUILDINGS = 0x7DD18, 0xDD38, 0xDED8, 0x6DA98
COVER = [FN, 0x22A9D0, 0x22D580, 0x22A070, 0x2345C0, 0x232400, 0x2B4750, 0x2CA3E0, 0x22C3B0, 0x333240]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, pers, items, tiles = rec[UNITS], rec[OFFICERS], rec[PERSONALITIES], rec[ITEMS], rec[TILES]
    rng = random.Random(149)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    live = [i for i in range(len(units)) if units[i][0x10] and u16(units[i], 0x12)]
    commanders = [ctx.officer_by_id(u16(units[i], 0x12)) for i in range(len(units)) if i in live]
    commanders = [k for k in commanders if k]
    active = [i for i in range(1, len(officers)) if officers[i][0xE6] and ctx.officer_force(i)]
    based = [i for i in active if 1 <= u16(officers[i], 0xE4) <= 0x3D]

    def chance(a, b, pokes):
        return ctx.run(pokes, lambda: o.call(FN, ctx.addr(OFFICERS, a), ctx.addr(OFFICERS, b)) & 0xFF)

    cases = []
    for a in commanders + rng.sample(based, min(150, len(based))):
        for b in rng.sample(commanders, min(6, len(commanders))) + rng.sample(active, 2):
            r = chance(a, b, [])
            if r is not None: cases.append([a, b, r, []])
    natural = len(cases)

    k36 = [x for x in range(1, 356) if pers[x][0xB6] == 0x36 or pers[x][0xBA] == 0x36]
    k39 = [x for x in range(1, 356) if pers[x][0xB6] == 0x39 or pers[x][0xBA] == 0x39]
    owned = {}
    for item in ctx.owned:
        owned.setdefault(u16(items[item], 0x4A), []).append(item)

    def unit_of(k):
        at = u16(officers[k], 0xE4)
        i = at - 0x3D if 0x3E <= at < 0x3E + 500 else 0
        return i if i and u16(units[i], 0x12) == u16(officers[k], 0x10) else 0

    for n in range(9000):
        a = rng.choice(commanders + based)
        b = rng.choice(commanders + active)
        p = []
        ua, ub = unit_of(a), unit_of(b)
        for _ in range(rng.randint(1, 4)):
            k = rng.randrange(14)
            if k == 0 and ub: p.append([f"{UNITS:#x}", ub, 0x1B, bytes([rng.choice([3, 4, 1, 7])]).hex()])
            elif k == 1 and ua: p.append([f"{UNITS:#x}", ua, 0x1B, bytes([rng.choice([3, 4, 1, 7])]).hex()])
            elif k == 2: p.append([f"{OFFICERS:#x}", a, 0xE6, bytes([rng.choice([1, 2, 3, 4])]).hex()])
            elif k == 3:
                p.append(["settings", 0, 0x108, struct.pack("<i", rng.choice([0, 1, 2])).hex()])
            elif k == 4:
                p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
                p.append(["settings", 0, 0x28, struct.pack("<i", rng.choice([0, 1, 2, 3, 4, 5])).hex()])
            elif k == 5:
                p.append(["settings", 0, 8 + rng.choice([0x3CC, 0x3E4]), struct.pack("<i", rng.choice([0, 50, 100, 150, 300])).hex()])
            elif k == 6: p.append(["worldstate", 0, 0x38, bytes([rng.choice([0, 1, 7, 255])]).hex()])
            elif k == 7:
                x = rng.choice(k36)
                p.append([f"{OFFICERS:#x}", a, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                p.append([f"{PERSONALITIES:#x}", x, 0xB4, struct.pack("<h", rng.choice([-1, 0, 5])).hex()])
            elif k == 8:
                x = rng.choice(k39)
                p.append([f"{OFFICERS:#x}", b, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                p.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-30, 10, 50, 300])).hex()])
            elif k == 9 and ua:
                # a friend's commander near a with a 0x36 of condition 9
                others = [i for i in live if i != ua and ctx.officer_force(ctx.officer_by_id(u16(units[i], 0x12))) == ctx.officer_force(a)]
                if others:
                    c = ctx.officer_by_id(u16(units[rng.choice(others)], 0x12))
                    x = rng.choice(k36)
                    p.append([f"{OFFICERS:#x}", c, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                    p.append([f"{PERSONALITIES:#x}", x, 0xA4, bytes([rng.choice([9, 9, 2, 0])]).hex()])
                    p.append([f"{PERSONALITIES:#x}", x, 0xA8, struct.pack("<i", rng.choice([0, 1, 2, 5])).hex()])
            elif k == 10 and owned.get(u16(officers[a], 0x10)):
                p.append([f"{ITEMS:#x}", rng.choice(owned[u16(officers[a], 0x10)]), 0x47, bytes([rng.choice([0, 1, 2])]).hex()])
            elif k == 11 and ua:
                # a unit of some force next to a (an escape route or not)
                h = u16(units[ua], 0x2A)
                near = [t for t in (h - 1, h + 1, h - 220, h + 220, h - 221, h + 221) if 0 <= t < 0xBD10]
                t = rng.choice(near)
                p.append([f"{TILES:#x}", t, 0x1C, struct.pack("<h", rng.choice([0] + live)).hex()])
            elif k == 12: p.append(["exe", "capture_extra", 0, struct.pack("<i", rng.choice([0, 1, 3])).hex()])
            elif k == 13:
                fa, fb = rng.randrange(1, 0x34), rng.randrange(1, 0x34)
                p.append([f"{FORCES:#x}", fa, 0xEA + fb, bytes([rng.choice([0, 1, 0x80])]).hex()])
        if not p: continue
        r = chance(a, b, p)
        if r is not None: cases.append([a, b, r, p])

    ctx.save("capture.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
