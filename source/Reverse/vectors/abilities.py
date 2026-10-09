# Vectors for Formulas/OfficerAbility.cs: the game's CPersonData::GetAbility (rva 0x22FCE0) run by the oracle on
# every officer of the snapshot, each ability, several bonus flags, then on fuzzed objects (pokes) that reach the
# branches the save does not. Run by run.py; writes abilities.json.gz.
import random, struct
from context import *

FN = 0x22FCE0
COVER = [FN, 0x1EE270, 0x22CA00, 0x2C6E80, 0x3E9BA0, 0x333320]
FLAG_SETS = (0, 1, 2, 4, 8, 0xF, 0xFFF)


def generate(ctx):
    o, rec, OWNED = ctx.o, ctx.rec, ctx.owned
    addr, force_of, poker = ctx.addr, ctx.officer_force, ctx.poker

    cases = []
    for i in range(len(rec[OFFICERS])):
        p = addr(OFFICERS, i)
        for a in range(5):
            for fl in FLAG_SETS:
                cases.append([i, a, fl, o.call32(FN, p, a, fl), []])
    natural = len(cases)

    rng = random.Random(14)
    SPECIAL_IDS = [0x100F, 0x1018, 0x1023, 0x102C, 0x1037, 0x1040, 0x104B, 0x1054, 0x105F, 0x1068, 0x100E, 0x1019, 0x1069, 5001, 5100, 5101]


    def officer_pokes(i, a):
        p = []
        def put(off, data): p.append([f"{OFFICERS:#x}", i, off, data.hex()])
        choices = [
            lambda: put(0x161, bytes([rng.choice([0, 1, 2, 3, 4, 5, 255])])),
            lambda: put(0xE6, bytes([rng.choice([0, 1, 2, 3, 7, 9])])),
            lambda: put(0xE7, bytes([rng.randrange(256)])),
            lambda: put(0x14C, bytes([rng.choice([0, 1, 2, 3, 4, 5, 6, 7, 255])])),
            lambda: put(0x15E, struct.pack("<H", rng.choice([0, 0x4000, 0xFFFF, 0x0001]))),
            lambda: put(0x1C5, bytes([rng.choice([0, 1])])),
            lambda: put(0x1BC, bytes([rng.choice([0, 1, 2, 3])])),
            lambda: put(0x10, struct.pack("<H", rng.choice(SPECIAL_IDS + [rng.randrange(1, 1851)]))),
            lambda: put(0xE2, struct.pack("<H", rng.choice([0, 1, 2, 30, 61, 62, 100, 0xFFFF]))),
            lambda: put(0xE0, bytes([rng.randrange(0, 60)])),
            lambda: put(0xEC + a, bytes([rng.choice([0, 1, 2, 50, 99, 100, 101, 119, 120, 121, 200, 255])])),
            lambda: put(0x1EC, bytes([rng.randrange(256)])),
            lambda: put(0x1ED, bytes([rng.randrange(256)])),
        ]
        for _ in range(rng.randint(1, 4)): rng.choice(choices)()
        return p


    def other_pokes(i, a):
        p = []
        f = force_of(i)
        oid = struct.unpack_from("<H", rec[OFFICERS][i], 0x10)[0]

        def give_item():
            # only items on the game's owned-items list: giving one off the list is a state the game never holds
            k = rng.choice(OWNED)
            p.extend([[f"{ITEMS:#x}", k, 0x4A, struct.pack("<H", oid).hex()],
                      [f"{ITEMS:#x}", k, 0x45, bytes([rng.randrange(0, 7)]).hex()],
                      [f"{ITEMS:#x}", k, 0x46, bytes([rng.choice([0, 1, 5, 9, 10, 11, 20, 255])]).hex()]])
        def give_seal():
            # 옥새 (item 123): onto the owned-items list first when nobody holds it
            if 123 not in OWNED: p.append(["list", "0x110", 123, ""])
            p.append([f"{ITEMS:#x}", 123, 0x4A, struct.pack("<H", oid).hex()])

        def give_unlisted():
            # an item off the list, put on it the way the game would
            k = rng.choice([x for x in range(202) if x not in OWNED])
            p.extend([["list", "0x110", k, ""], [f"{ITEMS:#x}", k, 0x4A, struct.pack("<H", oid).hex()],
                      [f"{ITEMS:#x}", k, 0x45, bytes([a + 1]).hex()], [f"{ITEMS:#x}", k, 0x46, bytes([rng.randrange(0, 15)]).hex()]])
        def doctrine_bits():
            # high 주의 numbers reach panel bits past the ten bytes at +0x1CB and entries past 0x4F
            p.append([f"{OFFICERS:#x}", i, 0x14C, bytes([rng.choice([17, 20, 40, 51, 52, 200, 255])]).hex()])
            for _ in range(4):
                p.append([f"{FORCES:#x}", f, 0x1CB + rng.randrange(42), bytes([rng.randrange(256)]).hex()])

        def corps():
            c = rec[OFFICERS][i][0xE0]
            c = c if c <= 0x33 else 0
            p.extend([[f"{CORPS:#x}", c, 0x10, bytes([rng.choice([0, 1, 0x33, 0x34, 0x2E, 0x32, 200, 255])]).hex()],
                      [f"{CORPS:#x}", c, 0x11, bytes([rng.choice([0, 1, 5])]).hex()],
                      [f"{CORPS:#x}", c, 0x12, struct.pack("<H", rng.choice([0, 1, 300])).hex()]])
        kinds = [
            give_unlisted,
            doctrine_bits,
            corps,
            lambda: p.append(["vec40", rec[OFFICERS][i][0xE7], rng.choice([0, 0x10, 0x13, 0x17]), ""]),
            lambda: p.append([f"{FORCES:#x}", f, 0x47, bytes([rng.randrange(0, 15)]).hex()]),
            lambda: p.append([f"{FORCES:#x}", f, 0x1CB + rng.randrange(10), bytes([rng.randrange(256)]).hex()]),
            lambda: p.append([f"{FORCES:#x}", f, 0x1D5 + rng.randrange(10), bytes([rng.randrange(256)]).hex()]),
            lambda: p.append([f"{FORCES:#x}", f, 0x10, struct.pack("<H", rng.choice([0, 1, oid])).hex()]),
            lambda: p.append([f"{ITEMS:#x}", rng.choice(OWNED), 0x4A, struct.pack("<H", oid).hex()]),
            give_item,
            give_seal,
            lambda: p.append(["settings", 0, 0xE58, struct.pack("<I", rng.choice([0, 1])).hex()]),
            lambda: p.append(["settings", 0, 0x4C4, struct.pack("<I", rng.choice([0, 1, 2])).hex()]),
            lambda: p.append(["settings", 0, 0x128, struct.pack("<I", rng.choice([0, 1, 2])).hex()]),
            lambda: p.append([f"{RANKS:#x}", rng.randrange(13), 0xAD + a, bytes([rng.randrange(256)]).hex()]),
            lambda: p.append([f"{OFFICES:#x}", rec[OFFICERS][i][0xE7], 0x34, struct.pack("<H", rng.choice([0, 1])).hex()]),
            lambda: p.append([f"{OFFICES:#x}", rec[OFFICERS][i][0xE7], 0x38 + a, bytes([rng.randrange(256)]).hex()]),
            lambda: p.append([f"{DOCTRINE:#x}", rng.randrange(80), 0x44, struct.pack("<h", rng.randrange(-200, 200)).hex()]),
        ]
        for _ in range(rng.randint(1, 3)): rng.choice(kinds)()
        return p


    for n in range(12000):
        i = rng.randrange(len(rec[OFFICERS]))
        a = rng.randrange(5)
        fl = rng.choice(FLAG_SETS + (rng.randrange(16),))
        pokes = officer_pokes(i, a) + (other_pokes(i, a) if rng.random() < 0.7 else [])
        undo = poker.apply(pokes)
        cases.append([i, a, fl, o.call32(FN, addr(OFFICERS, i), a, fl), pokes])
        poker.undo(undo)

    ctx.save("abilities.json.gz", {"function": f"{FN:#x}", "world": "world.json.gz", "cases": cases})
    return f"{natural} natural + {len(cases) - natural} fuzzed"
