# The real battle-step gate and deferred-damage transfer, followed by the next step's word addition.
import gzip, json, random, struct
from unicorn.x86_const import UC_X86_REG_R8
from context import *
from tactic_flow import generate as generate_flow
from tactic_targets import CITIES, GATES, BUILDINGS

COVER = [0x16FDD0, 0x17D650, 0x17D7D0, 0x17D960, 0x17A3E0, 0x1749A0, 0x172E20]


def generate(ctx):
    generate_flow(ctx, step_mode=True)
    path = os.path.join(ctx.output, 'engagement_tactic.json.gz')
    with gzip.open(path, 'rt') as file: data = json.load(file)
    o = ctx.o
    data['transfer'] = transfer(ctx)
    sides, record, frame = o.alloc(0x40), o.alloc(0x40), o.alloc(0x40)
    totals = []
    # Capture the already-validated troop-application boundary's argument without running engine side effects.
    def damage():
        totals.append(o.mu.reg_read(UC_X86_REG_R8) & 0xFFFFFFFF)
        return 0
    o.stub(0x178E30, fn=damage)
    rng = random.Random(0x1749A0)
    cases = []
    for n in range(1200):
        basic, extra = (0, 0) if n == 0 else (rng.choice([0, 1, 32767, 32768, 65535]), rng.randrange(65536))
        flag = rng.choice([0, 1, 7])
        o.poke(sides, struct.pack('<H2xI4xIIB3x', 1, 27, 0, 0, 1))
        o.poke(record, bytes(0x40)); o.poke(record, struct.pack('<Q', sides))
        o.poke(record + 8, struct.pack('<H2xI4xIIB3x', 2, 27, 0, 0, 1))
        o.poke(frame, bytes(0x40)); o.poke(frame + 0x20, struct.pack('<HHI', basic, extra, flag))
        totals.clear()
        next_step = o.call32(0x1749A0, 0, record, frame, read_after=[(frame, 0x40)])
        if len(totals) != 1: raise RuntimeError('The damage-application boundary was not reached exactly once')
        cases.append([[basic, extra, flag], list(struct.unpack_from('<HHI', o.after[0], 0x20)), totals[0], next_step])
    data['combine'] = cases
    ctx.save('engagement_tactic.json.gz', data)
    return f'battle tactic gates/deferred maps {len(data["cases"])}, isolated map transfer {len(data["transfer"])}, word addition {len(cases)}'


def transfer(ctx):
    o = ctx.o
    o.unstub()
    # Selection and gameplay were exercised above. This phase supplies map inputs at that boundary only.
    o.stub(0x17D650, 1)
    o.stub(0x172E20, 1)
    manager = ctx.g.exe + 0x1BB38A0
    sides, record, frame = o.alloc(0x40), o.alloc(0x40), o.alloc(0x40)
    heads, nodes = o.alloc(0x80), o.alloc(0x1000)
    rng = random.Random(0x16FDD0)
    amounts = [-2147483648, -65537, -65536, -1, 0, 1, 32767, 32768, 65535, 65536, 65537, 2147483647]
    cases = []
    def poke(t, i, off, value): return [hex(t), i, off, value.hex()]
    for n in range(1200):
        number = rng.randrange(1, 11)
        target = [number, rng.choice([5, 6, 27, 0, 11, 28]), 0, 0, 1]
        if n % 7 == 0: target[0] = 0
        before = [0, rng.choice([0, 1, 65535]), rng.choice([0, 7])]
        unit_map = [[hex(UNITS), i, rng.choice(amounts)] for i in dict.fromkeys([0, number, rng.randrange(1, 11)])]
        base_map = [[hex(t), i, rng.choice(amounts)] for t, i in [(CITIES, 0), (CITIES, number), (GATES, number), (CITIES, 20)]]
        if n % 11 == 0: unit_map = []
        if n % 13 == 0: base_map = []
        # Deliberately decouple insertion order, record index, stored unit number, and base building number.
        rng.shuffle(unit_map); rng.shuffle(base_map)
        p = [['exe', 'challenge_active', 0, '00000000']]
        for _, i, _ in unit_map:
            p += [poke(UNITS, i, 0x10, bytes([0 if n % 5 == 0 else 1])),
                  poke(UNITS, i, 0x12, struct.pack('<H', 1)), poke(UNITS, i, 0x40, struct.pack('<H', 500))]
        for t, i, _ in base_map:
            t = int(t, 16)
            building = number if t == CITIES else number + 51
            if i == 20: building = 62 if n % 2 else 63
            p += [poke(t, i, 0x4E, struct.pack('<H', building)), poke(t, i, 0x69, bytes([n % 5 == 1])),
                  poke(BUILDINGS, building, 0x10, bytes(2)), poke(BUILDINGS, building, 0x12, b'\1')]
        def run():
            o.poke(manager, bytes(0x3F0))
            for k, entries in enumerate([unit_map, base_map]):
                head = heads + k * 64
                ordered = sorted(entries, key=lambda e: ctx.addr(int(e[0], 16), e[1]))
                addresses = [nodes + (k * 16 + i) * 64 for i in range(len(ordered))]
                o.poke(head, bytes(64))
                o.poke(head, struct.pack('<QQQ', addresses[0], addresses[0], addresses[-1]) if addresses else struct.pack('<QQQ', head, head, head))
                o.poke(head + 0x18, b'\1\1')
                o.poke(manager + 0xB8 + 16 * k, struct.pack('<QQ', head, len(ordered)))
                # A sorted right spine has the exact iterator layout; this phase never inserts or erases.
                for i, (tab, index, amount) in enumerate(ordered):
                    node = addresses[i]
                    o.poke(node, bytes(64))
                    o.poke(node, struct.pack('<QQQ', head, addresses[i - 1] if i else head,
                                             addresses[i + 1] if i + 1 < len(addresses) else head))
                    o.poke(node + 0x20, struct.pack('<Qi', ctx.addr(int(tab, 16), index), amount))
            o.poke(sides, struct.pack('<H2xI4xIIB3x', 1, 27, 0, 0, 1))
            o.poke(record, bytes(0x40)); o.poke(record, struct.pack('<Q', sides))
            o.poke(record + 8, struct.pack('<H2xI4xIIB3x', *target))
            o.poke(record + 0x12, struct.pack('<H', 65535))
            o.poke(frame, bytes(0x40)); o.poke(frame + 0x20, struct.pack('<HHI', *before))
            step = o.call32(0x16FDD0, 0, record, frame, read_after=[(frame, 0x40)])
            return [list(struct.unpack_from('<HHI', o.after[0], 0x20)), step]
        result = ctx.run(p, run)
        if result is None: raise RuntimeError(f'Native isolated transfer failed in case {n}: {ctx.failures[-1]}')
        cases.append([target, before, unit_map, base_map, *result, p])
    o.unstub()
    return cases
