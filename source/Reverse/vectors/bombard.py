# Vectors for Bombard.Apply: the game's siege step (rva 0x29E840) on the standing buildings (cities, gates and the
# rest) hit by live units or nobody, with the damage, durability, the unit's distance, the force's 주의 panel 0x45,
# the commander's 개성 kind 0x67, the runtime extras and the exempt list fuzzed. The calls that follow a fall or a
# visual change (capture flow, events, messages, experience) are stubbed out. Run by run.py; writes
# bombard.json.gz: [building, damage, unit or null, durability after, fell, pokes].
import random, struct
from context import *

FN = 0x29E840
BUILDINGS, PERSONALITIES = 0x6DA98, 0x7DD18
COVER = [FN, 0x1F42B0, 0x233590]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, buildings, pers = rec[UNITS], rec[OFFICERS], rec[BUILDINGS], rec[PERSONALITIES]
    rng = random.Random(211)
    for rva, value in ((0x2E3050, 0), (0x2571F0, 0), (0x294B90, 0), (0x31EAB0, 1), (0x1B4990, 0), (0x2B8EC0, 0)):
        o.stub(rva, value)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    live = [i for i in range(len(units)) if units[i][0x10] and u16(units[i], 0x12)]
    standing = [i for i in range(1, len(buildings)) if u16(buildings[i], 0x10) < 0xBD10 and buildings[i][0x12]]

    def hit(b, damage, unit, pokes):
        def run():
            bp = ctx.addr(BUILDINGS, b)
            r = o.call(FN, bp, damage & 0xFFFFFFFF, ctx.addr(UNITS, unit) if unit is not None else 0, read_after=[(bp, 0x20)])
            return [u16(o.after[0], 0x14), r & 0xFF]
        return ctx.run(pokes, run)

    cases = []
    for b in rng.sample(standing, min(400, len(standing))) + list(range(1, 0x3E)):
        for unit in (None, rng.choice(live)):
            for damage in (0, 1, 50, 999):
                r = hit(b, damage, unit, [])
                if r is not None: cases.append([b, damage, unit, r[0], r[1], []])
    natural = len(cases)

    k67 = [x for x in range(1, 356) if pers[x][0xB6] == 0x67 or pers[x][0xBA] == 0x67]
    for n in range(7000):
        b = rng.choice(standing if rng.random() < 0.5 else list(range(1, 0x3E)))
        unit = rng.choice(live) if rng.random() < 0.85 else None
        damage = rng.choice([-5, 0, 1, 7, 100, 999, 5000, 20000])
        p = []
        for _ in range(rng.randint(1, 3)):
            k = rng.randrange(7)
            if k == 0: p.append([f"{BUILDINGS:#x}", b, 0x14, struct.pack("<H", rng.choice([0, 1, 2, 50, 1000, 9998, 9999, 12000])).hex()])
            elif k == 1 and unit is not None:
                h = u16(buildings[b], 0x10)
                near = [t for t in (h, h - 1, h + 1, h - 220, h + 220, h - 2, h + 2, h - 440, h + 440, h + 221, h - 221) if 0 <= t < 0xBD10]
                p.append([f"{UNITS:#x}", unit, 0x2A, struct.pack("<H", rng.choice(near)).hex()])
            elif k == 2 and unit is not None:
                p.append([f"{FORCES:#x}", ctx.officer_force(ctx.officer_by_id(u16(units[unit], 0x12))), 0x1CB + rng.randrange(10), bytes([rng.randrange(256)]).hex()])
            elif k == 3 and unit is not None:
                cmd = ctx.officer_by_id(u16(units[unit], 0x12))
                x = rng.choice(k67)
                p.append([f"{OFFICERS:#x}", cmd, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                p.append([f"{PERSONALITIES:#x}", x, 0xB4, struct.pack("<h", -1).hex()])
                p.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-50, -1, 1, 30, 300]))
                          .hex()])
            elif k == 4: p.append(["exe", rng.choice(["bombard_fx_pos", "bombard_fx_neg"]), 0, struct.pack("<i", rng.choice([0, 3, 100])).hex()])
            elif k == 5 and rng.random() < 0.3: p.append(["list", "0xf0", [[f"{BUILDINGS:#x}", b]], ""])
            elif k == 6: p.append([f"{BUILDINGS:#x}", b, 0x12, bytes([rng.choice([1, 2, 5, 9, 13, 20, 30])]).hex()])
        if not p: continue
        r = hit(b, damage, unit, p)
        if r is not None: cases.append([b, damage, unit, r[0], r[1], p])

    ctx.save("bombard.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
