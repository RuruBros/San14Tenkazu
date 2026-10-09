# Who holds the cities and gates, read from the running game (read-only), and the scenario events' 거점 소유자 변경
# run in the oracle: what the editor's [소속 주기] calls (GameMemory.GiveBase). Findings in notes/bases.md.
#   python bases.py list                  every city and gate: corps (its force, number, leader), building force, 태수,
#                                         gold, food, troops, durability
#   python bases.py give <base> <force>   runs exe+0x3A9300 (S14Event::SExecChangeFootholdOwner) in the oracle with the
#                                         base and the force's ruler (the game is not touched) and prints what it wrote:
#                                         the base and its building, the counties and hexes, other bases, and the rest
#                                         by place. The game must stand still ([정지]): the oracle copies its pages as it
#                                         reads them, and a moving game mixes moments (once gave other cities' 태수 away).
#   --quiet-map                           the two map redraws (exe+0x179A00, 0x190860) return at once: for a gate they
#                                         allocate where the oracle cannot follow the game's heap
import struct, sys

from game import Game

WORLD = 0x277DF18                     # [exe+] the game's world; the tables below are base + offset (base = world + 0x290)
CITIES, FORCES, CORPS, TILES, COUNTIES, BUILDINGS = 0xDAA8, 0xDCA0, 0xDE40, 0xDFE0, 0x6C860, 0x6D808
OFFICER_BY_ID = 0x158                 # world + 0x158 + id × 8: the officers by ID
GIVE = 0x3A9300
CITY_SIZE, GATE_SIZE, TILE_SIZE, COUNTY_SIZE = 0x168, 0xC0, 0x20, 0x98
QUIET = (0x179A00, 0x190860)


def u16(b, o): return struct.unpack_from("<H", b, o)[0]
def i32(b, o): return struct.unpack_from("<i", b, o)[0]


def bases(read, q, base):
    # base ID (+0x4E) -> record address; the table's slots past the cities hold the tribes, a placeholder and the gates
    out = {}
    for slot in range(63):
        p = q(base + CITIES + slot * 8)
        if p: out[u16(read(p, 0x50), 0x4E)] = p
    return out


def listing():
    g = Game()
    for id_, p in sorted(bases(g.read, g.q, g.base).items()):
        if not (1 <= id_ <= 46 or 52 <= id_ <= 61): continue
        r = g.read(p, 0x48)
        corps = u16(r, 0x30)
        c = g.q(g.base + CORPS + corps * 8)
        cr = g.read(c, 0x14) if c else bytes(0x14)
        b = g.read(g.q(g.base + BUILDINGS + id_ * 8), 0x20)
        gold = f"gold {i32(r, 0x34)} food {i32(r, 0x38)} " if id_ <= 46 else ""
        print(f"{id_:2} {g.utf16(p + 0x12, 4):4} corps {corps:2} (force {cr[0x10]}, #{cr[0x11]}, leader {u16(cr, 0x12)}) "
              f"building force {b[0x13]:2} 태수 {u16(r, 0x2E):4} {gold}troops {i32(r, 0x3C)} durability {u16(b, 0x14)}")


def give(base_id, force, quiet):
    from oracle import Oracle, PAGE
    o = Oracle()
    g = o.g
    world = o.q(g.exe + WORLD)
    places = bases(o.read, o.q, g.base)
    ruler = u16(o.read(o.q(g.base + FORCES + force * 8), 0x12), 0x10)
    p = o.q(world + OFFICER_BY_ID + ruler * 8)
    print(f"base {base_id} {g.utf16(places[base_id] + 0x12, 4)} -> force {force}, ruler {ruler} "
          f"{g.utf16(p + 0x12, 7)}{g.utf16(p + 0x20, 7)} (corps {o.read(p + 0xE0, 1)[0]}, base {u16(o.read(p + 0xE2, 2), 0)})")
    if quiet:
        for rva in QUIET: o.stub(rva)
    # what each written page held after the call, before the oracle puts it back
    after = {}
    settle = o._settle
    def keep(k):
        for page in o.pristine: after[page] = bytes(o.mu.mem_read(page, PAGE))
        settle(k)
    o._settle = keep
    ctx = o.alloc(0x100)
    o.poke(ctx, struct.pack("<QQ", ctx + 0x40, 0))
    o.poke(ctx + 0x40 + 0xC, struct.pack("<ii", base_id, ruler))
    print("returned", o.call(GIVE, 0, ctx))
    # where an address lies, by record
    sized = {a: (f"base {i} {g.utf16(a + 0x12, 4)}", CITY_SIZE if i <= 51 else GATE_SIZE) for i, a in places.items()}
    for k in range(3063):
        a = o.q(g.base + BUILDINGS + k * 8)
        if a: sized.setdefault(a, (f"building {k}", 0x20))
    for k in range(501):
        a = o.q(g.base + COUNTIES + k * 8)
        if a: sized.setdefault(a, (f"county {k}", COUNTY_SIZE))
    for k in range(52):
        for table, name, size in ((FORCES, "force", 0x120), (CORPS, "corps", 0x18)):
            a = o.q(g.base + table + k * 8)
            if a: sized.setdefault(a, (f"{name} {k}", size))
    starts = sorted(sized)
    tiles = o.q(g.base + TILES)
    def where(a):
        import bisect
        if tiles <= a < tiles + 220 * 220 * TILE_SIZE: return "tile", (a - tiles) % TILE_SIZE
        k = bisect.bisect_right(starts, a) - 1
        if k >= 0 and a < starts[k] + sized[starts[k]][1]: return sized[starts[k]][0], a - starts[k]
        if g.exe <= a < g.exe + g.exe_size: return "exe", a - g.exe
        return "heap", a
    found = {}
    for page, now in after.items():
        if page in o.own: continue
        was = bytes(o.mu.mem_read(page, PAGE))
        for i in range(PAGE):
            if was[i] != now[i]:
                name, off = where(page + i)
                found.setdefault(name, []).append((off, was[i], now[i]))
    for name in sorted(found, key=lambda n: (n in ("tile", "exe", "heap"), n)):
        changes = found[name]
        if name == "tile":
            print(f"tiles: {len(changes)} bytes; by field:", {f"+{off:#x}": sum(1 for c in changes if c[0] == off) for off in sorted({c[0] for c in changes})})
        elif name in ("exe", "heap"):
            print(f"{name}: {len(changes)} bytes")
        else:
            print(name + ": " + ", ".join(f"+{off:#x} {a:02x}->{b:02x}" for off, a, b in changes[:24]))


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"): sys.stdout.reconfigure(encoding="utf-8")
    if sys.argv[1] == "list":
        listing()
    elif sys.argv[1] == "give":
        give(int(sys.argv[2]), int(sys.argv[3]), "--quiet-map" in sys.argv)
