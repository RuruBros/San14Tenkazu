# Command manager gameplay membership and ID allocation, on copied native memory.
# Heap allocation, deleting destructors and graphical detachment execute natively and are host boundaries in C#.
import copy
import struct
import time
import gzip
import json
from context import *
from command_cancel import SIZES
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX

COVER = [0x1DE1A0, 0x1EC7D0, 0x1F22C0]


def read_store(ctx, audit):
    o = ctx.o
    mgr = o.q(ctx.raw + 0x871E0)
    header = o.read(mgr, 0xEB0)
    head, todo, seen, mapping = o.q(mgr), [], set(), {}
    todo.append(o.q(head + 8))
    while todo:
        p = todo.pop()
        if o.read(p + 0x19, 1)[0]: continue
        if not p or p in seen: raise RuntimeError('Command map cycle/null')
        seen.add(p)
        key = struct.unpack('<i', o.read(p + 0x20, 4))[0]
        mapping[str(key)] = hex(o.q(p + 0x28))
        todo.extend([o.q(p), o.q(p + 0x10)])
    if len(mapping) != o.q(mgr + 8): raise RuntimeError('Command map count differs')
    return dict(free_head=struct.unpack_from('<H', header, 0xEA8)[0],
                free_ids=list(struct.unpack_from('<1852H', header, 0x30)), by_id=mapping,
                active=audit.members(o.q(mgr + 0x18)), pending=audit.members(o.q(mgr + 0x28)))


def generate(ctx, names=None):
    o = ctx.o
    o.insn_limit = 300_000_000
    capture_native_pools(ctx, [(0x1B5C990, 24, 8)])
    audit = EngineAuditState(ctx)
    mgr = o.q(ctx.raw + 0x871E0)
    def store(): return read_store(ctx, audit)
    original, initial = audit.read(), store()
    snapshot = copy.deepcopy(audit.snapshot)
    commands = []
    for address, info in zip(initial['active'], snapshot['cmdactions']):
        pointer = int(address, 16)
        commands.append(dict(info, address=address, data=o.read(pointer, SIZES[info['kind']]).hex()))
    # The registration leaf needs the base header only; clone an actual class header and retain its vtable/list input.
    clone = o.alloc(0x80)
    template = next(c for c in commands if c['kind'] == 15)
    data = bytearray.fromhex(template['data'])
    struct.pack_into('<i', data, 0x58, -1)
    o.poke(clone, bytes(data))
    clone_input = bytes(data)
    commands.append(dict(template, address=hex(clone), data=data.hex()))
    snapshot.update(commands=commands, command_storage=initial)
    first = int(initial['active'][0], 16)
    destructor = o.q(o.q(first)) - ctx.g.exe
    frame, hooks = [None], []
    def event(rva, kind):
        def hook(mu, address, size, user):
            pointer = mu.reg_read(UC_X86_REG_RCX)
            if frame[0] is not None and pointer == first:
                frame[0].append(dict(kind=kind, command=hex(pointer), store=store()))
        return hook
    for rva, kind in [(0x33AE10, 'detach'), (destructor, 'dispose')]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, event(rva, kind), begin=ctx.g.exe + rva, end=ctx.g.exe + rva))
    # Existing general-list nodes provide real aliases; replace payloads only, restoring after each case.
    index = struct.unpack('<I', o.read(o.q(mgr + 0x18), 4))[0]
    first_node = o.q(o.q(ctx.g.exe + world.POOL_RVA) + index * 8)
    second_node = o.q(first_node + 8)
    fixtures = [dict(name='null_remove', operation='remove', command=None, mode=1),
                *[dict(name=f'remove_mode_{mode}', operation='remove', command=0, mode=mode) for mode in [0, 1, -1, 2]],
                dict(name='remove_all_pointer_aliases', operation='remove', command=0, mode=0, alias=True),
                dict(name='register_free_head', operation='register', command=len(commands)-1),
                *[dict(name=f'register_exhausted_{head}', operation='register', command=len(commands)-1,
                       free_head=head) for head in [1852, 65535]]]
    previous = None
    if names is not None:
        with gzip.open(os.path.join(ctx.output, 'command_storage.json.gz'), 'rt', encoding='utf-8') as file:
            previous = json.load(file)
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names): raise ValueError('Unknown command storage fixture')
    cases = []
    try:
        for f in fixtures:
            began, undo, captured, events = time.time(), [], [], []
            # Oracle scratch pages belong to the caller and are not restored by call's game-page settlement.
            o.poke(clone, clone_input)
            if f.get('alias'):
                undo.append((second_node, o.read(second_node, 8)))
                o.poke(second_node, struct.pack('<Q', first))
            if 'free_head' in f:
                undo.append((mgr + 0xEA8, o.read(mgr + 0xEA8, 2)))
                o.poke(mgr + 0xEA8, struct.pack('<H', f['free_head']))
            pointer = 0 if f['command'] is None else int(commands[f['command']]['address'], 16)
            before = audit.read()
            frame[0] = events
            try:
                def inspect():
                    result = dict(store=store(), delta=audit.delta(before, audit.read()))
                    if f['operation'] == 'register': result['data'] = o.read(pointer, 0x80).hex()
                    captured.append(result)
                    return []
                if f['operation'] == 'remove':
                    o.call(0x1F22C0, mgr, pointer, f['mode'], read_after=inspect)
                else:
                    result = o.call(0x1DE1A0, mgr, pointer, read_after=inspect) & 0xFF
                    captured[0]['success'] = bool(result)
                cases.append(dict(fixture=f, events=events, **captured[0]))
                print(f'  command storage {f["name"]}: active {len(captured[0]["store"]["active"])}, '
                      f'pending {len(captured[0]["store"]["pending"])} ({time.time()-began:.2f}s)', flush=True)
            finally:
                frame[0] = None
                for address, data in reversed(undo): o.poke(address, data)
        if audit.read() != original or store() != initial:
            raise RuntimeError('Command storage fixtures did not restore their input')
    finally:
        for hook in hooks: o.mu.hook_del(hook)
    if previous is not None:
        cases = [c for c in previous['cases'] if c['fixture']['name'] not in names] + cases
    ctx.save('command_storage_world.json.gz', snapshot)
    ctx.save('command_storage.json.gz', dict(world='command_storage_world.json.gz', cases=cases, gameplay_stubs=[],
        boundary='Native object allocation, deleting destructor and graphical bindings; manager gameplay state compared in full.'))
    return f'command storage: {len(cases)} cases'
