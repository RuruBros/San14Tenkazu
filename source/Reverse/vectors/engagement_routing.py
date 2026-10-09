# Native record eligibility and step 1 routing. Downstream steps/engine are explicit component boundaries.
import gzip, json, random, struct
from context import *
from tactic_targets import CITIES, GATES, BUILDINGS

COVER = [0x169C50, 0x174BC0, 0x233390, 0x233440, 0x496600, 0x2CA3E0]


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x169C50)
    manager, sides, record = o.alloc(0x100), o.alloc(0x40), o.alloc(0x40)
    live = [i for i in range(1, 501) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    entered = []
    def enter():
        entered.append(1)
        return 8
    # Observe admission at the actual step entry; no readiness, list, hostility or challenge gate is stubbed.
    o.stub(0x174BC0, fn=enter)
    o.stub(0x174E30, 0)
    def side(kind, i):
        if kind == 27:
            index = i if i <= 500 else 0
            person = ctx.officer_by_id(ctx.u16(UNITS, index, 0x12))
            return [i, kind, ctx.u16(UNITS, index, 0x2A), ctx.officer_force(person), 1]
        if kind in (5, 6):
            index = i if i <= (51 if kind == 5 else 10) else 0
            tab = CITIES if kind == 5 else GATES
            building = ctx.u16(tab, index, 0x4E)
            return [i, kind, ctx.u16(BUILDINGS, building, 0x10), rec[BUILDINGS][building][0x13], 1]
        if kind == 11:
            index = i if i <= 3000 else 0
            return [i, kind, ctx.u16(BUILDINGS, index, 0x10), rec[BUILDINGS][index][0x13], 1]
        return [i, kind, 0, 0, 1]
    def pack(a, b, missing=False):
        o.poke(manager, bytes(0x100)); o.poke(manager + 0xA0, struct.pack('<Q', record))
        o.poke(sides, struct.pack('<H2xI4xIIB3x', *a))
        o.poke(record, bytes(0x40)); o.poke(record, struct.pack('<Q', 0 if missing else sides))
        o.poke(record + 8, struct.pack('<H2xI4xIIB3x', *b))
    def poke(t, i, off, value): return [hex(t), i, off, value.hex()]
    def ref(t, i): return [hex(t), i]
    def object_for(s):
        kind, i = s[1], s[0]
        if kind == 27: return ref(UNITS, i if i <= 500 else 0)
        if kind in (5, 6): return ref(CITIES if kind == 5 else GATES, i if i <= (51 if kind == 5 else 10) else 0)
        return None
    cases = []
    for n in range(2400):
        pair = []
        for k in range(2):
            kind = rng.choice([27, 27, 27, 5, 6, 11, 255])
            indices = live + [0, 500, 501, 65535] if kind == 27 else [0, 1, 5, 7, 51, 52, 65535] if kind == 5 else [0, 1, 10, 11, 65535] if kind == 6 else [0, 1, 5, 7, 52, 62, 3000, 3001, 65535]
            pair.append(side(kind, rng.choice(indices)))
        a, b = pair
        # Stored force fields can differ from the ownership used by the outer hostility check.
        a[3] = rng.choice([0, a[3], 1, 3, 46, 51, 52])
        p = [['exe', 'challenge_active', 0, struct.pack('<i', n % 3 != 0).hex()],
             ['exe', 'challenge_kind', 0, struct.pack('<i', rng.choice([0, 3])).hex()],
             ['worldstate', 0, 0x3A, bytes([rng.choice([0, 1, 3, 46, 51])]).hex()],
             ['worldstate', 0, 0x165C, bytes([n % 2]).hex()]]
        for f in range(52):
            p += [poke(FORCES, f, 0xEA, bytes(rng.choices([0, 0, 0, 1, 128], k=52))),
                  poke(FORCES, f, 0x194, bytes([rng.choice([0, 0, 1, 3])]))]
        pending, engaged, exempt = [], [], []
        for s in pair:
            key = object_for(s)
            if s[1] == 27:
                i = key[1]
                p.append(poke(UNITS, i, 0x10, bytes([rng.choice([0, 1, 1, 2])])))
                if n % 7 == 0: engaged.append([key])
                if n % 11 == 0: pending.append([key, None, None])
                if n % 13 == 0: pending.append([None, ref(CITIES, 1), key])
            elif s[1] in (5, 6):
                p.append(poke(int(key[0], 16), key[1], 0x69, bytes([rng.choice([0, 0, 1])])))
                if n % 5 == 0: pending.append([None, key, None])
                building = s[0] if s[1] == 5 and s[0] <= 51 else s[0] + 51 if s[1] == 6 and 1 <= s[0] <= 10 else 0
                if n % 17 == 0: exempt.append([ref(BUILDINGS, building)])
            elif s[1] == 11 and n % 17 == 0: exempt.append([ref(BUILDINGS, s[0] if s[0] <= 3000 else 0)])
        p += [['listdata', '0x70', engaged, ''], ['listdata', '0x140', pending, ''], ['listdata', '0xf0', exempt, '']]
        def run():
            entered.clear(); pack(a, b)
            result = o.call32(0x169C50, manager)
            if len(entered) > 1: raise RuntimeError('One native record entered step 1 more than once')
            return [len(entered), result]
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'Native admission failed in case {n}: {ctx.failures[-1]}')
        cases.append([a, b, *result, p])
        if (n + 1) % 400 == 0: print(f'  native record admission: {n + 1}/2400', flush=True)
    o.unstub()
    # Step 1 runs its reach, siege and return-code branches; only subsequent engine/presentation is excluded.
    for rva, value in [(0x2571F0, 0), (0x2B8EC0, 0), (0x174520, 0), (0x1A3A00, 0), (0x1A3C50, 0),
                       (0x2ECB80, 0), (0x2E3050, 0), (0x294B90, 0), (0x31EAB0, 1), (0x1B4990, 0)]: o.stub(rva, value)
    with gzip.open(os.path.join(ctx.output, 'engagement.json.gz'), 'rt') as file: previous = json.load(file)['siege']
    starts = []
    for n in range(1200):
        if n < 300:
            a, b, p = side(27, rng.choice(live)), side(27, rng.choice(live)), []
        else:
            old = rng.choice(previous)
            a, b, p = old[0], old[1], list(old[4])
        missing = n % 23 == 0
        def run():
            pack(a, b, missing)
            reach = o.call32(0x2CA3E0, a[2], b[2])
            building = b[0] if b[1] == 11 else ctx.u16(CITIES if b[1] == 5 else GATES, b[0], 0x4E) if b[1] in (5, 6) else 0
            bp = ctx.addr(BUILDINGS, building)
            result = o.call32(0x174BC0, manager, record, read_after=[(bp, 32)])
            return [reach, result, struct.unpack_from('<H', o.after[0], 0x14)[0]]
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'Native step 1 failed in case {n}: {ctx.failures[-1]}')
        starts.append([a, b, missing, *result, p])
    ctx.save('engagement_routing.json.gz', {'admission': cases, 'start': starts, 'world': 'world.json.gz'})
    return f'outer record admission {len(cases)}, actual step 1 reach/siege/return routing {len(starts)}'
