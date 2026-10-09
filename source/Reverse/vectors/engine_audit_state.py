# State observed around native engine boundaries; this does not implement an engine.
import copy
import struct
from context import UNITS, world
from destruction_overflow import OverflowQueue
from unit_finalization import auxiliary_snapshot


class EngineAuditState:
    def __init__(self, ctx):
        self.ctx, self.o = ctx, ctx.o
        o = self.o
        self.snapshot = copy.deepcopy(ctx.snap)
        # The legacy "county_loss" name points into workspace 0: search node 36137, flags +0x1C.
        # 0x1BB7B00 + 36137 * 40 + 0x1C == 0x1D18984. It is not a gameplay rule or setting.
        self.snapshot['flags'].pop('county_loss', None)
        self.snapshot['tables'].update(auxiliary_snapshot(ctx))
        self.tables = {int(t, 16): (ctx.addr(int(t, 16), 0), data['size'], len(data['records']))
                       for t, data in self.snapshot['tables'].items()}
        self.queue = OverflowQueue(ctx)
        pool = ctx.g.exe + 0x27D10E0
        self.heads, self.counts = o.q(pool + 0x10), o.q(pool + 0x28)
        self.bound = struct.unpack('<I', o.read(pool + 0x40, 4))[0]
        self.wrappers = [(f'unit:{i}:{at:x}', ctx.addr(UNITS, i) + at)
                         for i in range(501) for at in range(0x160, 0x1F0, 16)]
        self.wrappers += [(f'caution:{i}', ctx.addr(0x80BA8, i) + 0x30) for i in range(501)]
        self.wrappers.append(('world:130', ctx.raw + 0x130))
        self.ws = o.q(ctx.raw + world.WORLDSTATE_RAW)

    def members(self, wrapper):
        o = self.o
        if not wrapper:
            return []
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        if index >= self.bound:
            return []
        node, seen, out = o.q(self.heads + index * 8), set(), []
        while node:
            if node in seen:
                raise RuntimeError('Engine audit list cycle')
            seen.add(node)
            out.append(hex(o.q(node)))
            node = o.q(node + 8)
        if len(out) != o.q(self.counts + index * 8):
            raise RuntimeError('Engine audit list count differs')
        return out

    def unit_cache(self):
        o = self.o
        manager = o.q(self.ctx.g.exe + 0x1B9A4A0)
        head = o.q(manager + 0xB0)
        todo, seen, keys = [o.q(head + 8)], set(), []
        while todo:
            node = todo.pop()
            if o.read(node + 0x19, 1)[0]:
                continue
            if not node or node in seen:
                raise RuntimeError('Engine audit unit cache cycle/null')
            seen.add(node)
            keys.append(hex(o.q(node + 0x20)))
            todo.extend([o.q(node), o.q(node + 0x10)])
        if len(keys) != o.q(manager + 0xB8):
            raise RuntimeError('Engine audit unit cache count differs')
        return sorted(keys)

    def read(self):
        o, ctx = self.o, self.ctx
        flags = {name: o.read(address, 8 if how == 'set' else 1 if how.startswith('ptrbyte') else 4)
                 if address else None
                 for name, (rva, how) in world.EXE_VALUES.items() if name in self.snapshot['flags']
                 for address in [world.exe_value_address(o, ctx.g.exe, rva, how)]}
        return dict(records={t: o.read(first, size * count) for t, (first, size, count) in self.tables.items()},
                    worldstate=o.read(self.ws, world.WORLDSTATE_SIZE),
                    lists={hex(h): world.list_items(o, ctx.raw, ctx.g.exe, h, spec)
                           for h, spec in world.LISTS.items() if h != 0x140},
                    nested={name: self.members(o.q(address)) for name, address in self.wrappers},
                    unit_cache=self.unit_cache(), queue=self.queue.items(), cmdactions=world.cmd_actions(o),
                    rng=o.read(ctx.g.exe + 0x1A47B90, 4),
                    settings=o.read(ctx.g.exe + world.SETTINGS_RVA, world.SETTINGS_SIZE), flags=flags)

    def delta(self, before, after):
        out = dict(records=[])
        for t, (_, size, count) in self.tables.items():
            if before['records'][t] == after['records'][t]:
                continue
            out['records'].extend([hex(t), i, after['records'][t][size * i:size * (i + 1)].hex()]
                                 for i in range(count)
                                 if before['records'][t][size * i:size * (i + 1)]
                                 != after['records'][t][size * i:size * (i + 1)])
        for name in after.keys() - {'records'}:
            if before[name] == after[name]:
                continue
            value = after[name]
            out[name] = value.hex() if isinstance(value, bytes) else (
                {k: v.hex() if isinstance(v, bytes) else v for k, v in value.items()}
                if name == 'flags' else value)
        return out
