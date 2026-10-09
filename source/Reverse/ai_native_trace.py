# Observers for native calls in copied Unicorn pages. No game hooks, native stubs or result replacement.
import struct
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RSP, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9


def linked_list(oracle, address):
    index_pointer = oracle.q(address + 8)
    index = struct.unpack('<I', oracle.read(index_pointer, 4))[0] if index_pointer else 0x14000
    capacity = struct.unpack('<I', oracle.read(oracle.exe + 0x27D1120, 4))[0]
    heads = oracle.q(oracle.exe + 0x27D10F0)
    node = oracle.q(heads + index * 8) if heads and index < capacity else 0
    result, seen = [], set()
    while node:
        if node in seen:
            raise AssertionError('native list cycle')
        seen.add(node)
        result.append(oracle.q(node))
        node = oracle.q(node + 8)
    return result


class Trace:
    def __init__(self, oracle, functions):
        # RVA -> list argument index, or None for a scalar result.
        self.oracle, self.functions = oracle, functions
        self.events, self.pending, self.handles = [], {}, []
        for rva in functions:
            self.handles.append(oracle.mu.hook_add(UC_HOOK_CODE, self.enter, None,
                                                 oracle.exe + rva, oracle.exe + rva))

    def enter(self, uc, address, size, user):
        rva = address - self.oracle.exe
        args = [uc.reg_read(r) for r in (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)]
        rsp = uc.reg_read(UC_X86_REG_RSP)
        return_address = self.oracle.q(rsp)
        event = {'rva': rva, 'args': args, 'return_address': return_address - self.oracle.exe, 'rsp': rsp}
        self.events.append(event)
        self.pending.setdefault(return_address, []).append(event)
        if not hasattr(self, 'returns'):
            self.returns = set()
        if return_address not in self.returns:
            self.returns.add(return_address)
            self.handles.append(uc.hook_add(UC_HOOK_CODE, self.leave, None, return_address, return_address))

    def leave(self, uc, address, size, user):
        pending = self.pending.get(address, [])
        if not pending or uc.reg_read(UC_X86_REG_RSP) != pending[-1]['rsp'] + 8:
            return
        event = pending.pop()
        event['result64'] = uc.reg_read(UC_X86_REG_RAX)
        event['result'] = event['result64'] & 0xFFFFFFFF
        index = self.functions[event['rva']]
        if index is not None:
            event['list'] = linked_list(self.oracle, event['args'][index])

    def clear(self):
        self.events, self.pending = [], {}

    def close(self):
        for handle in self.handles:
            self.oracle.mu.hook_del(handle)
