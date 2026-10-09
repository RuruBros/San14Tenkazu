# Vectors for Experience: the game's 공적 award (rva 0x169530), ability exp and levels (0x2B9D50) and what a battle
# event hands an officer (0x2B8EC0), read back from the officer's record after each call, on the live officers and
# with the amounts, standing, clan, 개성, flags, exp near the level thresholds, options and sliders fuzzed. The
# level-up record map (0x2589C0, message bookkeeping) is stubbed out. Run by run.py; writes experience.json.gz:
#   merit: [officer, amount, merit after, pokes]
#   exp:   [officer, ability, amount, exp after, level after, pokes]
#   gain:  [officer, kind, amount, merit after, exp0 after, level0 after, pokes]
import random, struct
from context import *

MERIT, EXP, GAIN = 0x169530, 0x2B9D50, 0x2B8EC0
PERSONALITIES, LEVELS = 0x7DD18, 0x277DF60
COVER = [MERIT, EXP, GAIN, 0x22BE10, 0x242890, 0x242A00, 0x233DE0, 0x2AD780]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    units, officers, pers, levels = rec[UNITS], rec[OFFICERS], rec[PERSONALITIES], rec[LEVELS]
    rng = random.Random(173)
    o.stub(0x2589C0, 0)

    def u16(b, off):
        return struct.unpack_from("<H", b, off)[0]

    live = [i for i in range(len(units)) if units[i][0x10] and u16(units[i], 0x12)]
    commanders = [k for k in (ctx.officer_by_id(u16(units[i], 0x12)) for i in live) if k]
    active = [i for i in range(1, len(officers)) if officers[i][0xE6] and ctx.officer_force(i)]

    def after(fn, p, *args, **kw):
        def run():
            o.call(fn, ctx.addr(OFFICERS, p), *args, read_after=[(ctx.addr(OFFICERS, p), 0x1F0)], **kw)
            return o.after[0]
        return run

    def fields(b, k=0):
        return u16(b, 0x1C0), struct.unpack_from("<i", b, 0x104 + 4 * k)[0], b[0xEC + k]

    k7a = [x for x in range(1, 356) if pers[x][0xB6] == 0x7A or pers[x][0xBA] == 0x7A]
    k4b = [x for x in range(1, 356) if pers[x][0xB6] == 0x4B or pers[x][0xBA] == 0x4B]

    def common_pokes(p, k):
        out = []
        for _ in range(rng.randint(0, 3)):
            c = rng.randrange(12)
            if c == 0: out.append([f"{OFFICERS:#x}", p, 0xE6, bytes([rng.choice([1, 2, 3, 4, 5])]).hex()])
            elif c == 1: out.append([f"{OFFICERS:#x}", p, 0x15E, struct.pack("<H", u16(officers[p], 0x15E) ^ 0x4000).hex()])
            elif c == 2: out.append([f"{OFFICERS:#x}", p, 0x1BC, bytes([rng.choice([0, 1, 2])]).hex()])
            elif c == 3: out.append([f"{OFFICERS:#x}", p, 0x14C, bytes([rng.randrange(8)]).hex()])
            elif c == 4: out.append([f"{FORCES:#x}", ctx.officer_force(p), 0x12, bytes([rng.choice([0, 8, 0xF])]).hex()])
            elif c == 5:
                x = rng.choice(k7a + k4b)
                out.append([f"{OFFICERS:#x}", p, 0x118 + 2 * rng.randrange(5), struct.pack("<H", x).hex()])
                out.append([f"{PERSONALITIES:#x}", x, 0xB8 + 4 * rng.randrange(2), struct.pack("<h", rng.choice([-50, 10, 100, 500])).hex()])
            elif c == 6:
                out.append(["settings", 0, 0xE58, struct.pack("<i", rng.choice([0, 1])).hex()])
                out.append(["settings", 0, 0x124, struct.pack("<i", rng.choice([0, 1, 2])).hex()])
            elif c == 7: out.append(["settings", 0, 8 + rng.choice([0x4A4, 0x51C]), struct.pack("<i", rng.choice([0, 50, 100, 200, 500])).hex()])
            elif c == 8:
                # exp just under or at the next level's need
                lv = officers[p][0xEC + k]
                need = struct.unpack_from("<i", levels[min(lv + 1, 100)], 0x10 + 4 * k)[0]
                out.append([f"{OFFICERS:#x}", p, 0x104 + 4 * k, struct.pack("<i", max(0, need - rng.choice([1, 10, 100, 1000]))).hex()])
            elif c == 9: out.append([f"{OFFICERS:#x}", p, 0xEC + k, bytes([rng.choice([0, 1, 50, 98, 99, 100])]).hex()])
            elif c == 10: out.append([f"{OFFICERS:#x}", p, 0x1C0, struct.pack("<H", rng.choice([0, 100, 65000, 65535])).hex()])
            elif c == 11: out.append([f"{FORCES:#x}", ctx.officer_force(p), 0x1CB + rng.randrange(10), bytes([rng.randrange(256)]).hex()])
        return out

    merit, exp, gain = [], [], []
    for p in rng.sample(active, min(400, len(active))):
        for amount in (0, 1, 7, 50, 300, 2000):
            r = ctx.run([], after(MERIT, p, amount))
            if r is not None: merit.append([p, amount, fields(r)[0], []])
        for k in range(5):
            amount = rng.choice([1, 20, 300, 5000, 20000])
            r = ctx.run([], after(EXP, p, k, amount, 0))
            if r is not None: exp.append([p, k, amount, fields(r, k)[1], fields(r, k)[2], []])
    for p in commanders:
        for kind in range(4):
            amount = rng.choice([1, 100, 1000, 9999])
            r = ctx.run([], after(GAIN, p, kind, amount))
            if r is not None: gain.append([p, kind, amount, *fields(r), []])
    natural = (len(merit), len(exp), len(gain))

    for n in range(3000):
        p = rng.choice(active)
        amount = rng.choice([-5, 0, 1, 3, 40, 999, 30000])
        pk = common_pokes(p, 0)
        r = ctx.run(pk, after(MERIT, p, amount))
        if r is not None: merit.append([p, amount, fields(r)[0], pk])
    for n in range(4000):
        p = rng.choice(active)
        k = rng.randrange(6)
        amount = rng.choice([0, 1, 300, 5000, 50000])
        pk = common_pokes(p, min(k, 4))
        r = ctx.run(pk, after(EXP, p, k, amount, 0))
        if r is not None: exp.append([p, k, amount, fields(r, min(k, 4))[1], fields(r, min(k, 4))[2], pk])
    for n in range(2000):
        p = rng.choice(commanders + active[:50])
        kind = rng.randrange(5)
        amount = rng.choice([0, 1, 250, 5000])
        pk = common_pokes(p, 0)
        r = ctx.run(pk, after(GAIN, p, kind, amount))
        if r is not None: gain.append([p, kind, amount, *fields(r), pk])

    ctx.save("experience.json.gz", {"merit": merit, "exp": exp, "gain": gain, "world": "world.json.gz"})
    return f"merit {natural[0]}+{len(merit) - natural[0]}, exp {natural[1]}+{len(exp) - natural[1]}, gain {natural[2]}+{len(gain) - natural[2]}"
