# Native two-slot dispatcher, including result placement, sequential mutations, maps and queue payloads.
import random, struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, encoded, TargetOracle
from tactic_damage import DamageMaps

COVER = [0x178900, 0x16E910, 0x16D750, 0x169530]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x178900)
    native = TargetOracle(ctx)
    manager, command, info = o.alloc(0x600), o.alloc(0x40), o.alloc(0x100)
    nodes, events = o.alloc(0x1000), o.alloc(0x20000)
    maps = DamageMaps(ctx); maps.maps = manager + 0xB8
    # Gameplay leaves, native list copying and merit awards execute. These callbacks only present their writes.
    for rva in [0x1A3A00, 0x1A3C50, 0x404080, 0x18ACF0, 0x18C270, 0x84DEF0, 0x1B4AB0]: o.stub(rva, 0)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[27, i] for i in live] + [[5, i] for i in range(1, 52)] + [[6, i] for i in range(1, 11)]
    places = [ctx.u16(UNITS, i, 0x2A) for i in live] + [ctx.u16(BUILDINGS, i, 0x10) for i in range(1, 62)]
    refs = {ctx.addr(t, i): [hex(t), i] for t in [UNITS, CITIES, GATES] for i in range(len(rec[t]))}
    treasuries = {}
    for kind, index in sources:
        bp = o.call(0x22A5A0, ctx.addr(UNITS, index)) if kind == 27 else ctx.addr(CITIES if kind == 5 else GATES, index)
        ref = refs[bp]
        if int(ref[0], 16) == GATES: ref = refs[o.call(0x22A7B0, bp)]
        treasuries[(kind, index)] = (int(ref[0], 16), ref[1])
    cases = []
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    for n in range(3000):
        tactic = rng.randrange(1, 201)
        source = rng.choice(sources)
        if n % 29 == 0: source = rng.choice([[27, 0], [27, 501], [5, 52], [6, 0], [6, 11], [0, 1], [11, 1], [28, 1]])
        if n % 31 == 0: tactic = 0
        main = rng.choice([source[1] if source[0] == 27 and source[1] <= 500 else 0, rng.choice(live), None])
        previous = [rng.randrange(1, 201) for _ in range(rng.randrange(11))]
        kinds = [n % 24, rng.randrange(24)]
        targets = [rng.choices(places, k=rng.randrange(5)), rng.choices(places, k=rng.randrange(5))]
        if n % 4 == 0: targets[1] = targets[0][:]
        links, center, immediate = rng.randrange(8), rng.choice(places + [48400]), rng.randrange(2)
        p = [['vecdata', [hex(TACTICS), tactic], 0x70, encoded(tactic, kinds, [1, 1],
             [rng.choice([0, 1, 10, 50, 255]), rng.choice([0, 1, 10, 50, 255])],
             [rng.randrange(1, 6), rng.randrange(1, 6)], ability=rng.randrange(3)).hex()],
             ['listdata', '0x140', [], ''], ['listdata', '0x70', [], ''],
             ['settings', 0, 0xE58, struct.pack('<i', rng.randrange(2)).hex()]]
        records = set()
        if tuple(source) in treasuries: records.add(treasuries[tuple(source)])
        if source[0] == 27 and source[1] <= 500:
            records.add((UNITS, source[1])); records.add((OFFICERS, ctx.officer_by_id(ctx.u16(UNITS, source[1], 0x12))))
        for h in set(targets[0] + targets[1]):
            records.add((TILES, h))
            u, b = ctx.u16(TILES, h, 0x1C), ctx.u16(TILES, h, 0x1A)
            if 0 < u <= 500:
                records.add((UNITS, u)); records.add((OFFICERS, ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))))
                p += [put(UNITS, u, 0x16, struct.pack('<H', rng.choice([1, 500, 5000, 20000]))),
                      put(UNITS, u, 0x18, struct.pack('<H', rng.choice([0, 100, 5000]))),
                      put(UNITS, u, 0x1A, bytes([rng.choice([0, 1, 50, 100])])),
                      put(UNITS, u, 0x23, bytes([rng.choice([0, 1, 100]) for _ in range(3)]))]
            if 1 <= b <= 61:
                records.add((BUILDINGS, b))
                table, index = (CITIES, b) if b < 52 else (GATES, b - 51)
                records.add((table, index))
                p += [put(table, index, 0x3C, struct.pack('<i', rng.choice([1, 500, 5000, 20000]))),
                      put(table, index, 0x40, struct.pack('<i', rng.choice([0, 100, 5000]))),
                      put(table, index, 0x44, bytes([rng.choice([0, 1, 50, 100])])),
                      put(BUILDINGS, b, 0x14, struct.pack('<H', rng.choice([1, 50, 1000, 9999])))]
        records = sorted(records)
        def run():
            o.poke(manager, bytes(0x600)); o.poke(command, bytes(0x40)); o.poke(info, bytes(0x100))
            o.poke(manager + 0x98, struct.pack('<Q', ctx.addr(UNITS, main) if main is not None else 0))
            maps.prepare(None)
            for i in range(11):
                row = manager + 0xD8 + i * 64
                o.poke(row, struct.pack('<Q', ctx.addr(TACTICS, previous[i - 1]) if 0 < i <= len(previous) else 0))
                for slot in range(2):
                    buf = events + (2 * i + slot) * 0x1000
                    o.poke(row + 0x10 + slot * 24, struct.pack('<QQQ', buf, buf, buf + 0x1000))
            o.poke(command, struct.pack('<IH2xQ', *source, ctx.addr(TACTICS, tactic)))
            o.poke(command + 0x10, o.read(native.out, 32))
            old = [o.read(native.pool + idx * 8, 8) for idx in native.indices]
            for slot, (idx, hs) in enumerate(zip(native.indices, targets)):
                for i, h in enumerate(hs):
                    node = nodes + slot * 0x800 + i * 16
                    o.poke(node, struct.pack('<QQ', ctx.addr(TILES, h), node + 16 if i + 1 < len(hs) else 0))
                o.poke(native.pool + idx * 8, struct.pack('<Q', nodes + slot * 0x800 if hs else 0))
            o.poke(info, struct.pack('<QQ', ctx.addr(TACTICS, tactic), links))
            o.poke(info + 0x20, struct.pack('<QI', center, 0))
            o.poke(info + 0x30, struct.pack('<I', immediate))
            battle = o.call(0x16D960); o.poke(battle + 0x98, bytes(4))
            result = []
            def inspect():
                maps.inspect(None)
                rows = []
                for i in range(11):
                    row = manager + 0xD8 + 64 * i
                    tp, kind, number = struct.unpack('<QII', o.read(row, 16))
                    if kind == 0: continue
                    outputs = []
                    for slot in range(2):
                        begin, end, cap = struct.unpack('<QQQ', o.read(row + 0x10 + 24 * slot, 24))
                        if end > cap or (end - begin) % 16: raise RuntimeError('invalid native dispatch results')
                        output = []
                        for at in range(begin, end, 16):
                            k, index, amount, unused, h = struct.unpack('<IIHHI', o.read(at, 16))
                            output.append([k, index, h, amount])
                        outputs.append(output)
                    rows.append([i, kind, number, outputs])
                index = struct.unpack('<I', o.read(o.q(ctx.raw + 0x140), 4))[0]
                node = o.q(o.q(ctx.g.exe + world.LISTS[0x140]['pool']) + index * 8)
                pending = []
                while node:
                    data = o.read(node, 56)
                    pending.append([*[refs.get(ptr) if ptr else None for ptr in struct.unpack_from('<4Q', data)],
                                    *struct.unpack_from('<3i', data, 32)])
                    node = struct.unpack_from('<Q', data, 48)[0]
                result.extend([rows, maps.result, pending, struct.unpack('<I', o.read(battle + 0x98, 4))[0]])
                return [(ctx.addr(t, i), world.TABLES[t][1]) for t, i in records]
            try:
                o.call(0x178900, manager, command, info, read_after=inspect)
                return [[hex(t), i, data.hex()] for (t, i), data in zip(records, o.after)], result
            finally:
                for idx, head in zip(native.indices, old): o.poke(native.pool + idx * 8, head)
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'tactic dispatcher oracle failed in case {n}, source {source}, kinds {kinds}, targets {targets}: {ctx.failures[-1]}')
        cases.append([tactic, source, main, previous, targets, links, center, immediate, *result, p])
        if (n + 1) % 500 == 0: print(f'  two-slot dispatcher: {n + 1}/3000', flush=True)
    ctx.save('tactic_dispatch.json.gz', {'apply': cases, 'world': 'world.json.gz'})
    return f'two-slot dispatch applications {len(cases)}'
