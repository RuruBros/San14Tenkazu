# Complete force reset with a real copied allocation at +0x1A0; this field is not a three-pointer vector.
import copy,random,struct
from context import FORCES,world
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX

COVER=[0x226030,0x2EBCC0,0x18770,0x3F6C50]


def generate(ctx,retained=()):
    if not ctx.frozen:raise RuntimeError('Owned force storage requires a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx)
    audit.wrappers.extend((f'force:{i}:{at:x}',ctx.addr(FORCES,i)+at) for i in range(52) for at in (0x250,0x260))
    baseline=audit.read();snapshot=copy.deepcopy(audit.snapshot);snapshot['nested']=baseline['nested']
    for key in snapshot['tables']:snapshot['tables'][key]['address']=hex(ctx.addr(int(key,16),0))
    rng=random.Random(0x226030);requests=[]
    def case(name,target=3,absent=(),enabled=True,randomize=False):
        pokes=[['worldstate',0,0x3A,'00']]
        if randomize:
            b=bytearray(ctx.rec[FORCES][target]);b[0x10:0x250]=rng.randbytes(0x240)
            pokes.append(['0xdf30',target,0,b.hex()])
        pokes.extend(['0xdf30',target,at,bytes(8).hex()] for at in absent)
        requests.append(dict(name=name,target=target,pokes=pokes,enabled=enabled))
    case('original_owned_storage')
    case('zero_slot',0)
    case('active_force',45)
    case('special_slot',51)
    case('first_list_absent',absent=(0x250,))
    case('second_list_absent',absent=(0x260,))
    case('both_lists_absent',absent=(0x250,0x260))
    case('pool_disabled',enabled=False)
    case('record_padding',randomize=True)
    prior={c['request']['name']:c for c in retained};cases=[];entered=[];frees=[];clears=[];active=[None];hooks=[]
    pool=ctx.g.exe+0x27D10E0;mov=lambda reg,value:bytes.fromhex(reg)+struct.pack('<Q',value)
    def entry(mu,address,size,data):
        f=active[0];force=ctx.addr(FORCES,f['target']);pointer=o.q(force+0x1A0)
        if not pointer:raise RuntimeError('Native allocation returned null')
        o.emu_write(pointer,bytes.fromhex('13579bdf'));o.emu_write(force+0x1A8,struct.pack('<Q',0xFEDCBA9876543210))
        if not f['enabled']:o.emu_write(pool+8,bytes(8))
        entered.append(dict(state=audit.read(),input=o.read(force,0x268).hex(),pointer=hex(pointer),
            indices={struct.unpack('<I',o.read(o.q(force+at),4))[0]:at for at in (0x250,0x260) if o.q(force+at)}))
    def free(mu,address,size,data):
        force=ctx.addr(FORCES,active[0]['target'])
        frees.append(dict(pointer=hex(mu.reg_read(UC_X86_REG_RCX)),argument2=mu.reg_read(UC_X86_REG_RDX),record=o.read(force,0x268).hex()))
    def clear(mu,address,size,data):
        if mu.reg_read(UC_X86_REG_RCX)!=pool:raise RuntimeError('Unexpected list pool')
        clears.append(entered[0]['indices'][mu.reg_read(UC_X86_REG_RDX)&0xffffffff])
    try:
        for r,fn in ((0x226030,entry),(0x3F6C50,free),(0x18770,clear)):
            hooks.append(o.mu.hook_add(UC_HOOK_CODE,fn,begin=ctx.g.exe+r,end=ctx.g.exe+r))
        for f in requests:
            if f['name'] in prior:
                saved=prior[f['name']]
                if saved['request']!=f:raise RuntimeError('Retained request differs')
                cases.append(saved);continue
            active[0]=f;entered.clear();frees.clear();clears.clear();force=ctx.addr(FORCES,f['target']);code=o.alloc(256)
            machine=bytes.fromhex('4883ec28')+mov('48b9',32)+mov('48b8',ctx.g.exe+0x3F6BC0)+bytes.fromhex('ffd0')
            machine+=mov('48b9',force)+bytes.fromhex('488981a0010000')+mov('48b8',ctx.g.exe+0x226030)+bytes.fromhex('ffd04883c428c3');o.poke(code,machine)
            def run():
                after=[]
                def inspect():after.append(audit.read());return []
                o.call(code-ctx.g.exe,read_after=inspect)
                if len(entered)!=1 or len(frees)!=1 or frees[0]['pointer']!=entered[0]['pointer'] or frees[0]['argument2']!=4:raise RuntimeError('Force storage release differs')
                delta=audit.delta(entered[0]['state'],after[0])
                if delta.keys()-{'records','nested'} or any(t!='0xdf30' for t,_,_ in delta['records']):raise RuntimeError('Unexpected force reset changes')
                return dict(request=f,fixture=dict(name=f['name'],target=f['target'],enabled=f['enabled'],pokes=f['pokes']+[['0xdf30',f['target'],0x1A0,entered[0]['input'][0x1A0*2:0x1B0*2]]]),
                    after=delta,frees=list(frees),clear_calls=list(clears))
            c=ctx.run(f['pokes'],run)
            if c is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
            cases.append(c);ctx.save('force_owned_storage_progress.json.gz',dict(cases=cases))
            print('  owned force',f['name'],'records',len(c['after']['records']),'clear',c['clear_calls'],flush=True)
        if audit.read()!=baseline:raise RuntimeError('Owned force collection did not roll back')
        ctx.save('force_owned_storage_world.json.gz',snapshot)
        ctx.save('force_owned_storage.json.gz',dict(world='force_owned_storage_world.json.gz',cases=cases,entry='0x226030',complete=True,
            gameplay_stubs=False,allocator_stubs=False,allocation_size=32,player_refresh=False))
        return f'{len(cases)} complete native owned-storage resets'
    finally:
        for h in hooks:o.mu.hook_del(h)
        o.unstub()


def retained_probe(probe,ctx):
    if not probe['complete'] or probe['entry']!='0x226030' or probe['target']!=3 or probe['allocation_size']!=32:raise RuntimeError('Probe differs')
    force=ctx.addr(FORCES,3);o=ctx.o
    indices={struct.unpack('<I',o.read(o.q(force+at),4))[0]:at for at in (0x250,0x260)}
    f=dict(name='original_owned_storage',target=3,pokes=probe['pokes'],enabled=True)
    return dict(request=f,fixture=dict(name=f['name'],target=3,enabled=True,pokes=f['pokes']+[['0xdf30',3,0x1A0,probe['input_record'][0x1A0*2:0x1B0*2]]]),
        after=probe['after'],frees=probe['frees'],clear_calls=[indices[i] for i in probe['clear_indices']])
