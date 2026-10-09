# Verify the native registered-prisoner iterator with real temporary-list allocation on the copied pool.
import copy
import struct
from context import *
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools

COVER = [0x230B10]


def generate(ctx):
    o = ctx.o
    capture_native_pools(ctx)
    audit = EngineAuditState(ctx)
    snapshot = copy.deepcopy(audit.snapshot)
    refs = {ctx.addr(t,i):[hex(t),i] for t,(_,_,count) in audit.tables.items() for i in range(count)}
    people = [r[0][1] for r in snapshot['lists']['0x100']['items'][:3]]
    unit = next(i for i,b in enumerate(ctx.rec[UNITS]) if i and b[0x10] and ctx.u16(UNITS,i,0x12))
    temporary = o.alloc(16)
    def word(person,at,value):
        return [hex(OFFICERS),person,at,struct.pack('<H',value&0xFFFF).hex()]
    fixtures = []
    for name,target,statuses,places in [
        ('one_matching',unit,[6,6,4],[unit+61,unit+62,unit+61]),
        ('original_list_order',unit,[6,6,6],[unit+61]*3),
        ('status_excludes',unit,[1,4,5],[unit+61]*3),
        ('place_excludes',unit,[6]*3,[1,52,562]),
        ('unit_zero_encoded_zero',0,[6]*3,[0,1,62]),
        ('unit_last_slot',500,[6]*3,[561,560,562])]:
        pokes = [p for person,status,place in zip(people,statuses,places)
                 for p in [[hex(OFFICERS),person,0xE6,bytes([status]).hex()],word(person,0xE4,place)]]
        fixtures.append(dict(name=name,unit=[hex(UNITS),target],pokes=pokes))
    code = b'\x48\x83\xec\x28'
    code += b'\x48\xb9'+struct.pack('<Q',temporary)+b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+0x21E2B0)+b'\xff\xd0'
    # The second call obtains the unit from the harness's own input slot, rather than a native result.
    input_slot = o.alloc(8)
    code += b'\x48\xb9'+struct.pack('<Q',input_slot)+b'\x48\x8b\x09'
    code += b'\x48\xba'+struct.pack('<Q',temporary)+b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+0x230B10)+b'\xff\xd0'
    code += b'\x48\x83\xc4\x28\xc3'
    harness = o.alloc(len(code)); o.poke(harness,code)
    original,cases = audit.read(),[]
    for fixture in fixtures:
        o.poke(temporary,struct.pack('<QQ',ctx.g.exe+0x13436B0,0))
        o.poke(input_slot,struct.pack('<Q',ctx.addr(UNITS,fixture['unit'][1])))
        def run():
            before, output = audit.read(),[]
            def inspect():
                output.extend(refs[int(p,16)] for p in audit.members(o.q(temporary+8)))
                if audit.read() != before: raise RuntimeError('Prisoner iteration changed gameplay state')
                return []
            result = o.call(harness-ctx.g.exe,read_after=inspect)
            if result != len(output): raise RuntimeError('Prisoner count differs from list')
            return dict(fixture=fixture,result=result,members=output)
        result = ctx.run(fixture['pokes'],run)
        if result is None: raise RuntimeError(str(ctx.failures[-1]))
        cases.append(result)
        print(f'  prisoner members {fixture["name"]}: {result["result"]}',flush=True)
    if audit.read() != original: raise RuntimeError('Prisoner iterator did not restore its input')
    ctx.save('prisoner_members_world.json.gz',snapshot)
    ctx.save('prisoner_members.json.gz',dict(world='prisoner_members_world.json.gz',cases=cases,gameplay_stubs=[]))
    return f'prisoner members: {len(cases)} native lists, no gameplay state writes'
