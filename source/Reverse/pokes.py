# Pokes: changes to the oracle's copies of the game's objects before a call (the game itself is never written).
# The same pokes go into the vectors, and Tests/Formulas/Vectors.Poke applies them to the C# World.
#   [table, index, offset, hex]   bytes of record `index` of the table at raw + table ("0xdf30")
#   ["settings", 0, offset, hex]  bytes of the options object (exe + 0x1A46CA0)
#   ["worldstate", 0, offset, hex] bytes of CWorldData ([raw + 0x871E8])
#   ["list", head, refs, ""]      append an element to the list at raw + head (world.LISTS): refs is
#                                 [[table, index] or null per field]; a bare index means [[items, index]]
#   ["vec40", office, length, ""] cut a CPostData's bonus vector (+0x40..+0x48) to `length` bytes
#   ["exe", name, 0, hex]         an exe global of world.EXE_VALUES (an "int" one: its dword)
#   ["vec", table, index, [at, length]] cut the vector at +at..+at+8 of a record to `length` bytes
#   ["vecdata", [table, index], at, hex] replace that vector with bytes in emulated scratch, then restore its pointers
import struct

import world


class Poker:
    def __init__(self, g, o):
        self.g, self.o = g, o
        self.raw = g.base - 0x290
        self.scratch = o.alloc(0x10000)
        self.used = 0

    def addr(self, table, i):
        return world.table_ptr(self.o, self.g, table, i)

    def _node(self, item_ptr):
        a = self.scratch + self.used
        self.used += 0x10
        self.o.poke(a, struct.pack("<QQ", item_ptr, 0))
        return a

    def apply(self, pokes):
        undo = []
        for t, idx, off, h in pokes:
            if t == "listdata":
                # Replace a caller-supplied list, including the node pool's count; no game memory is changed.
                head = int(idx, 16)
                spec = world.LISTS[head]
                index = struct.unpack('<I', self.o.read(self.o.q(self.raw + head), 4))[0]
                pool = self.o.q(self.g.exe + spec['pool'])
                slot = pool + index * 8
                count_slot = self.o.q(self.g.exe + spec['pool'] + 0x18) + index * 8
                undo += [(slot, self.o.read(slot, 8)), (count_slot, self.o.read(count_slot, 8))]
                nodes = []
                for refs in off:
                    node = self.scratch + self.used
                    self.used += 8 * (spec['next'] + 1)
                    ptrs = [0 if r is None else self.addr(int(r[0], 16), r[1]) for r in refs]
                    words = ptrs + [0] * (spec['next'] + 1 - len(ptrs))
                    self.o.poke(node, struct.pack(f'<{len(words)}Q', *words))
                    if nodes: self.o.poke(nodes[-1] + 8 * spec['next'], struct.pack('<Q', node))
                    nodes.append(node)
                self.o.poke(slot, struct.pack('<Q', nodes[0] if nodes else 0))
                self.o.poke(count_slot, struct.pack('<Q', len(nodes)))
                continue
            if t == "list":
                head = int(idx, 16) if isinstance(idx, str) else idx
                spec = world.LISTS[head]
                refs = [["0x77b18", off]] if isinstance(off, int) else off
                ptrs = [0 if r is None else self.addr(int(r[0], 16), r[1]) for r in refs]
                words = ptrs + [0] * (spec["next"] + 1 - len(ptrs))
                new = self.scratch + self.used
                self.used += 8 * len(words) + 8
                self.o.poke(new, struct.pack(f"<{len(words)}Q", *words))
                pool = self.o.q(self.g.exe + spec["pool"])
                slot = pool + struct.unpack("<I", self.o.read(self.o.q(self.raw + head), 4))[0] * 8
                node = self.o.q(slot)
                if not node:
                    at = slot
                else:
                    while self.o.q(node + 8 * spec["next"]): node = self.o.q(node + 8 * spec["next"])
                    at = node + 8 * spec["next"]
                undo.append((at, self.o.read(at, 8)))
                self.o.poke(at, struct.pack("<Q", new))
                continue
            if t == "exe":
                rva, how = world.EXE_VALUES[idx]
                a = world.exe_value_address(self.o, self.g.exe, rva, how)
                data = bytes.fromhex(h)
                # Named byte fields accept integer encodings but write only their own byte, never adjacent flags.
                if how.startswith('ptrbyte'): data = data[:1]
                if not a:
                    # An absent optional metadata object is a fuzzed input, materialized only in scratch.
                    # Restore the global pointer before freezing; native readers still execute their branches.
                    prefix = 'ptrbyte' if how.startswith('ptrbyte') else 'ptrint'
                    offset = int(how[len(prefix):])
                    base = self.scratch + self.used
                    self.used += 0x40
                    if self.used > 0x10000 or offset + len(data) > 0x40: raise ValueError('indirect exe poke exceeds scratch capacity')
                    self.o.poke(base, bytes(0x40))
                    pointer = self.g.exe + rva
                    undo.append((pointer, self.o.read(pointer, 8)))
                    self.o.poke(pointer, struct.pack('<Q', base))
                    a = base + offset
                undo.append((a, self.o.read(a, len(data))))
                self.o.poke(a, data)
                continue
            if t == "vecdata":
                table = int(idx[0], 16)
                rec = self.addr(table, idx[1])
                data = bytes.fromhex(h)
                a = self.scratch + self.used
                self.used += (len(data) + 15) & ~15
                if self.used > 0x10000: raise ValueError("vector pokes exceed scratch capacity")
                self.o.poke(a, data)
                undo.append((rec + off, self.o.read(rec + off, 16)))
                self.o.poke(rec + off, struct.pack("<QQ", a, a + len(data)))
                continue
            if t == "vec":
                table = int(idx[0], 16)
                rec = self.addr(table, idx[1])
                at, length = off
                begin = self.o.q(rec + at)
                undo.append((rec + at + 8, self.o.read(rec + at + 8, 8)))
                self.o.poke(rec + at + 8, struct.pack("<Q", begin + length))
                continue
            if t == "vec40":
                rec = self.addr(0x784F8, idx)
                begin = self.o.q(rec + 0x40)
                undo.append((rec + 0x48, self.o.read(rec + 0x48, 8)))
                self.o.poke(rec + 0x48, struct.pack("<Q", begin + off))
                continue
            data = bytes.fromhex(h)
            if t == "settings": a = self.g.exe + world.SETTINGS_RVA + off
            elif t == "worldstate": a = self.o.q(self.raw + world.WORLDSTATE_RAW) + off
            else: a = self.addr(int(t, 16), idx) + off
            undo.append((a, self.o.read(a, len(data))))
            self.o.poke(a, data)
        return undo

    def undo(self, undo):
        for a, old in reversed(undo): self.o.poke(a, old)
        self.used = 0
