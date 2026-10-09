# Deferred main-target damage: the native function fills result entries and two damage maps without troop writes.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, PERSONALITIES, encoded
from effect_oracle import EffectOracle

COVER = [0x176B80]


class DamageMaps:
    def __init__(self, ctx):
        self.ctx, self.o = ctx, ctx.o
        self.maps, self.heads = self.o.alloc(32), self.o.alloc(128)
        self.result = []

    def prepare(self, effect):
        for k in range(2):
            h = self.heads + 64 * k
            self.o.poke(h, bytes(64))
            self.o.poke(h, struct.pack('<QQQ', h, h, h))
            self.o.poke(h + 24, b'\x01\x01')
            self.o.poke(self.maps + 16 * k, struct.pack('<QQ', h, 0))

    def inspect(self, effect):
        result = []
        for k in range(2):
            head = self.o.q(self.maps + 16 * k)
            items, seen = [], set()
            def visit(node):
                if node == head: return
                if not node or node in seen: raise RuntimeError('invalid native damage map')
                seen.add(node)
                b = self.o.read(node, 48)
                visit(struct.unpack_from('<Q', b)[0])
                ptr, amount = struct.unpack_from('<Qi', b, 32)
                match = None
                for t in [UNITS] if k == 0 else [CITIES, GATES]:
                    for i in range(len(self.ctx.rec[t])):
                        if self.ctx.addr(t, i) == ptr: match = [hex(t), i, amount]; break
                    if match: break
                if match is None: raise RuntimeError(f'unknown native damage map key {ptr:#x}')
                items.append(match)
                visit(struct.unpack_from('<Q', b, 16)[0])
            visit(self.o.q(head + 8))
            if len(items) != self.o.q(self.maps + 16 * k + 8): raise RuntimeError('native damage map count differs')
            result.append(items)
        self.result = result


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x176B80)
    native, maps = EffectOracle(ctx), DamageMaps(ctx)
    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 52)] + [[None, [hex(GATES), i]] for i in range(1, 11)] + [[None, None], [0, None]]
    places = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)] + [0, 48399]
    cases = []
    def poke(t, i, off, b): return [hex(t), i, off, b.hex()]
    for n in range(5000):
        t, source, slot, links, target = rng.randrange(1, 201), rng.choice(sources), rng.randrange(2), rng.randrange(8), rng.choice(places)
        p = [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [1, 1], [1, 1], [rng.randrange(256), rng.randrange(256)], ability=rng.randrange(3)).hex()],
             poke(TACTICS, t, 0x40, bytes([rng.choice([0, 1, 2, 3, 4])])), ['settings', 0, 0xE58, struct.pack('<i', n % 2).hex()],
             ['settings', 0, 0xB8, struct.pack('<i', rng.randrange(2)).hex()]]
        if source[0] is not None:
            u = source[0]
            p += [poke(UNITS, u, 0x16, struct.pack('<H', rng.choice([0, 1, 199, 200, 9999, 65535]))),
                  poke(UNITS, u, 0x21, bytes([rng.randrange(2), rng.randrange(2)])),
                  poke(UNITS, u, 0x4D, struct.pack('<b', rng.choice([-10, -1, 0, 1, 10]))), poke(UNITS, u, 0x53, bytes([rng.choice([0, 1, 255])]))]
        elif source[1]:
            tab, i = int(source[1][0], 16), source[1][1]
            p += [poke(tab, i, 0x3C, struct.pack('<i', rng.choice([0, 1, 199, 200, 20000, 200000])))]
        u = struct.unpack_from('<h', rec[TILES][target], 0x1C)[0]
        if 0 < u <= 500:
            p += [poke(UNITS, u, 0x25, bytes([rng.randrange(2)])), poke(UNITS, u, 0x3C, bytes([rng.choice([5, 6, 27, 255])])),
                  poke(UNITS, u, 0x3E, struct.pack('<H', rng.choice([0, u, rng.randrange(501)])))]
        for f in range(52):
            if n % 3 == 0: p.append(poke(FORCES, f, 0x193, bytes([rng.choice([0, 16])])) )
        result = native.apply(0x176B80, t, source, slot, links, [target], p, arg4=maps.maps, stack_args=[maps.maps + 16], center=target,
                              prepare=maps.prepare, inspect=maps.inspect)
        if result is None: raise RuntimeError(f'tactic damage oracle failed: {ctx.failures[-1]}')
        cases.append([t, source, slot, links, target, *result, maps.result, p])
        if (n + 1) % 1000 == 0: print(f'  tactic damage: {n + 1}/5000', flush=True)
    ctx.save('tactic_damage.json.gz', {'compute': cases, 'world': 'world.json.gz'})
    return f'deferred main-target damage and maps {len(cases)}'
