# Vectors for Engagement: one engagement step as the game runs it, on records built in scratch — the powers,
# attack and defense values and the damage (0x16A030, 0x16AB50, 0x16A990, 0x16A780 in turn) and then the damage
# applied to the defender (0x1749A0 → 0x178E30), with the events, experience, destruction, messages and animations
# stubbed out; and the siege path (0x174BC0 → 0x29E840) for a base or building defender. Live units and bases,
# then fuzzed troops, morale, wounded, splits, provocation, 개성 and options. Run by run.py; writes
# engagement.json.gz:
#   fight: [a, b, [powerA, atkA, defA, powerB, atkB, defB] bits, damage, [troops, wounded, morale, gold, food,
#           lit] of the defender unit after (or [troops, wounded, morale] of the base), pokes]
#   siege: [a, b, durability after, fell, pokes]
# where a side is [index, kind, hex, force, split].
import gzip, json, random, struct, sys
from context import *
from reach import ring

POWERS, ATTACKS, DEFENSES, DAMAGE, APPLY, STEP1 = 0x16A030, 0x16AB50, 0x16A990, 0x16A780, 0x1749A0, 0x174BC0
CITIES, GATES, BUILDINGS, PERSONALITIES = 0xDD38, 0xDED8, 0x6DA98, 0x7DD18
COVER = [POWERS, ATTACKS, DEFENSES, DAMAGE, APPLY, 0x178E30, STEP1, 0x2AA2E0, 0x29E840, 0x270550, 0x2238F0, 0x169B70, 0x241620, 0x22E530, 0x267DB0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, buildings, cities, gates, pers, tiles = rec[UNITS], rec[OFFICERS], rec[BUILDINGS], rec[CITIES], rec[GATES], rec[PERSONALITIES], rec[TILES]
    rng = random.Random(239)
    cached = {}
    if '--refresh-apply' in sys.argv:
        if not ctx.frozen: raise SystemExit('--refresh-apply requires the same frozen snapshot.')
        with gzip.open(os.path.join(OUT, 'engagement.json.gz'), 'rt') as f: previous = json.load(f)
        # Reuse only previously recorded powers and damage from the same snapshot. Every application is
        # executed again by the oracle; new inputs execute every stage. No C# outputs enter the vectors.
        # Keep the complete preceding call sequence for base applications as an additional cross-check.
        cached = {json.dumps([c[0], c[1], c[5]]): c for c in previous['fight'] if c[1][1] == 0x1B}
    for rva, value in ((0x2571F0, 0), (0x2B8EC0, 0), (0x174520, 0), (0x1A3A00, 0), (0x1A3C50, 0), (0x2ECB80, 0), (0x2E3050, 0),
                       (0x294B90, 0), (0x31EAB0, 1), (0x1B4990, 0)):
        o.stub(rva, value)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    live = [i for i in range(len(units)) if units[i][0x10] and u16(units[i], 0x12)]
    empty = [i for i in range(1, len(units)) if i not in live]
    leaders = [k for k in range(1, len(officers)) if officers[k][0xE6] in (2, 3, 4, 5, 6, 7) and ctx.officer_force(k) and u16(officers[k], 0x10) < 0x1771]

    def unit_force(i):
        return ctx.officer_force(ctx.officer_by_id(u16(units[i], 0x12)))

    def unit_side(i, split=1):
        return [i, 0x1B, u16(units[i], 0x2A), unit_force(i), split]

    def base_side(kind, n):
        b = buildings[n if kind == 5 else 0x33 + n]
        return [n, kind, u16(b, 0x10), b[0x13], 1]

    side_a = o.alloc(0x40); node = o.alloc(0x100); buf = o.alloc(0x40); mgr = o.alloc(0x100)

    def put_record(a, b):
        o.poke(side_a, struct.pack("<HxxIIIIBxxx", a[0], a[1], 0, a[2], a[3], a[4]))
        o.poke(node, struct.pack("<QHxxIIIIBxxx", side_a, b[0], b[1], 0, b[2], b[3], b[4]) + bytes(0x100 - 0x24))
        o.poke(buf, bytes(0x40)); o.poke(mgr, bytes(0x100))

    def defender_ptr(b):
        if b[1] == 0x1B: return ctx.addr(UNITS, b[0]), 0x200
        return (ctx.addr(CITIES, b[0]), 0x168) if b[1] == 5 else (ctx.addr(GATES, b[0]), 0xC0)

    def fight(a, b, pokes):
        def run():
            put_record(a, b)
            # the units' hexes in the record follow the (possibly poked) records
            o.poke(side_a + 0xC, struct.pack("<I", u16(o.read(ctx.addr(UNITS, a[0]), 0x40), 0x2A) if a[1] == 0x1B else a[2]))
            o.poke(node + 0x14, struct.pack("<I", u16(o.read(ctx.addr(UNITS, b[0]), 0x40), 0x2A) if b[1] == 0x1B else b[2]))
            prior = cached.get(json.dumps([a, b, pokes]))
            if prior is None:
                o.call(POWERS, node, mgr, buf)
                o.call(ATTACKS, node, buf)
                o.call(DEFENSES, node, buf)
                o.call(DAMAGE, node, buf)
                floats = list(struct.unpack("<8I", o.read(buf, 32)))
                bits = [floats[0], floats[1], floats[2], floats[5], floats[6], floats[7]]
                damage = struct.unpack("<h", o.read(buf + 0x20, 2))[0]
            else:
                bits, damage = prior[2], prior[3]
                o.poke(buf + 0x20, struct.pack("<h", damage))
            dp, dn = defender_ptr(b)
            cmd = ctx.addr(OFFICERS, ctx.officer_by_id(u16(o.read(dp, 0x14), 0x12))) if b[1] == 0x1B else 0
            after = [(dp, dn)] + ([(cmd, 0x1F0)] if cmd else [])
            o.call(APPLY, 0, node, buf, read_after=after)
            d = o.after[0]
            if b[1] == 0x1B:
                c = o.after[1]
                res = [u16(d, 0x16), u16(d, 0x18), d[0x1A], struct.unpack_from("<i", d, 0x5C)[0], struct.unpack_from("<i", d, 0x60)[0], u16(c, 0x184)]
            else:
                res = [struct.unpack_from("<i", d, 0x3C)[0], struct.unpack_from("<i", d, 0x40)[0], d[0x44]]
            return [bits, damage, res]
        return ctx.run(pokes, run)

    def siege(a, b, pokes):
        def run():
            put_record(a, b)
            bidx = b[0] if b[1] == 5 else 0x33 + b[0] if b[1] == 6 else b[0]
            bp = ctx.addr(BUILDINGS, bidx)
            r = o.call(STEP1, 0, node, read_after=[(bp, 0x20)]) & 0xFF
            return [u16(o.after[0], 0x14), r]
        return ctx.run(pokes, run)

    base_sides = [base_side(5, c) for c in range(1, 0x34) if u16(buildings[c], 0x10) < 0xBD10] + [base_side(6, g) for g in range(1, 11) if u16(buildings[0x33 + g], 0x10) < 0xBD10]
    fights, sieges = [], []
    for i in live:
        for j in rng.sample(live, min(6, len(live))):
            if i == j: continue
            r = fight(unit_side(i), unit_side(j), [])
            if r is not None: fights.append([unit_side(i), unit_side(j), *r, []])
        for b in rng.sample(base_sides, 3):
            r = fight(unit_side(i), b, [])
            if r is not None: fights.append([unit_side(i), b, *r, []])
            r = fight(b, unit_side(i), [])
            if r is not None: fights.append([b, unit_side(i), *r, []])
            r = siege(unit_side(i), b, [])
            if r is not None: sieges.append([unit_side(i), b, *r, []])
    natural = (len(fights), len(sieges))
    print(f'  natural fight {natural[0]}, siege {natural[1]}, failures {len(ctx.failures)}')

    def unit_pokes(slot, cmd, hex_):
        return [[f"{UNITS:#x}", slot, 0x10, bytes([rng.choice([1, 1, 2])]).hex()], [f"{UNITS:#x}", slot, 0x12, struct.pack("<H", u16(officers[cmd], 0x10)).hex()],
                [f"{UNITS:#x}", slot, 0x16, struct.pack("<H", rng.choice([1, 500, 3000, 9000, 20000])).hex()], [f"{UNITS:#x}", slot, 0x18, struct.pack("<H", rng.choice([0, 100, 19990])).hex()],
                [f"{UNITS:#x}", slot, 0x1A, bytes([rng.choice([0, 1, 50, 100, 120])]).hex()], [f"{UNITS:#x}", slot, 0x1B, bytes([rng.randrange(1, 21)]).hex()],
                [f"{UNITS:#x}", slot, 0x2A, struct.pack("<H", hex_).hex()], [f"{TILES:#x}", hex_, 0x1C, struct.pack("<h", slot).hex()]]

    for n in range(6000):
        if n and n % 500 == 0: print(f'  fuzz {n}/6000: fight {len(fights)}, siege {len(sieges)}, failures {len(ctx.failures)}')
        a_live, b_live = rng.random() < 0.5, rng.random() < 0.5
        p = []
        if a_live: ia = rng.choice(live); a = unit_side(ia, rng.choice([1, 1, 2, 3]))
        else:
            ia = rng.choice(empty); cmd = rng.choice(leaders); h = rng.randrange(0xBD10)
            p += unit_pokes(ia, cmd, h); a = [ia, 0x1B, h, ctx.officer_force(cmd), rng.choice([1, 2])]
        kind = rng.choice(["unit", "unit", "unit", "base"])
        if kind == "unit":
            if b_live: ib = rng.choice([x for x in live if x != ia]); b = unit_side(ib, rng.choice([1, 1, 2]))
            else:
                ib = rng.choice([x for x in empty if x != ia]); cmd = rng.choice(leaders)
                near = [t for t in (a[2] - 1, a[2] + 1, a[2] - 220, a[2] + 220, a[2] + 221, a[2] - 221, a[2] + 2) if 0 <= t < 0xBD10]
                h = rng.choice(near); p += unit_pokes(ib, cmd, h); b = [ib, 0x1B, h, ctx.officer_force(cmd), 1]
        else:
            b = rng.choice(base_sides)
        for _ in range(rng.randint(0, 3)):
            k = rng.randrange(8)
            if k == 0: p.append([f"{UNITS:#x}", a[0], 0x25, bytes([rng.choice([0, 3])]).hex()]); p.append([f"{UNITS:#x}", a[0], 0x3E, struct.pack("<H", rng.choice([b[0], 0])).hex()])
            elif k == 1 and b[1] == 0x1B: p.append([f"{UNITS:#x}", b[0], 0x25, bytes([3]).hex()]); p.append([f"{UNITS:#x}", b[0], 0x3E, struct.pack("<H", rng.choice([a[0], 0])).hex()])
            elif k == 2:
                p.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
                p.append(["settings", 0, 0xCC, struct.pack("<i", rng.choice([0, 0, 1])).hex()])
                p.append(["settings", 0, 8 + 0x294, struct.pack("<i", rng.choice([0, 50, 100, 150])).hex()])
            elif k == 3 and b[1] == 0x1B: p.append([f"{UNITS:#x}", b[0], rng.choice([0x16, 0x18, 0x1A]), struct.pack("<H", rng.choice([0, 1, 30, 3000])).hex()[:4 if rng.random() < 0.5 else 2]])
            elif k == 4 and b[1] != 0x1B:
                t, i = (CITIES, b[0]) if b[1] == 5 else (GATES, b[0])
                p.append([f"{t:#x}", i, rng.choice([0x3C, 0x40]), struct.pack("<i", rng.choice([0, 1, 500, 9000])).hex()])
                p.append([f"{t:#x}", i, 0x44, bytes([rng.choice([0, 1, 60, 100])]).hex()])
            elif k == 5 and b[1] == 0x1B:
                cmd = ctx.officer_by_id(u16(units[b[0]], 0x12)) if b_live else None
                if cmd:
                    x = rng.choice([x for x in range(1, 356) if pers[x][0xA4] == 0x1E] or [1])
                    p.append([f"{OFFICERS:#x}", cmd, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
            elif k == 6 and b[1] == 0x1B: p.append([f"{UNITS:#x}", b[0], 0x5C, struct.pack("<i", rng.choice([0, 100, 50000])).hex()]); p.append([f"{UNITS:#x}", b[0], 0x60, struct.pack("<i", rng.choice([0, 1000, 400000])).hex()])
            elif k == 7 and a[1] == 0x1B: p.append([f"{UNITS:#x}", a[0], 0x4E, struct.pack("<b", rng.choice([-5, 0, 5])).hex()])
        p = [x for x in p if len(x[3]) in (2, 4, 8)]
        if kind == "base" and rng.random() < 0.5:
            r = siege(a, b, p)
            if r is not None: sieges.append([a, b, *r, p])
        else:
            r = fight(a, b, p)
            if r is not None: fights.append([a, b, *r, p])

    # The random pairs above mostly test Step1's rejection path. Place an attacker within an enemy target's
    # reach explicitly, including standalone buildings, so siege execution and falls are actually exercised.
    targets = list(base_sides)
    kinds = rec[0x73958]
    for bid in range(0x3E, len(buildings)):
        h, kind = u16(buildings[bid], 0x10), buildings[bid][0x12]
        if h < 0xBD10 and 0 < kind < len(kinds) and kinds[kind][0x2C] in (2, 3) and u16(tiles[h], 0x1A) == bid:
            targets.append([bid, 0xB, h, buildings[bid][0x13], 1])
    for n in range(600):
        b = rng.choice(targets)
        near = [h for h in ring(b[2], 2 if b[1] == 5 else 1) if u16(tiles[h], 0x1A) == 0]
        commands = [cmd for cmd in leaders if 0 < ctx.officer_force(cmd) < 46 and ctx.officer_force(cmd) != b[3]]
        if not near or not commands: continue
        ia, cmd, h = rng.choice(empty), rng.choice(commands), rng.choice(near)
        force = ctx.officer_force(cmd)
        a = [ia, 0x1B, h, force, 1]
        bid = b[0] if b[1] in (5, 0xB) else b[0] + 0x33
        p = unit_pokes(ia, cmd, h) + [[f"{UNITS:#x}", ia, 0x23, "000000"],
            [f"{FORCES:#x}", force, 0xEA + b[3], "00"], [f"{FORCES:#x}", force, 0x194, "00"],
            [f"{FORCES:#x}", b[3], 0x194, "00"],
            [f"{BUILDINGS:#x}", bid, 0x14, struct.pack("<H", rng.choice([1, 2, 50, 1000, 9999])).hex()]]
        r = siege(a, b, p)
        if r is not None: sieges.append([a, b, *r, p])
    print(f'  siege execution cases added; failures {len(ctx.failures)}')
    ctx.save("engagement.json.gz", {"fight": fights, "siege": sieges, "world": "world.json.gz"})
    return f"fight {natural[0]}+{len(fights) - natural[0]}, siege {natural[1]}+{len(sieges) - natural[1]}"
