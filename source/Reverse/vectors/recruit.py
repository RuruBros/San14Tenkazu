# Vectors for Recruit.Chance: the game's recruitment roll (rva 0x2784B0) for target/recruiter pairs among the live
# officers (free, prisoners, in service), with the game's own roll or a given one, then fuzzed: the target's +0xE8,
# +0x16C, +0x162 and status, the family and sworn ties, likes and hates, 상성, the recruiter's charm 개성 0x3F, the
# presets and the player. Run by run.py; writes recruit.json.gz: [target, recruiter, roll in, accepts, roll out, pokes].
import random, struct
from context import *

FN = 0x2784B0
PERSONALITIES = 0x7DD18
COVER = [FN, 0x2B5620, 0x2A9830, 0x2B2D20, 0x232EE0, 0x2C8A70, 0x2C3D50, 0x229550]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    officers, pers = rec[OFFICERS], rec[PERSONALITIES]
    rng = random.Random(227)
    out = o.alloc(0x100)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    active = [i for i in range(1, len(officers)) if officers[i][0xE6] and ctx.officer_force(i)]
    free = [i for i in range(1, len(officers)) if officers[i][0xE6] in (5, 6)]
    targets = free + rng.sample(active, min(300, len(active)))

    def chance(t, r, roll, pokes):
        def run():
            o.poke(out, struct.pack("<i", roll))
            res = o.call(FN, ctx.addr(OFFICERS, t), ctx.addr(OFFICERS, r), out) & 0xFF
            return [res, struct.unpack("<i", o.read(out, 4))[0]]
        return ctx.run(pokes, run)

    cases = []
    for t in targets:
        for r in rng.sample(active, 4):
            for roll in (-1, rng.randrange(100)):
                res = chance(t, r, roll, [])
                if res is not None: cases.append([t, r, roll, res[0], res[1], []])
    natural = len(cases)

    k3f = [x for x in range(1, 356) if pers[x][0xB6] == 0x3F or pers[x][0xBA] == 0x3F]
    for n in range(8000):
        t, r = rng.choice(targets + active), rng.choice(active)
        roll = rng.choice([-1, -1, rng.randrange(100)])
        p = []
        tid, rid = u16(officers[t], 0x10), u16(officers[r], 0x10)
        rforce = ctx.officer_force(r)
        ruler = ctx.byid[u16(rec[FORCES][rforce], 0x10)] if rforce and u16(rec[FORCES][rforce], 0x10) < len(ctx.byid) else 0
        for _ in range(rng.randint(1, 4)):
            k = rng.randrange(13)
            if k == 0: p.append([f"{OFFICERS:#x}", t, 0xE8, bytes([rng.choice([0, 30, 60, 70, 90, 99, 100, 120])]).hex()])
            elif k == 1: p.append([f"{OFFICERS:#x}", t, 0x16C, struct.pack("<h", rng.choice([-25, -20, -15, -10, -5, 0, 30, 100, 300])).hex()])
            elif k == 2: p.append([f"{OFFICERS:#x}", t, 0x162, struct.pack("<h", rng.choice([0, u16(officers[ruler], 0x10) if ruler else 0, rid])).hex()])
            elif k == 3: p.append([f"{OFFICERS:#x}", t, 0xE6, bytes([rng.choice([1, 2, 3, 4, 5, 6])]).hex()])
            elif k == 4: p.append([f"{OFFICERS:#x}", t, rng.choice([0xBA, 0xB4, 0xB6, 0xBC]), struct.pack("<H", rng.choice([0, rid, u16(officers[ruler], 0x10) if ruler else 0, u16(officers[rng.choice(active)], 0x10), tid])).hex()])
            elif k == 5: p.append([f"{OFFICERS:#x}", t, rng.choice([0xC0, 0xD0]) + 2 * rng.randrange(8), struct.pack("<H", rng.choice([rid, u16(officers[ruler], 0x10) if ruler else 0, 0])).hex()])
            elif k == 6: p.append([f"{OFFICERS:#x}", rng.choice([t, r]), 0xBE, bytes([rng.choice([0, 37, 75, 112, 149])]).hex()])
            elif k == 7:
                x = rng.choice(k3f)
                p.append([f"{OFFICERS:#x}", r, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                p.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-50, 10, 100])).hex()])
            elif k == 8:
                p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
                p.append(["settings", 0, rng.choice([0x28, 0x134]), struct.pack("<i", rng.choice([0, 1, 2, 3])).hex()])
            elif k == 9: p.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, rforce, rng.randrange(52)])]).hex()])
            elif k == 10: p.append(["exe", "recruit_preset2", 0, struct.pack("<i", rng.choice([0, 7, 20])).hex()])
            elif k == 11: p.append([f"{OFFICERS:#x}", t, 0xE0, bytes([rng.choice([0, rec[OFFICERS][r][0xE0], 0x33])]).hex()])
            elif k == 12: p.append([f"{OFFICERS:#x}", rng.choice(active), 0xB4, struct.pack("<H", tid).hex()])
        res = chance(t, r, roll, p)
        if res is not None: cases.append([t, r, roll, res[0], res[1], p])

    ctx.save("recruit.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
