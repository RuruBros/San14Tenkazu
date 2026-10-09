# Native string allocation and complete officer reset in a single copied-memory harness.
import copy,struct
from context import OFFICERS,world
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX

COVER=[0x226300,0x242690,0x2421B0,0x21E190]


def generate(ctx,retained=(),large=False,basename='officer_name_heap'):
    if not ctx.frozen:raise RuntimeError('Heap reset requires a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx);baseline=audit.read()
    snapshot=copy.deepcopy(audit.snapshot);snapshot['nested']=baseline['nested']
    for key in snapshot['tables']:snapshot['tables'][key]['address']=hex(ctx.addr(int(key,16),0))
    names=[dict(name='large_2048',wanted=2048,capacity=None,pokes=[])] if large else [dict(name='small_original',wanted=8,capacity=None,pokes=[])]
    if large:
        names.extend(dict(name=f'large_{wanted}',wanted=wanted,capacity=None,pokes=[]) for wanted in (2040,))
    else:
        names.extend(dict(name=f'small_capacity_{cap}',wanted=8,capacity=cap,pokes=[]) for cap in range(8,15))
        bits=ctx.u16(OFFICERS,551,0x15E)
        names.append(dict(name='small_forced_loyalty',wanted=8,capacity=None,pokes=[['0x73a50',551,0x15E,struct.pack('<H',bits|0x2000).hex()]]))
    cases=[];prior={c['fixture']['name']:c for c in retained};hooks=[];entered=[];frees=[];active=[None]
    person=ctx.addr(OFFICERS,551)
    mov=lambda reg,value:bytes.fromhex(reg)+struct.pack('<Q',value)
    def start(mu,address,size,data):
        f=active[0]
        pointer=o.q(person+0x198)
        if f['capacity'] is not None:o.emu_write(person+0x1B0,struct.pack('<Q',f['capacity']))
        capacity=o.q(person+0x1B0)
        if capacity<8:raise RuntimeError('Native growth did not allocate a heap name')
        o.emu_write(pointer,('HeapName'+'\0').encode('utf-16-le'));o.emu_write(person+0x1A8,struct.pack('<Q',8))
        allocation=o.q(pointer-8) if (capacity+1)*2>=4096 else pointer
        entered.append(dict(state=audit.read(),input=o.read(person+0x198,32).hex(),
            buffer=dict(pointer=hex(pointer),capacity=capacity,allocation=hex(allocation),data=o.read(pointer,(capacity+1)*2).hex())))
    def free(mu,address,size,data):frees.append(hex(mu.reg_read(UC_X86_REG_RCX)))
    def allocation(mu,address,size,data):
        if mu.reg_read(UC_X86_REG_RDX)!=active[0]['wanted']:raise RuntimeError('Harness requested a different string capacity')
    try:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE,start,begin=ctx.g.exe+0x226300,end=ctx.g.exe+0x226300))
        hooks.append(o.mu.hook_add(UC_HOOK_CODE,free,begin=ctx.g.exe+0x3F6C50,end=ctx.g.exe+0x3F6C50))
        hooks.append(o.mu.hook_add(UC_HOOK_CODE,allocation,begin=ctx.g.exe+0x21DC10,end=ctx.g.exe+0x21DC10))
        for f in names:
            if f['name'] in prior:
                saved=prior[f['name']]
                if saved['request']!=f:raise RuntimeError('Retained heap request differs')
                expected=f['capacity'] if f['capacity'] is not None else f['wanted']|7
                if saved['buffer']['capacity']==expected:
                    cases.append(saved);continue
            entered.clear();frees.clear();active[0]=f
            # Each harness has a distinct executable address; do not reuse a translated block after editing it.
            code=o.alloc(128)
            machine=bytes.fromhex('4883ec28')+mov('48b9',person+0x198)+mov('48ba',f['wanted'])+bytes.fromhex('4531c0')+mov('48b8',ctx.g.exe+0x21DC10)+bytes.fromhex('ffd0')
            machine+=mov('48b9',person)+mov('48b8',ctx.g.exe+0x226300)+bytes.fromhex('ffd04883c428c3');o.poke(code,machine)
            def run():
                after=[]
                def inspect():after.append(audit.read());return []
                o.call(code-ctx.g.exe,read_after=inspect)
                if len(entered)!=1 or frees!=[entered[0]['buffer']['allocation']]:raise RuntimeError('Allocation/reset/free sequence differs')
                delta=audit.delta(entered[0]['state'],after[0])
                if any(k!='records' for k in delta) or any(t!='0x73a50' or i!=551 for t,i,_ in delta['records']):raise RuntimeError('Unexpected heap reset mutation')
                return dict(request=f,fixture=dict(name=f['name'],person=551,pokes=f['pokes']+[['0x73a50',551,0x198,entered[0]['input']]]),
                    buffer=entered[0]['buffer'],after=delta,frees=list(frees))
            c=ctx.run(f['pokes'],run)
            if c is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
            cases.append(c);ctx.save(basename+'_progress.json.gz',dict(cases=cases))
            print('  heap',f['name'],c['buffer']['capacity'],c['frees'],flush=True)
        if audit.read()!=baseline:raise RuntimeError('Heap collection did not roll back')
        ctx.save(basename+'_world.json.gz',snapshot)
        ctx.save(basename+'.json.gz',dict(world=basename+'_world.json.gz',cases=cases,gameplay_stubs=False,
            allocator_stubs=False,complete=True,entry='0x226300',scope='Whole native officer slot reset including real copied allocator free.'))
        return f'{len(cases)} complete native heap officer resets'
    finally:
        for h in hooks:o.mu.hook_del(h)
        o.unstub()


def retained_probe(probe,large=False):
    if not probe['complete'] or probe['entry']!='0x226300' or probe['allocator_stubs']:raise RuntimeError('Not a complete heap reset probe')
    if probe['person']!=551:raise RuntimeError('Heap probe person differs')
    name='large_2048' if large else 'small_original';wanted=2048 if large else 8
    buffer=copy.deepcopy(probe['name']);buffer['allocation']=probe['frees'][0]
    return dict(request=dict(name=name,wanted=wanted,capacity=None,pokes=[]),fixture=dict(name=name,person=551,
        pokes=[['0x73a50',551,0x198,probe['input_record'][0x198*2:0x1B8*2]]]),buffer=buffer,after=probe['after'],frees=probe['frees'])
