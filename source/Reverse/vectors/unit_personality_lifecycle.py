# Native personality shutdown and reverse propagation use real copied pool nodes, without gameplay stubs.
import copy
import struct
from context import *
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from destruction_overflow import require_unchanged_snapshot
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RDX

COVER = [0x2A1F00,0x2A2100,0x2287C0,0x2265C0,0x234D80,0x234E60]


def generate(ctx):
    o = ctx.o
    capture_native_pools(ctx)
    audit = EngineAuditState(ctx)
    snapshot = copy.deepcopy(audit.snapshot)
    original = audit.read()
    initial_rng = o.read(ctx.g.exe+0x1A47B90,4)
    snapshot['nested'] = original['nested']
    pool = ctx.g.exe+0x27D10E0
    live = [i for i,b in enumerate(ctx.rec[UNITS]) if i and b[0x10] and ctx.u16(UNITS,i,0x12)]
    source,recipient,other = live[:3]
    commander = ctx.officer_by_id(ctx.u16(UNITS,source,0x12))
    oid = ctx.u16(OFFICERS,commander,0x10)
    recipient_oid = ctx.u16(UNITS,recipient,0x12)
    # Select a slot admitted by the native flag4 predicate and preserve the actual commander ID mapping.
    flag_slot = next(s for s in range(5) if o.call32(0x2305D0,ctx.addr(OFFICERS,commander),s))
    personality = o.call32(0x2305D0,ctx.addr(OFFICERS,commander),flag_slot)
    fixtures = []
    refresh_host = o.q(ctx.g.exe+0x1B91DC0)
    if not refresh_host: raise RuntimeError('This input has no active personality refresh host')
    def word(t,i,at,value): return [hex(t),i,at,struct.pack('<H',value&0xFFFF).hex()]
    def received(i,slot,pid=personality,person=oid,timer=123):
        return [word(UNITS,i,0x64+slot*2,pid),word(UNITS,i,0xAC+slot*2,person),word(UNITS,i,0xF4+slot*2,timer)]
    def case(name,kind='off',target=source,slot=flag_slot,notify=False,pokes=(),lists=None,refresh=True):
        fixtures.append(dict(name=name,kind=kind,unit=target,slot=slot,notify=notify,pokes=list(pokes),
                             propagated=lists or {},refresh_available=refresh))
    source_place = word(OFFICERS,commander,0xE4,source+61)
    prefix = [source_place,word(OFFICERS,commander,0x184,0x1FF),
              [hex(OFFICERS),commander,0x186,struct.pack('<10H',*range(100,110)).hex()]]
    linked = {f'{source}:{0x160+flag_slot*16:x}':[recipient,other,recipient,400,None]}
    pokes = prefix+received(recipient,0)+received(recipient,35)+received(other,3)
    pokes += received(recipient,1,person=recipient_oid)+received(recipient,2,pid=0)
    case('deactivate_propagated_duplicates',pokes=pokes,lists=linked)
    case('deactivate_without_refresh',pokes=pokes,lists=linked,refresh=False)
    # Duplicate slots must keep the second slot's list, even when the second slot is being deactivated.
    duplicate = prefix+[word(OFFICERS,commander,0x118,personality),word(OFFICERS,commander,0x11A,personality)]
    case('duplicate_slots_first_list_only',slot=1,pokes=duplicate+received(recipient,0)+received(other,0),
         lists={f'{source}:160':[recipient],f'{source}:170':[other]})
    case('slot_eight_timer_and_zero_personality',slot=8,pokes=prefix+received(recipient,4,pid=0),
         lists={f'{source}:{0x160+next(s for s in range(9) if o.call32(0x2305D0,ctx.addr(OFFICERS,commander),s)==0)*16:x}':[recipient]})
    case('slot_nine_timer_only',slot=9,pokes=prefix)
    case('all_nine_slots',kind='all_off',pokes=pokes,lists=linked)
    reverse = {f'{source}:{0x160+flag_slot*16:x}':[recipient,other,recipient]}
    for notify in [False,True]:
        case('received_reverse_'+str(int(notify)),kind='drop',target=recipient,slot=0,notify=notify,
             pokes=[source_place]+received(recipient,0),lists=reverse)
    case('received_last_slot',kind='drop',target=recipient,slot=35,notify=True,
         pokes=[source_place]+received(recipient,35),lists=reverse)
    case('received_source_not_commander',kind='drop',target=recipient,slot=0,notify=True,
         pokes=[word(OFFICERS,commander,0xE4,other+61)]+received(recipient,0),lists=reverse)
    case('received_personality_not_in_source_slots',kind='drop',target=recipient,slot=0,notify=True,
         pokes=[source_place]+received(recipient,0,pid=65535),lists=reverse)
    case('received_duplicate_source_slots',kind='drop',target=recipient,slot=0,notify=True,
         pokes=duplicate+received(recipient,0),lists={f'{source}:160':[recipient,other,recipient],f'{source}:170':[recipient]})
    case('received_source_id_above_6000',kind='drop',target=recipient,slot=0,notify=True,
         pokes=received(recipient,0,person=6001),lists=reverse)
    case('received_without_refresh',kind='drop',target=recipient,slot=0,notify=True,
         pokes=[source_place]+received(recipient,0),lists=reverse,refresh=False)
    case('received_slot_36_guard',kind='drop',target=recipient,slot=36,pokes=received(recipient,36,person=0))
    driver, marker = o.alloc(0x10000),o.alloc(1)
    o.poke(marker,b'\xc3')
    before, trace, armed = [],[],[False]
    def begin(mu,address,size,data): armed[0]=True; before.append(audit.read())
    hook = o.mu.hook_add(UC_HOOK_CODE,begin,begin=marker,end=marker)
    def refresh():
        if armed[0]: trace.append(o.mu.reg_read(UC_X86_REG_RDX))
        return 0
    o.stub(0x1A3C50,fn=refresh)
    def emit(code,rva,args):
        for op,value in zip([b'\x48\xb9',b'\x48\xba',b'\x49\xb8',b'\x49\xb9'],args):
            code.extend(op+struct.pack('<Q',value&0xFFFFFFFFFFFFFFFF))
        code.extend(b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+rva)+b'\xff\xd0')
    cases=[]
    try:
        for fixture in fixtures:
            before.clear();trace.clear();armed[0]=False
            code=bytearray(b'\x48\x83\xec\x28')
            for key,values in fixture['propagated'].items():
                unit,at=key.split(':');wrapper=o.q(ctx.addr(UNITS,int(unit))+int(at,16))
                index=struct.unpack('<I',o.read(wrapper,4))[0]
                emit(code,0x18770,[pool,index])
                for value in values:
                    emit(code,0x18DF0,[pool,index,1])
                    code.extend(b'\x48\xba'+struct.pack('<Q',ctx.addr(UNITS,value) if value is not None else 0)+b'\x48\x89\x10')
            # A physical refresh host boundary is an input. Do not stub its initializer or any gameplay helper.
            code.extend(b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+0x1B91DC0))
            code.extend(b'\x48\xba'+struct.pack('<Q',refresh_host if fixture['refresh_available'] else 0)+b'\x48\x89\x10')
            emit(code,marker-ctx.g.exe,[])
            pointer=ctx.addr(UNITS,fixture['unit'])
            if fixture['kind']=='all_off':
                for slot in range(9):emit(code,0x2A1F00,[pointer,slot,0,0])
            else:emit(code,0x2A1F00 if fixture['kind']=='off' else 0x2A2100,
                      [pointer,fixture['slot'],int(fixture['notify']),0])
            code.extend(b'\x48\x83\xc4\x28\xc3')
            o.poke(driver,bytes(code));o.mu.ctl_remove_cache(driver,driver+0x10000)
            output=[]
            def inspect():output.append(audit.read());return []
            result=ctx.run(fixture['pokes'],lambda:o.call(driver-ctx.g.exe,read_after=inspect))
            if result is None:raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
            if len(before)!=1 or len(output)!=1:raise RuntimeError('Missing native operation checkpoint')
            expected=copy.deepcopy(original['nested'])
            for key,values in fixture['propagated'].items():
                expected['unit:'+key]=[hex(ctx.addr(UNITS,i)) if i is not None else '0x0' for i in values]
            if before[0]['nested']!=expected:raise RuntimeError('Native fixture setup differs from explicit input lists')
            delta=audit.delta(before[0],output[0])
            if delta.keys()-{'records','nested'}:raise RuntimeError(f'Unexpected gameplay writes: {delta.keys()}')
            if any(int(t,16) not in [UNITS,OFFICERS] for t,i,b in delta['records']):raise RuntimeError('Unexpected table write')
            refs={ctx.addr(UNITS,i):i for i in range(501)}
            cases.append(dict(fixture=fixture,after=delta,refreshes=[refs[p] for p in trace]))
            print(f'  personality lifecycle {fixture["name"]}: records {len(delta["records"])}, refresh {len(trace)}',flush=True)
    finally:
        o.unstub();o.mu.hook_del(hook)
    if audit.read()!=original:raise RuntimeError('Personality fixtures did not restore the frozen input')
    if o.q(ctx.g.exe+0x1B91DC0)!=refresh_host:raise RuntimeError('The refresh host input was not restored')
    if not ctx.frozen:require_unchanged_snapshot(snapshot,initial_rng)
    ctx.save('unit_personality_lifecycle_world.json.gz',snapshot)
    ctx.save('unit_personality_lifecycle.json.gz',dict(world='unit_personality_lifecycle_world.json.gz',cases=cases,
        gameplay_stubs=[],presentation=['0x1a3c50'],native_pool=True))
    return f'personality lifecycle: {len(cases)} complete native bodies and actual pool propagation'
