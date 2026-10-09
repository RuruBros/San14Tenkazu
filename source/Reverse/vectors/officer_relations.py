# Run the actual relationship helpers and sworn-list sort with real copied temporary-list pools.
import copy
import struct
from context import *
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from destruction_overflow import require_unchanged_snapshot

COVER = [0x232EE0,0x232A00,0x2283F0,0x2280D0,0x24FD70]


def generate(ctx):
    o=ctx.o
    capture_native_pools(ctx)
    audit=EngineAuditState(ctx)
    snapshot=copy.deepcopy(audit.snapshot)
    original=audit.read()
    snapshot['nested']=original['nested']
    initial_rng=o.read(ctx.g.exe+0x1A47B90,4)
    available=[i for i,b in enumerate(ctx.rec[OFFICERS]) if i and ctx.u16(OFFICERS,i,0x10) in range(1,6001)]
    person,target,other,last=available[:4]
    tid,oid,lid=[ctx.u16(OFFICERS,i,0x10) for i in [target,other,last]]
    holes=[pid for pid in range(1,6001) if ctx.officer_by_id(pid) is None or ctx.officer_by_id(pid)<0]
    fixtures=[]
    def word(i,at,value):return [hex(OFFICERS),i,at,struct.pack('<H',value&0xFFFF).hex()]
    for kind,start,member,remove in [('likes',0xC0,0x232EE0,0x2283F0),('dislikes',0xD0,0x232A00,0x2280D0)]:
        values=[tid,0,oid,tid,6001,lid,0,tid]
        prefix=[[hex(OFFICERS),person,start,struct.pack('<8H',*values).hex()]]
        # Make the target ordinarily valid independent of its original status/marker state.
        valid=[word(target,0x15E,ctx.u16(OFFICERS,target,0x15E)&~0x4000),[hex(OFFICERS),target,0xE6,'04']]
        for name,entry,target_ref,extra in [
            ('member_present',member,target,valid),('member_absent',member,last,[]),
            ('member_null',member,None,[]),('member_invalid',member,target,valid[:-1]+[[hex(OFFICERS),target,0xE6,'00']]),
            ('remove_duplicate_and_invalid_ids',remove,target,valid),('remove_null_compacts',remove,None,[]),
            ('remove_invalid_pointer',remove,target,valid[:-1]+[[hex(OFFICERS),target,0xE6,'00']]),
            ('resolved_id_clamped',remove,target,valid+[word(other,0x10,6001)])]:
            # Use an actually absent ID for the negative membership case.
            pokes=prefix+extra
            if name=='member_absent':pokes=valid+[[hex(OFFICERS),person,start,struct.pack('<8H',*[tid]*8).hex()]]
            fixtures.append(dict(name=kind+'_'+name,kind=kind,entry=hex(entry),person=person,target=target_ref,pokes=pokes))
        if holes:
            fixtures.append(dict(name=kind+'_remove_unmapped_id',kind=kind,entry=hex(remove),person=person,target=target,
                pokes=valid+[[hex(OFFICERS),person,start,struct.pack('<8H',holes[0],oid,tid,0,lid,tid,6001,0).hex()]]))
    pool=ctx.g.exe+0x27D10E0
    wrapper=o.alloc(8);o.poke(wrapper,b'\0'*8)
    driver=o.alloc(0x10000)
    def emit(code,rva,args):
        for op,value in zip([b'\x48\xb9',b'\x48\xba',b'\x49\xb8',b'\x49\xb9'],args):code.extend(op+struct.pack('<Q',value&0xFFFFFFFFFFFFFFFF))
        code.extend(b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+rva)+b'\xff\xd0')
    for name,members,years in [('empty',[],[]),('singleton',[other],[200]),
            ('year_and_id',[last,target,other],[201,200,200]),('duplicates',[other,target,other,last],[200,200,200,201]),
            ('unsigned_year',[target,other,last],[65535,0,32768])]:
        fixtures.append(dict(name='sworn_sort_'+name,kind='sort',entry='0x24fd70',members=members,
            pokes=[word(i,0xAC,year) for i,year in zip(members,years)]))
    cases=[]
    for fixture in fixtures:
        output=[]
        after=[]
        def run():
            before=audit.read()
            if fixture['kind']=='sort':
                code=bytearray(b'\x48\x83\xec\x28')
                emit(code,0x18B60,[ctx.g.exe+0x27D1128,0,0])
                code.extend(b'\x48\xba'+struct.pack('<Q',wrapper)+b'\x48\x89\x02')
                for i in fixture['members']:
                    code.extend(b'\x48\xba'+struct.pack('<Q',wrapper)+b'\x48\x8b\x12\x8b\x12')
                    code.extend(b'\x48\xb9'+struct.pack('<Q',pool)+b'\x41\xb8\x01\x00\x00\x00')
                    code.extend(b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+0x18DF0)+b'\xff\xd0')
                    code.extend(b'\x48\xba'+struct.pack('<Q',ctx.addr(OFFICERS,i))+b'\x48\x89\x10')
                code.extend(b'\x48\xba'+struct.pack('<Q',wrapper)+b'\x48\x8b\x12\x8b\x12')
                code.extend(b'\x48\xb9'+struct.pack('<Q',pool)+b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+0x24FD70)+b'\xff\xd0')
                code.extend(b'\x48\x83\xc4\x28\xc3')
                o.poke(driver,bytes(code));o.mu.ctl_remove_cache(driver,driver+0x10000)
                def inspect():
                    refs={ctx.addr(OFFICERS,i):i for i in available}
                    output.append([refs[int(p,16)] for p in audit.members(o.q(wrapper))])
                    after.append(audit.read());return []
                result=o.call(driver-ctx.g.exe,read_after=inspect)
            else:
                def inspect():after.append(audit.read());return []
                result=o.call(int(fixture['entry'],16),ctx.addr(OFFICERS,fixture['person']),
                    ctx.addr(OFFICERS,fixture['target']) if fixture['target'] is not None else 0,read_after=inspect)
            if len(after)!=1:raise RuntimeError('Missing native post-operation checkpoint')
            delta=audit.delta(before,after[0])
            if delta.keys()-{'records'}:raise RuntimeError(f'Unexpected gameplay writes: {delta.keys()}')
            return dict(fixture=fixture,result=result if int(fixture['entry'],16) in [0x232EE0,0x232A00] else 0,
                after=delta,sorted=output[0] if output else [])
        result=ctx.run(fixture['pokes'],run)
        if result is None:raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
        cases.append(result);print('  officer relations',fixture['name'],flush=True)
    if audit.read()!=original:raise RuntimeError('Relationship fixtures did not restore the frozen input')
    if not ctx.frozen:require_unchanged_snapshot(snapshot,initial_rng)
    ctx.save('officer_relations_world.json.gz',snapshot)
    ctx.save('officer_relations.json.gz',dict(world='officer_relations_world.json.gz',cases=cases,gameplay_stubs=[],native_pool=True))
    return f'officer relations: {len(cases)} complete native helpers'
