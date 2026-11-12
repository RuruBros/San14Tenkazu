"""Offline original policy aggregation and personality condition/filter evaluation.

Only synchronization and memset are host boundaries. No policy visitor, merit,
condition, filter, validity or effect function is replaced. Never opens a game.
"""
import argparse
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_MEM_WRITE, UC_HOOK_MEM_UNMAPPED
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX,
                               UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP, UC_X86_REG_RIP)
from editor_consumer_audit import Native, vtable, sha
from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, require
from version_audit import MemoryImage

ROOTS = {0x41C200: 0x41E570, 0x41C440: 0x41E7B0, 0x27DF10: 0x27FCB0}


def mappings(old, new):
    functions = dict(ROOTS)
    globals_ = {}
    pending = list(functions.items())
    verified, changed = [], []
    while pending:
        a, b = pending.pop()
        left, right = old.describe(a), new.describe(b)
        if left['shape_sha256'] != right['shape_sha256']:
            require((a, b) == (0x22C3B0, 0x22DDF0), f'Unreviewed changed function: {a:x}/{b:x}')
            changed.append((a, b))
            continue
        verified.append((a, b))
        for l, r in zip(left['references'], right['references'], strict=True):
            globals_.setdefault(l['rva'], set()).add(r['rva'])
            if a == 0x27DF10 and old.is_code(l['rva']) and new.is_code(r['rva']) and l['rva'] not in functions:
                functions[l['rva']] = r['rva']
                pending.append((l['rva'], r['rva']))
        for l, r in zip(left['direct_edges'], right['direct_edges'], strict=True):
            if l['rva'] not in functions:
                functions[l['rva']] = r['rva']
                if l['rva'] < 0xA00000:
                    pending.append((l['rva'], r['rva']))
        require(len(functions) < 1000, 'Native call graph limit')
    return functions, globals_, verified, changed


class EffectsNative(Native):
    def __init__(self, image, november, functions, globals_):
        self.index = int(november)
        self.functions, self.globals = functions, globals_
        self.host = {self.rva(at): name for at, name in
                     ((0xFF8390, 'mutex-lock'), (0xFF83B4, 'mutex-unlock'), (0x101B9B0, 'memset'))}
        self.host_calls = []
        self.trace = []
        self.in_call = False
        super().__init__(image, november)
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.on_write)
        self.cpu.hook_add(UC_HOOK_MEM_UNMAPPED, self.on_fault)
        self.last_fault = None
        self.writes = set()
        self.policies, self.effects = self.root + 0x700000, self.root + 0x710000
        self.vectors, self.credits = self.root + 0x720000, self.root + 0x730000
        self.corps, self.formations = self.root + 0x740000, self.root + 0x750000
        self.units, self.terrains = self.root + 0x800000, self.root + 0x840000
        self.traits = self.root + 0x850000
        self.world = self.root + 0x90000
        self.tiles = 0x500000000
        self.pool_data = 0x400000000
        self.cpu.mem_map(self.tiles, 0x200000)
        self.cpu.mem_map(self.pool_data, 0x100000)
        self.cpu.msr_write(0xC0000101, self.root + 0xE00000)
        self.put(self.root + 0xE00008, struct.pack('<QQ', self.root + 0xF10000, self.root + 0xD00000))
        for name in ('CWorldData', 'CPolicyData', 'CPolicyEffectData', 'CDistrictData', 'CArmyUnitData', 'CFormationData', 'CHexData', 'CTerrainData', 'CAreaData'):
            self.vtables[name] = vtable(image, name)
        self.table(0x78D50, self.policies, 0xA8, 121, 'CPolicyData')
        self.table(0xDF30, self.forces, 0x268, 52, 'CForceData')
        self.table(0x80880, self.effects, 0xB0, 101, 'CPolicyEffectData')
        self.table(0xE0D0, self.corps, 0x30, 52, 'CDistrictData')
        self.table(0x7E838, self.units, 0x200, 501, 'CArmyUnitData')
        self.table(0x77428, self.formations, 0xC8, 21, 'CFormationData')
        self.table(0x7DD18, self.traits, 0xE8, 356, 'CPersonalityData')
        self.table(0x73860, self.terrains, 0x48, 31, 'CTerrainData')
        self.table(0x6CAF0, self.root + 0x870000, 0x98, 501, 'CAreaData')
        self.table(0xE270, self.tiles, 0x20, 48400, 'CHexData')
        self.put(self.root + 0x871E8, struct.pack('<Q', self.world))
        self.put(self.world, struct.pack('<Q', image.base + self.vtables['CWorldData']))
        self.put(self.world + 0xBC, b'\xff')
        self.put(self.global_at(0x2143268), bytes(8))
        # Empty pending-removal/command lists are an ordinary active-unit fixture.
        self.put(self.global_at(0x2144580), bytes(8))
        self.put(self.global_at(0x21445B0), bytes(4))
        self.pool = self.global_at(0x27D10E0)
        self.put(self.global_at(0x27D10B0), bytes(4))
        for number in range(7):
            self.put(self.root + 0x85DF0 + number * 8, struct.pack('<Q', self.credits + number * 0x18))
            self.put(self.credits + number * 0x18 + 0x10, struct.pack('<BxH', 3, number))
        self.reset_pool()
        self.setup_policy()
        self.setup_unit()

    def rva(self, old):
        return self.functions.get(old, old) if getattr(self, 'index', 0) else old

    def put(self, address, data):
        if hasattr(self, 'image') and self.image.is_code(address - self.image.base):
            require(bytes(data) == self.image.data[address - self.image.base:address - self.image.base + len(data)],
                    'Native executable code cannot be changed by a fixture')
        super().put(address, data)

    def global_at(self, old):
        if not self.index:
            return self.image.base + old
        matches = self.globals.get(old, set())
        require(len(matches) == 1, f'Unique Native global {old:x}: {matches}')
        return self.image.base + next(iter(matches))

    def table(self, offset, base, stride, count, name):
        self.put(self.root + offset, b''.join(struct.pack('<Q', base + n * stride) for n in range(count)))
        data = bytearray(stride * count)
        for number in range(count):
            struct.pack_into('<Q', data, number * stride, self.image.base + self.vtables[name])
        self.put(base, data)

    def reset_pool(self):
        n, count = 4096, 256
        nodes, heads, tails, sizes = self.pool_data, self.pool_data + 0x20000, self.pool_data + 0x21000, self.pool_data + 0x22000
        indices, order = self.pool_data + 0x23000, self.pool_data + 0x25000
        node_data = bytearray(n * 24)
        for i in range(n - 1):
            struct.pack_into('<Q', node_data, i * 24 + 8, nodes + (i + 1) * 24)
        index_data = bytearray(count * 24)
        for i in range(count):
            struct.pack_into('<Q', index_data, i * 24, i)
            if i + 1 < count:
                struct.pack_into('<Q', index_data, i * 24 + 8, indices + (i + 1) * 24)
        self.put(nodes, node_data)
        self.put(heads, bytes(count * 8)); self.put(tails, bytes(count * 8)); self.put(sizes, bytes(count * 8))
        self.put(indices, index_data); self.put(order, bytes(count * 8))
        self.put(self.pool, struct.pack('<18Q', 0, nodes, heads, tails, nodes, sizes, 0, n, count,
                                        0, indices, 0, 0, indices, order, 0, count, 0))

    def on_code(self, cpu, address, size, user):
        at = address - self.image.base
        if at in self.host:
            name = self.host[at]
            self.host_calls.append(name)
            value = 0
            if name == 'memset':
                dst, val, length = (cpu.reg_read(reg) for reg in (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8))
                require(length < 0x100000, 'Host memset limit')
                self.put(dst, bytes([val & 255]) * length)
                if self.in_call:
                    self.writes.update(range(dst, dst + length))
                value = dst
            sp = cpu.reg_read(UC_X86_REG_RSP)
            cpu.reg_write(UC_X86_REG_RAX, value)
            cpu.reg_write(UC_X86_REG_RIP, struct.unpack('<Q', cpu.mem_read(sp, 8))[0])
            cpu.reg_write(UC_X86_REG_RSP, sp + 8)
            return
        self.executed.add(at)
        if self.in_call:
            self.trace.append(at)

    def on_write(self, cpu, access, address, size, value, user):
        if self.in_call:
            self.writes.update(range(address, address + size))

    def on_fault(self, cpu, access, address, size, value, user):
        self.last_fault = dict(address=hex(address), size=size, rva=hex(cpu.reg_read(UC_X86_REG_RIP) - self.image.base))
        return False

    def invoke(self, entry, *args):
        self.reads.clear(); self.writes.clear()
        self.trace.clear()
        self.last_fault = None
        self.put(self.stack, struct.pack('<Q', self.stop))
        self.put(self.stack + 8, bytes(0x100))
        self.cpu.reg_write(UC_X86_REG_RSP, self.stack)
        for reg, value in zip((UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9), (*args, 0, 0, 0, 0)):
            self.cpu.reg_write(reg, value & 0xFFFFFFFFFFFFFFFF)
        self.in_call = True
        try:
            self.cpu.emu_start(self.image.base + self.rva(entry), self.stop, count=1000000)
        finally:
            self.in_call = False
        require(self.cpu.reg_read(UC_X86_REG_RIP) == self.stop, 'Native return/limit')
        require(self.cpu.reg_read(UC_X86_REG_RSP) == self.stack + 8, 'Native stack convention')
        allowed = lambda at: (self.pool <= at < self.pool + 0x90 or
                              self.pool_data <= at < self.pool_data + 0x100000 or
                              self.root + 0xD00000 <= at < self.root + 0x1000000 or
                              self.output <= at < self.output + 16)
        require(all(allowed(at) for at in self.writes), 'Native mutated an input/neighbor outside owned output/pool/stack')
        return self.cpu.reg_read(UC_X86_REG_RAX)

    def setup_policy(self):
        force = bytearray(0x268)
        struct.pack_into('<H', force, 0x10, 42)
        self.force = self.forces + 0x268
        self.put(self.force, self.record(force, 'CForceData'))
        self.put(self.officers + 42 * 0x1F0 + 0x14D, b'\x01')
        self.put(self.officers + 42 * 0x1F0 + 0x182, b'\x03')
        self.put(self.policies + 0xA8 + 0x98, b'\x01')
        for kind in range(1, 101):
            for level in range(10):
                self.put(self.effects + kind * 0xB0 + 0x82 + level * 2, struct.pack('<H', kind * 100 + level + 1))

    def policy_effect(self, kind, level_only=False):
        return self.invoke(0x41C440 if level_only else 0x41C200, self.force, kind) & 0xFFFFFFFF

    def setup_unit(self):
        self.unit = self.units + 0x200
        data = bytearray(0x200)
        data[0x10] = 1
        data[0x1B] = 1
        struct.pack_into('<H', data, 0x12, 42)
        struct.pack_into('<H', data, 0x16, 4000)
        struct.pack_into('<H', data, 0x2A, 10000)
        self.put(self.unit, self.record(data, 'CArmyUnitData'))
        self.put(self.officers + 42 * 0x1F0 + 0xE0, b'\x01')
        self.put(self.corps + 0x30 + 0x10, b'\x01')
        self.put(self.corps + 0x30 + 0x11, b'\x01')
        self.put(self.corps + 0x30 + 0x12, struct.pack('<H', 42))
        self.put(self.tiles + 10000 * 0x20 + 0x10, b'\x01')
        self.put(self.tiles + 10000 * 0x20 + 0x12, struct.pack('<H', 1))
        self.put(self.root + 0x870000 + 0x98 + 0x3A, struct.pack('<H', 10000))
        self.put(self.tiles + 10000 * 0x20 + 0x14, b'\x01')
        self.put(self.tiles + 10000 * 0x20 + 0x1C, struct.pack('<H', 1))

    def add_unit(self, slot, tile, force=1, gender=0):
        data = bytearray(0x200)
        data[0x10], data[0x1B] = 1, 1
        commander = 50 + slot
        struct.pack_into('<H', data, 0x12, commander)
        struct.pack_into('<H', data, 0x2A, tile)
        self.put(self.units + slot * 0x200, self.record(data, 'CArmyUnitData'))
        self.put(self.officers + commander * 0x1F0 + 0xE0, bytes([force]))
        self.put(self.officers + commander * 0x1F0 + 0xA9, bytes([gender]))
        self.put(self.corps + force * 0x30 + 0x10, bytes([force, 1]))
        self.put(self.corps + force * 0x30 + 0x12, struct.pack('<H', commander))
        self.put(self.forces + force * 0x268 + 0x10, struct.pack('<H', commander))
        self.put(self.tiles + tile * 0x20 + 0x1C, struct.pack('<H', slot))

    def condition(self, condition, value=0, filter_=11, mode=1, personality=256, record=None):
        self.reset_pool()
        wrapper = self.invoke(0x18B60, self.pool + 0x48, 0, 0)
        require(wrapper != 0, 'Native output list allocation')
        self.put(self.output, struct.pack('<QQ', 0, wrapper))
        data = bytearray(record) if record is not None else bytearray(0xE8)
        if record is None:
            data[0xA4], data[0xAC] = condition, filter_
            struct.pack_into('<i', data, 0xA8, value)
        at = self.traits + personality * 0xE8
        self.put(at, self.record(data, 'CPersonalityData'))
        got = self.invoke(0x27DF10, self.unit, at, mode, self.output) & 0xFFFFFFFF
        index = struct.unpack('<I', self.cpu.mem_read(wrapper, 4))[0]
        heads = struct.unpack('<Q', self.cpu.mem_read(self.pool + 0x10, 8))[0]
        node = struct.unpack('<Q', self.cpu.mem_read(heads + index * 8, 8))[0]
        targets = []
        while node:
            require(len(targets) < 501, 'Native target list cycle/limit')
            person, node = struct.unpack('<QQ', self.cpu.mem_read(node, 16))
            targets.append((person - self.units) // 0x200)
        return got, targets
