# Replay complete ordinary heap-name death using actual copied native allocation and free.
import copy,struct
from context import OFFICERS,world
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX

COVER=[0x295AA0,0x226300,0x241C30,0x242690,0x21E190]


def generate(ctx,retained=()):
    if not ctx.frozen:raise RuntimeError('Heap death requires a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx);baseline=audit.read()
    snapshot=copy.deepcopy(audit.snapshot);snapshot['nested']=baseline['nested']
    for key in snapshot['tables']:snapshot['tables'][key]['address']=hex(ctx.addr(int(key,16),0))
    person=ctx.addr(OFFICERS,551);bits=ctx.u16(OFFICERS,551,0x15E)
    word=lambda at,value:['0x73a50',551,at,struct.pack('<H',value).hex()]
    requests=[dict(name='original_dead_slot',capacity=None,pokes=[]),
        dict(name='free_officer',capacity=None,pokes=[['0x73a50',551,0xE6,'05']]),
        dict(name='free_minimum_heap',capacity=8,pokes=[['0x73a50',551,0xE6,'05']]),
        dict(name='marked_free_actual_death',capacity=None,pokes=[['0x73a50',551,0xE6,'05'],word(0x15E,bits|0x4000),['0x73a50',551,0x1C5,'01']]),
        dict(name='free_forced_loyalty',capacity=None,pokes=[['0x73a50',551,0xE6,'05'],word(0x15E,bits|0x2000)])]
    prior={c['request']['name']:c for c in retained};cases=[];entered=[];frees=[];active=[None];hooks=[]
    mov=lambda reg,value:bytes.fromhex(reg)+struct.pack('<Q',value)
    def entry(mu,address,size,data):
        pointer=o.q(person+0x198)
        if active[0]['capacity'] is not None:o.emu_write(person+0x1B0,struct.pack('<Q',active[0]['capacity']))
        capacity=o.q(person+0x1B0)
        if capacity<8:raise RuntimeError('Native allocation did not produce a heap name')
        o.emu_write(pointer,('HeapName\0').encode('utf-16-le'));o.emu_write(person+0x1A8,struct.pack('<Q',8))
        entered.append(dict(state=audit.read(),input=o.read(person+0x198,32).hex(),
            buffer=dict(pointer=hex(pointer),allocation=hex(pointer),capacity=capacity,data=o.read(pointer,(capacity+1)*2).hex())))
    def free(mu,address,size,data):frees.append(hex(mu.reg_read(UC_X86_REG_RCX)))
    def growth(mu,address,size,data):
        if mu.reg_read(UC_X86_REG_RDX)!=8:raise RuntimeError('Unexpected growth request')
    try:
        for r,fn in ((0x295AA0,entry),(0x3F6C50,free),(0x21DC10,growth)):
            hooks.append(o.mu.hook_add(UC_HOOK_CODE,fn,begin=ctx.g.exe+r,end=ctx.g.exe+r))
        for f in requests:
            if f['name'] in prior:
                saved=prior[f['name']]
                if saved['request']!=f:raise RuntimeError('Retained request differs')
                cases.append(saved);continue
            active[0]=f;entered.clear();frees.clear();code=o.alloc(256)
            machine=bytes.fromhex('4883ec58')+mov('48b9',person+0x198)+mov('48ba',8)+bytes.fromhex('4531c0')+mov('48b8',ctx.g.exe+0x21DC10)+bytes.fromhex('ffd0')
            machine+=mov('48b9',person)+bytes.fromhex('31d241b8010000004531c9')
            for at in (0x20,0x28,0x30,0x38,0x40):machine+=bytes.fromhex('48c74424')+bytes([at])+bytes(4)
            machine+=mov('48b8',ctx.g.exe+0x295AA0)+bytes.fromhex('ffd04883c458c3');o.poke(code,machine)
            def run():
                after=[]
                def inspect():after.append(audit.read());return []
                returned=o.call(code-ctx.g.exe,read_after=inspect)&0xffffffff
                if len(entered)!=1 or frees!=[entered[0]['buffer']['allocation']]:raise RuntimeError('Heap death release sequence differs')
                delta=audit.delta(entered[0]['state'],after[0])
                if delta.keys()-{'records','lists'} or any(t!='0x73a50' for t,_,_ in delta['records']):raise RuntimeError('Unexpected free/dead-slot mutation')
                return dict(request=f,fixture=dict(name=f['name'],person=551,pokes=f['pokes']+[['0x73a50',551,0x198,entered[0]['input']]]),
                    buffer=entered[0]['buffer'],after=delta,frees=list(frees),result=returned)
            c=ctx.run(f['pokes'],run)
            if c is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
            cases.append(c);ctx.save('officer_death_heap_progress.json.gz',dict(cases=cases))
            print('  heap death',f['name'],'records',len(c['after']['records']),c['frees'],flush=True)
        if audit.read()!=baseline:raise RuntimeError('Heap death did not roll back')
        ctx.save('officer_death_heap_world.json.gz',snapshot)
        ctx.save('officer_death_heap.json.gz',dict(world='officer_death_heap_world.json.gz',entry='0x295aa0',complete=True,
            gameplay_stubs=False,allocator_stubs=False,cases=cases,scope='Complete no-item free/dead-slot death, excluding ruler/player/history bodies.'))
        return f'{len(cases)} complete native heap deaths'
    finally:
        for h in hooks:o.mu.hook_del(h)
        o.unstub()


def retained_probe(probe):
    if not probe['complete'] or probe['entry']!='0x295aa0' or probe['allocator_stubs'] or probe['gameplay_stubs']:raise RuntimeError('Not a complete heap death probe')
    if probe['person']!=551 or (probe['wanted'],probe['name']['capacity']) not in ((8,15),(2048,2055)):raise RuntimeError('Probe input differs')
    if len(probe['frees'])!=1:raise RuntimeError('Exactly one actual allocation release is required')
    buffer=copy.deepcopy(probe['name']);buffer['allocation']=probe['frees'][0]
    name='original_dead_slot' if probe['wanted']==8 else 'original_dead_slot_large'
    return dict(request=dict(name=name,capacity=None,pokes=[]),fixture=dict(name=name,person=551,
        pokes=[['0x73a50',551,0x198,probe['input_record'][0x198*2:0x1B8*2]]]),buffer=buffer,after=probe['after'],frees=probe['frees'],result=probe['result'])


def publish_probe(ctx,probe,report,stem):
    # Package an already completed, independently guarded call without replaying native code.
    if not ctx.frozen or not report['complete'] or report['input_sha256']!=probe['input_sha256']:
        raise RuntimeError('A complete coherent heap-death input is required')
    case=retained_probe(probe);audit=EngineAuditState(ctx);baseline=audit.read()
    original=baseline['records'][OFFICERS][551*0x1F0:552*0x1F0];entered=bytes.fromhex(probe['input_record'])
    if original[:0x198]!=entered[:0x198] or original[0x1B8:]!=entered[0x1B8:]:
        raise RuntimeError('Retained death input differs outside the actual grown name')
    snapshot=copy.deepcopy(audit.snapshot);snapshot['nested']=baseline['nested']
    for key in snapshot['tables']:snapshot['tables'][key]['address']=hex(ctx.addr(int(key,16),0))
    ctx.save(stem+'_world.json.gz',snapshot)
    ctx.save(stem+'.json.gz',dict(world=stem+'_world.json.gz',entry='0x295aa0',complete=True,
        input_sha256=report['input_sha256'],gameplay_stubs=False,allocator_stubs=False,cases=[case],
        scope='Complete no-item dead-slot death with actual aligned heap allocation and release.'))
    return 1
