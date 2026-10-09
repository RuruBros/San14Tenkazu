# Vectors for Formulas/BattlePower.cs: the game's 전투력 (rva 0x2A8D40) for every unit slot and several split
# counts, without effects (its fourth argument 0), then on fuzzed units, officers, forces, 정책 and options.
# Run by run.py; writes battlepower.json.gz: [unit, split, effects, float bits, pokes].
import random, struct
from context import *

FN = 0x2A8D40
COVER = [FN, 0x268C50, 0x41C200, 0x26AA20, 0x258D90, 0x2A9D30, 0x2A9BB0, 0x22C590, 0x22C0F0, 0x22B090, 0x22CC40,
         0x22E9D0, 0x233110, 0x232B80]


def bits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units = rec[UNITS]
    live = [i for i in range(len(units)) if units[i][0x10] and struct.unpack_from("<H", units[i], 0x12)[0]]

    def power(i, split, effects, pokes):
        r = ctx.run(pokes, lambda: o.call(FN, ctx.addr(UNITS, i), 0, split, effects, ret_float=True))
        return None if r is None else bits(r)

    cases = []
    for i in range(len(units)):
        for split in (1, 2, 3, 4, 6):
            cases.append([i, split, 0, power(i, split, 0, []), []])
    natural = len(cases)

    # Officers who can lead (status 2..7 and in a force) and empty unit slots: units are made in the oracle's
    # copies when the map has none (or too few) to cover the formula.
    officers = rec[OFFICERS]
    leaders = [struct.unpack_from("<H", officers[k], 0x10)[0] for k in range(1, len(officers))
               if officers[k][0xE6] in (2, 3, 4, 5, 6, 7) and ctx.officer_force(k) and struct.unpack_from("<H", officers[k], 0x10)[0] < 0x1771]
    empty = [i for i in range(1, len(units)) if i not in live]

    rng = random.Random(41)
    tiles = rec[TILES]
    siege_tiles = [t for t in range(len(tiles)) if tiles[t][0x10] in (5, 6, 8, 9, 10, 11)]
    water_tiles = [t for t in range(len(tiles)) if rec[TERRAINS][tiles[t][0x10] if tiles[t][0x10] < 31 else 0][0x2C] & 2]

    def pokes_for(i):
        p = []
        u = units[i]
        if i not in live and i != 0 and rng.random() < 0.9:
            # make a unit in the empty slot: kind, commander, troops, morale, formations, tile
            cid = rng.choice(leaders)
            p += [[f"{UNITS:#x}", i, 0x10, bytes([rng.choice([1, 1, 1, 2])]).hex()],
                  [f"{UNITS:#x}", i, 0x12, struct.pack("<H", cid).hex()],
                  [f"{UNITS:#x}", i, 0x16, struct.pack("<H", rng.choice([500, 2000, 5000, 8000, 12000, 20000])).hex()],
                  [f"{UNITS:#x}", i, 0x1A, bytes([rng.choice([30, 70, 100, 110, 130])]).hex()],
                  [f"{UNITS:#x}", i, 0x1B, bytes([rng.randrange(1, 21)]).hex()],
                  [f"{UNITS:#x}", i, 0x1C, bytes([rng.randrange(1, 21)]).hex()],
                  [f"{UNITS:#x}", i, 0x2A, struct.pack("<H", rng.randrange(0xBD10)).hex()],
                  [f"{UNITS:#x}", i, 0x152, struct.pack("<H", rng.randrange(0xBD10)).hex()]]
            u = bytearray(u); u[0x12:0x14] = struct.pack("<H", cid); u = bytes(u)
        cid = struct.unpack_from("<H", u, 0x12)[0]
        ci = ctx.officer_by_id(cid) if cid < 0x1771 else 0
        f = ctx.officer_force(ci) if ci >= 0 else 0
        force = rec[FORCES][f]

        def unit(off, data): p.append([f"{UNITS:#x}", i, off, data.hex()])
        def officer(idx, off, data): p.append([f"{OFFICERS:#x}", idx, off, data.hex()])
        def frc(off, data): p.append([f"{FORCES:#x}", f, off, data.hex()])
        def slot_officer():
            # an officer into a 정책 slot of the unit's force, enacted or not, with a 정책 and level
            field = rng.choice([0x54, 0x72, 0x90, 0xAE, 0xCC]); k = rng.randrange(7)
            oi = rng.randrange(1, len(rec[OFFICERS]))
            oid = struct.unpack_from("<H", rec[OFFICERS][oi], 0x10)[0]
            frc(field + 2 * k, struct.pack("<H", oid))
            frc(field + 0xE + 2 * k, struct.pack("<H", rng.choice([0, 0, 0, 1, 5])))
            officer(oi, 0x14D, bytes([rng.choice([0, 1, 10, 50, 100, 120, 121, 200, rng.randrange(121)])]))
            officer(oi, 0x182, bytes([rng.choice([0, 1, 3, 5, 9, 10, 11, 255])]))
            if rng.random() < 0.5:
                officer(oi, 0x1BC, bytes([rng.choice([0, 1, 2])]))
                officer(oi, 0x1C2, bytes([rng.choice([0, 1, 3, 6, 7, 9])]))
                officer(oi, 0x1C3, bytes([rng.choice([0, 1, 2, 3, 4])]))
            if rng.random() < 0.3: officer(oi, 0xE6, bytes([rng.choice([1, 2, 3])]))
        choices = [
            lambda: unit(0x16, struct.pack("<H", rng.choice([0, 1, 99, 100, 1000, 5000, 9999, 20000, 40000, 65535]))),
            lambda: unit(0x1A, bytes([rng.choice([0, 1, 50, 99, 100, 101, 120, 150, 200, 255])])),
            lambda: unit(0x23, bytes([rng.choice([0, 1, 2])])),
            lambda: unit(0x1B, bytes([rng.choice([0, 1, 5, 6, 7, 13, 14, 15, 19, 20, 21, 30])])),
            lambda: unit(0x1C, bytes([rng.choice([0, 6, 14, 15, 20, 21])])),
            lambda: unit(0x2A, struct.pack("<H", rng.choice(siege_tiles + water_tiles + [0, 0xBD0F, 0xBD10, rng.randrange(0xBD10)]))),
            lambda: unit(0x152, struct.pack("<H", rng.choice([0, 0xBD10, rng.randrange(0xBD10)]))),
            lambda: unit(0x12, struct.pack("<H", rng.choice([0, 1, 0x1770, 0x1771, struct.unpack_from("<H", rec[OFFICERS][rng.randrange(1, 1851)], 0x10)[0]]))),
            lambda: unit(0x10, bytes([rng.choice([0, 1, 2])])),
            lambda: officer(ci, 0xEC, bytes([rng.choice([0, 1, 30, 75, 99, 100, 120, 150, 255])])),
            lambda: officer(ci, 0x161, bytes([rng.choice([0, 1, 2, 3])])),
            lambda: officer(ci, 0xE0, bytes([rng.choice([0, 1, 0x33, 0x34, rng.randrange(52)])])),
            slot_officer, slot_officer,
            lambda: frc(0x16C, struct.pack("<h", rng.choice([0, 1]))),
            lambda: frc(0x10, struct.pack("<H", rng.choice([0, struct.unpack_from("<H", force, 0x10)[0]]))),
            lambda: frc(0x1CB + 8, bytes([rng.randrange(256)])),   # panels 0x41..0x48: 0x47 is the 사기 one
            lambda: frc(0x1D5 + 8, bytes([rng.randrange(256)])),
            lambda: frc(0x1CB + 4, bytes([rng.randrange(256)])),   # panel 0x26 (allies)
            lambda: p.append([f"{POLICIES:#x}", rng.randrange(1, 121), 0x98 + rng.randrange(8), bytes([rng.choice([0, 10, 10, 101, 255])]).hex()]),
            lambda: p.append([f"{CREDITS:#x}", rng.randrange(7), 0x10, bytes([rng.choice([2, 3, 3, 4])]).hex()]),
            lambda: p.append([f"{CREDITS:#x}", rng.randrange(7), 0x12, struct.pack("<H", rng.choice([0, 1, 2, 5])).hex()]),
            lambda: p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()]),
            lambda: p.append(["settings", 0, 0xEC, struct.pack("<i", rng.choice([0, 1, 2])).hex()]),
            lambda: p.append(["settings", 0, 0xD8, struct.pack("<i", rng.choice([0, 1, 2])).hex()]),
            lambda: p.append(["settings", 0, 8 + 0x354, struct.pack("<i", rng.choice([-50, 0, 50, 99, 100, 150, 200, 201, 300])).hex()]),
            lambda: p.append(["settings", 0, 8 + 0x2DC, struct.pack("<i", rng.choice([0, 50, 100, 200])).hex()]),
            lambda: p.append(["worldstate", 0, 0xBC, bytes([rng.choice([0, 1, 0xFF])]).hex()]),
            lambda: p.append(["worldstate", 0, 0x44, struct.pack("<i", rng.choice([1, 2, 3])).hex()]),
        ]
        for _ in range(rng.randint(1, 5)): rng.choice(choices)()
        return p

    for n in range(12000):
        i = rng.choice(live) if live and rng.random() < 0.5 else rng.choice(empty) if rng.random() < 0.97 else 0
        split = rng.choice([1, 1, 2, 3, 5, 10])
        p = pokes_for(i)
        cases.append([i, split, 0, power(i, split, 0, p), p])

    cases = [c for c in cases if c[3] is not None]
    ctx.save("battlepower.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
