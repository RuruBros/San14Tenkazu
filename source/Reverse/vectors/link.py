# Vectors for Link.Value / Link.Grade: the game's officer link value (rva 0x2A6B60) and grade (0x2A6AA0) for random
# pairs and for the save's related pairs (family, 의형제, 친애, 혐오), with and without the 개성 term, then on fuzzed
# relations, 상성, 개성 and forces. Run by run.py; writes link.json.gz: [a, b, personality, value, grade, pokes].
import random, struct
from context import *

FN, GRADE = 0x2A6B60, 0x2A6AA0
PERSONALITIES = 0x7DD18
COVER = [FN, GRADE, 0x2AFAC0, 0x2C8180, 0x22E310, 0x22B840, 0x2C9BD0, 0x2C8AC0, 0x2C5780, 0x2C6EF0, 0x2C3B50, 0x2A5E20,
         0x2C5460, 0x2B2DA0, 0x2305D0, 0x22C630, 0x16F7F0, 0x232A00, 0x2C8A70]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    officers = rec[OFFICERS]
    n_off = len(officers)

    def oid(i):
        return struct.unpack_from("<H", officers[i], 0x10)[0]

    def u16(i, off):
        return struct.unpack_from("<H", officers[i], off)[0]

    active = [i for i in range(1, n_off) if officers[i][0xE6]]

    def run(a, b, pers, pokes):
        def call():
            pa, pb = ctx.addr(OFFICERS, a), ctx.addr(OFFICERS, b)
            return o.call32(FN, pa, pb, pers), o.call32(GRADE, pa, pb, pers) & 0xFF
        return ctx.run(pokes, call)

    rng = random.Random(23)
    pairs = [(rng.choice(active), rng.choice(active)) for _ in range(1500)]
    for i in active:
        for off in (0xB4, 0xB6, 0xBA) + tuple(0xC0 + 2 * k for k in range(8)) + tuple(0xD0 + 2 * k for k in range(8)):
            rid = u16(i, off)
            if rid and rid < len(ctx.byid) and ctx.byid[rid] > 0:
                pairs.append((i, ctx.byid[rid]))
        sw = u16(i, 0xBC)
        if sw:
            for j in active:
                if j != i and u16(j, 0xBC) == sw:
                    pairs.append((i, j))
                    break
    cases = []
    for a, b in pairs:
        for pers in (0, 1):
            r = run(a, b, pers, [])
            if r: cases.append([a, b, pers, r[0], r[1], []])
    natural = len(cases)

    p6c = [k for k in range(1, 356) if rec[PERSONALITIES][k][0xB6] == 0x6C or rec[PERSONALITIES][k][0xBA] == 0x6C]
    for n in range(10000):
        a, b = rng.choice(pairs) if rng.random() < 0.5 else (rng.choice(active), rng.choice(active))
        p = []

        def put(i, off, data):
            p.append([f"{OFFICERS:#x}", i, off, data.hex()])

        ida, idb = oid(a), oid(b)

        def second_effect():
            # a 개성 whose second effect is 0x6C, in one of b's slots
            k = rng.randrange(1, 256)
            p.extend([[f"{PERSONALITIES:#x}", k, 0xB6, bytes([rng.choice([0, 3, 0x6B])]).hex()],
                      [f"{PERSONALITIES:#x}", k, 0xBA, bytes([0x6C]).hex()],
                      [f"{PERSONALITIES:#x}", k, 0xBC, struct.pack("<h", rng.choice([-500, -100, 7, 2500])).hex()],
                      [f"{OFFICERS:#x}", b, 0x118 + 2 * rng.randrange(5), struct.pack("<H", k).hex()]])

        def sworn():
            put(a, 0xBC, struct.pack("<H", rng.choice([0, 33])))
            put(b, 0xBC, struct.pack("<H", rng.choice([0, 33])))

        choices = [
            lambda: put(a, 0xC0 + 2 * rng.randrange(8), struct.pack("<H", idb)),
            lambda: put(b, 0xC0 + 2 * rng.randrange(8), struct.pack("<H", ida)),
            lambda: put(a, 0xD0 + 2 * rng.randrange(8), struct.pack("<H", idb)),
            lambda: put(b, 0xD0 + 2 * rng.randrange(8), struct.pack("<H", ida)),
            lambda: put(a, 0xB2, struct.pack("<H", rng.choice([0, u16(b, 0xB2), 77]))),
            lambda: put(a, 0xB4, struct.pack("<H", rng.choice([0, idb, u16(b, 0xB4)]))),
            lambda: put(a, 0xB6, struct.pack("<H", rng.choice([0, idb, u16(b, 0xB6)]))),
            lambda: put(b, 0xB4, struct.pack("<H", rng.choice([0, ida]))),
            lambda: put(b, 0xB6, struct.pack("<H", rng.choice([0, ida]))),
            lambda: put(a, 0xBA, struct.pack("<H", rng.choice([0, idb]))),
            sworn,
            lambda: put(rng.choice([a, b]), 0xBE, bytes([rng.choice([0, 1, 37, 74, 75, 76, 112, 149, 150, 200, 255])])),
            lambda: put(b, 0x118 + 2 * rng.randrange(9), struct.pack("<H", rng.choice(p6c + [0, 0x163, 0x164, rng.randrange(356)]))),
            lambda: put(rng.choice([a, b]), 0x10, struct.pack("<H", rng.choice([ida, idb, 0x13ED, 0x1450]))),
            lambda: put(rng.choice([a, b]), 0xE0, bytes([rng.choice([officers[a][0xE0], officers[b][0xE0], 0])])),
            lambda: p.append([f"{FORCES:#x}", ctx.officer_force(a), 0x178, bytes([rng.randrange(256)]).hex()]),
            lambda: p.append([f"{PERSONALITIES:#x}", rng.choice(p6c), 0xB8 + 4 * rng.randrange(2),
                              struct.pack("<h", rng.choice([-300, -100, 0, 5, 2000, 3000])).hex()]),
            lambda: p.append([f"{PERSONALITIES:#x}", rng.choice(p6c + [rng.randrange(1, 356)]), 0xBF, bytes([rng.randrange(256)]).hex()]),
            second_effect,
            lambda: p.append(["exe", "link_floor", 0, struct.pack("<i", rng.choice([0, 0, 5, 20, -10])).hex()]),
        ]
        for _ in range(rng.randint(1, 5)):
            rng.choice(choices)()
        pers = rng.choice([0, 1])
        r = run(a, b, pers, p)
        if r: cases.append([a, b, pers, r[0], r[1], p])

    cases = [c for c in cases if c[3] is not None]
    ctx.save("link.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
