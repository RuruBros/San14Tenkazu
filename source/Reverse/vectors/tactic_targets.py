# Native tactic getters, target lists (0x16BE90), and the troop capacity used by healing targets.
# Expected hex lists come directly from the native pool after the call, before Oracle restores its pages.
import random, struct
from context import *
from battlepower_fx import ring

TACTICS, CITIES, GATES, BUILDINGS, PERSONALITIES = 0x774D0, 0xDD38, 0xDED8, 0x6DA98, 0x7DD18
TARGETS, CAPACITY = 0x16BE90, 0x2B2920
GETTERS = [0x16D510, 0x16D5A0, 0x16D630, 0x16D6C0]
COVER = [TARGETS, CAPACITY, *GETTERS, 0x16FC30, 0x16CDA0, 0x233390, 0x2A7AA0]


def encoded(index, kinds, ranges, amounts=(10, 10), levels=(1, 1), flags=1, ability=1):
    values = [(flags, 1), *[(v, 7) for v in kinds], *[(v, 1) for v in ranges],
              *[(v, 3) for v in amounts], *[(v, 5) for v in levels], (ability, 7)]
    result = bytearray(40)
    for i, (value, rotate) in enumerate(values):
        v = value ^ (index & 255)
        result[4 * i] = ((v >> rotate) | (v << (8 - rotate))) & 255
    return bytes(result)


class TargetOracle:
    def __init__(self, ctx):
        self.ctx, self.o = ctx, ctx.o
        o = self.o
        self.out = o.alloc(0x100)
        self.pool = o.q(ctx.g.exe + world.POOL_RVA)
        count = struct.unpack("<I", o.read(ctx.g.exe + world.POOL_RVA + 0x30, 4))[0]
        indices = []
        for i in range(count - 1, 0, -1):
            if not o.q(self.pool + i * 8): indices.append(i)
            if len(indices) == 2: break
        if len(indices) != 2: raise RuntimeError("no two empty native output pool slots")
        self.indices = indices
        for slot, i in enumerate(indices):
            cell = self.out + 0x80 + 8 * slot
            o.poke(self.out + 8 + 16 * slot, struct.pack("<Q", cell))
            o.poke(cell, struct.pack("<I", i))

    def call(self, tactic, source, target, force, pokes):
        o, ctx = self.o, self.ctx

        def run():
            lists = []

            def read_lists():
                for i in self.indices:
                    items, seen = [], set()
                    node = o.q(self.pool + i * 8)
                    while node:
                        if node in seen: raise RuntimeError("native target list has a cycle")
                        seen.add(node)
                        ptr, node = struct.unpack("<QQ", o.read(node, 16))
                        items.append((ptr - ctx.addr(TILES, 0)) // 0x20)
                    lists.append(items)
                return []

            o.call(TARGETS, self.out, ctx.addr(TACTICS, tactic) if tactic is not None else 0,
                   ctx.addr(TILES, source) if source is not None else 0,
                   ctx.addr(TILES, target) if target is not None else 0, stack_args=[force], read_after=read_lists)
            return lists
        return ctx.run(pokes, run)


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(161690)
    readable = [int(i) for i, e in ctx.snap["tables"][f"{TACTICS:#x}"]["extra"].items() if "vec70" in e]
    if not readable: raise RuntimeError("no tactic vectors readable in this snapshot")
    units, tiles, buildings = rec[UNITS], rec[TILES], rec[BUILDINGS]

    def word(b, at): return struct.unpack_from("<H", b, at)[0]
    def poke(t, i, at, data): return [f"{t:#x}", i, at, data.hex()]
    def vec(t, data): return ["vecdata", [f"{TACTICS:#x}", t], 0x70, data.hex()]

    getters = []

    def get(t, slot, bit, pokes):
        def run():
            p = ctx.addr(TACTICS, t)
            return [o.call(f, p, slot) & 255 for f in GETTERS] + [o.call(0x16CDA0, p) & 255, o.call(0x16FC30, p, bit) & 255]
        result = ctx.run(pokes, run)
        if result is not None: getters.append([t, slot, bit, result, pokes])

    for t in readable:
        for slot in (-1, 0, 1, 2):
            for bit in (0, 1, 7, 8, -1): get(t, slot, bit, [])
    for n in range(1500):
        t, slot, bit = rng.randrange(201), rng.choice([-1, 0, 1, 2]), rng.randrange(-1, 10)
        v = encoded(t, [rng.randrange(256) for _ in range(2)], [rng.randrange(256) for _ in range(2)],
                    [rng.randrange(256) for _ in range(2)], [rng.randrange(256) for _ in range(2)], rng.randrange(256), rng.randrange(256))
        if n % 2:
            p = [vec(t, v[:rng.choice([0, 36, 44])]), poke(TACTICS, t, 0x44, bytes([rng.randrange(256)])),
                 poke(TACTICS, t, 0x62, rng.randbytes(9))]
        else: p = [vec(t, v + rng.randbytes(rng.randrange(4)))]
        get(t, slot, bit, p)

    print(f"  tactic getters: {len(getters)} cases, oracle failures {len(ctx.failures)}", flush=True)

    native = TargetOracle(ctx)
    targets = []
    live = [i for i in range(1, len(units)) if units[i][0x10] and word(units[i], 0x12)]
    unit_hexes = [word(units[i], 0x2A) for i in live]
    building_hexes = [word(buildings[i], 0x10) for i in range(1, len(buildings)) if buildings[i][0x12] and word(buildings[i], 0x10) < 48400]
    places = list(dict.fromkeys(unit_hexes + building_hexes))

    def target(t, source, dest, force, p):
        result = native.call(t, source, dest, force, p)
        if result is not None: targets.append([t, source, dest, force, result, p])

    for t in readable:
        for source in unit_hexes + building_hexes[:15]:
            dest = rng.choice(places)
            u = struct.unpack_from("<h", tiles[source], 0x1C)[0]
            force = ctx.officer_force(ctx.officer_by_id(word(units[u], 0x12))) if 0 < u <= 500 else tiles[source][0x14]
            target(t, source, dest, force, [])
    natural = len(targets)
    print(f"  natural targets: {natural} cases, oracle failures {len(ctx.failures)}", flush=True)

    # Every effect kind on an actual map position, in both slots, with both encoded and plain data.
    for n in range(6000):
        t, kind = rng.randrange(1, 201), n % 24
        source = rng.choice(places + [0, 219, 48399])
        dest = rng.choice([source] + ring(source, 4) + places)
        force = rng.randrange(0x34)
        ranges, flags = [rng.randrange(4), rng.randrange(4)], rng.randrange(4)
        kinds = [kind, rng.randrange(24)] if n % 2 else [rng.randrange(24), kind]
        if n % 3:
            p = [vec(t, encoded(t, kinds, ranges, flags=flags))]
        else:
            p = [vec(t, b""), poke(TACTICS, t, 0x44, bytes([flags])), poke(TACTICS, t, 0x62, bytes(kinds + ranges))]
        for h in (source, dest):
            u = struct.unpack_from("<h", tiles[h], 0x1C)[0]
            b = word(tiles[h], 0x1A)
            if 0 < u <= 500:
                p += [poke(UNITS, u, 0x23, rng.randbytes(3)), poke(UNITS, u, 0x18, struct.pack("<H", rng.choice([0, 1, 20000]))),
                      poke(UNITS, u, 0x16, struct.pack("<H", rng.choice([0, 1, 9999, 20000, 65535])))]
                if n % 13 == 0: p.append(["list", "0x70", [[f"{UNITS:#x}", u]], ""])
            if 0 < b <= 61:
                tab, i = (CITIES, b) if b < 52 else (GATES, b - 51)
                p += [poke(tab, i, 0x49, rng.randbytes(3)), poke(BUILDINGS, b, 0x13, bytes([rng.choice([force, 0, rng.randrange(52)])]))]
                if n % 17 == 0: p.append(["list", "0x140", [None, [f"{tab:#x}", i], None], ""])
        if n % 7 == 0: p.append(poke(FORCES, force, 0xEA + tiles[dest][0x14], bytes([rng.choice([0, 1, 0x80])])) )
        target(t, source, dest, force, p)
        if (n + 1) % 1000 == 0: print(f"  fuzzed targets: {n + 1}/6000, oracle failures {len(ctx.failures)}", flush=True)
    for t, source, dest in [(None, places[0], places[-1]), (readable[0], None, places[0]), (0, places[0], None)]:
        target(t, source, dest, 1, [])
    for kind in range(24):
        t = readable[-1]
        target(t, places[0], None, 1, [vec(t, encoded(t, [kind, kind], [1, 1]))])

    capacities = []
    for i in range(len(rec[OFFICERS])):
        result = ctx.run([], lambda: o.call32(CAPACITY, ctx.addr(OFFICERS, i)))
        if result is not None: capacities.append([i, result, []])
    for n in range(2000):
        i = rng.randrange(1, len(rec[OFFICERS]))
        p = [poke(OFFICERS, i, 0xE6, bytes([rng.choice([1, 2, 3, 4, 6])])), poke(OFFICERS, i, 0xE7, bytes([rng.randrange(256)]))]
        force = ctx.officer_force(i)
        p.append(poke(FORCES, force, 0x47, bytes([rng.randrange(16)])))
        for slot in range(3):
            pid = rng.randrange(1, 356)
            p += [poke(OFFICERS, i, 0x118 + slot * 2, struct.pack("<H", pid)),
                  poke(PERSONALITIES, pid, 0xB6, struct.pack("<Bx h Bx h B", 0x4D, rng.choice([-101, -100, 100, 2000, 3000]), 0x4D, 500, 16)),
                  ["vec", [f"{PERSONALITIES:#x}", pid], [0xC8, 0], ""]]
        result = ctx.run(p, lambda: o.call32(CAPACITY, ctx.addr(OFFICERS, i)))
        if result is not None: capacities.append([i, result, p])
    ctx.save("tactic_targets.json.gz", {"getters": getters, "targets": targets, "capacity": capacities, "natural_targets": natural,
             "readable_tactics": readable, "world": "world.json.gz"})
    return f"getters {len(getters)}, targets {natural} natural + {len(targets) - natural} fuzzed, capacity {len(capacities)}, readable tactics {len(readable)}/201"
