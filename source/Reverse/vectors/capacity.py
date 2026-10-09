# Direct vectors for Engagement.MaxTroops (0x22E530 -> 0x267DB0). Damage application seldom reaches the
# capacity clamp, so verify city sizes, tribe home cities, gate doctrine flags and the runtime extra directly.
import random, struct
from context import *

CITIES, GATES, BUILDINGS, COUNTIES = 0xDD38, 0xDED8, 0x6DA98, 0x6CAF0
FN = 0x22E530
COVER = [FN, 0x267DB0, 0x232FB0]


def generate(ctx):
    rng = random.Random(241)
    cases = []

    def check(table, index, pokes):
        result = ctx.run(pokes, lambda: ctx.o.call32(FN, ctx.addr(table, index)))
        if result is not None: cases.append([f"{table:#x}", index, result, pokes])

    def owner(table, index, force):
        # Base_Force reads a city's corps or a gate's county centre, not the building's owner byte.
        if table == CITIES:
            corps = ctx.rec[CITIES][index][0x30]
            return [[f"{CORPS:#x}", corps, 0x10, bytes([force, 1]).hex()]]
        bid = struct.unpack_from('<H', ctx.rec[GATES][index], 0x4E)[0]
        hex_ = struct.unpack_from('<H', ctx.rec[BUILDINGS][bid], 0x10)[0]
        county = struct.unpack_from('<H', ctx.rec[TILES][hex_], 0x12)[0]
        centre = struct.unpack_from('<H', ctx.rec[COUNTIES][county], 0x3A)[0]
        return [[f"{COUNTIES:#x}", county, 0x34, bytes([max(1, force)]).hex()],
                [f"{TILES:#x}", centre, 0x14, bytes([force]).hex()]]

    for table in (CITIES, GATES):
        for index in range(len(ctx.rec[table])): check(table, index, [])
    natural = len(cases)
    # Exercise all tribe home and non-home ownership combinations directly.
    for city in range(0x2F, 0x34):
        for force in range(0x2E, 0x33):
            check(CITIES, city, owner(CITIES, city, force))
    for _ in range(2500):
        table = rng.choice([CITIES, CITIES, GATES])
        index = rng.randrange(1, len(ctx.rec[table]))
        bid = struct.unpack_from("<H", ctx.rec[table][index], 0x4E)[0]
        force = rng.randrange(52)
        pokes = owner(table, index, force) + [[f"{BUILDINGS:#x}", bid, 0x13, bytes([force]).hex()],
                ["exe", "capacity_extra", 0, struct.pack("<i", rng.choice([0, 1, 50000])).hex()]]
        if table == CITIES:
            pokes.append([f"{CITIES:#x}", index, 0xB8, bytes([rng.choice([0, 1, 2, 3, 4, 5, 255])]).hex()])
        else:
            pokes += [[f"{FORCES:#x}", force, off, bytes([rng.choice([0, 0x80])]).hex()] for off in (0x1D1, 0x1DB)]
        check(table, index, pokes)
    ctx.save("capacity.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
