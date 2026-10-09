# Vectors for Damage.Get: the game's engagement damage (rva 0x16A1D0) for pairs of units, cities, gates and
# buildings with attack and defense values of many sizes, then fuzzed troops, 정책 0x15, officers in the attacking
# city (개성 kind 0x44, away or not), 지역 이점 kind 3 (the 주's cities a force holds), the defender's 개성 kind 0x38
# and the player's force. Run by run.py; writes damage.json.gz: [[table, index] attacker, atkA bits, defA bits,
# [table, index] defender, atkB bits, defB bits, damage, pokes].
import random, struct
from context import *

FN = 0x16A1D0
PERSONALITIES, CITIES, GATES, BUILDINGS, STATES, ADVANTAGES = 0x7DD18, 0xDD38, 0xDED8, 0x6DA98, 0xDC80, 0x85990
COVER = [FN, 0x232820, 0x232240, 0x231C60, 0x234810, 0x333160, 0x232DE0, 0x2AD780, 0x2B4DC0, 0x2C6450, 0x2AD6C0,
         0x2AAAA0, 0x92440, 0x2AB010, 0x2AB0B0, 0x2AB2E0, 0x22C990]


def fbits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, pers = rec[UNITS], rec[OFFICERS], rec[PERSONALITIES]
    live = [i for i in range(len(units)) if units[i][0x10] and struct.unpack_from("<H", units[i], 0x12)[0]]
    empty = [i for i in range(1, len(units)) if i not in live]

    def oid(k):
        return struct.unpack_from("<H", officers[k], 0x10)[0]

    leaders = [k for k in range(1, len(officers)) if officers[k][0xE6] in (2, 3, 4, 5, 6, 7) and ctx.officer_force(k) and oid(k) < 0x1771]
    out = o.alloc(0x100)

    def damage(a, atkA, defA, b, atkB, defB, pokes):
        def run():
            o.poke(out, struct.pack("<i", 0x5A5A5A5A))
            # (rcx attacker, xmm1 atkA, xmm2 defA, r9 defender; on the stack atkB, defB, out)
            o.call(FN, ctx.addr(a[0], a[1]), 0, 0, ctx.addr(b[0], b[1]), xmm=(0.0, atkA, defA), stack_args=(fbits(atkB), fbits(defB), out))
            return struct.unpack("<i", o.read(out, 4))[0]
        return ctx.run(pokes, run)

    rng = random.Random(61)
    cases = []
    sides = [(UNITS, i) for i in live] + [(CITIES, c) for c in range(1, 52)] + [(GATES, g) for g in range(1, 11)] + [(BUILDINGS, b) for b in range(1, 62)]
    for n in range(400):
        a, b = rng.choice(sides), rng.choice(sides)
        atkA, defA, atkB, defB = (rng.choice([0.0, 50.0, 500.0, 1234.5, 3000.0, 9000.0]) for _ in range(4))
        r = damage(a, atkA, defA, b, atkB, defB, [])
        if r is not None and r != 0x5A5A5A5A: cases.append([[f"{a[0]:#x}", a[1]], fbits(atkA), fbits(defA), [f"{b[0]:#x}", b[1]], fbits(atkB), fbits(defB), r, []])
    natural = len(cases)

    k44 = [x for x in range(1, 356) if pers[x][0xB6] == 0x44 or pers[x][0xBA] == 0x44]
    k38 = [x for x in range(1, 356) if pers[x][0xB6] == 0x38 or pers[x][0xBA] == 0x38]
    adv_k3 = []
    for s in range(1, 23):
        ai = rec[STATES][s][0x30]
        ai = ai if ai < 18 else 0
        adv_k3.append((s, ai))

    def unit_pokes(i):
        cmd = rng.choice(leaders)
        return [[f"{UNITS:#x}", i, 0x10, bytes([rng.choice([1, 1, 2])]).hex()],
                [f"{UNITS:#x}", i, 0x12, struct.pack("<H", oid(cmd)).hex()],
                [f"{UNITS:#x}", i, 0x16, struct.pack("<H", rng.choice([1, 100, 2500, 9000, 30000, 65535])).hex()]], cmd

    for n in range(9000):
        p = []
        r = rng.random
        kind_a = rng.choice(["unit", "unit", "city", "city", "gate", "building"])
        kind_b = rng.choice(["unit", "unit", "city", "gate", "building"])
        if kind_a == "unit":
            if live and r() < 0.3: a = (UNITS, rng.choice(live))
            else:
                a = (UNITS, rng.choice(empty)); pk, _ = unit_pokes(a[1]); p += pk
        elif kind_a == "city": a = (CITIES, rng.randrange(1, 52))
        elif kind_a == "gate": a = (GATES, rng.randrange(1, 11))
        else: a = (BUILDINGS, rng.randrange(1, 3001))
        if kind_b == "unit":
            if live and r() < 0.3: b = (UNITS, rng.choice(live)); cmd_b = None
            else:
                b = (UNITS, rng.choice([e for e in empty if e != a[1]])); pk, cmd_b = unit_pokes(b[1]); p += pk
        elif kind_b == "city": b = (CITIES, rng.randrange(1, 52)); cmd_b = None
        elif kind_b == "gate": b = (GATES, rng.randrange(1, 11)); cmd_b = None
        else: b = (BUILDINGS, rng.randrange(1, 3001)); cmd_b = None
        if r() < 0.3: p.append([f"{a[0]:#x}", a[1], 0x3C if a[0] in (CITIES, GATES) else 0x16, (struct.pack("<i", rng.choice([0, 1, 777, 40000, 200000])) if a[0] in (CITIES, GATES) else struct.pack("<H", rng.choice([0, 1, 777, 40000]))).hex()])
        if r() < 0.3: p.append([f"{b[0]:#x}", b[1], 0x3C if b[0] in (CITIES, GATES) else 0x16, (struct.pack("<i", rng.choice([0, 1, 777, 40000, 200000])) if b[0] in (CITIES, GATES) else struct.pack("<H", rng.choice([0, 1, 777, 40000]))).hex()])
        if a[0] == BUILDINGS and r() < 0.5: p.append([f"{BUILDINGS:#x}", a[1], 0x14, struct.pack("<H", rng.choice([0, 1, 300, 5000, 60000])).hex()])
        if a[0] == BUILDINGS and r() < 0.3: p.append([f"{BUILDINGS:#x}", a[1], 0x13, bytes([rng.choice([0, 1, 7, 44])]).hex()])
        if a[0] == CITIES:
            fi = rec[CORPS][rec[CITIES][a[1]][0x30] if rec[CITIES][a[1]][0x30] <= 0x33 else 0][0x10]
            fi = fi if fi <= 0x33 else 0
            # officers based in the city, with 개성 kind 0x44, some away
            based = [k for k in range(1, len(officers)) if struct.unpack_from("<H", officers[k], 0xE2)[0] == struct.unpack_from("<H", rec[CITIES][a[1]], 0x4E)[0]]
            for k in rng.sample(based, min(len(based), rng.randint(0, 3))):
                p.append([f"{OFFICERS:#x}", k, 0x118 + 2 * rng.randrange(5), struct.pack("<H", rng.choice(k44)).hex()])
                if r() < 0.3: p.append([f"{OFFICERS:#x}", k, 0xE4, struct.pack("<H", rng.choice([0, 1, 60, 61, 62, 0x100, 0x232, 0x3E])).hex()])
                if r() < 0.2: p.append([f"{OFFICERS:#x}", k, 0xE6, bytes([rng.choice([1, 2, 5, 6, 7, 8])]).hex()])
            if r() < 0.3: p.append([f"{PERSONALITIES:#x}", rng.choice(k44), 0xB8, struct.pack("<h", rng.choice([-150, -50, -1, 0, 3, 40, 2500])).hex()])
            if r() < 0.3:
                field = rng.choice([0x54, 0x72, 0x90, 0xAE, 0xCC]); k = rng.randrange(7); oi = rng.choice(leaders)
                p += [[f"{FORCES:#x}", fi, field + 2 * k, struct.pack("<H", oid(oi)).hex()], [f"{FORCES:#x}", fi, field + 0xE + 2 * k, struct.pack("<H", 0).hex()],
                      [f"{OFFICERS:#x}", oi, 0x14D, bytes([rng.randrange(1, 121)]).hex()], [f"{OFFICERS:#x}", oi, 0x182, bytes([rng.randrange(1, 11)]).hex()]]
            if r() < 0.4:
                # the force holds the cities of a 주 whose 이점 has kind 3
                s, ai = rng.choice(adv_k3)
                j = rng.randrange(2)
                p.append([f"{ADVANTAGES:#x}", ai, 0x96 + j, bytes([3]).hex()])
                p.append([f"{ADVANTAGES:#x}", ai, 0x98 + j, bytes([rng.choice([0, 5, 20, 100, 255])]).hex()])
                p.append([f"{ADVANTAGES:#x}", ai, 0x95, bytes([rng.choice([0, 1, 2, 3])]).hex()])
                if r() < 0.5: p.append(["vec", [f"{ADVANTAGES:#x}", ai], [0xA0, rng.choice([0, 0x10])], ""])
                for c in rng.sample(range(1, 52), rng.randint(0, 3)):
                    p.append([f"{CITIES:#x}", c, 0xCE, bytes([s]).hex()])
                    p.append([f"{CITIES:#x}", c, 0x30, bytes([rec[CITIES][a[1]][0x30]]).hex()])
            if r() < 0.1: p.append(["worldstate", 0, 0x40, struct.pack("<i", rng.choice([0, 1, 2, 4])).hex()])
        if b[0] == CITIES and r() < 0.3:
            # a tribe city with a governor (no damage from others)
            p.append([f"{CITIES:#x}", b[1], 0x30, bytes([rng.choice([0x2E, 0x30, 0x32, 1, 5])]).hex()])
            if r() < 0.5: p.append([f"{CITIES:#x}", b[1], 0x2E, struct.pack("<H", oid(rng.choice(leaders))).hex()])
        if cmd_b is not None and r() < 0.4:
            k = rng.choice(k38)
            p.append([f"{OFFICERS:#x}", cmd_b, 0x118 + 2 * rng.randrange(5), struct.pack("<H", k).hex()])
            if r() < 0.5: p.append([f"{PERSONALITIES:#x}", k, 0xB4, struct.pack("<h", rng.choice([-1, 0, 5])).hex()])
        if r() < 0.2: p.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, 1, 7, 0x34, rng.randrange(52)])]).hex()])
        atkA, defA, atkB, defB = (rng.choice([0.0, 1.0, 50.0, 333.3, 999.0, 1500.0, 3000.0, 9000.0, 20000.0, -50.0]) for _ in range(4))
        res = damage(a, atkA, defA, b, atkB, defB, p)
        if res is not None and res != 0x5A5A5A5A:
            cases.append([[f"{a[0]:#x}", a[1]], fbits(atkA), fbits(defA), [f"{b[0]:#x}", b[1]], fbits(atkB), fbits(defB), res, p])

    ctx.save("damage.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
