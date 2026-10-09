# Native officer slot filtering and complete unit tactic candidate lists; no game function stubs.
import random, struct
from context import *
from tactic_targets import TACTICS, encoded, TargetOracle

REFERENCES = 0x79118
COVER = [0x22A320, 0x2ADD40, 0x231360, 0x234090]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x2ADD40)
    native = TargetOracle(ctx)
    idx = native.indices[0]
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    slots, candidates = [], []
    pointers = {ctx.addr(TACTICS, i): i for i in range(201)}
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    for n in range(2000):
        person, slot, tactic = rng.randrange(len(rec[OFFICERS])), rng.randrange(10), rng.randrange(256)
        p = [put(OFFICERS, person, 0x130 + slot, bytes([tactic])),
             put(OFFICERS, person, 0x10, struct.pack('<H', rng.choice([ctx.u16(OFFICERS, person, 0x10), 4100, 4101, 4200, 4201, 5100, 5101, 5200, 5201]))),
             ['vecdata', [hex(TACTICS), min(tactic, 200)], 0x70, encoded(min(tactic, 200), [1, 1], [0, 0], flags=rng.choice([0, 2])).hex()]]
        ref = rng.randrange(31)
        p.append(put(REFERENCES, ref, 0x52, bytes([tactic, rng.randrange(2)])))
        result = ctx.run(p, lambda: o.call(0x22A320, ctx.addr(OFFICERS, person), slot) & 255)
        if result is None: raise RuntimeError(f'officer tactic slot oracle failed: {ctx.failures[-1]}')
        slots.append([person, slot, result, p])
    for n in range(3000):
        unit = rng.choice(live + [0])
        person = ctx.officer_by_id(ctx.u16(UNITS, unit, 0x12))
        h = ctx.u16(UNITS, unit, 0x2A)
        terrain = rec[TILES][h][0x10] if h < 48400 else rec[TILES][0][0x10]
        p = [put(TERRAINS, terrain, 0x2C, bytes([rng.choice([0, 2])])),
             put(UNITS, unit, 0x1C, bytes([rng.choice([0, 1, 10, 20, 21, 255])])),
             put(UNITS, unit, 0x1D, bytes([rng.randrange(256) for _ in range(3)]))]
        if person:
            known = [rng.randrange(256) for _ in range(10)]
            p.append(put(OFFICERS, person, 0x130, bytes(known)))
            for t in set(known):
                if t <= 200:
                    p += [put(TACTICS, t, 0x6C, struct.pack('<I', rng.choice([0, 1, 1 << 10, 0x1FFFFF, 0xFFFFFFFF]))),
                          ['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [1, 1], [0, 0], flags=rng.choice([0, 2])).hex()]]
        def run():
            output = []
            def inspect():
                node = o.q(native.pool + idx * 8)
                while node:
                    ptr, node = struct.unpack('<QQ', o.read(node, 16))
                    output.append(pointers[ptr])
                return []
            o.call(0x2ADD40, ctx.addr(UNITS, unit), native.out, read_after=inspect)
            return output
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'unit tactic candidates oracle failed: {ctx.failures[-1]}')
        candidates.append([unit, result, p])
    ctx.save('unit_tactics.json.gz', {'slots': slots, 'candidates': candidates, 'world': 'world.json.gz'})
    return f'officer slots {len(slots)}, unit candidate lists {len(candidates)}'
