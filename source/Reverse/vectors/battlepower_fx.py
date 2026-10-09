# Vectors for BattlePower with effects (the game's 0x2A8D40, fourth argument 1): the 군 facility bonus (0x2B07C0)
# and the units fighting alongside (0x2AF7E0: hex rings, bases, engagement lists, officer links). Units are made in
# empty slots of the oracle's copies, with allies placed in the hexes around them (the unit's +0x2A and the hex's
# +0x1C kept in step), then relations, lists and flags fuzzed. Run by run.py; writes battlepower_fx.json.gz
# (same rows as battlepower.json.gz).
import random, struct
from context import *

FN = 0x2A8D40
COUNTIES = 0x6CAF0
COVER = [FN, 0x2AF7E0, 0x2A7490, 0x2A7AA0, 0x2A7890, 0x2C9310, 0x233440, 0x2A7400, 0x22D740, 0x32C130, 0x232730,
         0x2B07C0, 0x22BAE0]

EVEN = [(0, 0), (-1, 0), (0, -1), (1, 0), (-1, 1), (0, 1), (1, 1)]
ODD = [(0, 0), (-1, -1), (0, -1), (1, -1), (-1, 0), (0, 1), (1, 0)]


def ring(center, radius):
    # Map.Ring (0x2A7890)
    out = []
    for r in range(1, radius + 1):
        row, col = divmod(center, 220)
        for _ in range(r):
            dc, dr = (EVEN if abs(col) % 2 == 0 else ODD)[4]
            row += dr; col += dc
        for d in (2, 3, 6, 5, 4, 1):
            for _ in range(r):
                dc, dr = (EVEN if abs(col) % 2 == 0 else ODD)[d]
                row += dr; col += dc
                if 0 <= col < 220 and 0 <= row < 220: out.append(row * 220 + col)
    return out


def bits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, tiles = rec[UNITS], rec[OFFICERS], rec[TILES]
    live = [i for i in range(len(units)) if units[i][0x10] and struct.unpack_from("<H", units[i], 0x12)[0]]
    empty = [i for i in range(1, len(units)) if i not in live]

    def oid(k):
        return struct.unpack_from("<H", officers[k], 0x10)[0]

    leaders = [k for k in range(1, len(officers)) if officers[k][0xE6] in (2, 3, 4, 5, 6, 7) and ctx.officer_force(k) and oid(k) < 0x1771]
    by_corps = {}
    for k in leaders: by_corps.setdefault(officers[k][0xE0], []).append(k)
    held = [t for t in range(len(tiles)) if 0 < struct.unpack_from("<H", tiles[t], 0x12)[0] <= 0x1F4]

    def power(i, split, pokes):
        r = ctx.run(pokes, lambda: o.call(FN, ctx.addr(UNITS, i), 0, split, 1, ret_float=True))
        return None if r is None else bits(r)

    cases = []
    for i in live:
        for split in (1, 3):
            cases.append([i, split, 1, power(i, split, []), []])
    natural = len(cases)

    rng = random.Random(97)

    def unit_pokes(slot, cmd_index, hex_):
        return [[f"{UNITS:#x}", slot, 0x10, bytes([1]).hex()],
                [f"{UNITS:#x}", slot, 0x12, struct.pack("<H", oid(cmd_index)).hex()],
                [f"{UNITS:#x}", slot, 0x16, struct.pack("<H", rng.choice([800, 3000, 9000, 15000])).hex()],
                [f"{UNITS:#x}", slot, 0x1A, bytes([rng.choice([40, 90, 100, 120])]).hex()],
                [f"{UNITS:#x}", slot, 0x1B, bytes([rng.randrange(1, 21)]).hex()],
                [f"{UNITS:#x}", slot, 0x23, bytes([0]).hex()], [f"{UNITS:#x}", slot, 0x24, bytes([0]).hex()],
                [f"{UNITS:#x}", slot, 0x25, bytes([0]).hex()],
                [f"{UNITS:#x}", slot, 0x2A, struct.pack("<H", hex_).hex()],
                [f"{UNITS:#x}", slot, 0x152, struct.pack("<H", hex_).hex()],
                [f"{TILES:#x}", hex_, 0x1C, struct.pack("<h", slot).hex()]]

    for n in range(9000):
        p = []
        slots = rng.sample(empty, 5)
        i = slots[0]
        corps = rng.choice([c for c in by_corps if len(by_corps[c]) >= 2])
        cmd = rng.choice(by_corps[corps])
        r0 = rng.random()
        if r0 < 0.75: center = rng.choice(held)
        elif r0 < 0.9: center = rng.choice([0, 1, 219, 218]) * 220 + rng.randrange(220) if rng.random() < 0.5 else rng.randrange(220) * 220 + rng.choice([0, 1, 218, 219])
        else: center = rng.randrange(len(tiles))
        p += unit_pokes(i, cmd, center)
        around = ring(center, 3)
        for j in slots[1:1 + rng.randint(0, 4)]:
            mate = rng.choice(by_corps[corps]) if rng.random() < 0.8 else rng.choice(leaders)
            h = rng.choice(around)
            p += unit_pokes(j, mate, h)
            r = rng.random()
            if r < 0.4:   # a link: the base commander likes the mate, or the mate likes him
                a, b = (cmd, mate) if rng.random() < 0.5 else (mate, cmd)
                p.append([f"{OFFICERS:#x}", a, 0xC0 + 2 * rng.randrange(8), struct.pack("<H", oid(b)).hex()])
            elif r < 0.5:
                p.append([f"{OFFICERS:#x}", mate, 0xD0 + 2 * rng.randrange(8), struct.pack("<H", oid(cmd)).hex()])
            if rng.random() < 0.1: p.append(["list", "0x70", [[f"{UNITS:#x}", j]], ""])
            if rng.random() < 0.1: p.append(["list", "0x140", [[f"{UNITS:#x}", rng.choice([i, j])], [f"{UNITS:#x}", i], [f"{UNITS:#x}", j]], ""])
            if rng.random() < 0.1: p.append([f"{UNITS:#x}", j, 0x23 + rng.randrange(3), bytes([1]).hex()])
            if rng.random() < 0.1: p.append([f"{OFFICERS:#x}", mate, 0x15E, struct.pack("<H", 0x4000).hex()])
            if rng.random() < 0.15:
                # the mate's commander based where the mate stands: a base whose building is on that hex
                p.append([f"{OFFICERS:#x}", mate, 0xE2, struct.pack("<H", rng.choice([0, 1, 5, 30, 52, 55, 61, 62, 100])).hex()])
        if rng.random() < 0.1: p.append([f"{UNITS:#x}", i, 0x23 + rng.randrange(3), bytes([1]).hex()])
        if rng.random() < 0.08: p.append([f"{OFFICERS:#x}", cmd, 0x15E, struct.pack("<H", 0x4000).hex()])
        if rng.random() < 0.05: p.append([f"{UNITS:#x}", i, 0x12, struct.pack("<H", rng.choice([0x1771, 0x1F00])).hex()])
        for _ in range(rng.choice([0, 0, 1, 2])):
            # junk unit numbers in the hexes around: out of range, empty slots, the unit itself
            p.append([f"{TILES:#x}", rng.choice(around), 0x1C, struct.pack("<h", rng.choice([-1, 0, 500, 501, 0x7FFF, rng.choice(empty), i])).hex()])
        if rng.random() < 0.05:
            # an engagement whose base or target is not a real one
            p.append(["list", "0x140", [None, [f"{UNITS:#x}", rng.choice(empty)], [f"{UNITS:#x}", slots[1]]], ""])
        if rng.random() < 0.1: p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
        if rng.random() < 0.1: p.append(["settings", 0, 0xD8, struct.pack("<i", rng.choice([0, 1, 2])).hex()])
        if rng.random() < 0.1: p.append(["settings", 0, 8 + 0x2DC, struct.pack("<i", rng.choice([0, 50, 100, 200])).hex()])
        if rng.random() < 0.1: p.append([f"{FORCES:#x}", ctx.officer_force(cmd), 0x1CB + 4, bytes([rng.randrange(256)]).hex()])
        if rng.random() < 0.1: p.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, ctx.officer_force(cmd)])]).hex()])
        split = rng.choice([1, 1, 2, 4])
        cases.append([i, split, 1, power(i, split, p), p])

    cases = [c for c in cases if c[3] is not None]
    ctx.save("battlepower_fx.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
