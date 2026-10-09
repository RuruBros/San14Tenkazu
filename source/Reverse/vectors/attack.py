# Vectors for Attack.VsUnit (the game's attack value against units, rva 0x2B21A0), and through siege.py and
# defense.py for Attack.VsBase (0x2B2440) and Attack.Defense (0x2A9E50): live and made-up units and bases, with
# fuzzed formations (values, the force's +0x193 bits), 개성 lit and received, buffs, age, 주의 0x28 near own bases,
# 정책 (aptitude) and base durability. Run by run.py; writes attack.json.gz: [unit, base table, base, power bits,
# a5, float bits, pokes].
import random, struct
from context import *
from battlepower_fx import ring

FN = 0x2B21A0
PERSONALITIES, CITIES, GATES, BUILDINGS = 0x7DD18, 0xDD38, 0xDED8, 0x6DA98
COVER = [FN, 0x26A2C0, 0x2A5F70, 0x2A5EA0, 0x2B2720, 0x22B140, 0x41C140, 0x41BFC0, 0x2C9900, 0x2B4750, 0x2B48A0, 0x232D00]


def fbits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def generate(ctx, fn=FN, name="attack.json.gz", kind=5, buff=0x4B, seed=55):
    o, rec = ctx.o, ctx.rec
    units, officers, tiles, terrains, formations = rec[UNITS], rec[OFFICERS], rec[TILES], rec[TERRAINS], rec[FORMATIONS]
    pers = rec[PERSONALITIES]
    live = [i for i in range(len(units)) if units[i][0x10] and struct.unpack_from("<H", units[i], 0x12)[0]]
    empty = [i for i in range(1, len(units)) if i not in live]

    def oid(k):
        return struct.unpack_from("<H", officers[k], 0x10)[0]

    leaders = [k for k in range(1, len(officers)) if officers[k][0xE6] in (2, 3, 4, 5, 6, 7) and ctx.officer_force(k) and oid(k) < 0x1771]

    def formation_index(u):
        t = struct.unpack_from("<H", u, 0x2A)[0]
        t = t if t < 0xBD10 else 0
        kind = tiles[t][0x10]
        terr = terrains[kind if kind < 31 else 0]
        f = u[0x1C] if terr[0x2C] & 2 else u[0x1B]
        return f if f < 0x15 else 0

    def call(unit_slot, base_table, base_index, power, a5, pokes, formation=None):
        def run():
            up = ctx.addr(UNITS, unit_slot) if unit_slot is not None else 0
            bp = ctx.addr(base_table, base_index) if base_table else 0
            # the formation the game passes: the unit's as it stands after the pokes (read back from the copies)
            if formation is None and unit_slot is not None:
                u = o.read(up, 0x200)
                fp = ctx.addr(FORMATIONS, formation_index_live(u))
            else:
                fp = ctx.addr(FORMATIONS, formation) if formation is not None else 0
            return fbits(o.call(fn, 0, up, bp, fp, xmm=(power,), stack_args=(a5, 0), ret_float=True))
        return ctx.run(pokes, run)   # None when the oracle failed

    def formation_index_live(u):
        t = struct.unpack_from("<H", u, 0x2A)[0]
        t = t if t < 0xBD10 else 0
        kind = o.read(ctx.addr(TILES, t) + 0x10, 1)[0]
        terr = o.read(ctx.addr(TERRAINS, kind if kind < 31 else 0) + 0x2C, 1)[0]
        f = u[0x1C] if terr & 2 else u[0x1B]
        return f if f < 0x15 else 0

    rng = random.Random(seed)
    cases = []
    for i in live:
        for a5 in (1, 0):
            p = rng.choice([1.0, 37.5, 512.25, 3000.0])
            cases.append([i, None, 0, fbits(p), a5, call(i, None, 0, p, a5, []), []])
    for table, n in ((CITIES, 52), (GATES, 11)):
        for b in range(n):
            p = rng.choice([1.0, 100.0, 777.0])
            cases.append([None, f"{table:#x}", b, fbits(p), 1, call(None, table, b, p, 1, []), []])
    natural = len(cases)

    kinds = {k: [x for x in range(1, 356) if pers[x][0xB6] == k or pers[x][0xBA] == k] for k in (kind, 19, 31, 97)}
    useful = sorted(set(sum(kinds.values(), [])))
    cities_by_force = {}
    for c in range(1, 52):
        corps = rec[CORPS][rec[CITIES][c][0x30] if rec[CITIES][c][0x30] <= 0x33 else 0]
        cities_by_force.setdefault(corps[0x10], []).append(c)

    for n in range(9000):
        p = []
        if live and rng.random() < 0.3:
            i = rng.choice(live)
            cmd_id = struct.unpack_from("<H", units[i], 0x12)[0]
            cmd = ctx.officer_by_id(cmd_id) if cmd_id < 0x1771 else 0
        else:
            i = rng.choice(empty)
            cmd = rng.choice(leaders)
            p += [[f"{UNITS:#x}", i, 0x10, bytes([rng.choice([1, 1, 2])]).hex()],
                  [f"{UNITS:#x}", i, 0x12, struct.pack("<H", oid(cmd)).hex()],
                  [f"{UNITS:#x}", i, 0x16, struct.pack("<H", 5000).hex()],
                  [f"{UNITS:#x}", i, 0x1B, bytes([rng.randrange(0, 22)]).hex()],
                  [f"{UNITS:#x}", i, 0x1C, bytes([rng.randrange(0, 22)]).hex()],
                  [f"{UNITS:#x}", i, 0x2A, struct.pack("<H", rng.randrange(0xBD10)).hex()]]
        fi = ctx.officer_force(cmd) if cmd >= 0 else 0
        r = rng.random
        if r() < 0.4:
            for _ in range(rng.randint(1, 4)):
                p.append([f"{UNITS:#x}", i, 0x64 + 2 * rng.randrange(36), struct.pack("<H", rng.choice(useful + [0, 0x164, rng.randrange(356)])).hex()])
        if r() < 0.4:
            p.append([f"{OFFICERS:#x}", cmd, 0x118 + 2 * rng.randrange(5), struct.pack("<H", rng.choice(useful)).hex()])
            p.append([f"{OFFICERS:#x}", cmd, 0x184, struct.pack("<H", rng.randrange(0x200)).hex()])
        if r() < 0.2:
            k = rng.choice(useful)
            p.append([f"{PERSONALITIES:#x}", k, 0xB4, struct.pack("<h", rng.choice([-1, 0, 3])).hex()])
            p.append([f"{PERSONALITIES:#x}", k, 0xB8, struct.pack("<h", rng.choice([-200, -3, 4, 15, 3000])).hex()])
        if r() < 0.3: p.append([f"{UNITS:#x}", i, buff, struct.pack("<b", rng.choice([-128, -20, -1, 0, 3, 10, 127])).hex()])
        if r() < 0.3: p.append([f"{FORCES:#x}", fi, 0x193, bytes([rng.choice([0, 2, 0x20, 0x22, 0xFF])]).hex()])
        if r() < 0.15: p.append([f"{UNITS:#x}", i, rng.choice([0x1B, 0x1C]), bytes([rng.choice([5, 7, 0x13, 0x14])]).hex()])
        if r() < 0.3: p.append([f"{OFFICERS:#x}", cmd, 0xAC, struct.pack("<H", rng.choice([100, 120, 130, 135, 140, 150, 160, 400])).hex()])
        if r() < 0.1: p.append(["worldstate", 0, 0x34, struct.pack("<h", rng.choice([190, 200, 230, 260])).hex()])
        if r() < 0.08: p.append(["worldstate", 0, 0x44, struct.pack("<i", rng.choice([1, 2, 3])).hex()])
        if r() < 0.08: p.append(["worldstate", 0, 0x50, struct.pack("<i", rng.choice([0, 1])).hex()])
        if r() < 0.1: p.append([f"{OFFICERS:#x}", cmd, 0x10, struct.pack("<H", rng.choice([231, 247, 271, 631, 5121, oid(cmd)])).hex()])
        if r() < 0.1: p.append(["list", "0x70", [[f"{UNITS:#x}", i]], ""])
        if r() < 0.3:
            p.append([f"{FORCES:#x}", fi, 0x1CB + 4, bytes([rng.choice([0x80, 0xFF, 0])]).hex()])
            p.append([f"{FORCES:#x}", fi, 0x1D5 + 4, bytes([rng.choice([0, 0, 0x80])]).hex()])
        if r() < 0.3 and cities_by_force.get(fi):
            # stand on the ring around one of the force's cities (or one step off it)
            c = rng.choice(cities_by_force[fi])
            b = struct.unpack_from("<H", rec[CITIES][c], 0x4E)[0]
            o_ = rec[BUILDINGS][b if b <= 0xBB8 else 0]
            center = struct.unpack_from("<H", o_, 0x10)[0]
            hexes = [h for h in ring(center, 2) if h not in ring(center, 1)]
            if hexes: p.append([f"{UNITS:#x}", i, 0x2A, struct.pack("<H", rng.choice(hexes + ring(center, 1))).hex()])
        if r() < 0.2:
            field = rng.choice([0x54, 0x72, 0x90, 0xAE, 0xCC]); k = rng.randrange(7)
            oi = rng.choice(leaders)
            p.append([f"{FORCES:#x}", fi, field + 2 * k, struct.pack("<H", oid(oi)).hex()])
            p.append([f"{FORCES:#x}", fi, field + 0xE + 2 * k, struct.pack("<H", 0).hex()])
            p.append([f"{OFFICERS:#x}", oi, 0x14D, bytes([rng.randrange(1, 121)]).hex()])
            p.append([f"{OFFICERS:#x}", oi, 0x182, bytes([rng.randrange(1, 11)]).hex()])
        if r() < 0.05: p.append(["vec", [f"{FORMATIONS:#x}", rng.randrange(21)], [0xA8, rng.choice([0, 0x10])], ""])
        if r() < 0.05: p.append([f"{FORMATIONS:#x}", rng.randrange(21), rng.choice([0x83, 0x84, 0x86]), bytes([rng.choice([0, 1, 30, 46, 200])]).hex()])
        power = rng.choice([1.0, 0.5, 123.456, 999.0, 4000.75])
        a5 = rng.choice([1, 1, 0])
        cases.append([i, None, 0, fbits(power), a5, call(i, None, 0, power, a5, p), p])
    for n in range(500):
        table, nb = rng.choice([(CITIES, 52), (GATES, 11)])
        b = rng.randrange(nb)
        p = [[f"{table:#x}", b, 0x4E, struct.pack("<H", rng.choice([0, 1, 51, 52, 61, 62, 100])).hex()]] if rng.random() < 0.5 else []
        if rng.random() < 0.4:
            bid = struct.unpack_from("<H", rec[table][b], 0x4E)[0]
            p.append([f"{BUILDINGS:#x}", bid if bid <= 0xBB8 else 0, 0x14, struct.pack("<H", rng.choice([0, 1, 500, 2999, 6000, 65535])).hex()])
            if rng.random() < 0.3: p.append([f"{BUILDINGS:#x}", bid if bid <= 0xBB8 else 0, 0x12, bytes([rng.choice([0, 1])]).hex()])
        power = rng.choice([1.0, 250.0])
        cases.append([None, f"{table:#x}", b, fbits(power), 1, call(None, table, b, power, 1, p), p])

    cases = [c for c in cases if c[5] is not None]
    ctx.save(name, {"function": f"{fn:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
