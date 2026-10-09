# Native ailment success, duration resistance, and complete effect application.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, PERSONALITIES, encoded
from effect_oracle import EffectOracle

COVER = [0x16F910, 0x178470, 0x175F60, 0x2EEA60, 0x2EEF10, 0x26A750]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x16F910)
    native = EffectOracle(ctx)
    # 0x2EEF10 invokes base speech only after the ailment bytes and provocation fields are written.
    # Its UI path reaches Windows syscalls; exclude that presentation callback in live and frozen runs.
    # Success rolls, immunity, resistance, duration setters and result-vector writes still run natively.
    o.stub(0x1B4AB0, 0)
    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[i, None] for i in live] + [[None, [hex(CITIES), i]] for i in range(1, 52)] + [[None, [hex(GATES), i]] for i in range(1, 11)] + [[None, None], [0, None]]
    places = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    checks, direct, cases = [], [], []
    def poke(t, i, off, b): return [hex(t), i, off, b.hex()]
    def vector(t, b): return ['vecdata', [hex(TACTICS), t], 0x70, b.hex()]
    def person(side):
        return ctx.officer_by_id(ctx.u16(UNITS, side[0], 0x12)) if side[0] is not None else ctx.officer_by_id(ctx.u16(int(side[1][0], 16), side[1][1], 0x2E)) if side[1] else 0
    def traits(side, kinds):
        p = person(side)
        if not p: return []
        out = [poke(OFFICERS, p, 0x118, struct.pack('<5H', *range(0x100, 0x105))), poke(OFFICERS, p, 0x184, struct.pack('<H', rng.randrange(32)))]
        for i in range(5):
            k = rng.choice(kinds)
            out += [poke(PERSONALITIES, 0x100 + i, 0xB4, struct.pack('<hBBhBBh', rng.choice([-1, 0, 7]), k, 0, rng.choice([-100, -10, 0, 1, 10, 100, 2000]), rng.choice([k, 0]), 0, rng.choice([-100, 0, 20, 100])))]
        return out

    for n in range(2400):
        t, source, target = rng.randrange(1, 201), rng.choice(sources), rng.choice(sources)
        p = [vector(t, encoded(t, [2, 23], [1, 1], ability=rng.randrange(3)))]
        if n % 3 == 0: p += traits(source, [98, 99, 1])
        def run():
            native.setup(t, source, 0)
            u, b = target
            return o.call32(0x16F910, native.info, ctx.addr(UNITS, u) if u is not None else 0, ctx.addr(int(b[0], 16), b[1]) if b else 0)
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'ailment success oracle failed: {ctx.failures[-1]}')
        checks.append([t, source, target, result, p])
    print(f'  ailment success: {len(checks)}', flush=True)

    for n in range(2400):
        target, source, kind, duration = rng.choice(sources[:-2]), rng.choice(live + [0, None]), rng.randrange(3), rng.choice([-1000, -1, 0, 1, 2, 120, 240, 255, 1000])
        u, b = target
        table, index, at = (UNITS, u, 0x23) if u is not None else (int(b[0], 16), b[1], 0x49)
        source_hex = ctx.u16(UNITS, source, 0x2A) if source is not None and source else None
        p = [poke(table, index, at, bytes(rng.choice([0, 1, 120, 255]) for _ in range(3)))]
        if n % 2 == 0: p += traits(target, [9, 10, 11, 12, 21, 22, 23, 24])
        def run():
            o.call(0x2EEA60 if u is not None else 0x2EEF10, ctx.addr(table, index), ctx.addr(UNITS, source) if source is not None else 0, kind, duration,
                stack_args=[ctx.addr(TILES, source_hex) if source_hex is not None else 0, o.q(ctx.g.exe + 0x1A47B98), 0], read_after=[(ctx.addr(table, index), world.TABLES[table][1])])
            return o.after[0].hex()
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'add ailment oracle failed: {ctx.failures[-1]}')
        direct.append([target, source, kind, duration, source_hex, result, p])
    print(f'  add ailment: {len(direct)}', flush=True)

    for n in range(4000):
        t, source, kind, slot, links = rng.randrange(1, 201), rng.choice(sources), [2, 3, 4, 23][n % 4], rng.randrange(2), rng.randrange(8)
        p = [vector(t, encoded(t, [kind, kind], [1, 1], [rng.randrange(256), rng.randrange(256)], ability=rng.randrange(3)))]
        if source[0] is not None: p.append(poke(UNITS, source[0], 0x21, bytes([rng.randrange(2), rng.randrange(2)])))
        if n % 4 == 0: p += traits(source, [98, 99, 1])
        targets = rng.choices(places + [0, 48399], k=rng.randrange(5))
        for h in set(targets):
            u = struct.unpack_from('<h', rec[TILES][h], 0x1C)[0]
            if 0 < u <= 500: p.append(poke(UNITS, u, 0x23, bytes(rng.choice([0, 1, 120, 255]) for _ in range(3))))
        for f in range(52):
            if n % 3 == 0: p.append(poke(FORCES, f, 0x193, bytes([rng.choice([0, 4])])) )
        result = native.apply(0x175F60 if kind == 23 else 0x178470, t, source, slot, links, targets, p, arg4=None if kind == 23 else kind - 2)
        if result is None: raise RuntimeError(f'tactic ailment oracle failed: {ctx.failures[-1]}')
        cases.append([t, source, slot, links, kind, targets, *result, p])
        if (n + 1) % 1000 == 0: print(f'  tactic ailment: {n + 1}/4000', flush=True)
    ctx.save('tactic_ailment.json.gz', {'success': checks, 'add': direct, 'apply': cases, 'world': 'world.json.gz'})
    return f'success {len(checks)}, duration writes {len(direct)}, applications {len(cases)}'
