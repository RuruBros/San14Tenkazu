# Verify the real consumer and pool clear while observing the three explicit engine boundaries.
# Boundary fixtures change later inputs; they do not claim to implement capture/destruction effects.
import collections, random, struct
from context import *
from tactic_targets import CITIES, GATES
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

COVER = [0x44F060, 0x325AF0, 0x33DFE0, 0x22B980]
HANDLERS = [0x258EE0, 0x284D60, 0x284010]


class ConsumerOracle:
    def __init__(self, ctx, capacity=132):
        self.ctx, self.o = ctx, ctx.o
        o = self.o
        self.pool = ctx.g.exe + 0x2144570
        self.index = struct.unpack('<I', o.read(o.q(ctx.raw + 0x140), 4))[0]
        self.slots = [o.q(self.pool + at) + 8 * self.index for at in [0x10, 0x18, 0x28]]
        self.nodes = o.alloc(capacity * 64)
        self.refs = {ctx.addr(t, i): [hex(t), i] for t in [UNITS, CITIES, GATES, CORPS] for i in range(len(ctx.rec[t]))}
        self.count, self.calls = 0, []

    def ptr(self, ref): return self.ctx.addr(int(ref[0], 16), ref[1]) if ref else 0

    def payload(self, request):
        return struct.pack('<4Q4i', *[self.ptr(r) for r in request[:4]], request[6], request[4], request[5], 0)

    def prepare(self, requests):
        o = self.o
        self.old = [(a, o.read(a, 8)) for a in [*self.slots, self.pool + 0x30]]
        old_count = o.q(self.slots[2])
        for i, request in enumerate(requests):
            o.poke(self.nodes + i * 64, self.payload(request) + struct.pack('<QQ',
                self.nodes + (i + 1) * 64 if i + 1 < len(requests) else 0, self.nodes + (i - 1) * 64 if i else 0))
        self.count = len(requests)
        for a, v in zip(self.slots, [self.nodes if requests else 0, self.nodes + (len(requests) - 1) * 64 if requests else 0, len(requests)]):
            o.poke(a, struct.pack('<Q', v))
        o.poke(self.pool + 0x30, struct.pack('<Q', o.q(self.pool + 0x30) - old_count + len(requests)))
        self.total = o.q(self.pool + 0x30)
        self.free = o.q(self.pool + 0x20)
        self.calls.clear()

    def restore(self):
        for a, data in reversed(self.old): self.o.poke(a, data)

    def call_args(self, kind):
        o = self.o
        regs = [o.mu.reg_read(r) for r in [UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9]]
        ref = lambda p: self.refs[p] if p else None
        signed = lambda n: n - 0x100000000 if n & 0x80000000 else n
        target, a, b, value = regs
        if kind == 0:
            reason = struct.unpack('<i', o.read(o.mu.reg_read(UC_X86_REG_RSP) + 0x28, 4))[0]
            return [kind, ref(target), ref(a), ref(b), None, signed(value & 0xFFFFFFFF), reason, 0, False]
        if kind == 1: return [kind, ref(target), ref(b), None, ref(a), 0, 0, signed(value & 0xFFFFFFFF), False]
        return [kind, ref(target), ref(a), None, None, 0, 0, 0, bool(b & 255)]

    def action(self, action):
        o = self.o
        kind = action[0]
        if kind == 'poke':
            _, table, index, at, data = action
            o.emu_write(self.ctx.addr(int(table, 16), index) + at, bytes.fromhex(data))
        elif kind == 'replace': o.emu_write(self.nodes + action[1] * 64, self.payload(action[2]))
        elif kind == 'append':
            node = self.nodes + self.count * 64
            o.emu_write(node, self.payload(action[1]) + struct.pack('<QQ', 0, o.q(self.slots[1])))
            o.emu_write(o.q(self.slots[1]) + 48, struct.pack('<Q', node))
            o.emu_write(self.slots[1], struct.pack('<Q', node))
            o.emu_write(self.slots[2], struct.pack('<Q', self.count + 1))
            o.emu_write(self.pool + 0x30, struct.pack('<Q', o.q(self.pool + 0x30) + 1))
            self.count += 1
        else: raise ValueError(f'Unknown consumer boundary action: {action}')

    def inspect_clear(self):
        o = self.o
        cleared = [o.q(a) for a in self.slots]
        if cleared != [0, 0, 0] or o.q(self.pool + 0x30) != self.total - self.initial_count:
            raise RuntimeError(f'Native queue clear failed: {cleared}')
        if self.count:
            if o.q(self.pool + 0x20) != self.nodes or o.q(self.nodes + (self.count - 1) * 64 + 48) != self.free:
                raise RuntimeError('Native queue nodes were not returned to the free chain')
        return cleared


def generate(ctx):
    o, rec = ctx.o, ctx.rec
    rng = random.Random(0x44F060)
    native = ConsumerOracle(ctx)
    cases, stats = [], collections.Counter()
    live = [i for i in range(1, len(rec[UNITS])) if rec[UNITS][i][0x10] and ctx.u16(UNITS, i, 0x12)]
    unit = [hex(UNITS), live[0]]
    attacker = [hex(UNITS), live[1]]
    city, gate = [hex(CITIES), 5], [hex(GATES), 1]
    def request():
        return [rng.choice([None, [hex(UNITS), 0], unit, [hex(UNITS), 400]]),
                rng.choice([None, [hex(CITIES), 0], city, gate]),
                rng.choice([None, [hex(UNITS), 0], attacker]), rng.choice([None, city, gate]),
                rng.choice([-2147483648, -1, 0, 1, 7, 2147483647]), rng.choice([-1, 0, 3, 4, 99]),
                rng.choice([-2147483648, -1, 0, 1, 2147483647])]
    current_actions = []
    def boundary(kind):
        def run():
            native.calls.append(native.call_args(kind))
            for ordinal, action in current_actions:
                if ordinal == len(native.calls) - 1: native.action(action)
            return 0
        return run
    for kind, rva in enumerate(HANDLERS): o.stub(rva, fn=boundary(kind))
    try:
        for n in range(800):
            size = [0, 1, 2, 5, 128][n % 5] if n < 40 else rng.randrange(1, 9)
            requests = [request() for _ in range(size)]
            actions, p = [], []
            # Force all route kinds, including the exact lower/upper tribe boundaries.
            force = [0, 1, 45, 46, 47, 48, 49, 50, 51][n % 9]
            corps = 51
            p += [[hex(CITIES), 5, 0x30, bytes([corps]).hex()],
                  [hex(CORPS), corps, 0x10, bytes([force, 1, 1, 0]).hex()],
                  [hex(FORCES), force, 0x10, '0100'], [hex(FORCES), force, 0x194, bytes([n % 52]).hex()]]
            if n % 12 == 3:
                p.append([hex(UNITS), attacker[1], 0x12, struct.pack('<H', 6001).hex()])
            elif n % 12 == 4:
                commander = ctx.officer_by_id(ctx.u16(UNITS, attacker[1], 0x12))
                p.append([hex(OFFICERS), commander, 0xE0, 'ff'])
            elif n % 12 == 5:
                p.append([hex(UNITS), attacker[1], 0x12, '0000'])
            if requests:
                requests[0] = [unit if n % 3 == 0 else None, city, attacker, gate, -1, 4, -2147483648]
            if 46 <= force <= 50:
                # UnitForce dereferences its source even if invalid; a null tribe source is a native fault.
                for r in requests:
                    if r[1] == city and r[2] is None: r[2] = [hex(UNITS), 0]
            if size >= 2 and n % 4 == 0:
                requests[:2] = [[unit, city, attacker, gate, 1, 3, 7], [unit, city, attacker, gate, -1, 4, -7]]
                actions.append([0, ['poke', hex(UNITS), unit[1], 0x10, '00']])
                stats['recheck'] += 1
            if size >= 2 and n % 4 == 1:
                replacement = [None, city, attacker, gate, 1, 3, 2147483647]
                actions.append([0, ['replace', 1, replacement]]); stats['replace'] += 1
            if requests and n % 4 == 2:
                actions.append([0, ['append', [unit, city, attacker, gate, -2147483648, -1, 0]]]); stats['append'] += 1
            current_actions[:] = actions
            def run():
                native.prepare(requests); native.initial_count = len(requests)
                clear = []
                try:
                    o.call(0x44F060, read_after=lambda: clear.extend(native.inspect_clear()) or [])
                    return [list(native.calls), clear]
                finally: native.restore()
            result = ctx.run(p, run)
            if result is None: raise RuntimeError(f'Consumer oracle failed at case {n}: {ctx.failures[-1]}')
            for call in result[0]: stats[['unit', 'base', 'tribe'][call[0]]] += 1
            stats['empty'] += not requests
            cases.append([requests, actions, *result, p])
            if (n + 1) % 100 == 0: print(f'  destruction consumers {n + 1}/800: {dict(stats)}', flush=True)
    finally: o.unstub()
    ctx.save('destruction_consumer.json.gz', {'cases': cases, 'stats': dict(stats), 'world': 'world.json.gz',
        'boundaries': [hex(r) for r in HANDLERS], 'native_clear': True})
    return f'consumer routing, dynamic inputs and native pool clear {len(cases)}, {dict(stats)}'
