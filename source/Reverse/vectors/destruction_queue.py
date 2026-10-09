# Native queue registration including complete payloads, duplicate priority and capacity boundaries.
import random, struct
from context import *
from tactic_targets import CITIES, GATES

COVER = [0x31EAB0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x31EAB0)
    request, nodes = o.alloc(48), o.alloc(140 * 64)
    pool = o.q(ctx.g.exe + world.LISTS[0x140]['pool'])
    tails = o.q(ctx.g.exe + world.LISTS[0x140]['pool'] + 8)
    counts = o.q(ctx.g.exe + world.LISTS[0x140]['pool'] + 0x18)
    index = struct.unpack('<I', o.read(o.q(ctx.raw + 0x140), 4))[0]
    head_slot, tail_slot, count_slot = pool + index * 8, tails + index * 8, counts + index * 8
    refs = {ctx.addr(t, i): [hex(t), i] for t in [UNITS, CITIES, GATES] for i in range(len(rec[t]))}
    units = [None] + [[hex(UNITS), i] for i in range(1, 501)]
    bases = [None] + [[hex(CITIES), i] for i in range(52)] + [[hex(GATES), i] for i in range(11)]
    def payload():
        return [rng.choice(units), rng.choice(bases), rng.choice(units), rng.choice(bases), rng.choice([0, 1]), rng.choice([0, 3, 4])]
    def pack(p): return struct.pack('<4Q3i4x', *[ctx.addr(int(r[0], 16), r[1]) if r else 0 for r in p[:4]], 0, *p[4:])
    cases = []
    for n in range(1800):
        count = rng.choice([0, 1, 2, 7, 126, 127, 128, 129])
        initial = [payload() for _ in range(count)]
        incoming = rng.choice(initial)[:] if initial and n % 3 == 0 else payload()
        if initial and n % 4 == 0: incoming[0:2] = initial[0][0:2]
        old = [o.read(head_slot, 8), o.read(tail_slot, 8), o.read(count_slot, 8)]
        try:
            for i, p in enumerate(initial):
                node = nodes + i * 64
                o.poke(node, pack(p) + struct.pack('<QQ', node + 64 if i + 1 < count else 0, node - 64 if i else 0))
            o.poke(head_slot, struct.pack('<Q', nodes if initial else 0))
            o.poke(tail_slot, struct.pack('<Q', nodes + (count - 1) * 64 if initial else 0))
            o.poke(count_slot, struct.pack('<Q', count)); o.poke(request, pack(incoming))
            result = []
            def inspect():
                node, seen = o.q(head_slot), set()
                while node:
                    if node in seen: raise RuntimeError('Cyclic native destruction queue')
                    seen.add(node)
                    b = o.read(node, 56)
                    ps = [refs.get(p) if p else None for p in struct.unpack_from('<4Q', b)]
                    unused, mode, reason = struct.unpack_from('<3i', b, 32)
                    result.append([*ps, mode, reason]); node = struct.unpack_from('<Q', b, 48)[0]
                if len(result) != o.q(count_slot): raise RuntimeError('Native queue count differs from traversal')
                return []
            accepted = o.call(0x31EAB0, ctx.raw, request, read_after=inspect) & 0xFFFFFFFF
            cases.append([initial, incoming, accepted, result])
        finally:
            o.poke(head_slot, old[0]); o.poke(tail_slot, old[1]); o.poke(count_slot, old[2])
        if (n + 1) % 300 == 0: print(f'  destruction queue: {n + 1}/1800', flush=True)
    ctx.save('destruction_queue.json.gz', {'cases': cases, 'world': 'world.json.gz'})
    return f'ordered destruction payloads, duplicate priority and overflow {len(cases)}'
