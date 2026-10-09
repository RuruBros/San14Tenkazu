# Complete cancellation on copied memory, including the location-dependent orphaned item refund.
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
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_R8

COVER = [0x334780, 0x1F1B30, 0x22B460, 0x24A9C0, 0x323ED0, 0x32B0E0, 0x224670, 0x32A5E0, 0x1D7DA0, 0x1F6720]


def generate(ctx, names=None):
    if not ctx.frozen: raise RuntimeError('These fixtures reuse the existing verified frozen command input')
    o = ctx.o
    o.insn_limit = 300_000_000
    capture_native_pools(ctx, [(0x1B5C990, 24, 8)])
    audit = EngineAuditState(ctx)
    with gzip.open(os.path.join(ctx.output, 'return_command_world.json.gz'), 'rt', encoding='utf-8') as file:
        snapshot = json.load(file)
    if audit.snapshot['tables'] != snapshot['tables'] or audit.snapshot['worldstate'] != snapshot['worldstate']:
        raise RuntimeError('The reused command catalog belongs to a different frozen input')
    commands = snapshot['commands']
    index = next(i for i,c in enumerate(commands) if c['kind'] == 8 and c['handlers']['0xd8'] == '0x22b460')
    command = commands[index]; pointer = int(command['address'], 16)
    item = next(n[0][1] for n in snapshot['lists']['0x110']['items'] if n[0][1])
    first = ctx.officer_by_id(command['officers'][0])
    target = struct.unpack('<H', o.read(pointer + 0x6C, 2))[0]
    def cp(at, value, fmt='i'): return [at, struct.pack('<'+fmt,value).hex()]
    common = [['worldstate',0,0x3A,'00'], [hex(ITEMS),item,0x4F,'ff']]
    common += [[hex(OFFICERS),ctx.officer_by_id(pid),0xE6,'06'] for pid in command['officers']]
    fixtures = []
    def fixture(name, command_pokes=None, pokes=None):
        fixtures.append(dict(name=name, command=index, command_pokes=[cp(0x75,0,'B'),cp(0x76,item,'H')]+(command_pokes or []),
                             pokes=common+(pokes or [])))
    fixture('original_city_home')
    fixture('gate_home',pokes=[[hex(OFFICERS),first,0xE2,'3400']])
    fixture('invalid_home',pokes=[[hex(OFFICERS),first,0xE2,'0000']])
    fixture('invalid_first_person',pokes=[[hex(OFFICERS),first,0xE6,'00']])
    for county in [0,1,500,501]:
        fixture(f'county_{county}',command_pokes=[cp(0x4C,target,'H'),cp(0x6C,target,'H')],
                pokes=[[hex(TILES),target,0x12,struct.pack('<H',county).hex()]])
    fixture('out_of_bounds_force',command_pokes=[cp(0x75,52,'B')])
    fixture('invalid_item',pokes=[[hex(ITEMS),item,0x50,'02']])
    fixture('serving_return')
    fixtures[-1]['pokes'] = common[:2]
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names): raise ValueError('Unknown orphan fixture')
    original, initial_store = audit.read(), read_store(ctx,audit)
    frame, hooks = [None], []
    def trace(rva):
        def entry(mu,address,size,user):
            if frame[0] is None: return
            frame[0]['calls'].append(hex(rva))
            if rva == 0x323ED0: frame[0]['county'].append(mu.reg_read(UC_X86_REG_R8))
            if rva == 0x32A5E0: frame[0]['removal_data']=o.read(pointer,len(bytes.fromhex(command['data']))).hex()
            if rva == 0x24A9C0: frame[0]['at_shuffle']=audit.read()
            if rva == 0x1D7DA0:
                p=mu.reg_read(UC_X86_REG_RCX)
                frame[0]['allocations'].append(dict(address=hex(p),data=o.read(p,0x80).hex()))
            if rva == 0x1F6720: frame[0]['new_pointer']=mu.reg_read(UC_X86_REG_RCX)
        return entry
    for rva in COVER: hooks.append(o.mu.hook_add(UC_HOOK_CODE,trace(rva),begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    for rva in [*PRESENTATION,0x1B4990]: o.stub(rva)
    cases=[]; bugs=[]
    try:
        for f in fixtures:
            began, undo = time.time(), []
            for at,h in f['command_pokes']:
                b=bytes.fromhex(h); undo.append((pointer+at,o.read(pointer+at,len(b)))); o.poke(pointer+at,b)
            try:
                def run():
                    before,after=audit.read(),[]
                    active=dict(calls=[],county=[],allocations=[]); frame[0]=active
                    def inspect():
                        result=dict(after=audit.delta(before,audit.read()),store=read_store(ctx,audit))
                        if 'new_pointer' in active:
                            p=active.pop('new_pointer'); wrapper=o.q(p+0x38); slot=struct.unpack('<I',o.read(wrapper,4))[0]
                            node=o.q(o.q(ctx.g.exe+0x2143270+0x10)+slot*8); ds=[]
                            while node: ds.append(o.read(node,24).hex()); node=o.q(node+24)
                            result['new_command']=dict(address=hex(p),data=o.read(p,0x80).hex(),descriptors=ds)
                        after.append(result); return []
                    try:
                        o.call(0x334780,pointer,read_after=inspect)
                        return dict(fixture=f,**active,**after[0])
                    finally: frame[0]=None
                result=ctx.run(f['pokes'],run)
                if result is None: raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
                if '0x24a9c0' in result['calls']: raise RuntimeError('Normal nonempty county input unexpectedly shuffled')
                cases.append(result)
                print(f'  orphan item {f["name"]}: county {result["county"]}, {len(result["after"]["records"])} records ({time.time()-began:.2f}s)',flush=True)
            finally:
                for a,b in reversed(undo): o.poke(a,b)
        home=ctx.u16(OFFICERS,first,0xE2)
        county_pokes=[[hex(0x6CAF0),n[0][1],0x35,'00'] for n in snapshot['lists']['0xb0']['items']
                     if bytes.fromhex(snapshot['tables']['0x6caf0']['records'][n[0][1]])[0x35] == home]
        if not county_pokes: raise RuntimeError('The native empty-county probe requires an actual city with registered counties')
        bug=dict(name='empty_home_counties',command=index,command_pokes=[cp(0x75,0,'B'),cp(0x76,item,'H')],pokes=common+county_pokes)
        undo=[]
        for at,h in bug['command_pokes']:
            b=bytes.fromhex(h); undo.append((pointer+at,o.read(pointer+at,len(b)))); o.poke(pointer+at,b)
        try:
            before=[]; active=dict(calls=[],county=[],allocations=[])
            def fail():
                before.append(audit.read()); frame[0]=active
                try: return o.call(0x334780,pointer)
                finally: frame[0]=None
            result=ctx.run(bug['pokes'],fail)
            if result is not None or '0x24a9c0' not in active['calls'] or active['county']:
                raise RuntimeError('The empty-county fixture did not reproduce the analyzed native failure')
            error=ctx.failures[-1]['error']
            if o.last_fault != (0, ctx.g.exe + 0x1F1EBE): raise RuntimeError(f'Unexpected empty-county failure: {error}')
            delta=audit.delta(before[0],active.pop('at_shuffle'))
            bugs.append(dict(fixture=bug,error=error,fault_address='0x0',fault_rva='0x1f1ebe',before_failure=delta,**active))
            print(f'  orphan item empty_home_counties: native null read, no item refund ({error})',flush=True)
        finally:
            for a,b in reversed(undo): o.poke(a,b)
    finally:
        o.unstub()
        for hook in hooks: o.mu.hook_del(hook)
    if audit.read()!=original or read_store(ctx,audit)!=initial_store: raise RuntimeError('Orphan fixtures did not restore their frozen input')
    ctx.save('orphan_item_world.json.gz',snapshot)
    ctx.save('orphan_item.json.gz',dict(world='orphan_item_world.json.gz',cases=cases,bugs=bugs,gameplay_stubs=[],
        presentation=[hex(r) for r in [*PRESENTATION,0x1B4990]],
        physical_fields=[[0,8],[0x18,16],[0x30,16]],
        scope='Complete native cancellation, orphan item reservation/owner/county/state/owned-list, return construction and command storage'))
    return f'orphan items: {len(cases)} complete cancellations'
