# Vectors for Reach: whether one force may attack another (0x2CA280), what stands on a hex (0x16EA90) and whether
# an attack reaches from one hex to another (0x2CA3E0, which the capture chance and the AI ask), on the live map
# and with the units, bases, relations and flags fuzzed. Run by run.py; writes reach.json.gz:
#   hostile:  [a, b, result, pokes]              force numbers
#   objectat: [hex, kind, number, pokes]         kind 5 city, 6 gate, 0x1B unit, 0xB building, 0xFF none
#   reach:    [from, to, result, pokes]          hexes
import random, struct
from context import *
from battlepower_fx import ring

HOSTILE, OBJECTAT, REACH = 0x2CA280, 0x16EA90, 0x2CA3E0
CITIES, GATES, BUILDINGS, OBJECTKINDS = 0xDD38, 0xDED8, 0x6DA98, 0x73958
COVER = [REACH, HOSTILE, OBJECTAT, 0x2C6CD0, 0x2C6950, 0x22FB10, 0x2AA850, 0x232650, 0x232BD0, 0xEE90, 0x231500]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, tiles, buildings, cities, gates, kinds = rec[UNITS], rec[TILES], rec[BUILDINGS], rec[CITIES], rec[GATES], rec[OBJECTKINDS]
    live = [i for i in range(len(units)) if units[i][0x10] and struct.unpack_from("<H", units[i], 0x12)[0]]
    rng = random.Random(131)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    # --- hostile
    hostile = []
    for a in range(0x36):
        for b in range(0x36):
            r = ctx.run([], lambda: o.call(HOSTILE, a, b) & 0xFF)
            if r is not None: hostile.append([a, b, r, []])
    for n in range(3000):
        a, b = rng.randrange(0x34), rng.randrange(0x34)
        p = []
        for _ in range(rng.randint(1, 3)):
            k = rng.randrange(5)
            if k == 0: p.append([f"{FORCES:#x}", a, 0xEA + b, bytes([rng.choice([0, 1, 0x40, 0x7F, 0x80, 0xFF])]).hex()])
            elif k == 1: p.append([f"{FORCES:#x}", rng.choice([a, b]), 0x194, bytes([rng.choice([a, b, 0, 0x33, 0xFF])]).hex()])
            elif k == 2: p.append([f"{FORCES:#x}", rng.choice([a, b]), 0x12, bytes([rng.choice([0, 2, 3, 0xFD])]).hex()])
            elif k == 3: p.append(["worldstate", 0, 0x165C, bytes([rng.choice([0, 1])]).hex()])
            else: p.append([f"{FORCES:#x}", b, 0xEA + a, bytes([rng.choice([0, 1, 0x80])]).hex()])
        r = ctx.run(p, lambda: o.call(HOSTILE, a, b) & 0xFF)
        if r is not None: hostile.append([a, b, r, p])

    # --- what stands on a hex (outputs through two dwords in scratch)
    out = o.alloc(0x100)

    def object_at(hex_, pokes):
        def run():
            o.poke(out, bytes(16))
            o.call(OBJECTAT, out, out + 8, ctx.addr(TILES, hex_))
            k = struct.unpack("<I", o.read(out, 4))[0]
            n = struct.unpack("<I", o.read(out + 8, 4))[0]
            return [k, n]
        return ctx.run(pokes, run)

    unit_hexes = [u16(units[i], 0x2A) for i in live]
    building_hexes = [u16(buildings[i], 0x10) for i in range(1, len(buildings)) if u16(buildings[i], 0x10) < 0xBD10 and buildings[i][0x12]]
    objectat = []
    for h in unit_hexes + building_hexes + [rng.randrange(0xBD10) for _ in range(500)]:
        r = object_at(h, [])
        if r is not None: objectat.append([h, r[0], r[1], []])
    for n in range(2000):
        h = rng.choice(unit_hexes + building_hexes)
        p = []
        k = rng.randrange(4)
        if k == 0: p.append([f"{TILES:#x}", h, 0x1C, struct.pack("<h", rng.choice([0, rng.choice(live), rng.randrange(1, 501), 501, 600])).hex()])
        elif k == 1: p.append([f"{TILES:#x}", h, 0x1A, struct.pack("<H", rng.choice([0, rng.randrange(1, 0x34), rng.randrange(0x34, 0x3E), rng.randrange(0x3E, 0xBB9), 0xBB9, 0xFFFF])).hex()])
        elif k == 2: p.append([f"{BUILDINGS:#x}", tiles[h][0x1A] if u16(tiles[h], 0x1A) < 0xBB9 else 0, 0x12, bytes([rng.choice([0, 1, 5, 30])]).hex()])
        else:
            b = u16(tiles[h], 0x1A)
            if 1 <= b <= 0x33: p.append([f"{CITIES:#x}", b, 0x69, bytes([rng.choice([0, 1])]).hex()])
            elif 0x34 <= b <= 0x3D: p.append([f"{GATES:#x}", b - 0x33, 0x69, bytes([rng.choice([0, 1])]).hex()])
            else: p.append([f"{TILES:#x}", h, 0x1C, struct.pack("<h", rng.choice(live)).hex()])
        r = object_at(h, p)
        if r is not None: objectat.append([h, r[0], r[1], p])

    # --- reach
    def reach(a, b, pokes):
        return ctx.run(pokes, lambda: o.call(REACH, a, b) & 0xFF)

    city_hexes = [u16(buildings[i], 0x10) for i in range(1, 0x34) if u16(buildings[i], 0x10) < 0xBD10]
    gate_hexes = [u16(buildings[i], 0x10) for i in range(0x34, 0x3E) if u16(buildings[i], 0x10) < 0xBD10]
    other_hexes = [h for h in building_hexes if h not in city_hexes and h not in gate_hexes]
    sources = unit_hexes + city_hexes + gate_hexes + rng.sample(other_hexes, min(40, len(other_hexes)))
    cases = []
    for a in sources:
        for b in [a] + ring(a, 4):
            r = reach(a, b, [])
            if r is not None: cases.append([a, b, r, []])
    natural = len(cases)

    def unit_at(h):
        i = struct.unpack_from("<h", tiles[h], 0x1C)[0]
        return i if 1 <= i <= 500 else None

    for n in range(8000):
        a = rng.choice(sources)
        b = rng.choice([a] + ring(a, 4))
        p = []
        ua, ub = unit_at(a), unit_at(b)
        ba, bb = u16(tiles[a], 0x1A), u16(tiles[b], 0x1A)
        for _ in range(rng.randint(1, 3)):
            k = rng.randrange(9)
            if k == 0 and ua: p.append([f"{UNITS:#x}", ua, rng.choice([0x23, 0x25]), bytes([rng.choice([0, 0, 5])]).hex()])
            elif k == 1 and ua:
                p.append([f"{UNITS:#x}", ua, 0x25, bytes([3]).hex()])
                p.append([f"{UNITS:#x}", ua, 0x3C, bytes([rng.choice([0x1B, 5, 6, 0xB, 0xFF])]).hex()])
                p.append([f"{UNITS:#x}", ua, 0x3E, struct.pack("<H", rng.choice([ub or 0, bb, bb - 0x33 if bb >= 0x34 else 0, 0])).hex()])
            elif k == 2 and ua: p.append([f"{UNITS:#x}", ua, 0x16, struct.pack("<H", rng.choice([0, 1, 500])).hex()])
            elif k == 3 and 1 <= ba <= 0x3D:
                t, i = (CITIES, ba) if ba <= 0x33 else (GATES, ba - 0x33)
                sub = rng.randrange(3)
                if sub == 0: p.append([f"{t:#x}", i, 0x49, bytes([rng.choice([0, 1])]).hex()])
                elif sub == 1:
                    p.append([f"{t:#x}", i, 0x4B, bytes([1]).hex()])
                    p.append([f"{t:#x}", i, 0x62, bytes([rng.choice([0x1B, 5, 6, 0xB, 0xFF])]).hex()])
                    p.append([f"{t:#x}", i, 0x64, struct.pack("<H", rng.choice([ub or 0, bb, bb - 0x33 if bb >= 0x34 else 0, 0])).hex()])
                else: p.append([f"{t:#x}", i, 0x3C, struct.pack("<i", rng.choice([0, 1, 5000])).hex()])
            elif k == 4:
                fa = rng.randrange(0x34); fb = rng.randrange(0x34)
                p.append([f"{FORCES:#x}", fa, 0xEA + fb, bytes([rng.choice([0, 1, 0x80])]).hex()])
            elif k == 5 and ua: p.append([f"{FORCES:#x}", ctx.officer_force(ctx.officer_by_id(u16(units[ua], 0x12))), 0x193, bytes([rng.choice([0, 0x80, 0xFF])]).hex()])
            elif k == 6 and ua: p.append([f"{UNITS:#x}", ua, rng.choice([0x1B, 0x1C]), bytes([rng.randrange(1, 21)]).hex()])
            elif k == 7: p.append([f"{TILES:#x}", b, 0x1C, struct.pack("<h", rng.choice([0, rng.choice(live)])).hex()])
            elif k == 8 and bb and bb < 0xBB9: p.append([f"{BUILDINGS:#x}", bb, 0x13, bytes([rng.randrange(0x34)]).hex()])
        if not p: continue
        r = reach(a, b, p)
        if r is not None: cases.append([a, b, r, p])

    ctx.save("reach.json.gz", {"hostile": hostile, "objectat": objectat, "reach": cases, "world": "world.json.gz"})
    return f"hostile {len(hostile)}, objectat {len(objectat)}, reach {natural} natural + {len(cases) - natural} fuzzed"
