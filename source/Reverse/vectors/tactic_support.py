# Morale recovery, ailment removal and wounded recovery: execute each complete native leaf and its mutators.
# Input lists and the output std::vector live in emulated scratch; no gameplay function is stubbed.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, TargetOracle, encoded

FUNCTIONS = {15: 0x176320, 19: 0x178260, 20: 0x177E40}
COVER = [*FUNCTIONS.values(), 0x16B640, 0x16D870, 0x241620, 0x241690, 0x231320, 0x17A130]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x177E40)
    native = TargetOracle(ctx)
    idx = native.indices[0]
    container = native.out
    info, nodes, events = o.alloc(0x100), o.alloc(0x1000), o.alloc(0x1000)
    vector = info + 0x80
    obj_out = info + 0x60
    readable = [int(i) for i, e in ctx.snap["tables"][f"{TACTICS:#x}"]["extra"].items() if "vec70" in e and int(i)]

    def word(b, at): return struct.unpack_from("<H", b, at)[0]
    def poke(t, i, at, data): return [hex(t), i, at, data.hex()]

    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and word(rec[UNITS][i], 0x12)]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 52) if rec[CITIES][i][0x69] == 0]
    sources += [[None, [hex(GATES), i]] for i in range(1, 11) if rec[GATES][i][0x69] == 0] + [[None, None], [0, None]]
    places = [word(rec[UNITS][i], 0x2A) for i in live]
    places += [word(rec[BUILDINGS][i], 0x10) for i in range(1, len(rec[BUILDINGS])) if rec[BUILDINGS][i][0x12] and word(rec[BUILDINGS][i], 0x10) < 48400]
    cases, factors = [], []

    def setup(t, source, links):
        unit, base = source
        o.poke(info, bytes(0x100))
        o.poke(info, struct.pack("<QQQQ", ctx.addr(TACTICS, t), links,
            ctx.addr(UNITS, unit) if unit is not None else 0, ctx.addr(int(base[0], 16), base[1]) if base else 0))
        o.poke(info + 0x28, struct.pack("<Q", vector))
        o.poke(vector, struct.pack("<QQQ", events, events, events + 0x1000))

    def factor(t, source, p):
        def run():
            setup(t, source, 0)
            return [struct.pack("<f", o.call(f, info, ret_float=True)).hex() for f in (0x16B640, 0x16D870)]
        result = ctx.run(p, run)
        if result is not None: factors.append([t, source, result, p])

    def apply(t, source, slot, links, kind, targets, p):
        def run():
            setup(t, source, links)
            for i, h in enumerate(targets):
                o.poke(nodes + 16 * i, struct.pack("<QQ", ctx.addr(TILES, h), nodes + 16 * (i + 1) if i + 1 < len(targets) else 0))
            old_head = o.read(native.pool + idx * 8, 8)
            o.poke(native.pool + idx * 8, struct.pack("<Q", nodes if targets else 0))
            records = []
            try:
                for h in targets:
                    o.call(0x16EA90, obj_out, obj_out + 4, ctx.addr(TILES, h), read_after=[(obj_out, 8)])
                    k, i = struct.unpack("<II", o.after[0])
                    table = UNITS if k == 27 else CITIES if k == 5 else GATES if k == 6 else None
                    if table is not None and (table, i) not in records: records.append((table, i))
                reads = [(ctx.addr(tab, i), world.TABLES[tab][1]) for tab, i in records] + [(vector, 24), (events, 16 * len(targets))]
                o.call(FUNCTIONS[kind], info, slot, container, read_after=reads)
                changed = [[hex(tab), i, o.after[n].hex()] for n, (tab, i) in enumerate(records)]
                begin, end, cap = struct.unpack("<QQQ", o.after[-2])
                if begin != events or end > cap: raise RuntimeError("unexpected native result vector storage")
                output = []
                for at in range(0, end - begin, 16):
                    k, i, amount, unused, h = struct.unpack_from("<IIHHI", o.after[-1], at)
                    output.append([k, i, h, amount])
                return [changed, output]
            finally:
                o.poke(native.pool + idx * 8, old_head)
        result = ctx.run(p, run)
        if result is not None: cases.append([t, source, slot, links, kind, targets, result[0], result[1], p])

    for source in sources:
        t = rng.choice(readable)
        factor(t, source, [])
        for kind in FUNCTIONS:
            apply(t, source, 0, 0, kind, rng.sample(places, 3), [])
    for n in range(4000):
        t, source, slot, links, kind = rng.randrange(1, 201), rng.choice(sources), rng.randrange(2), rng.randrange(8), list(FUNCTIONS)[n % 3]
        amounts, ability = [rng.randrange(256), rng.randrange(256)], rng.choice([0, 1, 2, 255])
        if n % 4 == 1:
            p = [["vecdata", [hex(TACTICS), t], 0x70, ""], poke(TACTICS, t, 0x66, bytes(amounts)), poke(TACTICS, t, 0x6A, bytes([ability]))]
        else:
            p = [["vecdata", [hex(TACTICS), t], 0x70, encoded(t, [kind, kind], [1, 1], amounts, ability=ability).hex()]]
        targets = rng.choices(places + [0, 48399], k=rng.randrange(5))
        unit, base = source
        if unit is not None:
            p += [poke(UNITS, unit, 0x21, bytes([rng.randrange(2), rng.randrange(2)]))]
        elif base:
            tab, i = int(base[0], 16), base[1]
            if n % 10 == 0: p.append(poke(tab, i, 0x2E, struct.pack("<H", rng.choice([0, 6000, 65535]))))
        for h in set(targets):
            u = struct.unpack_from("<h", rec[TILES][h], 0x1C)[0]
            b = word(rec[TILES][h], 0x1A)
            if 0 < u <= 500:
                p += [poke(UNITS, u, 0x16, struct.pack("<HHB", rng.choice([0, 1, 9999, 20000, 65535]), rng.choice([0, 1, 1000, 20000]), rng.randrange(256))),
                      poke(UNITS, u, 0x23, rng.randbytes(3)), poke(UNITS, u, 0x3C, bytes([rng.randrange(256)])),
                      poke(UNITS, u, 0x3E, struct.pack("<H", rng.randrange(65536)))]
                if n % 23 == 0: p.append(["list", "0x70", [[hex(UNITS), u]], ""])
            if 0 < b < 62:
                tab, i = (CITIES, b) if b < 52 else (GATES, b - 51)
                p += [poke(tab, i, 0x3C, struct.pack("<iiB", rng.choice([0, 1, 200000, 300000]), rng.choice([0, 1, 50000, 300000]), rng.randrange(256))),
                      poke(tab, i, 0x49, rng.randbytes(3))]
        if n % 4 in (0, 1): factor(t, source, p)
        apply(t, source, slot, links, kind, targets, p)
        if (n + 1) % 1000 == 0: print(f"  support {n + 1}/4000, oracle failures {len(ctx.failures)}", flush=True)
    ctx.save("tactic_support.json.gz", {"factors": factors, "apply": cases, "world": "world.json.gz"})
    return f"factors {len(factors)}, support applications {len(cases)}"
