# Vectors for Injury.Apply: the game's injury roll (rva 0x27D4B0) on the live officers, by mode and with an
# attacking commander or none, then fuzzed: the state, the +0x15E bits, the options and presets, the death slider,
# the world's counter, 개성 0x35 (own and received), 0x52 on the attacker and 0x2F on the own unit, the ruler. The
# death itself and the messages are stubbed; a stub on 0x295AA0 records the death. Run by run.py; writes
# injury.json.gz: [person, mode, attacker or null, state after, died, pokes].
import random, struct
from context import *

FN = 0x27D4B0
PERSONALITIES = 0x7DD18
COVER = [FN, 0x2C89D0, 0x233650, 0x3EAAB0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, pers = rec[UNITS], rec[OFFICERS], rec[PERSONALITIES]
    rng = random.Random(223)
    died = [False]

    def death():
        died[0] = True
        return 1
    for rva, value in ((0x167CB0, 0), (0x16ECE0, 0), (0x368360, 0), (0x1680B0, 0), (0x59E4C0, 0), (0x233960, 0), (0x1B4990, 0)):
        o.stub(rva, value)
    o.stub(0x295AA0, fn=death)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    live = [i for i in range(len(units)) if units[i][0x10] and u16(units[i], 0x12)]
    commanders = [k for k in (ctx.officer_by_id(u16(units[i], 0x12)) for i in live) if k]
    active = [i for i in range(1, len(officers)) if officers[i][0xE6] and ctx.officer_force(i)]

    def roll(p, mode, attacker, pokes):
        def run():
            died[0] = False
            pp = ctx.addr(OFFICERS, p)
            o.call(FN, pp, mode, ctx.addr(OFFICERS, attacker) if attacker is not None else 0, 0, read_after=[(pp, 0x1F0)])
            return [o.after[0][0x161], int(died[0])]
        return ctx.run(pokes, run)

    cases = []
    for p in commanders + rng.sample(active, min(300, len(active))):
        for mode in (1, 2, 3):
            attacker = rng.choice(commanders) if rng.random() < 0.7 else None
            r = roll(p, mode, attacker, [])
            if r is not None: cases.append([p, mode, attacker, r[0], r[1], []])
    natural = len(cases)

    k35 = [x for x in range(1, 356) if pers[x][0xB6] == 0x35 or pers[x][0xBA] == 0x35]
    k52 = [x for x in range(1, 356) if pers[x][0xB6] == 0x52 or pers[x][0xBA] == 0x52]
    k2f = [x for x in range(1, 356) if pers[x][0xB6] == 0x2F or pers[x][0xBA] == 0x2F]

    def unit_of(k):
        at = u16(officers[k], 0xE4)
        i = at - 0x3D if 0x3E <= at < 0x3E + 500 else 0
        return i if i and u16(units[i], 0x12) == u16(officers[k], 0x10) else 0

    for n in range(8000):
        p = rng.choice(commanders + active[:200])
        mode = rng.choice([1, 1, 1, 2, 3])
        attacker = rng.choice(commanders) if rng.random() < 0.7 else None
        pk = []
        for _ in range(rng.randint(1, 4)):
            c = rng.randrange(12)
            if c == 0: pk.append([f"{OFFICERS:#x}", p, 0x161, bytes([rng.choice([0, 1, 2, 2, 3])]).hex()])
            elif c == 1: pk.append([f"{OFFICERS:#x}", p, 0x15E, struct.pack("<H", u16(officers[p], 0x15E) ^ rng.choice([0x400, 0x4000])).hex()])
            elif c == 2:
                pk.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
                pk.append(["settings", 0, 0x30, struct.pack("<i", rng.choice([0, 1, 2, 3])).hex()])
                pk.append(["settings", 0, rng.choice([0x28, 0x134]), struct.pack("<i", rng.choice([0, 2, 3, 4])).hex()])
            elif c == 3: pk.append(["settings", 0, rng.choice([0x2B4, 0xD0]), struct.pack("<i", rng.choice([0, 0, 1])).hex()])
            elif c == 4: pk.append(["settings", 0, 8 + 0x1A4, struct.pack("<i", rng.choice([0, 50, 100, 300])).hex()])
            elif c == 5: pk.append(["worldstate", 0, 0x38, bytes([rng.choice([0, 3, 90, 200])]).hex()])
            elif c == 6:
                x = rng.choice(k35)
                pk.append([f"{OFFICERS:#x}", p, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                pk.append([f"{PERSONALITIES:#x}", x, 0xA4, bytes([rng.choice([0, 9, 2])]).hex()])
            elif c == 7 and unit_of(p): pk.append([f"{UNITS:#x}", unit_of(p), 0x64 + 2 * rng.randrange(36), struct.pack("<H", rng.choice(k35)).hex()])
            elif c == 8 and attacker is not None:
                x = rng.choice(k52)
                pk.append([f"{OFFICERS:#x}", attacker, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                pk.append([f"{PERSONALITIES:#x}", x, 0xB4, struct.pack("<h", -1).hex()])
                pk.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-5, 0, 1, 5, 40])).hex()])
            elif c == 9 and unit_of(p):
                x = rng.choice(k2f)
                pk.append([f"{OFFICERS:#x}", p, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                pk.append([f"{PERSONALITIES:#x}", x, 0xB4, struct.pack("<h", -1).hex()])
                pk.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-20, 0, 10, 60])).hex()])
            elif c == 10: pk.append([f"{OFFICERS:#x}", p, 0xE6, bytes([rng.choice([1, 2, 3, 4])]).hex()])
            elif c == 11: pk.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, ctx.officer_force(p), rng.randrange(52)])]).hex()])
        r = roll(p, mode, attacker, pk)
        if r is not None: cases.append([p, mode, attacker, r[0], r[1], pk])

    ctx.save("injury.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
