# Vectors for BattlePower.Facility: the game's 군 facility bonus (rva 0x2B07C0) for hexes in held 군 and at random,
# every force number, then on fuzzed 군, forces, options. Run by run.py; writes facility.json.gz:
# [tile, force, float bits, sum, pokes].
import random, struct
from context import *

FN = 0x2B07C0
COUNTIES = 0x6CAF0
COVER = [FN, 0x2337B0, 0x233AD0, 0x3E9E60]


def bits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    out = o.alloc(0x1000)
    tiles, counties = rec[TILES], rec[COUNTIES]

    def facility(t, fi, pokes):
        def call():
            o.poke(out, struct.pack("<i", 0x5A5A5A5A))
            r = o.call(FN, ctx.addr(TILES, t), fi, out, ret_float=True)
            return bits(r), struct.unpack("<i", o.read(out, 4))[0]
        return ctx.run(pokes, call)

    held = [t for t in range(len(tiles)) if 0 < struct.unpack_from("<H", tiles[t], 0x12)[0] <= 0x1F4]
    rng = random.Random(77)
    sample = rng.sample(held, 1500) + [rng.randrange(len(tiles)) for _ in range(200)]
    cases = []
    for t in sample:
        c = counties[min(struct.unpack_from("<H", tiles[t], 0x12)[0], 0x1F4) if struct.unpack_from("<H", tiles[t], 0x12)[0] <= 0x1F4 else 0]
        owner = c[0x78]
        for fi in {owner, rng.randrange(0, 56), 0, 46, 51}:
            r = facility(t, fi, [])
            if r: cases.append([t, fi, r[0], r[1], []])
    natural = len(cases)

    def pokes_for(t, fi):
        p = []
        cid = struct.unpack_from("<H", tiles[t], 0x12)[0]
        cid = cid if cid <= 0x1F4 else 0
        c = counties[cid]
        neigh = [struct.unpack_from("<H", c, 0x56 + 2 * s)[0] for s in range(8)]
        f = fi if 0 <= fi <= 0x33 else 0

        def cty(i, off, data): p.append([f"{COUNTIES:#x}", i if i <= 0x1F4 else 0, off, data.hex()])
        def frc(off, data): p.append([f"{FORCES:#x}", f, off, data.hex()])
        choices = [
            lambda: cty(cid, 0x78, bytes([rng.choice([fi & 0xFF, 0, rng.randrange(52)])])),
            lambda: cty(rng.choice(neigh), 0x78, bytes([rng.choice([fi & 0xFF, rng.randrange(52)])])),
            lambda: cty(cid, 0x66, bytes([rng.randrange(256)])),
            lambda: cty(rng.choice(neigh + [cid]), 0x68, struct.pack("<H", rng.choice([0, 1, 50, 100, 250, 300, 999, 1000, 65535]))),
            lambda: cty(rng.choice(neigh + [cid]), 0x76, struct.pack("<H", rng.choice([0, 1, 100, 300, 1000]))),
            lambda: cty(rng.choice(neigh + [cid]), 0x34, bytes([rng.choice([0, 1, 51, 52])])),
            lambda: cty(cid, 0x56 + 2 * rng.randrange(8), struct.pack("<H", rng.choice([0, 0x1F4, 0x1F5, rng.randrange(1, 0x1F5)]))),
            lambda: frc(0xEA + rng.randrange(52), bytes([rng.choice([0, 1, 0x80, 0x81, 12])])),
            lambda: frc(0x1CB + 4, bytes([rng.randrange(256)])),    # 주의 0x27 (allies' facilities)
            lambda: frc(0x1D5 + 4, bytes([rng.randrange(256)])),
            lambda: frc(0x1CB + 8, bytes([rng.randrange(256)])),    # 주의 0x46 (facility percent)
            lambda: frc(0x10, struct.pack("<H", rng.choice([0, 1]))),
            lambda: p.append([f"{DOCTRINE:#x}", rng.choice([0x27, 0x46]), 0x44, struct.pack("<h", rng.choice([-5, 0, 1, 5, 30])).hex()]),
            lambda: p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()]),
            lambda: p.append(["settings", 0, 0x28, struct.pack("<i", rng.choice([0, 2, 3, 4, 5])).hex()]),
            lambda: p.append(["settings", 0, 0xC4, struct.pack("<i", rng.choice([0, 1, 2, 3])).hex()]),
            lambda: p.append(["settings", 0, 8 + 0x24C, struct.pack("<i", rng.choice([0, 50, 100, 150, 200])).hex()]),
            lambda: p.append(["settings", 0, 8 + 0x264, struct.pack("<i", rng.choice([0, 50, 100, 150, 200])).hex()]),
            lambda: p.append([f"{TILES:#x}", t, 0x12, struct.pack("<H", rng.choice([0x1F5, 0xFFFF, 0])).hex()]),
            lambda: p.append(["worldstate", 0, 0x3A, bytes([rng.choice([0, fi & 0xFF, rng.randrange(52), 0x34])]).hex()]),
        ]
        for _ in range(rng.randint(1, 5)): rng.choice(choices)()
        return p

    for n in range(8000):
        t = rng.choice(held) if rng.random() < 0.95 else rng.randrange(len(tiles))
        cid = struct.unpack_from("<H", tiles[t], 0x12)[0]
        owner = counties[cid if cid <= 0x1F4 else 0][0x78]
        fi = owner if rng.random() < 0.7 else rng.choice([0, 1, 45, 46, 50, 51, 52, rng.randrange(52)])
        p = pokes_for(t, fi)
        r = facility(t, fi, p)
        if r: cases.append([t, fi, r[0], r[1], p])

    cases = [c for c in cases if c[2] is not None]
    ctx.save("facility.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
