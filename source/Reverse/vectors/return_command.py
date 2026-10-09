# Native cancellation including allocation/registration, return initialization, routing and source destruction.
# No predicted native location or date is supplied as a C# input.
import copy
import gzip
import json
import struct
import time
from context import *
from command_storage import read_store
from engine_audit_state import EngineAuditState
from engagement_tactic_sequence import PRESENTATION
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX

COVER = [0x334780, 0x224670, 0x1D7DA0, 0x1DE1A0, 0x1F6720, 0x22B460, 0x231790, 0x33A5B0]


def generate(ctx):
    o = ctx.o
    o.insn_limit = 300_000_000
    capture_native_pools(ctx, [(0x1B5C990, 24, 8)])
    audit = EngineAuditState(ctx)
    original = audit.read()
    snapshot = copy.deepcopy(audit.snapshot)
    for head in [0xA0, 0xB0, 0xD0]:
        snapshot['lists'][hex(head)] = {'items': world.list_items(o, ctx.raw, ctx.g.exe, head,
            {'pool': world.POOL_RVA, 'next': 1, 'fields': 1})}
    initial = read_store(ctx, audit)
    with gzip.open(os.path.join(ctx.output, 'command_storage_world.json.gz'), 'rt', encoding='utf-8') as file:
        previous = json.load(file)
    if previous['tables'] != snapshot['tables'] or previous['worldstate'] != snapshot['worldstate'] or previous['command_storage'] != initial:
        raise RuntimeError('The reused command catalog belongs to a different input')
    commands = previous['commands'][:len(initial['active'])]
    for c in commands:
        pointer = int(c['address'], 16)
        if o.read(pointer, len(bytes.fromhex(c['data']))).hex() != c['data']:
            raise RuntimeError('The reused command header changed')
        vt = o.q(pointer)
        c['handlers'] = {hex(at): hex(o.q(vt + at) - ctx.g.exe)
                         for at in [0x18, 0x38, 0x68, 0x90, 0xA8, 0xB0, 0xC8, 0xD8, 0xF0, 0x110, 0x118]}
    snapshot.update(commands=commands, command_storage=initial)
    return_vtable = ctx.g.exe + 0x13A1E78  # Immediate address installed by 0x1D7DA0, not inferred from an existing kind 15.
    snapshot['return_handlers'] = {hex(at): hex(o.q(return_vtable + at) - ctx.g.exe)
                                   for at in [0x18, 0x38, 0x68, 0x90, 0xA8, 0xB0, 0xC8, 0xD8, 0xF0, 0xF8, 0x110, 0x118]}
    def cp(at, value, fmt='i'): return [at, struct.pack('<' + fmt, value).hex()]
    ret = next(i for i, c in enumerate(commands) if c['kind'] == 15)
    many = next(i for i, c in enumerate(commands) if c['kind'] == 1 and len(c['officers']) >= 4)
    refs = {ctx.addr(t, i): [hex(t), i] for t, (_, _, count) in audit.tables.items() for i in range(count)}
    for index in [ret, many]:
        pointer = int(commands[index]['address'], 16)
        wrapper = o.q(pointer + 0x38)
        slot = struct.unpack('<I', o.read(wrapper, 4))[0]
        node = o.q(o.q(ctx.g.exe + 0x2143270 + 0x10) + slot * 8)
        targets = []
        while node:
            a, kind, secondary_kind, b = struct.unpack('<QiiQ', o.read(node, 24))
            targets.append(dict(primary=refs.get(a, hex(a)), primary_kind=kind,
                                secondary=refs.get(b, hex(b)), secondary_kind=secondary_kind))
            node = o.q(node + 24)
        commands[index]['destinations'] = targets
    first_person = ctx.officer_by_id(commands[ret]['officers'][0])
    fixtures = [dict(name='original_return_cancellation', command=ret, command_pokes=[], pokes=[]),
                dict(name='ordered_repeated_return', command=many, command_pokes=[cp(0x50, -7)],
                     officer_ids=commands[many]['officers'].copy(), pokes=[]),
                dict(name='return_to_gate', command=ret, command_pokes=[],
                     pokes=[[hex(OFFICERS), first_person, 0xE2, '3400']]),
                dict(name='no_serving_officers', command=ret, command_pokes=[],
                     pokes=[[hex(OFFICERS), ctx.officer_by_id(pid), 0xE6, '06'] for pid in commands[ret]['officers']])]
    ids = fixtures[1]['officer_ids']
    ids[-1] = ids[0]
    fixtures[1]['pokes'] = [[hex(OFFICERS), ctx.officer_by_id(pid), 0xE6, '06'] for pid in commands[many]['officers']]
    fixtures[1]['pokes'] += [[hex(OFFICERS), ctx.officer_by_id(pid), 0xE6, bytes([status]).hex()]
                           for pid, status in zip(ids[:4], [1, 2, 3, 4])]
    for f in fixtures: f['pokes'].insert(0, ['worldstate', 0, 0x3A, '00'])
    frame, hooks = [None], []
    def constructor(mu, address, size, user):
        if frame[0] is not None:
            pointer = mu.reg_read(UC_X86_REG_RCX)
            frame[0]['allocations'].append(dict(address=hex(pointer), data=o.read(pointer, 0x80).hex()))
    hooks.append(o.mu.hook_add(UC_HOOK_CODE, constructor, begin=ctx.g.exe + 0x1D7DA0, end=ctx.g.exe + 0x1D7DA0))
    def body(mu, address, size, user):
        if frame[0] is not None:
            frame[0]['new_command'] = mu.reg_read(UC_X86_REG_RCX)
    hooks.append(o.mu.hook_add(UC_HOOK_CODE, body, begin=ctx.g.exe + 0x1F6720, end=ctx.g.exe + 0x1F6720))
    def descriptors(pointer):
        wrapper = o.q(pointer + 0x38)
        if not wrapper: return []
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        heads = o.q(ctx.g.exe + 0x2143270 + 0x10)
        node, result, seen = o.q(heads + index * 8), [], set()
        while node:
            if node in seen: raise RuntimeError('Destination descriptor cycle')
            seen.add(node)
            result.append(o.read(node, 24).hex())
            node = o.q(node + 24)
        return result
    for rva in [*PRESENTATION, 0x1B4990]: o.stub(rva)
    cases = []
    try:
        for f in fixtures:
            began, undo = time.time(), []
            pointer = int(commands[f['command']]['address'], 16)
            if 'officer_ids' in f:
                index = struct.unpack('<I', o.read(o.q(pointer + 0x20), 4))[0]
                node = o.q(o.q(ctx.g.exe + world.CMD_POOL_RVA) + index * 8)
                for pid in f['officer_ids']:
                    undo.append((node, o.read(node, 4)))
                    o.poke(node, struct.pack('<i', pid)); node = o.q(node + 8)
                if node: raise RuntimeError('Officer list replacement length differs')
            for at, data in f['command_pokes']:
                data = bytes.fromhex(data)
                undo.append((pointer + at, o.read(pointer + at, len(data)))); o.poke(pointer + at, data)
            try:
                def run():
                    before, after = audit.read(), []
                    active = dict(allocations=[], new_command=None)
                    frame[0] = active
                    def inspect():
                        result = dict(delta=audit.delta(before, audit.read()), store=read_store(ctx, audit))
                        new = active['new_command']
                        if new is not None:
                            result['new_command'] = dict(address=hex(new), data=o.read(new, 0x80).hex(),
                                descriptors=descriptors(new))
                        after.append(result)
                        return []
                    try:
                        o.call(0x334780, pointer, read_after=inspect)
                        return dict(fixture=f, allocations=active['allocations'], **after[0])
                    finally: frame[0] = None
                result = ctx.run(f['pokes'], run)
                if result is None: raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
                cases.append(result)
                print(f'  return command {f["name"]}: {len(result["allocations"])} allocations, '
                      f'{len(result["delta"]["records"])} changed records ({time.time()-began:.2f}s)', flush=True)
            finally:
                for address, data in reversed(undo): o.poke(address, data)
    finally:
        o.unstub()
        for hook in hooks: o.mu.hook_del(hook)
    if audit.read() != original or read_store(ctx, audit) != initial:
        raise RuntimeError('Return construction fixtures did not restore their input')
    ctx.save('return_command_world.json.gz', snapshot)
    ctx.save('return_command.json.gz', dict(world='return_command_world.json.gz', cases=cases, gameplay_stubs=[],
        presentation=[hex(r) for r in [*PRESENTATION, 0x1B4990]],
        physical_fields=[[0, 8], [0x18, 16], [0x30, 16]]))
    return f'return command: {len(cases)} cases'
