# Native challenge siege gates and last-durability protection; only downstream engine/presentation is excluded.
import random, struct
from context import *
from tactic_targets import BUILDINGS

COVER = [0x29E840, 0x496700, 0x515330]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x515330)
    for rva, value in [(0x2E3050, 0), (0x2571F0, 0), (0x294B90, 0), (0x31EAB0, 1), (0x1B4990, 0), (0x2B8EC0, 0)]: o.stub(rva, value)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    cases = []
    for n in range(2400):
        b = rng.choice([0, 1, 5, 7, 51, 52, 61, 62])
        unit = rng.choice(live + [None])
        damage = rng.choice([-5, 0, 1, 7, 100, 9999, 20000])
        p = [['exe', 'challenge_active', 0, struct.pack('<i', n % 3 != 0).hex()],
             ['exe', 'challenge_kind', 0, struct.pack('<i', rng.choice([0, 1, 3])).hex()],
             ['exe', 'challenge_flags', 0, struct.pack('<i', 2).hex()],
             ['exe', 'challenge_rules', 0, struct.pack('<i', rng.choice([0, 64])).hex()],
             [hex(BUILDINGS), b, 0x14, struct.pack('<H', rng.choice([0, 1, 2, 50, 9999])).hex()]]
        def run():
            bp = ctx.addr(BUILDINGS, b)
            value = o.call(0x29E840, bp, damage & 0xFFFFFFFF, ctx.addr(UNITS, unit) if unit is not None else 0, read_after=[(bp, 32)])
            return [struct.unpack_from('<H', o.after[0], 0x14)[0], value & 255]
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'Challenge siege failed in case {n}: {ctx.failures[-1]}')
        cases.append([b, damage, unit, *result, p])
    ctx.save('bombard_challenge.json.gz', {'cases': cases, 'world': 'world.json.gz'})
    return f'challenge siege admission and protected durability {len(cases)}'
