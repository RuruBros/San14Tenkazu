# Vectors for Tactic.Fire: the game's 전법 roll (rva 0x16B880) over an engagement object and a list of tactics, both
# built in the oracle's scratch memory: the engagement names the attacker unit (+0x98) or base (+0xA0) and the
# defender unit (+0xA8) or base (+0xB0); the list is a tlib ptr_list (+8 → a pool index whose node chain holds
# the CTacticsData pointers). Run by run.py; writes tactic.json.gz:
# [[attacker unit, attacker base], [defender unit, defender base], tactic indexes, fired index or -1, pokes].
import random, struct
from context import *
import world

FN = 0x16B880
TACTICS, CITIES, GATES = 0x774D0, 0xDD38, 0xDED8
COVER = [FN]


def generate(ctx):
    o, rec, g = ctx.o, ctx.rec, ctx.g
    units, officers, tactics = rec[UNITS], rec[OFFICERS], rec[TACTICS]
    live = [i for i in range(len(units)) if units[i][0x10] and struct.unpack_from("<H", units[i], 0x12)[0]]
    empty = [i for i in range(1, len(units)) if i not in live]

    def oid(k):
        return struct.unpack_from("<H", officers[k], 0x10)[0]

    leaders = [k for k in range(1, len(officers)) if officers[k][0xE6] in (2, 3, 4, 5, 6, 7) and ctx.officer_force(k) and oid(k) < 0x1771]

    # the engagement object, the list container and its head cell, the node chain
    eng = o.alloc(0x200)
    cont = o.alloc(0x100)
    head = cont + 0x80
    nodes = o.alloc(0x1000)
    pool = o.q(g.exe + world.POOL_RVA)
    count = struct.unpack("<I", o.read(g.exe + world.POOL_RVA + 0x30, 4))[0]
    idx = count - 1
    while idx > 0 and o.q(pool + idx * 8): idx -= 1
    o.poke(cont + 8, struct.pack("<Q", head))
    o.poke(head, struct.pack("<I", idx))

    def fire(a_unit, a_base, d_unit, d_base, tlist, pokes):
        def run():
            o.poke(eng + 0x98, struct.pack("<QQQQ", ctx.addr(UNITS, a_unit) if a_unit is not None else 0,
                                           ctx.addr(a_base[0], a_base[1]) if a_base else 0,
                                           ctx.addr(UNITS, d_unit) if d_unit is not None else 0,
                                           ctx.addr(d_base[0], d_base[1]) if d_base else 0))
            for i, t in enumerate(tlist):
                o.poke(nodes + 16 * i, struct.pack("<QQ", ctx.addr(TACTICS, t), nodes + 16 * (i + 1) if i + 1 < len(tlist) else 0))
            o.poke(pool + idx * 8, struct.pack("<Q", nodes if tlist else 0))
            r = o.call(FN, eng, cont)
            if not r: return -1
            return (r - ctx.addr(TACTICS, 0)) // 0x90
        return ctx.run(pokes, run)

    rng = random.Random(83)
    cases = []

    def side(kind):
        if kind == "unit":
            return (rng.choice(live) if live and rng.random() < 0.4 else rng.choice(empty)), None
        return None, ((CITIES, rng.randrange(1, 52)) if kind == "city" else (GATES, rng.randrange(1, 11)))

    def unit_pokes(i):
        cmd = rng.choice(leaders)
        return [[f"{UNITS:#x}", i, 0x10, bytes([1]).hex()], [f"{UNITS:#x}", i, 0x12, struct.pack("<H", oid(cmd)).hex()],
                [f"{UNITS:#x}", i, 0x16, struct.pack("<H", 5000).hex()], [f"{UNITS:#x}", i, 0x1B, bytes([rng.randrange(1, 21)]).hex()],
                [f"{UNITS:#x}", i, 0x1C, bytes([rng.randrange(1, 21)]).hex()], [f"{UNITS:#x}", i, 0x2A, struct.pack("<H", rng.randrange(0xBD10)).hex()]], cmd

    for n in range(8000):
        p = []
        au, ab = side(rng.choice(["unit", "unit", "unit", "city", "gate"]))
        du, db = side(rng.choice(["unit", "unit", "city", "gate"]))
        if au is not None and au in empty: pk, cmd = unit_pokes(au); p += pk
        else: cmd = None
        if du is not None and du in empty and du != au: pk, _ = unit_pokes(du); p += pk
        tlist = [rng.randrange(1, 201) for _ in range(rng.choice([0, 1, 1, 2, 3, 5]))]
        r = rng.random
        if tlist and r() < 0.5:
            t = rng.choice(tlist)
            p.append([f"{TACTICS:#x}", t, 0x40, bytes([rng.choice([0, 1, 2, 5, 8, 9, 12, 255])]).hex()])
        if r() < 0.3: p.append([f"{FORMATIONS:#x}", rng.randrange(1, 21), 0x88 + 2 * rng.randrange(9), struct.pack("<H", rng.choice([0, 1, 50, 100, 150, 400])).hex()])
        if r() < 0.3: p.append([f"{TERRAINS:#x}", rng.randrange(1, 31), 0x2E + 2 * rng.randrange(9), struct.pack("<H", rng.choice([0, 30, 100, 200])).hex()])
        if r() < 0.3:
            p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
            p.append(["settings", 0, 0x28, struct.pack("<i", rng.choice([0, 1, 2, 3, 4, 5])).hex()])
            p.append(["settings", 0, 0x134, struct.pack("<i", rng.choice([0, 1, 2, 3, 4])).hex()])
        if r() < 0.3: p.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, rng.randrange(52), ctx.officer_force(cmd) if cmd else 1])]).hex()])
        if cmd is not None and r() < 0.15: p.append([f"{OFFICERS:#x}", cmd, 0xE0, bytes([rng.choice([0x33, 0x2E, 0x30, 0])]).hex()])
        if r() < 0.3: p.append(["worldstate", 0, 0x450, struct.pack("<i", rng.choice([0, 3, 77, 1000])).hex()])
        if r() < 0.3: p.append(["worldstate", 0, 0x45C, struct.pack("<i", rng.choice([0, 1, 12345])).hex()])
        res = fire(au, ab, du, db, tlist, p)
        if res is not None:
            cases.append([[au, ["city" if ab and ab[0] == CITIES else "gate", ab[1]] if ab else None],
                          [du, ["city" if db and db[0] == CITIES else "gate", db[1]] if db else None], tlist, res, p])

    o.poke(pool + idx * 8, struct.pack("<Q", 0))
    ctx.save("tactic.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{len(cases)} cases"
