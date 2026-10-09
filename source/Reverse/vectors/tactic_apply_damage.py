# Complete immediate/splash damage leaf, with actual troop writes, experience and pending destruction queues.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, PERSONALITIES, encoded
from effect_oracle import EffectOracle
from tactic_damage import DamageMaps

COVER = [0x176B80, 0x178E30, 0x174520, 0x270550, 0x17A090, 0x31EAB0, 0x2B8EC0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x178E30)
    native, maps = EffectOracle(ctx), DamageMaps(ctx)
    # Post-write unit presentation and the destruction marker; native damage/queue/experience still run.
    for rva in [0x1A3A00, 0x1A3C50, 0x404080]: o.stub(rva, 0)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 52)] + [[None, [hex(GATES), i]] for i in range(1, 11)]
    places = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    refs = {ctx.addr(t, i): [hex(t), i] for t in [UNITS, CITIES, GATES] for i in range(len(rec[t]))}
    treasuries = {}
    for source in sources:
        bp = o.call(0x22A5A0, ctx.addr(UNITS, source[0])) if source[0] is not None else ctx.addr(int(source[1][0], 16), source[1][1])
        reference = refs[bp]
        if int(reference[0], 16) == GATES: reference = refs[o.call(0x22A7B0, bp)]
        treasuries[str(source)] = (int(reference[0], 16), reference[1])
    cases = []
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    for n in range(3000):
        t, source, slot, links = rng.randrange(1, 201), rng.choice(sources), rng.randrange(2), rng.randrange(8)
        targets = rng.choices(places, k=rng.randrange(1, 5))
        center, immediate = rng.choice(targets + [48400]), rng.randrange(2)
        p = [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [1, 1], [1, 1], [rng.randrange(256), rng.randrange(256)], ability=rng.randrange(3)).hex()],
             ['listdata', '0x140', [], ''], ['listdata', '0x70', [], ''],
             ['settings', 0, 0xE58, struct.pack('<i', rng.randrange(2)).hex()],
             ['settings', 0, 0xCC, struct.pack('<i', rng.randrange(2)).hex()],
             ['settings', 0, 0x29C, struct.pack('<i', rng.choice([0, 50, 100, 150])).hex()]]
        extra = [treasuries[str(source)]]
        if source[0] is not None:
            u = source[0]
            extra += [(UNITS, u), (OFFICERS, ctx.officer_by_id(ctx.u16(UNITS, u, 0x12)))]
            p.append(put(UNITS, u, 0x21, bytes([rng.randrange(2), rng.randrange(2)])))
        for h in set(targets):
            u, b = ctx.u16(TILES, h, 0x1C), ctx.u16(TILES, h, 0x1A)
            if 0 < u <= 500:
                person = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))
                if (OFFICERS, person) not in extra: extra.append((OFFICERS, person))
                p += [put(UNITS, u, 0x16, struct.pack('<H', rng.choice([0, 1, 10, 500, 5000, 20000]))),
                      put(UNITS, u, 0x18, struct.pack('<H', rng.choice([0, 100, 19990]))),
                      put(UNITS, u, 0x1A, bytes([rng.choice([0, 1, 50, 100])]))]
                if n % 5 == 0:
                    p += [put(UNITS, u, 0x10, b'\x02'),
                          put(UNITS, u, 0x5C, struct.pack('<ii', rng.choice([0, 100, 50000]), rng.choice([0, 1000, 400000])))]
                if person and n % 3 == 0:
                    p += [put(OFFICERS, person, 0x118, struct.pack('<5H', 256, 0, 0, 0, 0)),
                          put(OFFICERS, person, 0x184, bytes(20)), put(PERSONALITIES, 256, 0xA4, b'\x1e'),
                          put(PERSONALITIES, 256, 0xB4, struct.pack('<hBBhBBh', rng.choice([-1, 0, 1, 30, 720, 721, 32767]), 13, 0, 50, 0, 0, 0)),
                          ['vecdata', [hex(PERSONALITIES), 256], 0xC8, ''], put(PERSONALITIES, 256, 0xBF, b'\x14')]
            if 1 <= b <= 61:
                table, i = (CITIES, b) if b < 52 else (GATES, b - 51)
                p += [put(table, i, 0x3C, struct.pack('<i', rng.choice([0, 1, 500, 5000, 20000]))),
                      put(table, i, 0x40, struct.pack('<i', rng.choice([0, 100, 19990]))),
                      put(table, i, 0x44, bytes([rng.choice([0, 1, 50, 100])]))]
        state = []
        def prepare(effect):
            maps.prepare(effect)
            o.poke(native.info + 0x30, struct.pack('<I', immediate))
            manager = o.call(0x16D960)
            o.poke(manager + 0x98, bytes(4))
            state.append(manager)
        def inspect(effect):
            maps.inspect(effect)
            index = struct.unpack('<I', o.read(o.q(ctx.raw + 0x140), 4))[0]
            node = o.q(o.q(ctx.g.exe + world.LISTS[0x140]['pool']) + index * 8)
            entries = []
            while node:
                data = o.read(node, 56)
                pointers = struct.unpack_from('<4Q', data)
                entries.append([*[refs.get(ptr) if ptr else None for ptr in pointers], *struct.unpack_from('<3i', data, 32)])
                node = struct.unpack_from('<Q', data, 48)[0]
            state.append([entries, struct.unpack('<I', o.read(state[0] + 0x98, 4))[0]])
        result = native.apply(0x176B80, t, source, slot, links, targets, p, arg4=maps.maps,
                              stack_args=[maps.maps + 16], center=center, prepare=prepare, inspect=inspect, extra_records=extra)
        if result is None: raise RuntimeError(f'immediate tactic damage oracle failed: {ctx.failures[-1]}')
        cases.append([t, source, slot, links, center, immediate, targets, *result, maps.result, state[1], p])
        if (n + 1) % 1000 == 0: print(f'  immediate tactic damage: {n + 1}/3000', flush=True)
    ctx.save('tactic_apply_damage.json.gz', {'apply': cases, 'world': 'world.json.gz'})
    return f'immediate/splash damage applications, maps and queues {len(cases)}'
