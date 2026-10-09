# Native ordered link selection and target copying; no gameplay function stubs.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, encoded, TargetOracle

COVER = [0x175350, 0x2C4F90, 0x4982A0, 0x17CA80, 0x1682B0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x175350)
    native = TargetOracle(ctx)
    info, queue, nodes, inputs = o.alloc(0x40), o.alloc(0x100), o.alloc(16 * 64), o.alloc(0x1000)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    people = [ctx.officer_by_id(ctx.u16(UNITS, i, 0x12)) for i in live]
    targets = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    tactic_refs = {ctx.addr(TACTICS, i): i for i in range(201)}
    cases = []
    date = struct.unpack_from('<i', bytes.fromhex(ctx.snap['worldstate']), 0x454)[0]
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    for n in range(1200):
        person, target, mode = rng.choice(people + [0]), rng.choice(targets + [None]), rng.randrange(2)
        candidates = rng.choices(live + [0], k=rng.randrange(25))
        p = [['listdata', '0x70', [], ''], ['listdata', '0x140', [], ''],
             ['settings', 0, 0xE58, struct.pack('<i', rng.randrange(2)).hex()],
             ['settings', 0, 0x28, struct.pack('<i', rng.randrange(5)).hex()],
             ['settings', 0, 0x134, struct.pack('<i', rng.randrange(5)).hex()],
             ['settings', 0, 0x53C, struct.pack('<i', rng.choice([-100, 0, 50, 100, 150, 300])).hex()]]
        for name in ['tactic_link_p0_1','tactic_link_p0_2','tactic_link_p1_1','tactic_link_p1_2',
                     'tactic_link_p2_1','tactic_link_p2_2','tactic_link_ai_1','tactic_link_ai_2']:
            p.append(['exe', name, 0, struct.pack('<i', rng.choice([-1, 0, 30, 100, 150])).hex()])
        for name, value in [('challenge_active', rng.randrange(2)), ('challenge_kind', rng.choice([0, 1, 3])), ('challenge_flags', rng.choice([0, 2]))]:
            p.append(['exe', name, 0, struct.pack('<i', value).hex()])
        p += [['worldstate', 0, 0x3A, bytes([rng.choice([ctx.officer_force(person), 0, 51])]).hex()],
              ['worldstate', 0, 0x165D, bytes([min(len(candidates), 5)]).hex()],
              ['worldstate', 0, 0x165E, struct.pack('<5H', *([ctx.u16(UNITS, u, 0x12) for u in candidates[:5]] + [0] * (5 - min(5, len(candidates))))).hex()],
              ['worldstate', 0, 0x1668, struct.pack('<5i', *[date - rng.choice([0, 2, 3, 4]) for _ in range(5)]).hex()]]
        for t, kind in zip([191,192,193], [5,15,17]):
            p += [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [kind, kind], [0, 1], flags=3).hex()],
                  put(TACTICS, t, 0x6C, struct.pack('<I', 0xFFFFFFFF))]
        for u in set(candidates):
            p.append(put(UNITS, u, 0x1D, bytes(rng.choices([0,191,192,193,255], k=3))))
            commander = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))
            if commander:
                p += [put(OFFICERS, commander, 0x130, bytes(rng.choices([0,191,192,193,255], k=10))),
                      put(OFFICERS, commander, 0x15E, struct.pack('<H', struct.unpack_from('<H',rec[OFFICERS][commander],0x15E)[0] & ~32 | rng.choice([0,32])))]
                if person and n % 3 == 0:
                    p += [put(OFFICERS, person, 0xBC, struct.pack('<H', 33)), put(OFFICERS, commander, 0xBC, struct.pack('<H', 33))]
        def run():
            o.poke(queue, bytes(0x100)); o.poke(info, bytes(0x40))
            # This is the native caller's preallocated queue. Copy its already initialized mutex layout.
            o.poke(queue + 0x40, o.read(ctx.g.exe + 0x27D1128 + 0x40, 0x50))
            for i in range(16):
                node = nodes + i * 64
                o.poke(node, bytes(64)); o.poke(node + 0x30, struct.pack('<Q', node + 64 if i < 15 else 0))
            o.poke(queue + 0x20, struct.pack('<Q', nodes))
            o.poke(info + 0x10, struct.pack('<I', mode))
            o.poke(info + 0x20, struct.pack('<Q', ctx.addr(TILES, target) if target is not None else 0))
            idx = native.indices[0]; old = o.read(native.pool + idx * 8, 8)
            for i, u in enumerate(candidates):
                node = inputs + i * 16
                o.poke(node, struct.pack('<QQ', ctx.addr(UNITS, u), node + 16 if i + 1 < len(candidates) else 0))
            o.poke(native.pool + idx * 8, struct.pack('<Q', inputs if candidates else 0))
            result = []
            def inspect():
                node = o.q(queue + 0x10)
                while node:
                    kind, unit, tactic = struct.unpack('<IH2xQ', o.read(node, 16))
                    lists = []
                    for slot in range(2):
                        cell = o.q(node + 0x18 + 16 * slot)
                        index = struct.unpack('<I', o.read(cell, 4))[0] if cell else 0x14000
                        ptr = o.q(native.pool + index * 8)
                        hs = []
                        while ptr:
                            hptr, ptr = struct.unpack('<QQ', o.read(ptr, 16))
                            hs.append((hptr - ctx.addr(TILES, 0)) // 32)
                        lists.append(hs)
                    result.append([kind, unit, tactic_refs[tactic], lists])
                    node = o.q(node + 0x30)
                return []
            try:
                count = o.call(0x175350, info, queue, native.out, ctx.addr(OFFICERS, person), read_after=inspect) & 0xFFFFFFFF
                if count != len(result): raise RuntimeError(f'native link count {count}, queue {len(result)}')
                return result
            finally: o.poke(native.pool + idx * 8, old)
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'tactic link oracle failed in case {n}: {ctx.failures[-1]}')
        cases.append([person, target, mode, candidates, result, p])
        if (n + 1) % 200 == 0: print(f'  ordered tactic links: {n + 1}/1200', flush=True)
    ctx.save('tactic_link.json.gz', {'cases': cases, 'world': 'world.json.gz'})
    return f'ordered unit/tactic/target selections {len(cases)}'
