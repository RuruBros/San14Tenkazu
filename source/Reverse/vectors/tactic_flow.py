# Native main selection, link queues, sequential effect dispatch and source cooldowns.
import random, struct
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RDX
from context import *
from tactic_targets import TACTICS, CITIES, GATES, BUILDINGS, encoded, TargetOracle
from tactic_damage import DamageMaps
from battlepower_fx import ring

COVER = [0x172E20, 0x16CA30, 0x16C950, 0x1675C0, 0x16B880, 0x231340, 0x496700, 0x515330]


def generate(ctx, step_mode=False):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x172E20)
    native = TargetOracle(ctx)
    manager = ctx.g.exe + 0x1BB38A0 if step_mode else o.alloc(0x700)
    sides, nodes, events = o.alloc(0x40), o.alloc(16 * 64), o.alloc(0x20000)
    record, frame = o.alloc(0x40), o.alloc(0x40)
    maps = DamageMaps(ctx); maps.maps = manager + 0xB8
    # These callbacks present already calculated states/results. Native allocation and gameplay still run.
    for rva in [0x1A3A00, 0x1A3C50, 0x404080, 0x18ACF0, 0x18C270, 0x84DEF0, 0x1B4AB0, 0x168BB0]: o.stub(rva, 0)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    sources = [[27, i] for i in live] + [[5, i] for i in range(1, 52)] + [[6, i] for i in range(1, 11)]
    tables = [UNITS, OFFICERS, CITIES, GATES, BUILDINGS, TILES]
    starts = {t: ctx.addr(t, 0) for t in tables}
    for t in tables:
        size, count = world.TABLES[t][1:]
        if ctx.addr(t, count - 1) != starts[t] + (count - 1) * size: raise RuntimeError(f'Noncontiguous table {t:#x}')
    refs = {ctx.addr(t, i): [hex(t), i] for t in [UNITS, CITIES, GATES] for i in range(len(rec[t]))}
    tactic_refs = {ctx.addr(TACTICS, i): i for i in range(201)}
    captured = []

    def items(wrapper):
        cell = o.q(wrapper + 8)
        index = struct.unpack('<I', o.read(cell, 4))[0] if cell else 0x14000
        node, result = o.q(native.pool + index * 8), []
        while node:
            ptr, node = struct.unpack('<QQ', o.read(node, 16)); result.append(ptr)
        return result

    def capture(mu, addr, size, data):
        captured.extend(tactic_refs[p] for p in items(mu.reg_read(UC_X86_REG_RDX)))

    hook = o.mu.hook_add(UC_HOOK_CODE, capture, begin=ctx.g.exe + 0x16B880, end=ctx.g.exe + 0x16B880)
    def put(t, i, off, data): return [hex(t), i, off, data.hex()]
    def side(s):
        kind, i = s
        if kind == 27: return [i, kind, ctx.u16(UNITS, i, 0x2A), ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, i, 0x12))), 1]
        t = CITIES if kind == 5 else GATES
        b = ctx.addr(t, i)
        return [i, kind, o.call32(0x22D740, b), o.call32(0x22CD10 if kind == 5 else 0x22CD80, b), 1]

    cases = []
    try:
        for n in range(1200):
            source, target = rng.choice(sources), rng.choice(sources)
            if n >= 900: source = [27, rng.choice(live)]
            a, b = side(source), side(target)
            p = [['listdata', '0x140', [], ''], ['listdata', '0x70', [], ''],
                 ['exe', 'challenge_active', 0, struct.pack('<i', n % 3 != 0).hex()],
                 ['exe', 'challenge_kind', 0, struct.pack('<i', rng.choice([0, 3])).hex()],
                 ['exe', 'challenge_flags', 0, struct.pack('<i', rng.choice([0, 1, 2, 3]) if step_mode else rng.choice([0, 2])).hex()],
                 ['exe', 'challenge_rules', 0, struct.pack('<i', rng.choice([0, 64])).hex()],
                 ['settings', 0, 0xE58, struct.pack('<i', n % 2).hex()],
                 ['settings', 0, 0x28, struct.pack('<i', rng.randrange(6)).hex()],
                 ['settings', 0, 0x134, struct.pack('<i', rng.randrange(6)).hex()],
                 ['worldstate', 0, 0x38, bytes([rng.randrange(256)]).hex()]]
            if n >= 100:
                for t, kind in zip([191, 192, 193], [n % 24, rng.randrange(24), rng.randrange(24)]):
                    p += [['vecdata', [hex(TACTICS), t], 0x70, encoded(t, [kind, rng.randrange(24)],
                          [rng.randrange(2), rng.randrange(2)], [10, 10], flags=rng.randrange(4)).hex()],
                          put(TACTICS, t, 0x42, struct.pack('<H', rng.choice([0, 1, 8, 50, 150, 65535]))),
                          put(TACTICS, t, 0x40, bytes([rng.randrange(9)])), put(TACTICS, t, 0x6C, struct.pack('<I', 0xFFFFFFFF))]
                if source[0] == 27:
                    u = source[1]; person = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))
                    p += [put(UNITS, u, 0x1D, bytes(rng.choices([0, 191, 192, 193], k=3))),
                          put(OFFICERS, person, 0x130, bytes([191, 192, 193, 0, 0, 0, 0, 0, 0, 0])),
                          put(UNITS, u, 0x1B, bytes([rng.choice([1, 2, 14, 15])])),
                          put(FORCES, a[3], 0x193, bytes([rng.choice([0, 64])]))]
                else: p.append(put(CITIES if source[0] == 5 else GATES, source[1], 0x48, bytes([rng.choice([0, 191, 192, 193])])) )
            if n >= 900:
                center = a[2]; commander = ctx.officer_by_id(ctx.u16(UNITS, source[1], 0x12))
                mates = [source[1]] + rng.sample([u for u in live if u != source[1]], 2)
                positions = [center] + rng.sample(ring(center, 2), 2)
                p += [['exe', 'challenge_active', 0, '01000000'], ['exe', 'challenge_kind', 0, '00000000'],
                      ['exe', 'challenge_flags', 0, '02000000'], ['worldstate', 0, 0x165D, '00']]
                for h in ring(center, 4) + [center]: p.append(put(TILES, h, 0x1C, bytes(2)))
                for u, h in zip(mates, positions):
                    person = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))
                    p += [put(UNITS, u, 0x2A, struct.pack('<H', h)), put(UNITS, u, 0x152, struct.pack('<H', h)),
                          put(TILES, h, 0x1C, struct.pack('<h', u)), put(UNITS, u, 0x23, bytes(3)),
                          put(UNITS, u, 0x1D, bytes([191, 192, 193])), put(OFFICERS, person, 0x130, bytes([191, 192, 193, 0, 0, 0, 0, 0, 0, 0])),
                          put(OFFICERS, person, 0xE0, bytes([rec[OFFICERS][commander][0xE0]])), put(OFFICERS, person, 0xE2, bytes(2)),
                          put(OFFICERS, person, 0xBC, struct.pack('<H', 33)),
                          put(OFFICERS, person, 0x15E, struct.pack('<H', ctx.u16(OFFICERS, person, 0x15E) & ~0x4000))]
                for t, kinds in zip([191, 192, 193], [[5, 15], [n % 24, rng.randrange(24)], [19, 20]]):
                    p.append(['vecdata', [hex(TACTICS), t], 0x70, encoded(t, kinds, [1, 1], flags=3).hex()])
            if n % 29 == 0: a[1] = rng.choice([0, 11, 28])
            if n % 31 == 0: b[1] = rng.choice([0, 11, 28])

            step_inputs = [rng.choice([0, 1, 500, 30000, 65535]), rng.choice([0, 1, 499, 30000, 65535]),
                           rng.choice([0, 0, 1, 65535]), rng.choice([0, 0, 7]), n % 23 == 0] if step_mode else []
            if step_mode and n >= 100:
                if source[0] == 27:
                    u = source[1]; person = ctx.officer_by_id(ctx.u16(UNITS, u, 0x12))
                    p += [put(UNITS, u, 0x10, bytes([rng.choice([0, 1, 1, 2])])),
                          put(UNITS, u, 0x20, bytes([rng.choice([0, 0, 1, 255])])),
                          put(UNITS, u, 0x23, bytes([rng.choice([0, 0, 1])])),
                          put(UNITS, u, 0x25, bytes([rng.choice([0, 0, 1])])),
                          put(UNITS, u, 0x37, bytes([rng.choice([0, 3, 4, 5, 6, 7, 255])])),
                          put(UNITS, u, 0x38, bytes([rng.choice([b[1], 255])])),
                          put(UNITS, u, 0x3A, struct.pack('<H', rng.choice([b[0], 65535]))),
                          put(UNITS, u, 0x3C, bytes([rng.choice([b[1], 255])])),
                          put(UNITS, u, 0x3E, struct.pack('<H', rng.choice([b[0], 65535]))),
                          put(OFFICERS, person, 0x15E, struct.pack('<H', ctx.u16(OFFICERS, person, 0x15E) | (32 if n % 2 else 0)))]
                    if n % 5 == 0:
                        p += [['worldstate', 0, 0x3A, bytes([a[3]]).hex()], ['worldstate', 0, 0x165D, '01'],
                              ['worldstate', 0, 0x165E, struct.pack('<H', ctx.u16(UNITS, u, 0x12)).hex()],
                              ['worldstate', 0, 0x1668, struct.pack('<i', 100).hex()], ['worldstate', 0, 0x454, struct.pack('<i', rng.choice([102, 103, 104])).hex()]]
                    if n >= 900:
                        p += [put(UNITS, u, 0x10, b'\1'), put(UNITS, u, 0x20, b'\0'), put(UNITS, u, 0x23, b'\0'), put(UNITS, u, 0x25, b'\0'),
                              put(UNITS, u, 0x37, b'\0'), put(UNITS, u, 0x3C, bytes([b[1]])), put(UNITS, u, 0x3E, struct.pack('<H', b[0]))]
                        step_inputs[:2] = [65535, 0]
                else:
                    t = CITIES if source[0] == 5 else GATES; index = source[1]
                    governor = rng.choice([i for i in range(1, len(rec[OFFICERS])) if rec[OFFICERS][i][0xE6] in (2, 3, 4, 5)])
                    p += [put(t, index, 0x2E, struct.pack('<H', ctx.u16(OFFICERS, governor, 0x10))),
                          put(OFFICERS, governor, 0xE4, struct.pack('<H', index if source[0] == 5 else index + 51)),
                          put(OFFICERS, governor, 0x161, bytes([rng.randrange(4)])),
                          put(t, index, 0x4C, struct.pack('<H', rng.choice([0, 0, 1, 65535]))),
                          put(t, index, 0x49, bytes([rng.choice([0, 0, 1])])), put(t, index, 0x4B, bytes([rng.choice([0, 1])])),
                          put(t, index, 0x62, bytes([rng.choice([b[1], 255])])), put(t, index, 0x64, struct.pack('<H', rng.choice([b[0], 65535])))]

            def run():
                # The real global's thread-initialization guard starts at +0x3F0; keep it outside the fixture.
                captured.clear(); o.poke(manager, bytes(0x3F0 if step_mode else 0x700))
                o.poke(manager + 0x48, o.read(ctx.g.exe + 0x27D1128 + 0x40, 0x50))
                for i in range(16):
                    o.poke(nodes + i * 64, bytes(64))
                    o.poke(nodes + i * 64 + 0x30, struct.pack('<Q', nodes + (i + 1) * 64 if i < 15 else 0))
                o.poke(manager + 0x28, struct.pack('<Q', nodes)); maps.prepare(None)
                for i in range(11):
                    if step_mode: o.poke(manager + 0xD8 + 64 * i + 8, struct.pack('<I', 255))
                    for slot in range(2):
                        buf = events + (2 * i + slot) * 0x1000
                        o.poke(manager + 0xD8 + 64 * i + 0x10 + 24 * slot, struct.pack('<QQQ', buf, buf, buf + 0x1000))
                for i, s in enumerate([a, b]): o.poke(sides + 0x20 * i, struct.pack('<H2xI4xIIB3x', *s))
                if step_mode:
                    troop_count, basic, extra, fired_before, missing_source = step_inputs
                    o.poke(record, bytes(0x40)); o.poke(record, struct.pack('<Q', 0 if missing_source else sides))
                    o.poke(record + 8, o.read(sides + 0x20, 0x18)); o.poke(record + 0x12, struct.pack('<H', troop_count))
                    o.poke(frame, bytes(0x40)); o.poke(frame + 0x20, struct.pack('<HHI', basic, extra, fired_before))
                    allowed = o.call32(0x17D650, sides, sides + 0x20)
                battle = o.call(0x16D960); o.poke(battle + 0x98, bytes(4))
                before = {t: o.read(starts[t], world.TABLES[t][1] * world.TABLES[t][2]) for t in tables}
                result = []
                def inspect():
                    maps.inspect(None)
                    queue, rows, changed = [], [], []
                    node = o.q(manager + 0x18)
                    while node:
                        kind, unit, tactic = struct.unpack('<IH2xQ', o.read(node, 16))
                        targets = [[(ptr - starts[TILES]) // 32 for ptr in items(node + 16 + 16 * slot)] for slot in range(2)]
                        queue.append([kind, unit, tactic_refs[tactic], targets]); node = o.q(node + 0x30)
                    for i in range(11):
                        row = manager + 0xD8 + i * 64
                        tp, kind, number = struct.unpack('<QII', o.read(row, 16))
                        if kind == 255: continue
                        outputs = []
                        for slot in range(2):
                            begin, end, cap = struct.unpack('<QQQ', o.read(row + 16 + slot * 24, 24))
                            if end > cap or (end - begin) % 16: raise RuntimeError('Invalid native flow result vector')
                            output = []
                            for at in range(begin, end, 16):
                                k, index, amount, unused, h = struct.unpack('<IIHHI', o.read(at, 16)); output.append([k, index, h, amount])
                            outputs.append(output)
                        rows.append([i, tactic_refs[tp], kind, number, outputs])
                    for t in tables:
                        size, count = world.TABLES[t][1:]; after = o.read(starts[t], size * count)
                        for i in range(count):
                            part = after[i * size:(i + 1) * size]
                            if part != before[t][i * size:(i + 1) * size]: changed.append([hex(t), i, part.hex()])
                    index = struct.unpack('<I', o.read(o.q(ctx.raw + 0x140), 4))[0]
                    node = o.q(o.q(ctx.g.exe + world.LISTS[0x140]['pool']) + index * 8); pending = []
                    while node:
                        data = o.read(node, 56)
                        pending.append([*[refs.get(ptr) if ptr else None for ptr in struct.unpack_from('<4Q', data)], *struct.unpack_from('<3i', data, 32)])
                        node = struct.unpack_from('<Q', data, 48)[0]
                    result.extend([queue, rows, maps.result, pending, struct.unpack('<I', o.read(battle + 0x98, 4))[0], changed])
                    if step_mode: result.append(list(struct.unpack('<HHI', o.read(frame + 0x20, 8))))
                    return []
                if step_mode:
                    next_step = o.call(0x16FDD0, 0, record, frame, read_after=inspect) & 0xFFFFFFFF
                    return [int(bool(result[0])), list(captured), *result, next_step, allowed]
                fired = o.call(0x172E20, manager, sides, sides + 0x20, read_after=inspect) & 0xFFFFFFFF
                return [fired, list(captured), *result]
            result = ctx.run(p, run)
            if result is None: raise RuntimeError(f'Native tactic flow failed in case {n}, sides {a}/{b}, step inputs {step_inputs}: {ctx.failures[-1]}')
            cases.append([a, b, result, p, step_inputs] if step_mode else [a, b, result, p])
            if (n + 1) % 100 == 0: print(f'  native tactic flow: {n + 1}/1200', flush=True)
    finally: o.mu.hook_del(hook)
    ctx.save('engagement_tactic.json.gz' if step_mode else 'tactic_flow.json.gz', {'cases': cases, 'world': 'world.json.gz', 'tables': [hex(t) for t in tables]})
    return f'main selections, composed queues and whole-table state deltas {len(cases)}'
