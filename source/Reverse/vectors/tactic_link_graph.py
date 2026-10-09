# Native neighboring graph expansion, ordering, and linked tactic queue creation; no gameplay stubs.
import random, struct
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_R8
from context import *
from tactic_targets import TACTICS, BUILDINGS, encoded, TargetOracle
from battlepower_fx import ring

COVER = [0x16DE70, 0x16DC80, 0x167350, 0x2AF7E0, 0x175350]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x16DE70)
    native = TargetOracle(ctx)
    info, queue, nodes = o.alloc(0x40), o.alloc(0x100), o.alloc(16 * 64)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)
            and ctx.officer_by_id(ctx.u16(UNITS, i, 0x12))]
    targets = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    tactic_refs = {ctx.addr(TACTICS, i): i for i in range(201)}
    cases, captured = [], []

    def items(wrapper):
        cell = o.q(wrapper + 8)
        index = struct.unpack('<I', o.read(cell, 4))[0] if cell else 0x14000
        node, result, seen = o.q(native.pool + index * 8), [], set()
        while node:
            if node in seen: raise RuntimeError('Cyclic native graph list')
            seen.add(node)
            ptr, node = struct.unpack('<QQ', o.read(node, 16))
            result.append(ptr)
        return result

    def capture(mu, addr, size, data):
        captured.extend((ptr - ctx.addr(UNITS, 0)) // 512 for ptr in items(mu.reg_read(UC_X86_REG_R8)))

    hook = o.mu.hook_add(UC_HOOK_CODE, capture, begin=ctx.g.exe + 0x175350, end=ctx.g.exe + 0x175350)
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    try:
        for n in range(1200):
            source = rng.choice(live)
            commander = ctx.officer_by_id(ctx.u16(UNITS, source, 0x12))
            target, mode = rng.choice(targets), rng.randrange(2)
            p = [['listdata', '0x70', [], ''], ['listdata', '0x140', [], ''],
                 ['exe', 'challenge_active', 0, struct.pack('<i', n % 2).hex()],
                 ['exe', 'challenge_kind', 0, struct.pack('<i', 0).hex()],
                 ['exe', 'challenge_flags', 0, struct.pack('<i', 2).hex()]]
            if n >= 100:
                center = rng.choice([ctx.u16(UNITS, source, 0x2A), 0, 219, 48399, 22500])
                around = ring(center, 4)
                mates = [source] + rng.sample([u for u in live if u != source], min(len(live) - 1, rng.randrange(2, 13)))
                positions = [center] + rng.sample(around, len(mates) - 1)
                # Clear surrounding occupants so every synthetic graph edge has an explicit input.
                for h in ring(center, 6) + [center]: p.append(put(TILES, h, 0x1C, struct.pack('<h', 0)))
                for u, h in zip(mates, positions):
                    person = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))
                    p += [put(UNITS, u, 0x2A, struct.pack('<H', h)), put(UNITS, u, 0x152, struct.pack('<H', h)),
                          put(TILES, h, 0x1C, struct.pack('<h', u)), put(UNITS, u, 0x23, bytes(3)),
                          put(UNITS, u, 0x1D, bytes([191, 192, 193])),
                          put(OFFICERS, person, 0xE0, bytes([rec[OFFICERS][commander][0xE0]])),
                          put(OFFICERS, person, 0xE2, bytes(2)), put(OFFICERS, person, 0xBC, struct.pack('<H', 33)),
                          put(OFFICERS, person, 0x15E, struct.pack('<H', ctx.u16(OFFICERS, person, 0x15E) & ~0x4000)),
                          put(OFFICERS, person, 0x130, bytes([191, 192, 193, 0, 0, 0, 0, 0, 0, 0]))]
                    if rng.randrange(8) == 0: p.append(put(UNITS, u, 0x23, bytes([1])))
                    if rng.randrange(10) == 0: p.append(put(OFFICERS, person, 0x15E, struct.pack('<H', 0x4000)))
                for t, kind in zip([191, 192, 193], [5, 15, 17]):
                    p += [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [kind, kind], [0, 1], flags=3).hex()],
                          put(TACTICS, t, 0x6C, struct.pack('<I', 0xFFFFFFFF))]

            def run():
                captured.clear()
                o.poke(queue, bytes(0x100)); o.poke(info, bytes(0x40))
                o.poke(queue + 0x40, o.read(ctx.g.exe + 0x27D1128 + 0x40, 0x50))
                for i in range(16):
                    node = nodes + i * 64
                    o.poke(node, bytes(64)); o.poke(node + 0x30, struct.pack('<Q', node + 64 if i < 15 else 0))
                o.poke(queue + 0x20, struct.pack('<Q', nodes))
                o.poke(info + 8, struct.pack('<Q', ctx.addr(UNITS, source)))
                o.poke(info + 0x10, struct.pack('<I', mode))
                o.poke(info + 0x20, struct.pack('<Q', ctx.addr(TILES, target)))
                result = []
                def inspect():
                    node = o.q(queue + 0x10)
                    while node:
                        kind, unit, tactic = struct.unpack('<IH2xQ', o.read(node, 16))
                        lists = [[(ptr - ctx.addr(TILES, 0)) // 32 for ptr in items(node + 0x10 + 16 * slot)] for slot in range(2)]
                        result.append([kind, unit, tactic_refs[tactic], lists]); node = o.q(node + 0x30)
                    return []
                count = o.call(0x16DE70, info, queue, read_after=inspect) & 0xFFFFFFFF
                if count != len(result): raise RuntimeError(f'Graph link count {count} != queue {len(result)}')
                return [list(captured), result]
            result = ctx.run(p, run)
            if result is None: raise RuntimeError(f'Graph oracle failed in case {n}: {ctx.failures[-1]}')
            cases.append([source, target, mode, result, p])
            if (n + 1) % 200 == 0: print(f'  tactic link graph: {n + 1}/1200', flush=True)
    finally:
        o.mu.hook_del(hook)
    ctx.save('tactic_link_graph.json.gz', {'cases': cases, 'world': 'world.json.gz'})
    return f'expanded ordered graphs and native linked queues {len(cases)}'
