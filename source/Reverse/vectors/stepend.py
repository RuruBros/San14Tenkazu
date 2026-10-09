# Vectors for StepEnd.Apply: the game's end-of-step losses (rva 0x174E30) — the defender's county's development,
# order and population, and the forces' relation — for engagement records built in scratch from live units and
# bases, then fuzzed: the governor's 개성 0x7D, the attacker's 주의 panel 0x30, the county's values and centre hex,
# the relation bytes. Pairs with the player's force or a tribe (46..50) are left out (the game tells the player and
# runs tribe events there). Run by run.py; writes stepend.json.gz:
# [[index, kind, hex, force] a, [index, kind, hex, force] b, [agri, comm, barr, pop, order, relAB, relBA, ownerPanel], pokes].
import random, struct
from context import *

FN = 0x174E30
COUNTIES, CITIES, GATES, BUILDINGS, PERSONALITIES = 0x6CAF0, 0xDD38, 0xDED8, 0x6DA98, 0x7DD18
COVER = [FN, 0x2700A0, 0x2586E0, 0x2AD610, 0x22C810, 0x240B20, 0x2410C0, 0x241AA0, 0x17A0D0, 0x924B0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, tiles, counties, buildings, pers = rec[UNITS], rec[OFFICERS], rec[TILES], rec[COUNTIES], rec[BUILDINGS], rec[PERSONALITIES]
    rng = random.Random(191)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    player = bytes.fromhex(ctx.snap["worldstate"])[0x3A]
    live = [i for i in range(len(units)) if units[i][0x10] and u16(units[i], 0x12)]

    def unit_force(i):
        return ctx.officer_force(ctx.officer_by_id(u16(units[i], 0x12)))

    def ok_force(f):
        return 1 <= f <= 0x32 and f != player and not (0x2E <= f <= 0x32)

    sides = [(i, 0x1B, u16(units[i], 0x2A), unit_force(i)) for i in live if ok_force(unit_force(i))]
    for c in range(1, 0x34):
        b = buildings[c]
        if u16(b, 0x10) < 0xBD10 and ok_force(b[0x13]): sides.append((c, 5, u16(b, 0x10), b[0x13]))
    for g in range(1, 11):
        b = buildings[0x33 + g]
        if u16(b, 0x10) < 0xBD10 and ok_force(b[0x13]): sides.append((g, 6, u16(b, 0x10), b[0x13]))

    node = o.alloc(0x100)
    side_a = o.alloc(0x100)

    def county_of(hex_):
        c = u16(tiles[hex_], 0x12)
        return c if c < 0x1F5 else 0

    def owner_of(county):
        # as the copies stand now (a poke may have moved the centre hex)
        t = u16(o.read(ctx.addr(COUNTIES, county), 0x40), 0x3A)
        f = o.read(ctx.addr(TILES, t if t < 0xBD10 else 0), 0x20)[0x14]
        return f if f < 0x34 else 0

    def apply(a, b, pokes):
        def run():
            o.poke(side_a, struct.pack("<HxxIIII", a[0], a[1], 0, a[2], a[3]))
            o.poke(node, struct.pack("<QHxxIIII", side_a, b[0], b[1], 0, b[2], b[3]))
            cty = county_of(b[2])
            cp, fa, fb = ctx.addr(COUNTIES, cty), ctx.addr(FORCES, a[3]), ctx.addr(FORCES, b[3])
            own = ctx.addr(FORCES, owner_of(cty))
            o.call(FN, 0, node, read_after=[(cp, 0x98), (fa, 0x268), (fb, 0x268), (own, 0x268)])
            c, ra, rb, ow = o.after
            return [u16(c, 0x3C), u16(c, 0x3E), u16(c, 0x40), u16(c, 0x52), c[0x54], ra[0x13 + b[3]], rb[0x13 + a[3]],
                    struct.unpack_from("<h", ow, 0x1BE)[0]]
        return ctx.run(pokes, run)

    cases = []
    for a in sides:
        for b in rng.sample(sides, min(12, len(sides))):
            if a[3] == b[3]: continue
            r = apply(a, b, [])
            if r is not None: cases.append([list(a), list(b), r, []])
    natural = len(cases)

    k7d = [x for x in range(1, 356) if pers[x][0xB6] == 0x7D or pers[x][0xBA] == 0x7D]
    for n in range(6000):
        a, b = rng.choice(sides), rng.choice(sides)
        if a[3] == b[3]: continue
        cty = county_of(b[2])
        p = []
        for _ in range(rng.randint(1, 4)):
            k = rng.randrange(10)
            if k == 0 and cty:
                gov = u16(counties[cty], 0x36)
                g = ctx.officer_by_id(gov) if gov else 0
                if g:
                    x = rng.choice(k7d)
                    p.append([f"{OFFICERS:#x}", g, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                    p.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-5, 0, 1, 100])).hex()])
                else:
                    p.append([f"{COUNTIES:#x}", cty, 0x36, struct.pack("<H", rng.choice([0, u16(officers[rng.choice(live)], 0x10) if live else 0])).hex()])
            elif k == 1: p.append([f"{FORCES:#x}", a[3], 0x1CB + rng.randrange(10), bytes([rng.randrange(256)]).hex()])
            elif k == 2 and cty: p.append([f"{COUNTIES:#x}", cty, rng.choice([0x3C, 0x3E, 0x40]), struct.pack("<H", rng.choice([0, 1, 99, 100, 101, 300, 599, 600, 999, 1000])).hex()])
            elif k == 3 and cty: p.append([f"{COUNTIES:#x}", cty, 0x52, struct.pack("<H", rng.choice([0, 100, 5000, 5060, 30000, 60000, 65000])).hex()])
            elif k == 4 and cty: p.append([f"{COUNTIES:#x}", cty, 0x54, bytes([rng.choice([0, 1, 10, 50, 100])]).hex()])
            elif k == 5 and cty: p.append([f"{COUNTIES:#x}", cty, 0x3A, struct.pack("<H", rng.choice([0, u16(counties[cty], 0x3A), b[2]])).hex()])
            elif k == 6: p.append([f"{FORCES:#x}", a[3], 0x13 + b[3], bytes([rng.choice([0, 5, 15, 16, 30, 100, 200])]).hex()])
            elif k == 7: p.append([f"{FORCES:#x}", b[3], 0x13 + a[3], bytes([rng.choice([0, 1, 29, 50, 199, 200])]).hex()])
            elif k == 8 and cty:
                own = owner_of(cty)
                if own: p.append([f"{FORCES:#x}", own, 0x1BE, struct.pack("<h", rng.choice([-1000, -999, 0, 999, 1000])).hex()])
            elif k == 9 and a[1] == 0x1B:
                cmd = ctx.officer_by_id(u16(units[a[0]], 0x12))
                if cmd: p.append([f"{OFFICERS:#x}", cmd, 0xE2, struct.pack("<H", rng.choice([0, 1, 40, 70])).hex()])
        if not p: continue
        r = apply(a, b, p)
        if r is not None: cases.append([list(a), list(b), r, p])

    ctx.save("stepend.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
