# Native integer-list member removal, retaining the original command object and manager storage.
import copy
import struct
from context import *
from engine_audit_state import EngineAuditState
from command_storage import read_store
from command_cancel import SIZES

COVER = [0x228710, 0x18940]


def generate(ctx):
    o = ctx.o
    audit = EngineAuditState(ctx)
    snapshot = copy.deepcopy(audit.snapshot)
    initial_store = read_store(ctx, audit)
    commands = []
    for address, info in zip(initial_store['active'], snapshot['cmdactions']):
        ptr = int(address, 16)
        vt = o.q(ptr)
        commands.append(dict(info, address=address, data=o.read(ptr, SIZES[info['kind']]).hex(),
            handlers={hex(at): hex(o.q(vt+at)-ctx.g.exe) for at in [0x18,0x38,0x68,0x90,0xA8,0xB0,0xC8,0xD8,0xF0,0x110,0x118]}))
    snapshot.update(commands=commands, command_storage=initial_store)
    index = max(range(len(commands)), key=lambda i: len(commands[i]['officers']))
    selected = commands[index]
    original_ids = selected['officers']
    if len(original_ids) < 2: raise RuntimeError('A real command with multiple members is required')
    pointer = int(selected['address'], 16)
    slot = struct.unpack('<I', o.read(o.q(pointer+0x20), 4))[0]
    heads = o.q(ctx.g.exe+0x1B5C990+0x10)
    node = o.q(heads+slot*8)
    nodes = []
    while node:
        if node in nodes: raise RuntimeError('Command member cycle')
        nodes.append(node)
        node = o.q(node+8)
    if len(nodes) != len(original_ids): raise RuntimeError('Native command member count differs')
    missing = next(i for i in range(1, 6001) if i not in original_ids)
    fixtures = [dict(name=name, command=index, id=identifier, officer_ids=ids)
                for name, identifier, ids in [
                    ('remove_first', original_ids[0], original_ids), ('remove_last', original_ids[-1], original_ids),
                    ('missing_id', missing, original_ids), ('zero_id', 0, original_ids), ('negative_id', -1, original_ids),
                    ('maximum_id', 6000, [6000]+original_ids[1:]),
                    ('over_maximum_id', 6001, [6001]+original_ids[1:]),
                    ('all_member_aliases', original_ids[0], [original_ids[0]]*len(nodes))]]
    original = audit.read()
    cases = []
    for f in fixtures:
        undo = [(node, o.read(node, 4)) for node in nodes]
        try:
            for node, identifier in zip(nodes, f['officer_ids']): o.poke(node, struct.pack('<i', identifier))
            before, after = audit.read(), []
            o.call(0x228710, pointer, f['id'] & 0xFFFFFFFF,
                   read_after=lambda: after.append(dict(delta=audit.delta(before, audit.read()), store=read_store(ctx, audit),
                                                        data=o.read(pointer, SIZES[selected['kind']]).hex())) or [])
            cases.append(dict(fixture=f, **after[0]))
            print(f'  member removal {f["name"]}', flush=True)
        finally:
            for address, value in undo: o.poke(address, value)
        if audit.read() != original or read_store(ctx, audit) != initial_store:
            raise RuntimeError('Member removal did not restore the input')
    ctx.save('command_members_world.json.gz', snapshot)
    ctx.save('command_members.json.gz', dict(world='command_members_world.json.gz', cases=cases,
        gameplay_stubs=[], presentation=[], native_entry='0x228710',
        scope='Real allocated integer nodes; only copied payload IDs vary, without reallocating or fabricating nodes.'))
    return f'command members: {len(cases)} native calls'
