# Complete effect leaf execution with caller-owned native input lists and result vectors.
import struct
from context import *
from tactic_targets import TACTICS, CITIES, GATES, TargetOracle


class EffectOracle:
    def __init__(self, ctx):
        self.ctx, self.o = ctx, ctx.o
        self.native = TargetOracle(ctx)
        self.idx = self.native.indices[0]
        self.info, self.nodes, self.events = self.o.alloc(0x100), self.o.alloc(0x1000), self.o.alloc(0x1000)
        self.vector, self.obj_out = self.info + 0x80, self.info + 0x60

    def setup(self, tactic, source, links, center=48400):
        o, ctx = self.o, self.ctx
        unit, base = source
        o.poke(self.info, bytes(0x100))
        o.poke(self.info, struct.pack('<QQQQ', ctx.addr(TACTICS, tactic), links,
            ctx.addr(UNITS, unit) if unit is not None else 0, ctx.addr(int(base[0], 16), base[1]) if base else 0))
        o.poke(self.info + 0x20, struct.pack('<Q', center))
        o.poke(self.info + 0x28, struct.pack('<Q', self.vector))
        o.poke(self.vector, struct.pack('<QQQ', self.events, self.events, self.events + 0x1000))

    def apply(self, fn, tactic, source, slot, links, targets, pokes, arg4=None, stack_args=(), center=48400, prepare=None, inspect=None, include_tiles=False, include_buildings=False, extra_records=()):
        o, ctx, native = self.o, self.ctx, self.native

        def run():
            self.setup(tactic, source, links, center)
            if prepare: prepare(self)
            for i, h in enumerate(targets):
                o.poke(self.nodes + 16 * i, struct.pack('<QQ', ctx.addr(TILES, h), self.nodes + 16 * (i + 1) if i + 1 < len(targets) else 0))
            old_head = o.read(native.pool + self.idx * 8, 8)
            o.poke(native.pool + self.idx * 8, struct.pack('<Q', self.nodes if targets else 0))
            records = list(extra_records)
            try:
                for h in targets:
                    if include_tiles and (TILES, h) not in records: records.append((TILES, h))
                    o.call(0x16EA90, self.obj_out, self.obj_out + 4, ctx.addr(TILES, h), read_after=[(self.obj_out, 8)])
                    k, i = struct.unpack('<II', o.after[0])
                    table = UNITS if k == 27 else CITIES if k == 5 else GATES if k == 6 else None
                    if table is not None and (table, i) not in records: records.append((table, i))
                    if include_buildings:
                        from tactic_targets import BUILDINGS
                        building = struct.unpack('<H', o.read(ctx.addr(TILES, h) + 0x1A, 2))[0]
                        if building <= 3000 and (BUILDINGS, building) not in records: records.append((BUILDINGS, building))
                reads = [(ctx.addr(t, i), world.TABLES[t][1]) for t, i in records] + [(self.vector, 24), (self.events, 16 * len(targets))]
                args = [self.info, slot, native.out] + ([] if arg4 is None else [arg4])
                def read_all():
                    if inspect: inspect(self)
                    return reads
                o.call(fn, *args, stack_args=stack_args, read_after=read_all)
                changed = [[hex(t), i, o.after[n].hex()] for n, (t, i) in enumerate(records)]
                begin, end, cap = struct.unpack('<QQQ', o.after[-2])
                if begin != self.events or end > cap: raise RuntimeError('unexpected native result vector storage')
                output = []
                for at in range(0, end - begin, 16):
                    k, i, amount, unused, h = struct.unpack_from('<IIHHI', o.after[-1], at)
                    output.append([k, i, h, amount])
                return [changed, output]
            finally: o.poke(native.pool + self.idx * 8, old_head)
        return ctx.run(pokes, run)
