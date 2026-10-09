# World+0x70 traversal and complete unit reset, with real native pool clear operations.
# The finalizer callback fixtures exercise traversal only; unit_finalization runs its real engine body.
import collections, random, struct
from context import *
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX

COVER = [0x28E290, 0x226830, 0x18770]


class GeneralListOracle:
    def __init__(self, ctx, wrapper, capacity=132):
        self.ctx, self.o = ctx, ctx.o
        self.pool = ctx.g.exe + 0x27D10E0
        self.index = struct.unpack('<I', self.o.read(wrapper, 4))[0]
        if self.index >= struct.unpack('<I', self.o.read(self.pool + 0x40, 4))[0]:
            raise RuntimeError('General-list wrapper index is out of bounds')
        self.slots = [self.o.q(self.pool + at) + 8 * self.index for at in [0x10, 0x18, 0x28]]
        self.nodes, self.capacity = self.o.alloc(capacity * 24), capacity

    def prepare(self, pointers):
        if len(pointers) > self.capacity: raise RuntimeError('General-list fixture capacity exceeded')
        o = self.o
        self.old = [(a, o.read(a, 8)) for a in [*self.slots, self.pool + 0x30]]
        old_count = o.q(self.slots[2])
        for i, p in enumerate(pointers):
            o.poke(self.nodes + i * 24, struct.pack('<3Q', p,
                self.nodes + (i + 1) * 24 if i + 1 < len(pointers) else 0,
                self.nodes + (i - 1) * 24 if i else 0))
        self.count = len(pointers)
        for at, value in zip(self.slots, [self.nodes if pointers else 0,
                self.nodes + (len(pointers) - 1) * 24 if pointers else 0, len(pointers)]):
            o.poke(at, struct.pack('<Q', value))
        o.poke(self.pool + 0x30, struct.pack('<Q', o.q(self.pool + 0x30) - old_count + len(pointers)))
        self.total, self.free = o.q(self.pool + 0x30), o.q(self.pool + 0x20)

    def restore(self):
        for at, data in reversed(self.old): self.o.poke(at, data)

    def append(self, pointer):
        if self.count >= self.capacity: raise RuntimeError('General-list append capacity exceeded')
        o, node = self.o, self.nodes + self.count * 24
        o.emu_write(node, struct.pack('<3Q', pointer, 0, o.q(self.slots[1])))
        o.emu_write(o.q(self.slots[1]) + 8, struct.pack('<Q', node))
        o.emu_write(self.slots[1], struct.pack('<Q', node))
        self.count += 1
        o.emu_write(self.slots[2], struct.pack('<Q', self.count))
        o.emu_write(self.pool + 0x30, struct.pack('<Q', o.q(self.pool + 0x30) + 1))

    def inspect_clear(self, removed):
        o = self.o
        if [o.q(a) for a in self.slots] != [0, 0, 0] or o.q(self.pool + 0x30) != self.total - removed:
            raise RuntimeError('Native general-list clear bookkeeping differs')
        if self.count and (o.q(self.pool + 0x20) != self.nodes or
                o.q(self.nodes + (self.count - 1) * 24 + 8) != self.free):
            raise RuntimeError('Native general-list nodes were not returned to the free chain')


def generate(ctx):
    o, rng = ctx.o, random.Random(0x226830)
    live = [i for i, r in enumerate(ctx.rec[UNITS]) if i and r[0x10] and ctx.u16(UNITS, i, 0x12)]
    unit = live[0]
    refs = {ctx.addr(UNITS, i): i for i in range(len(ctx.rec[UNITS]))}
    ref = lambda p: refs[p] if p else None
    pointer = lambda i: ctx.addr(UNITS, i) if i is not None else 0
    pending = GeneralListOracle(ctx, o.q(ctx.raw + 0x70))
    calls, actions, traversal, resets, clear_calls = [], [], [], [], []
    stats = collections.Counter()
    def boundary():
        calls.append(ref(o.mu.reg_read(UC_X86_REG_RCX)))
        for ordinal, action in actions:
            if ordinal != len(calls) - 1: continue
            if action[0] == 'replace':
                o.emu_write(pending.nodes + action[1] * 24, struct.pack('<Q', pointer(action[2])))
            elif action[0] == 'append': pending.append(pointer(action[1]))
            else: raise RuntimeError('Unknown pending-finalizer boundary action')
        return 0
    o.stub(0x282130, fn=boundary)
    try:
        for n in range(800):
            count = [0, 1, 2, 5, 128][n % 5] if n < 40 else rng.randrange(1, 9)
            members = [rng.choice([None, 0, unit, live[1], 400]) for _ in range(count)]
            actions[:] = []
            if count > 1 and n % 3 == 0:
                actions.append([0, ['replace', 1, live[1]]]); stats['replace'] += 1
            elif count and n % 3 == 1:
                actions.append([0, ['append', unit]]); stats['append'] += 1
            pending.prepare([pointer(i) for i in members]); calls.clear()
            try:
                o.call(0x28E290, read_after=lambda: pending.inspect_clear(count) or [])
            finally: pending.restore()
            traversal.append([members, list(actions), list(calls)])
            stats['callbacks'] += len(calls); stats['null'] += calls.count(None)
            stats['invalid'] += sum(i in [0, 400] for i in calls)
            if (n + 1) % 200 == 0: print(f'  pending finalizers {n + 1}/800: {dict(stats)}', flush=True)
    finally: o.unstub()
    lists = [(at, o.q(ctx.addr(UNITS, unit) + at)) for at in range(0x160, 0x1F0, 16)]
    if not all(p for _, p in lists): raise RuntimeError('Reset fixtures require nine initialized list wrappers')
    native_lists = [GeneralListOracle(ctx, p, capacity=8) for _, p in lists]
    pool = ctx.g.exe + 0x27D10E0
    def cleared(mu, address, size, data):
        if mu.reg_read(UC_X86_REG_RCX) != pool: raise RuntimeError('Unexpected pool in unit reset')
        index = mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF
        clear_calls.append(next(at for (at, _), fixture in zip(lists, native_lists) if fixture.index == index))
    hook = o.mu.hook_add(UC_HOOK_CODE, cleared, begin=ctx.g.exe + 0x18770, end=ctx.g.exe + 0x18770)
    try:
        for n in range(600):
            before = bytearray(rng.randbytes(0x200))
            enabled = n % 5 != 0
            counts, retained = [], []
            for (at, p), fixture in zip(lists, native_lists):
                present = n % 4 == 0 or rng.randrange(4) != 0
                struct.pack_into('<Q', before, at, p if present else 0)
                count = rng.randrange(5); counts.append(count)
                fixture.prepare([pointer(unit)] * count)
                if present: retained.append(at)
            total, free = o.q(pool + 0x30), o.q(pool + 0x20)
            pokes = [[hex(UNITS), unit, 0, before.hex()]]
            old_enabled = o.read(pool + 8, 8)
            if not enabled: o.poke(pool + 8, bytes(8))
            clear_calls.clear(); result = []
            def inspect():
                result.append(o.read(ctx.addr(UNITS, unit), 0x200).hex())
                for (at, _), fixture, count in zip(lists, native_lists, counts):
                    slots = [o.q(a) for a in fixture.slots]
                    if enabled and at in retained:
                        if slots != [0, 0, 0]: raise RuntimeError('Unit external list was not cleared')
                    elif slots != [fixture.nodes if count else 0, fixture.nodes + (count - 1)*24 if count else 0, count]:
                        raise RuntimeError('Absent/disabled unit list changed')
                removed = sum(count for (at, _), count in zip(lists, counts) if enabled and at in retained)
                if o.q(pool + 0x30) != total - removed: raise RuntimeError('Unit reset pool total differs')
                returned = [fixture.nodes + i*24 for (at, _), fixture, count in reversed(list(zip(lists, native_lists, counts)))
                            if enabled and at in retained for i in range(count)]
                if o.q(pool + 0x20) != (returned[0] if returned else free):
                    raise RuntimeError('Unit reset free-chain head differs')
                for i, node in enumerate(returned):
                    if o.q(node + 8) != (returned[i+1] if i+1 < len(returned) else free):
                        raise RuntimeError('Unit reset free-chain nodes differ')
                return []
            try:
                ctx.run(pokes, lambda: o.call(0x226830, ctx.addr(UNITS, unit), read_after=inspect))
                if not result: raise RuntimeError(f'Unit reset oracle failed: {ctx.failures[-1]}')
                expected = retained if enabled else []
                if clear_calls != expected: raise RuntimeError('Unit reset list order differs')
                resets.append([unit, enabled, before.hex(), result[0], list(clear_calls)])
                stats['reset_clears'] += len(clear_calls)
            finally:
                o.poke(pool + 8, old_enabled)
                for fixture in reversed(native_lists): fixture.restore()
            if (n + 1) % 200 == 0: print(f'  unit resets {n + 1}/600: {dict(stats)}', flush=True)
    finally: o.mu.hook_del(hook)
    ctx.save('unit_lifecycle_world.json.gz', ctx.snap)
    ctx.save('unit_lifecycle.json.gz', {'world': 'unit_lifecycle_world.json.gz', 'traversal': traversal,
        'resets': resets, 'stats': dict(stats), 'native_clear': True, 'finalizer_boundary': hex(0x282130)})
    return f'pending traversal 800, complete unit reset 600: {dict(stats)}'
