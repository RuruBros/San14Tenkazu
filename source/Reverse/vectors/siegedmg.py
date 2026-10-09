# Vectors for Attack.SiegeDamage: the game's siege damage (rva 0x2AA2E0) for live and made-up units with each
# formation, with and without effects, then fuzzed: troops, the 파성 boost +0x4E, the force's +0x193, the
# formation's break value, the commander's 개성 (kinds 8 and 19, always on) and age, the aptitude 정책. Run by
# run.py; writes siegedmg.json.gz: [unit, formation or null, effects, float bits, pokes].
import random, struct
from context import *

FN = 0x2AA2E0
PERSONALITIES = 0x7DD18
COVER = [FN, 0x2B27B0, 0x22E240]


def fbits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, pers = rec[UNITS], rec[OFFICERS], rec[PERSONALITIES]
    rng = random.Random(233)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    live = [i for i in range(len(units)) if units[i][0x10] and u16(units[i], 0x12)]
    empty = [i for i in range(1, len(units)) if i not in live]
    leaders = [k for k in range(1, len(officers)) if officers[k][0xE6] in (2, 3, 4, 5, 6, 7) and ctx.officer_force(k) and u16(officers[k], 0x10) < 0x1771]

    def call(unit, formation, effects, pokes):
        def run():
            return fbits(o.call(FN, ctx.addr(UNITS, unit), ctx.addr(FORMATIONS, formation) if formation is not None else 0, effects, 0, ret_float=True))
        return ctx.run(pokes, run)

    cases = []
    for i in live:
        for f in [None] + list(range(1, 21)):
            for effects in (0, 1):
                r = call(i, f, effects, [])
                if r is not None: cases.append([i, f, effects, r, []])
    natural = len(cases)

    k8 = [x for x in range(1, 356) if pers[x][0xB6] in (8, 19) or pers[x][0xBA] in (8, 19)]
    for n in range(8000):
        i = rng.choice(live) if rng.random() < 0.4 else rng.choice(empty)
        f = rng.choice([None] + list(range(1, 21)))
        effects = rng.choice([0, 1])
        p = []
        cmd = None
        if i in empty:
            cmd = rng.choice(leaders)
            p += [[f"{UNITS:#x}", i, 0x10, bytes([rng.choice([1, 1, 2])]).hex()], [f"{UNITS:#x}", i, 0x12, struct.pack("<H", u16(officers[cmd], 0x10)).hex()],
                  [f"{UNITS:#x}", i, 0x16, struct.pack("<H", rng.choice([0, 100, 3000, 15000, 65535])).hex()], [f"{UNITS:#x}", i, 0x1B, bytes([rng.randrange(1, 21)]).hex()],
                  [f"{UNITS:#x}", i, 0x2A, struct.pack("<H", rng.randrange(0xBD10)).hex()]]
        else:
            cmd = ctx.officer_by_id(u16(units[i], 0x12))
        for _ in range(rng.randint(0, 3)):
            k = rng.randrange(7)
            if k == 0: p.append([f"{UNITS:#x}", i, 0x4E, struct.pack("<b", rng.choice([-10, -3, 0, 2, 10])).hex()])
            elif k == 1 and cmd: p.append([f"{FORCES:#x}", ctx.officer_force(cmd), 0x193, bytes([rng.choice([0, 2, 0xFF])]).hex()])
            elif k == 2 and f: p.append([f"{FORMATIONS:#x}", f, 0x85, bytes([rng.choice([0, 1, 30, 60, 90])]).hex()])
            elif k == 3 and f: p.append(["vec", [f"{FORMATIONS:#x}", f], [0xA8, 0], ""])
            elif k == 4 and cmd:
                x = rng.choice(k8)
                p.append([f"{OFFICERS:#x}", cmd, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                p.append([f"{PERSONALITIES:#x}", x, 0xB4, struct.pack("<h", rng.choice([-1, -1, 0])).hex()])
                p.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-30, -5, 3, 12, 50])).hex()])
            elif k == 5 and cmd: p.append([f"{OFFICERS:#x}", cmd, 0x1C0 - 0x1C0 + 0x163, bytes([rng.choice([0, 1])]).hex()])
            elif k == 6 and cmd: p.append([f"{OFFICERS:#x}", cmd, 0xE8 - 0xE8 + 0x14D, bytes([rng.choice([30, 60, 70, 90])]).hex()])
        r = call(i, f, effects, p)
        if r is not None: cases.append([i, f, effects, r, p])

    ctx.save("siegedmg.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
